using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Lunil.CodeGen.Cil.Emission;
using Lunil.CodeGen.Cil.Jit;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.CodeGen.Cil.Tests;

public sealed class LuaJitExecutorTests
{
    [Fact]
    public void ReleaseDefaultEnablesQualifiedTier1Tier2AndLoopOsrAutoPolicy()
    {
        Assert.Equal(LuaJitPolicy.Auto, LuaJitExecutorOptions.Default.Policy);
        Assert.True(LuaJitExecutorOptions.Default.EnableTier2);
        Assert.False(LuaJitExecutorOptions.Default.EnableTier2ManagedFallback);
        Assert.True(LuaJitExecutorOptions.Default.EnableLoopOsr);
        Assert.False(LuaJitExecutorOptions.Default.EnableLoopOsrManagedFallback);

        var constructed = new LuaJitExecutorOptions();
        Assert.Equal(LuaJitPolicy.Auto, constructed.Policy);
        Assert.True(constructed.EnableTier2);
        Assert.False(constructed.EnableTier2ManagedFallback);
        Assert.True(constructed.EnableLoopOsr);
        Assert.False(constructed.EnableLoopOsrManagedFallback);
    }

    [Fact]
    public void DefaultTier2ProfileImportPrewarmsAutomaticEligibility()
    {
        var module = Compile("local value = ...; return value + 1");
        using var training = CreateExecutor(LuaJitExecutorOptions.Default);
        var payload = training.ExportProfile(module);
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default);

        var result = executor.ImportProfile(module, payload);

        Assert.Equal(LuaJitProfileImportStatus.Imported, result.Status);
        Assert.Equal(LuaJitTier2State.Profiling, executor.GetTier2State(module, 0));
    }

    [Fact]
    public void ExplicitTier2DisableRejectsProfileImportAndProfiling()
    {
        var module = Compile("local value = ...; return value + 1");
        using var training = CreateExecutor(LuaJitExecutorOptions.Default);
        var payload = training.ExportProfile(module);
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            EnableTier2 = false,
        });

        var result = executor.ImportProfile(module, payload);

        Assert.Equal(LuaJitProfileImportStatus.Disabled, result.Status);
        Assert.Equal(LuaJitTier2State.Disabled, executor.GetTier2State(module, 0));
    }

    [Fact]
    public void InterpreterOnlyNeverTouchesTheDynamicCompiler()
    {
        var compiler = new CountingCompiler(ReflectionEmitLuaTier1Compiler.Instance);
        using var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.InterpreterOnly,
                SynchronousCompilation = true,
            },
            compiler: compiler);
        var module = Compile("return 1 + 2");
        var state = new LuaState();

        var result = executor.Execute(state, state.CreateMainClosure(module));

        AssertValues(result, LuaValue.FromInteger(3));
        Assert.Equal(0, compiler.CallCount);
        Assert.Equal(0, executor.Statistics.CompiledInvocations);
        Assert.True(executor.Statistics.InterpreterFallbacks > 0);
    }

    [Fact]
    public void PreferJitSynchronouslyCompilesAndPublishesEvents()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
        });
        var events = new ConcurrentQueue<LuaJitEvent>();
        executor.EventOccurred += (_, jitEvent) => events.Enqueue(jitEvent);
        var module = Compile("local value = { number = 7 }; return value.number + 1");
        var state = new LuaState();

        var result = executor.Execute(state, state.CreateMainClosure(module));

        AssertValues(result, LuaValue.FromInteger(8));
        Assert.Equal(LuaJitFunctionState.Ready, executor.GetFunctionState(module, 0));
        Assert.True(executor.Statistics.CompiledInvocations > 0);
        Assert.True(executor.Statistics.EstimatedCodeBytes > 0);
        Assert.True(executor.Statistics.CompiledCanonicalInstructions > 0);
        Assert.Equal(
            executor.Statistics.CompiledInvocations,
            executor.Statistics.SchedulerExits);
        Assert.True(executor.Statistics.Tier1CompileAllocatedBytes > 0);
        Assert.True(executor.Statistics.Tier1PlanInstructions > 0);
        Assert.Contains(events, jitEvent => jitEvent.Kind == LuaJitEventKind.Queued);
        var completed = Assert.Single(
            events,
            jitEvent => jitEvent.Kind == LuaJitEventKind.CompilationCompleted);
        var metrics = Assert.IsType<LuaJitCompilationMetrics>(completed.CompilationMetrics);
        Assert.True(metrics.AllocatedBytes > 0);
        Assert.Equal(
            metrics.CanonicalInstructionCount,
            metrics.DirectCanonicalInstructionCount + metrics.SlowPathCanonicalInstructionCount);
        Assert.True(metrics.PlanInstructionCount > metrics.CanonicalInstructionCount);
        Assert.True(metrics.EstimatedCodeBytes > 0);
    }

    [Fact]
    public void Tier1HotPathDoesNotAllocateAFactoryClosurePerSchedulerEntry()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            EnableTier2 = false,
        });
        var module = Compile("""
            local values = {}
            local total = 0
            local index = 0
            while index < 2000 do
                index = index + 1
                local key = (index & 127) + 1
                values[key] = index
                total = total + values[key]
            end
            return total
            """);
        var state = new LuaState();
        var closure = state.CreateMainClosure(module);
        AssertValues(executor.Execute(state, closure), LuaValue.FromInteger(2_001_000));
        AssertValues(executor.Execute(state, closure), LuaValue.FromInteger(2_001_000));

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        AssertValues(executor.Execute(state, closure), LuaValue.FromInteger(2_001_000));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.InRange(allocated, 0, 256 * 1024);
        Assert.True(executor.Statistics.SchedulerExits > 1_000);
    }

    [Fact]
    public void TerminalEligibilityRejectionInstallsABoundedFunctionReferenceRoute()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = 1,
            BackedgeThreshold = 1,
            SynchronousCompilation = true,
            EnableTier2 = false,
            EnableLoopOsr = false,
        });
        var events = new ConcurrentQueue<LuaJitEvent>();
        executor.EventOccurred += (_, jitEvent) => events.Enqueue(jitEvent);
        var module = Compile("""
            local values = {}
            local total = 0
            for index = 1, 100 do
                values[index] = index
                total = total + values[index]
            end
            return total
            """);
        var state = new LuaState();
        var closure = state.CreateMainClosure(module);

        AssertValues(executor.Execute(state, closure), LuaValue.FromInteger(5_050));
        AssertValues(executor.Execute(state, closure), LuaValue.FromInteger(5_050));

        Assert.Equal(LuaJitFunctionState.Cold, executor.GetFunctionState(module, 0));
        Assert.Equal(0, executor.Statistics.CompiledInvocations);
        Assert.Equal(1, executor.Statistics.InterpreterFallbacks);
        Assert.Equal(0, executor.Statistics.Deoptimizations);
        Assert.Equal(1, executor.Statistics.FunctionEntries);
        Assert.Single(events, static item => item.Kind == LuaJitEventKind.Fallback);
        Assert.Equal(
            LuaJitEligibilityReason.SlowPathDensityTooHigh,
            executor.GetFunctionEligibility(module, 0).Reason);
    }

    [Fact]
    public void TerminalReferenceRouteMatchesStandaloneInterpreterAllocation()
    {
        var module = Compile("""
            local values = {}
            local total = 0
            for index = 1, 500 do
                values[index] = index
                total = total + values[index]
            end
            return total
            """);
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = 1,
            BackedgeThreshold = 1,
            SynchronousCompilation = true,
            EnableTier2 = false,
            EnableLoopOsr = false,
        });
        var interpreterState = new LuaState();
        var interpreterClosure = interpreterState.CreateMainClosure(module);
        var jitState = new LuaState();
        var jitClosure = jitState.CreateMainClosure(module);
        var interpreter = new LuaInterpreter();
        AssertValues(
            interpreter.Execute(interpreterState, interpreterClosure),
            LuaValue.FromInteger(125_250));
        AssertValues(
            executor.Execute(jitState, jitClosure),
            LuaValue.FromInteger(125_250));

        var interpreterAllocated = MeasureCurrentThreadAllocation(
            3,
            () => AssertValues(
                interpreter.Execute(interpreterState, interpreterClosure),
                LuaValue.FromInteger(125_250)));
        var jitAllocated = MeasureCurrentThreadAllocation(
            3,
            () => AssertValues(
                executor.Execute(jitState, jitClosure),
                LuaValue.FromInteger(125_250)));

        Assert.InRange(jitAllocated, 0, interpreterAllocated + 4_096);
        Assert.Equal(1, executor.Statistics.InterpreterFallbacks);
    }

    [Fact]
    public void SieveFallbackUsesOneReferenceRouteTransition()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = 1,
            BackedgeThreshold = 1,
            SynchronousCompilation = true,
            EnableTier2 = false,
            EnableLoopOsr = false,
        });
        var events = new ConcurrentQueue<LuaJitEvent>();
        executor.EventOccurred += (_, jitEvent) => events.Enqueue(jitEvent);
        var module = Compile("""
            local limit = 500
            local sieve = {}
            for index = 2, limit do sieve[index] = true end
            for index = 2, 22 do
                if sieve[index] then
                    for composite = index * index, limit, index do
                        sieve[composite] = false
                    end
                end
            end
            local count = 0
            for index = 2, limit do
                if sieve[index] then count = count + 1 end
            end
            return count
            """);

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(95));
        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(95));

        Assert.Equal(0, executor.Statistics.CompiledInvocations);
        Assert.Equal(1, executor.Statistics.InterpreterFallbacks);
        Assert.Equal(0, executor.Statistics.Deoptimizations);
        Assert.Single(events, static item => item.Kind == LuaJitEventKind.Fallback);
    }

    [Fact]
    public void InvocationHotnessQualifiesSmallRecursiveFunctions()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = 8,
            BackedgeThreshold = int.MaxValue,
            SynchronousCompilation = true,
            EnableTier2 = false,
            EnableLoopOsr = false,
        });
        var module = Compile("""
            local function fib(n)
                if n < 2 then return n end
                return fib(n - 1) + fib(n - 2)
            end
            return fib(12)
            """);

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(144));

        var recursiveFunction = Assert.Single(
            module.Functions.Where(static function => function.Id != 0));
        Assert.Equal(
            LuaJitFunctionState.Ready,
            executor.GetFunctionState(module, recursiveFunction.Id));
        var eligibility = executor.GetFunctionEligibility(module, recursiveFunction.Id);
        Assert.True(eligibility.IsAutoEligible);
        Assert.Equal(LuaJitEligibilityReason.Eligible, eligibility.Reason);
        Assert.True(executor.Statistics.CompiledInvocations > 0);
        Assert.InRange(executor.Statistics.InterpreterFallbacks, 1, 4);
    }

    [Fact]
    public void FunctionRouteCacheKeepsMixedModuleDecisionsIndependent()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = 1,
            BackedgeThreshold = 1,
            SynchronousCompilation = true,
            EnableTier2 = false,
            EnableLoopOsr = false,
        });
        var module = Compile("""
            local function table_sum(count)
                local values = {}
                local total = 0
                for index = 1, count do
                    values[index] = index
                    total = total + values[index]
                end
                return total
            end
            local function arithmetic(count)
                local total = 0
                for index = 1, count do total = total + index end
                return total
            end
            return table_sum(20), arithmetic(20)
            """);

        AssertValues(
            ExecuteFresh(executor, module),
            LuaValue.FromInteger(210),
            LuaValue.FromInteger(210));

        var tableFunction = Assert.Single(module.Functions.Where(function =>
            executor.GetFunctionEligibility(module, function.Id).Reason ==
                LuaJitEligibilityReason.SlowPathDensityTooHigh));
        var arithmeticFunction = Assert.Single(module.Functions.Where(function =>
            function.Id != 0 &&
            executor.GetFunctionEligibility(module, function.Id).IsAutoEligible));
        Assert.Equal(
            LuaJitFunctionState.Cold,
            executor.GetFunctionState(module, tableFunction.Id));
        Assert.Equal(
            LuaJitFunctionState.Ready,
            executor.GetFunctionState(module, arithmeticFunction.Id));
        Assert.True(executor.Statistics.CompiledInvocations > 0);
    }

    [Fact]
    public async Task AutoPolicyCompilesAsynchronouslyAfterTheHotThreshold()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            FunctionEntryThreshold = 1,
            BackedgeThreshold = 1,
        });
        var module = Compile("local total = 0; for i = 1, 10 do total = total + i end; return total");

        var firstState = new LuaState();
        var first = executor.Execute(firstState, firstState.CreateMainClosure(module));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await executor.WaitForIdleAsync(timeout.Token);
        var secondState = new LuaState();
        var second = executor.Execute(secondState, secondState.CreateMainClosure(module));

        AssertValues(first, LuaValue.FromInteger(55));
        AssertValues(second, LuaValue.FromInteger(55));
        Assert.Equal(LuaJitFunctionState.Ready, executor.GetFunctionState(module, 0));
        Assert.True(executor.Statistics.CompilationCompleted >= 1);
        Assert.True(executor.Statistics.CompiledInvocations >= 1);
        Assert.Equal(LuaJitTier2State.Profiling, executor.GetTier2State(module, 0));
        Assert.Equal(0, executor.Statistics.Tier2CompilationQueued);
    }

    [Fact]
    public void AutoEligibilityRejectsSchedulerBoundaryDenseFunctionsBeforeQueueing()
    {
        var compiler = new CountingCompiler(ReflectionEmitLuaTier1Compiler.Instance);
        using var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                FunctionEntryThreshold = 1,
                BackedgeThreshold = 1,
                SynchronousCompilation = true,
            },
            compiler: compiler);
        var events = new ConcurrentQueue<LuaJitEvent>();
        executor.EventOccurred += (_, jitEvent) => events.Enqueue(jitEvent);
        var module = Compile("""
            local values = {}
            local total = 0
            for index = 1, 10 do
                values[index] = index
                total = total + values[index]
            end
            return total
            """);

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(55));
        var eligibility = executor.GetFunctionEligibility(module, 0);

        Assert.False(eligibility.IsAutoEligible);
        Assert.Equal(LuaJitEligibilityReason.SlowPathDensityTooHigh, eligibility.Reason);
        Assert.Equal(0, compiler.CallCount);
        Assert.Equal(0, executor.Statistics.CompilationQueued);
        Assert.Equal(0, executor.GetFunctionProfile(module, 0).Samples);
        Assert.Equal(0, executor.Statistics.Tier2EligibilityEvaluated);
        Assert.Equal(0, executor.Statistics.Tier2CompilationQueued);
        Assert.Equal(1, executor.Statistics.EligibilityEvaluated);
        Assert.Equal(1, executor.Statistics.EligibilityRejected);
        var rejected = Assert.Single(
            events,
            static jitEvent => jitEvent.Kind == LuaJitEventKind.EligibilityRejected);
        Assert.Equal("JIT1103", rejected.DiagnosticCode);
        Assert.Equal(eligibility, rejected.Eligibility);
    }

    [Fact]
    public void HotArithmeticLoopHasDeterministicAutoEligibilityEvidence()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = int.MaxValue,
            BackedgeThreshold = 1,
            SynchronousCompilation = true,
            EnableTier2 = false,
        });
        var module = Compile(
            "local total = 0; for index = 1, 20 do total = total + index end; return total");

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(210));
        var eligibility = executor.GetFunctionEligibility(module, 0);

        Assert.True(eligibility.IsCompilable);
        Assert.True(eligibility.IsAutoEligible);
        Assert.Equal(LuaJitEligibilityReason.Eligible, eligibility.Reason);
        Assert.Equal(LuaJitBreakEvenClass.WithinCurrentInvocation, eligibility.BreakEvenClass);
        Assert.Equal(1, eligibility.BackedgeCount);
        Assert.Equal(1, executor.Statistics.EligibilityAccepted);
        Assert.Equal(1, executor.Statistics.CompilationCompleted);
    }

    [Fact]
    public void PreferJitOverridesTheBenefitFilterButNotVerification()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            EnableTier2 = false,
        });
        var module = Compile("return 7");

        var eligibility = executor.GetFunctionEligibility(module, 0);
        Assert.False(eligibility.IsAutoEligible);
        Assert.True(eligibility.IsCompilable);
        Assert.Equal(LuaJitEligibilityReason.NoRepeatedWork, eligibility.Reason);

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(7));
        Assert.Equal(LuaJitFunctionState.Ready, executor.GetFunctionState(module, 0));
        Assert.Equal(1, executor.Statistics.CompilationCompleted);
    }

    [Fact]
    public void ImportedProfileCannotBypassTheAutoBenefitFilter()
    {
        var module = Compile("return 9");
        byte[] profile;
        using (var training = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            EnableTier2 = true,
        }))
        {
            AssertValues(ExecuteFresh(training, module), LuaValue.FromInteger(9));
            profile = training.ExportProfile(module);
        }

        var compiler = new CountingCompiler(ReflectionEmitLuaTier1Compiler.Instance);
        using var warmed = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.Auto,
                FunctionEntryThreshold = 1_000,
                BackedgeThreshold = int.MaxValue,
                SynchronousCompilation = true,
                EnableTier2 = true,
            },
            compiler: compiler);
        Assert.True(warmed.ImportProfile(module, profile).Succeeded);

        AssertValues(ExecuteFresh(warmed, module), LuaValue.FromInteger(9));

        Assert.Equal(0, compiler.CallCount);
        Assert.Equal(0, warmed.Statistics.CompilationQueued);
        Assert.Equal(LuaJitEligibilityReason.NoRepeatedWork,
            warmed.GetFunctionEligibility(module, 0).Reason);
    }

    [Fact]
    public void RequireJitReturnsAStableDiagnosticForAnUncompilableFunction()
    {
        var source = new System.Text.StringBuilder("local value = 0;");
        for (var index = 0; index < 2_000; index++)
        {
            source.Append("value = value + 1;");
        }

        source.Append("return value");
        var module = Compile(source.ToString());
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.RequireJit,
            EnableTier2 = false,
        });

        var failure = Assert.Throws<LuaJitException>(() => ExecuteFresh(executor, module));

        Assert.Equal("JIT1006", failure.DiagnosticCode);
        var eligibility = executor.GetFunctionEligibility(module, 0);
        Assert.False(eligibility.IsCompilable);
        Assert.Equal(LuaJitEligibilityReason.EstimatedCodeSizeTooLarge, eligibility.Reason);
        Assert.Equal(0, executor.Statistics.CompilationQueued);
    }

    [Fact]
    public void BackedgeCounterCanTriggerTier1WithinASingleInvocation()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = int.MaxValue,
            BackedgeThreshold = 1,
            SynchronousCompilation = true,
        });
        var module = Compile("local total = 0; for i = 1, 4 do total = total + i end; return total");

        var result = ExecuteFresh(executor, module);

        AssertValues(result, LuaValue.FromInteger(10));
        Assert.True(executor.Statistics.Backedges >= 1);
        Assert.True(executor.Statistics.CompiledInvocations >= 1);
        Assert.Equal(LuaJitFunctionState.Ready, executor.GetFunctionState(module, 0));
    }

    [Fact]
    public async Task ConcurrentFirstUseHasOnlyOneCompilation()
    {
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var compiler = new BlockingCompiler(
            ReflectionEmitLuaTier1Compiler.Instance,
            started,
            release);
        using var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.PreferJit,
                SynchronousCompilation = true,
            },
            compiler: compiler);
        var module = Compile("return 42");
        var tasks = new List<Task<LuaExecutionResult>>
        {
            Task.Run(() => ExecuteFresh(executor, module)),
        };
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        tasks.AddRange(Enumerable.Range(0, 7)
            .Select(_ => Task.Run(() => ExecuteFresh(executor, module))));

        release.Set();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, result => AssertValues(result, LuaValue.FromInteger(42)));
        Assert.Equal(1, compiler.CallCount);
        Assert.Equal(1, executor.Statistics.CompilationCompleted);
    }

    [Fact]
    public async Task BoundedCompileQueueRejectsExcessWorkWithoutBlockingExecution()
    {
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var compiler = new BlockingCompiler(
            ReflectionEmitLuaTier1Compiler.Instance,
            started,
            release);
        using var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.PreferJit,
                CompilationQueueCapacity = 1,
                MaximumConcurrentCompilations = 1,
            },
            compiler: compiler);
        var first = Compile("return 1");
        var second = Compile("return 2");
        var third = Compile("return 3");

        AssertValues(ExecuteFresh(executor, first), LuaValue.FromInteger(1));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        AssertValues(ExecuteFresh(executor, second), LuaValue.FromInteger(2));
        AssertValues(ExecuteFresh(executor, third), LuaValue.FromInteger(3));

        Assert.True(executor.Statistics.QueueRejected >= 1);
        Assert.Equal(LuaJitFunctionState.Cold, executor.GetFunctionState(third, 0));
        release.Set();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await executor.WaitForIdleAsync(timeout.Token);
        Assert.Equal(LuaJitFunctionState.Ready, executor.GetFunctionState(first, 0));
        Assert.Equal(LuaJitFunctionState.Ready, executor.GetFunctionState(second, 0));
    }

    [Fact]
    public void DynamicCodeDisabledFallsBackWithoutCallingReflectionEmit()
    {
        var compiler = new CountingCompiler(ReflectionEmitLuaTier1Compiler.Instance);
        var tier2Compiler = new CountingTier2Compiler(
            ProfileGuidedLuaTier2Compiler.Instance);
        var capabilities = new TestDynamicCodeCapabilities(false, false);
        var options = LuaJitExecutorOptions.Default with
        {
            FunctionEntryThreshold = 1,
            SynchronousCompilation = true,
        };
        using var executor = CreateExecutor(
            options,
            capabilities,
            compiler,
            tier2Compiler);
        var module = Compile("return 7");
        var state = new LuaState();

        var result = executor.Execute(state, state.CreateMainClosure(module));

        AssertValues(result, LuaValue.FromInteger(7));
        Assert.Equal(0, compiler.CallCount);
        Assert.Equal(0, tier2Compiler.CallCount);
        Assert.False(executor.IsDynamicCodeAvailable);
        Assert.Equal(LuaJitFunctionState.Failed, executor.GetFunctionState(module, 0));
        Assert.Equal(LuaJitTier2State.Disabled, executor.GetTier2State(module, 0));
        Assert.Equal(0, executor.GetFunctionProfile(module, 0).Samples);
        Assert.Equal(
            LuaJitProfileImportStatus.Disabled,
            executor.ImportProfile(module, executor.ExportProfile(module)).Status);
    }

    [Fact]
    public void RequireJitReportsUnavailableOrFailedCompilation()
    {
        var module = Compile("return 7");
        using var unavailable = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.RequireJit,
                SynchronousCompilation = true,
            },
            new TestDynamicCodeCapabilities(false, false),
            new FailingCompiler());
        var unavailableState = new LuaState();

        var unsupported = Assert.Throws<LuaJitException>(() => unavailable.Execute(
            unavailableState,
            unavailableState.CreateMainClosure(module)));

        Assert.Equal("JIT1001", unsupported.DiagnosticCode);

        using var failed = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.RequireJit,
                SynchronousCompilation = true,
            },
            compiler: new FailingCompiler());
        var failedState = new LuaState();
        var failure = Assert.Throws<LuaJitException>(() => failed.Execute(
            failedState,
            failedState.CreateMainClosure(module)));
        Assert.Equal("JIT1003", failure.DiagnosticCode);
    }

    [Fact]
    public void RequireJitCompilesBeforeExecutingWhenDynamicCodeIsAvailable()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.RequireJit,
        });
        var module = Compile("return 13");

        var result = ExecuteFresh(executor, module);

        AssertValues(result, LuaValue.FromInteger(13));
        Assert.Equal(0, executor.Statistics.InterpreterFallbacks);
        Assert.True(executor.Statistics.CompiledInvocations >= 1);
        Assert.Equal(LuaJitFunctionState.Ready, executor.GetFunctionState(module, 0));
    }

    [Fact]
    public void RetryPolicyCanRecoverFromATransientCompilationFailure()
    {
        var compiler = new FailOnceCompiler(ReflectionEmitLuaTier1Compiler.Instance);
        using var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.PreferJit,
                SynchronousCompilation = true,
                MaximumCompilationAttempts = 2,
                CompilationRetryBackoff = TimeSpan.Zero,
            },
            compiler: compiler);
        var module = Compile("local value = 7; return value");
        var state = new LuaState();

        var result = executor.Execute(state, state.CreateMainClosure(module));

        AssertValues(result, LuaValue.FromInteger(7));
        Assert.Equal(2, compiler.CallCount);
        Assert.Equal(LuaJitFunctionState.Ready, executor.GetFunctionState(module, 0));
        Assert.Equal(1, executor.Statistics.CompilationFailed);
        Assert.Equal(1, executor.Statistics.CompilationCompleted);
    }

    [Fact]
    public void CacheBudgetEvictsLeastRecentlyUsedCompiledFunctions()
    {
        var compiler = new FixedSizeCompiler(
            ReflectionEmitLuaTier1Compiler.Instance,
            estimatedCodeBytes: 128);
        using var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.PreferJit,
                SynchronousCompilation = true,
                MaximumCodeCacheBytes = 128,
            },
            compiler: compiler);
        var first = Compile("return 1");
        var second = Compile("return 2");

        _ = ExecuteFresh(executor, first);
        _ = ExecuteFresh(executor, second);

        Assert.Equal(LuaJitFunctionState.Invalidated, executor.GetFunctionState(first, 0));
        Assert.Equal(LuaJitFunctionState.Ready, executor.GetFunctionState(second, 0));
        Assert.Equal(1, executor.Statistics.CacheEvictions);
        Assert.Equal(128, executor.Statistics.EstimatedCodeBytes);

        executor.Invalidate(second);
        Assert.Equal(LuaJitFunctionState.Invalidated, executor.GetFunctionState(second, 0));
        Assert.Equal(0, executor.Statistics.EstimatedCodeBytes);
    }

    [Fact]
    public void FunctionRoutesHonorExplicitInvalidationAndClearCacheRecompilation()
    {
        var compiler = new CountingCompiler(ReflectionEmitLuaTier1Compiler.Instance);
        using var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.PreferJit,
                SynchronousCompilation = true,
                EnableTier2 = false,
                EnableLoopOsr = false,
            },
            compiler: compiler);
        var module = Compile("local value = 20; return value + 1");

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(21));
        Assert.Equal(1, compiler.CallCount);
        executor.Invalidate(module);
        Assert.Equal(LuaJitFunctionState.Invalidated, executor.GetFunctionState(module, 0));

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(21));
        Assert.Equal(2, compiler.CallCount);
        executor.ClearCache();
        Assert.Equal(LuaJitFunctionState.Invalidated, executor.GetFunctionState(module, 0));

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(21));
        Assert.Equal(3, compiler.CallCount);
        Assert.Equal(LuaJitFunctionState.Ready, executor.GetFunctionState(module, 0));
    }

    [Fact]
    public void EventSubscriberFailuresCannotBreakExecutionAndDisposeIsFinal()
    {
        var module = Compile("return 9");
        var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
        });
        executor.EventOccurred += static (_, _) => throw new InvalidOperationException("observer");

        var result = ExecuteFresh(executor, module);

        AssertValues(result, LuaValue.FromInteger(9));
        executor.Dispose();
        var state = new LuaState();
        Assert.Throws<ObjectDisposedException>(() => executor.Execute(
            state,
            state.CreateMainClosure(module)));
    }

    [Fact]
    public void ReadyCodeCacheDoesNotRetainModuleStateOrClosureOwners()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
        });

        var references = CompileAndReleaseOwners(executor);
        for (var attempt = 0; attempt < 10 && references.Any(static item => item.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.All(references, reference => Assert.False(reference.IsAlive));
        Assert.True(executor.Statistics.EstimatedCodeBytes > 0);
    }

    [Fact]
    public void TerminalFunctionRouteDoesNotRetainModuleStateOrClosureOwners()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = 1,
            BackedgeThreshold = 1,
            SynchronousCompilation = true,
            EnableTier2 = false,
            EnableLoopOsr = false,
        });

        var references = RouteRejectedFunctionAndReleaseOwners(executor);
        for (var attempt = 0; attempt < 10 && references.Any(static item => item.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.All(references, reference => Assert.False(reference.IsAlive));
        Assert.Equal(1, executor.Statistics.InterpreterFallbacks);
    }

    [Fact]
    public void DisposeCancelsAnActiveAsynchronousCompilation()
    {
        using var started = new ManualResetEventSlim();
        using var canceled = new ManualResetEventSlim();
        var compiler = new CancellableCompiler(started, canceled);
        var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.PreferJit,
            },
            compiler: compiler);
        var module = Compile("return 5");

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(5));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        executor.Dispose();

        Assert.True(canceled.IsSet);
        Assert.Equal(0, executor.Statistics.EstimatedCodeBytes);
    }

    [Fact]
    public void DisposeDoesNotPublishACompilerResultThatIgnoredCancellation()
    {
        using var started = new ManualResetEventSlim();
        using var returned = new ManualResetEventSlim();
        var compiler = new CancellationIgnoringCompiler(started, returned);
        var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.PreferJit,
            },
            compiler: compiler);
        var completedEvents = 0;
        executor.EventOccurred += (_, jitEvent) =>
        {
            if (jitEvent.Kind == LuaJitEventKind.CompilationCompleted)
            {
                Interlocked.Increment(ref completedEvents);
            }
        };
        var module = Compile("return 5");

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(5));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        executor.Dispose();

        Assert.True(returned.IsSet);
        Assert.Equal(0, Volatile.Read(ref completedEvents));
        Assert.Equal(0, executor.Statistics.EstimatedCodeBytes);
    }

    [Fact]
    public void EventSubscriberCanDisposeExecutorFromCompilationWorker()
    {
        using var disposed = new ManualResetEventSlim();
        var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
        });
        executor.EventOccurred += (_, jitEvent) =>
        {
            if (jitEvent.Kind == LuaJitEventKind.CompilationStarted)
            {
                executor.Dispose();
                disposed.Set();
            }
        };
        var module = Compile("return 17");

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(17));

        Assert.True(disposed.Wait(TimeSpan.FromSeconds(10)));
        var state = new LuaState();
        Assert.Throws<ObjectDisposedException>(() => executor.Execute(
            state,
            state.CreateMainClosure(module)));
    }

    [Fact]
    public void DefaultAutoTier2PromotesExactNumericHotspots()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = 1,
            BackedgeThreshold = 1,
            SynchronousCompilation = true,
            Tier2InvocationThreshold = 1,
            Tier2BackedgeThreshold = int.MaxValue,
        });
        var events = new ConcurrentQueue<LuaJitEvent>();
        executor.EventOccurred += (_, jitEvent) => events.Enqueue(jitEvent);
        var module = Compile(
            "local total = 0; for i = 1, 5 do total = total + i end; return total");

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(15));
        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(15));

        Assert.Equal(LuaJitCompilationTier.Tier2, executor.GetFunctionTier(module, 0));
        Assert.Equal(LuaJitTier2State.Ready, executor.GetTier2State(module, 0));
        var tier2Plan = Assert.IsType<LuaJitTier2Plan>(executor.GetTier2Plan(module, 0));
        Assert.Equal(LuaJitTier2CodeKind.ExactNumericSpecializedCil, tier2Plan.CodeKind);
        Assert.True(
            tier2Plan.Optimizations.Any(
                optimization => optimization.Kind == LuaJitOptimizationKind.NumericBinary),
            $"Profile: {FormatProfile(executor.GetFunctionProfile(module, 0))}");
        Assert.Contains(
            tier2Plan.DeoptMap,
            entry => tier2Plan.Optimizations.Any(optimization =>
                optimization.ProgramCounter == entry.ProgramCounter &&
                optimization.Kind == LuaJitOptimizationKind.NumericBinary));
        Assert.All(
            tier2Plan.DeoptMap,
            entry => Assert.True(
                entry.FrameTopMaterialized && entry.PendingTransformMaterialized));
        Assert.Equal(1, executor.Statistics.Tier2CompilationCompleted);
        Assert.True(executor.Statistics.Tier2Invocations >= 1);
        Assert.Equal(1, executor.Statistics.Tier2EligibilityEvaluated);
        Assert.Equal(1, executor.Statistics.Tier2EligibilityAccepted);
        Assert.Equal(0, executor.Statistics.Tier2EligibilityRejected);
        var accepted = Assert.Single(events, static jitEvent =>
            jitEvent.Kind == LuaJitEventKind.Tier2EligibilityAccepted);
        var eligibility = Assert.IsType<LuaJitTier2Eligibility>(
            accepted.Tier2Eligibility);
        Assert.True(eligibility.IsAutoEligible);
        Assert.Equal(LuaJitTier2EligibilityReason.Eligible, eligibility.Reason);
        Assert.Null(eligibility.DiagnosticCode);
        Assert.Equal(
            LuaJitTier2CodeKind.ExactNumericSpecializedCil,
            eligibility.ExpectedCodeKind);
    }

    [Fact]
    public void DefaultAutoTier2RejectsManagedTableProfileWithoutQueueingCompilation()
    {
        var compiler = new CountingTier2Compiler(ProfileGuidedLuaTier2Compiler.Instance);
        using var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.PreferJit,
                SynchronousCompilation = true,
                Tier2InvocationThreshold = 1,
                Tier2BackedgeThreshold = int.MaxValue,
            },
            tier2Compiler: compiler);
        var events = new ConcurrentQueue<LuaJitEvent>();
        executor.EventOccurred += (_, jitEvent) => events.Enqueue(jitEvent);
        var module = Compile("local target, key = ...; return target[key]");

        ExecuteTableLookup(executor, module, extraKeys: 0, expected: LuaValue.Nil);
        ExecuteTableLookup(executor, module, extraKeys: 0, expected: LuaValue.Nil);

        Assert.Equal(0, compiler.CallCount);
        Assert.Equal(0, executor.Statistics.Tier2CompilationQueued);
        Assert.Equal(1, executor.Statistics.Tier2EligibilityRejected);
        Assert.Equal(LuaJitCompilationTier.Tier1, executor.GetFunctionTier(module, 0));
        Assert.Equal(LuaJitTier2State.Profiling, executor.GetTier2State(module, 0));
        var rejected = Assert.Single(events, static jitEvent =>
            jitEvent.Kind == LuaJitEventKind.Tier2EligibilityRejected);
        var eligibility = Assert.IsType<LuaJitTier2Eligibility>(
            rejected.Tier2Eligibility);
        Assert.False(eligibility.IsAutoEligible);
        Assert.Equal(
            LuaJitTier2EligibilityReason.ManagedSemanticBoundary,
            eligibility.Reason);
        Assert.Equal(
            LuaJitTier2DiagnosticCodes.ManagedSemanticBoundary,
            rejected.DiagnosticCode);
    }

    [Fact]
    public void DefaultAutoTier2RejectsPolymorphicNumericProfiles()
    {
        var compiler = new CountingTier2Compiler(ProfileGuidedLuaTier2Compiler.Instance);
        using var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.PreferJit,
                SynchronousCompilation = true,
                Tier2InvocationThreshold = 2,
                Tier2BackedgeThreshold = int.MaxValue,
            },
            tier2Compiler: compiler);
        var module = Compile("local value = ...; return value + 1");

        AssertValues(
            ExecuteFresh(executor, module, LuaValue.FromInteger(2)),
            LuaValue.FromInteger(3));
        AssertValues(
            ExecuteFresh(executor, module, LuaValue.FromFloat(2.5)),
            LuaValue.FromFloat(3.5));
        AssertValues(
            ExecuteFresh(executor, module, LuaValue.FromInteger(4)),
            LuaValue.FromInteger(5));

        Assert.Equal(0, compiler.CallCount);
        var eligibility = executor.GetTier2PromotionEligibility(module, 0);
        Assert.False(eligibility.IsAutoEligible);
        Assert.Equal(
            LuaJitTier2EligibilityReason.PolymorphicNumericProfile,
            eligibility.Reason);
        Assert.Equal(
            LuaJitTier2DiagnosticCodes.PolymorphicNumericProfile,
            eligibility.DiagnosticCode);
        Assert.Equal(0, executor.Statistics.Tier2CompilationQueued);
    }

    [Fact]
    public void DefaultAutoTier2RefusesUnexpectedManagedCompilerOutput()
    {
        using var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.PreferJit,
                SynchronousCompilation = true,
                Tier2InvocationThreshold = 1,
                Tier2BackedgeThreshold = int.MaxValue,
            },
            tier2Compiler: new ManagedCodeKindTier2Compiler(
                ProfileGuidedLuaTier2Compiler.Instance));
        var events = new ConcurrentQueue<LuaJitEvent>();
        executor.EventOccurred += (_, jitEvent) => events.Enqueue(jitEvent);
        var module = Compile("local value = ...; return value + 1");

        AssertValues(
            ExecuteFresh(executor, module, LuaValue.FromInteger(2)),
            LuaValue.FromInteger(3));
        AssertValues(
            ExecuteFresh(executor, module, LuaValue.FromInteger(4)),
            LuaValue.FromInteger(5));

        Assert.Equal(LuaJitCompilationTier.Tier1, executor.GetFunctionTier(module, 0));
        Assert.Equal(LuaJitTier2State.Failed, executor.GetTier2State(module, 0));
        Assert.Equal(0, executor.Statistics.Tier2CompilationCompleted);
        Assert.Equal(1, executor.Statistics.Tier2CompilationFailed);
        Assert.Contains(events, static jitEvent =>
            jitEvent.Kind == LuaJitEventKind.Tier2CompilationFailed &&
            jitEvent.DiagnosticCode == LuaJitTier2DiagnosticCodes.UnexpectedCodeKind);
    }

    [Fact]
    public void StableIntegerHotspotsUseProfileSpecializedTier2Cil()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            EnableTier2 = true,
            Tier2InvocationThreshold = 1,
            Tier2BackedgeThreshold = int.MaxValue,
        });
        var events = new ConcurrentQueue<LuaJitEvent>();
        executor.EventOccurred += (_, jitEvent) => events.Enqueue(jitEvent);
        var module = Compile("""
            local total = 0
            local first = 0
            local second = 1
            local index = 0
            while index < 128 do
                local next = first + second
                first = second
                second = next
                total = total + (next & 1023)
                index = index + 1
            end
            return total
            """);

        var expected = ExecuteFresh(executor, module);
        AssertValues(ExecuteFresh(executor, module), expected.Values.ToArray());

        var plan = Assert.IsType<LuaJitTier2Plan>(executor.GetTier2Plan(module, 0));
        Assert.Equal(LuaJitTier2CodeKind.ExactNumericSpecializedCil, plan.CodeKind);
        Assert.Contains(
            plan.Optimizations,
            static optimization => optimization.Kind == LuaJitOptimizationKind.NumericBinary);
        Assert.Equal(LuaJitCompilationTier.Tier2, executor.GetFunctionTier(module, 0));
        var completed = Assert.Single(
            events,
            static jitEvent => jitEvent.Kind == LuaJitEventKind.Tier2CompilationCompleted);
        var metrics = Assert.IsType<LuaJitTier2CompilationMetrics>(
            completed.Tier2CompilationMetrics);
        Assert.True(metrics.LivenessCacheHit);
        Assert.Equal(LuaJitTier2CodeKind.ExactNumericSpecializedCil, metrics.CodeKind);
        Assert.True(metrics.OptimizationCount > 0);
        Assert.True(metrics.SpecializedOptimizationCount > 0);
        Assert.True(metrics.DeoptSiteCount > 0);
        Assert.True(metrics.AllocatedBytes > 0);
        Assert.True(metrics.CilEmissionDuration > TimeSpan.Zero);
        Assert.True(metrics.DelegateCreationDuration > TimeSpan.Zero);
    }

    [Fact]
    public void IntegerSpecializedTier2PreservesLuaNumericEdgeSemantics()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            EnableTier2 = true,
            Tier2InvocationThreshold = 1,
            Tier2BackedgeThreshold = int.MaxValue,
        });
        var module = Compile("""
            local left, right = ...
            local quotient = left // right
            local remainder = left % right
            local bits = (left & 255) ~ (right | 3)
            local shifted = (left << -1) + (right >> 65)
            return quotient, remainder, bits, shifted,
                left + right, left - right, left * right,
                left < right, left <= right, left == right
            """);
        var warmArguments = new[]
        {
            LuaValue.FromInteger(-17),
            LuaValue.FromInteger(5),
        };

        _ = ExecuteFresh(executor, module, warmArguments);
        _ = ExecuteFresh(executor, module, warmArguments);
        Assert.Equal(
            LuaJitTier2CodeKind.ExactNumericSpecializedCil,
            Assert.IsType<LuaJitTier2Plan>(executor.GetTier2Plan(module, 0)).CodeKind);

        AssertValues(
            ExecuteFresh(
                executor,
                module,
                LuaValue.FromInteger(long.MinValue),
                LuaValue.FromInteger(-1)),
            LuaValue.FromInteger(long.MinValue),
            LuaValue.FromInteger(0),
            LuaValue.FromInteger(-1),
            LuaValue.FromInteger(4_611_686_018_427_387_904),
            LuaValue.FromInteger(long.MaxValue),
            LuaValue.FromInteger(long.MinValue + 1),
            LuaValue.FromInteger(long.MinValue),
            LuaValue.FromBoolean(true),
            LuaValue.FromBoolean(true),
            LuaValue.FromBoolean(false));
    }

    [Fact]
    public void ExactFloatAndMixedNumericProfilesUseSpecializedTier2Cil()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            EnableTier2 = true,
            Tier2InvocationThreshold = 1,
            Tier2BackedgeThreshold = int.MaxValue,
        });
        var floats = Compile("""
            local left, right = ...
            return left // right, left % right,
                left < right, left <= right, left == right
            """);
        var floatArguments = new[]
        {
            LuaValue.FromFloat(5.5),
            LuaValue.FromFloat(2.0),
        };

        _ = ExecuteFresh(executor, floats, floatArguments);
        AssertValues(
            ExecuteFresh(executor, floats, floatArguments),
            LuaValue.FromFloat(2.0),
            LuaValue.FromFloat(1.5),
            LuaValue.FromBoolean(false),
            LuaValue.FromBoolean(false),
            LuaValue.FromBoolean(false));
        Assert.Equal(
            LuaJitTier2CodeKind.ExactNumericSpecializedCil,
            Assert.IsType<LuaJitTier2Plan>(executor.GetTier2Plan(floats, 0)).CodeKind);

        var mixed = Compile("""
            local floating, integer = ...
            return floating < integer, floating <= integer,
                floating == integer, floating > integer
            """);
        var mixedArguments = new[]
        {
            LuaValue.FromFloat(1.5),
            LuaValue.FromInteger(2),
        };
        _ = ExecuteFresh(executor, mixed, mixedArguments);
        _ = ExecuteFresh(executor, mixed, mixedArguments);
        Assert.Equal(
            LuaJitTier2CodeKind.ExactNumericSpecializedCil,
            Assert.IsType<LuaJitTier2Plan>(executor.GetTier2Plan(mixed, 0)).CodeKind);

        AssertValues(
            ExecuteFresh(
                executor,
                mixed,
                LuaValue.FromFloat(9_223_372_036_854_775_808d),
                LuaValue.FromInteger(long.MaxValue)),
            LuaValue.FromBoolean(false),
            LuaValue.FromBoolean(false),
            LuaValue.FromBoolean(false),
            LuaValue.FromBoolean(true));
    }

    [Fact]
    public void Tier2GuardFailureRestoresCanonicalExecutionAndReprofiles()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            EnableTier2 = true,
            Tier2InvocationThreshold = 2,
            Tier2BackedgeThreshold = int.MaxValue,
            MaximumTier2GuardFailures = 1,
        });
        var module = Compile("local value = ...; return value + 1");

        AssertValues(
            ExecuteFresh(executor, module, LuaValue.FromInteger(2)),
            LuaValue.FromInteger(3));
        AssertValues(
            ExecuteFresh(executor, module, LuaValue.FromInteger(4)),
            LuaValue.FromInteger(5));
        AssertValues(
            ExecuteFresh(executor, module, LuaValue.FromInteger(6)),
            LuaValue.FromInteger(7));
        Assert.Equal(LuaJitCompilationTier.Tier2, executor.GetFunctionTier(module, 0));

        var stringState = new LuaState();
        var result = executor.Execute(
            stringState,
            stringState.CreateMainClosure(module),
            [LuaValue.FromString(stringState.Strings.GetOrCreate("6"u8))]);

        AssertValues(result, LuaValue.FromInteger(7));
        Assert.Equal(1, executor.Statistics.Tier2GuardFailures);
        Assert.Equal(1, executor.Statistics.Tier2Invalidations);
        Assert.Equal(LuaJitCompilationTier.Tier1, executor.GetFunctionTier(module, 0));
        Assert.Equal(LuaJitTier2State.Profiling, executor.GetTier2State(module, 0));

        var reprofilingState = new LuaState();
        AssertValues(
            executor.Execute(
                reprofilingState,
                reprofilingState.CreateMainClosure(module),
                [LuaValue.FromString(reprofilingState.Strings.GetOrCreate("8"u8))]),
            LuaValue.FromInteger(9));
        AssertValues(
            ExecuteFresh(executor, module, LuaValue.FromInteger(10)),
            LuaValue.FromInteger(11));
        var eligibility = executor.GetTier2PromotionEligibility(module, 0);
        Assert.False(eligibility.IsAutoEligible);
        Assert.Equal(
            LuaJitTier2EligibilityReason.NoNumericHotspot,
            eligibility.Reason);
        Assert.Equal(1, executor.Statistics.Tier2CompilationCompleted);
        Assert.True(executor.Statistics.Tier2EligibilityRejected >= 1);
    }

    [Fact]
    public void ProfileUsesBoundedOwnerFreeTableShapeSignatures()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            EnableTier2 = true,
            Tier2InvocationThreshold = int.MaxValue,
            Tier2BackedgeThreshold = int.MaxValue,
            MaximumPolymorphicShapes = 2,
        });
        var module = Compile("local target, key = ...; return target[key]");
        for (var shape = 0; shape < 3; shape++)
        {
            var state = new LuaState();
            var table = state.CreateTable();
            for (var key = 1; key <= shape; key++)
            {
                table.Set(LuaValue.FromInteger(key), LuaValue.FromInteger(key));
            }

            var result = executor.Execute(
                state,
                state.CreateMainClosure(module),
                [LuaValue.FromTable(table), LuaValue.FromInteger(1)]);
            Assert.Single(result.Values);
        }

        var profile = executor.GetFunctionProfile(module, 0);
        var tableSite = Assert.Single(profile.Sites.Where(
            site => site.Opcode == LuaIrOpcode.GetTable));
        Assert.True(tableSite.IsMegamorphic);
        Assert.Equal(2, tableSite.TableShapes.Length);
        Assert.All(tableSite.TableShapes, shape => Assert.False(shape.HasMetatable));
    }

    [Fact]
    public void ProfileExportIsDeterministicAndChecksumProtected()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            EnableTier2 = true,
            Tier2InvocationThreshold = int.MaxValue,
            Tier2BackedgeThreshold = int.MaxValue,
        });
        var module = Compile("local value = ...; return value + 1");
        AssertValues(
            ExecuteFresh(executor, module, LuaValue.FromInteger(1)),
            LuaValue.FromInteger(2));
        AssertValues(
            ExecuteFresh(executor, module, LuaValue.FromInteger(2)),
            LuaValue.FromInteger(3));

        var first = executor.ExportProfile(module);
        var second = executor.ExportProfile(module);
        var corrupted = first.ToArray();
        corrupted[^1] ^= 0xff;
        using var imported = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            EnableTier2 = true,
        });

        var result = imported.ImportProfile(module, first);
        var rejected = imported.ImportProfile(module, corrupted);

        Assert.Equal(first, second);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(
            executor.GetFunctionProfile(module, 0).Samples,
            imported.GetFunctionProfile(module, 0).Samples);
        Assert.Equal(LuaJitProfileImportStatus.Rejected, rejected.Status);
        Assert.Equal(LuaJitProfileDiagnosticCodes.Malformed, rejected.DiagnosticCode);
    }

    [Fact]
    public void ProfileImportRejectsDifferentCanonicalModule()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            EnableTier2 = true,
        });
        var source = Compile("return 1");
        var other = Compile("return 2");
        AssertValues(ExecuteFresh(executor, source), LuaValue.FromInteger(1));
        var payload = executor.ExportProfile(source);

        var result = executor.ImportProfile(other, payload);

        Assert.Equal(LuaJitProfileImportStatus.Incompatible, result.Status);
        Assert.Equal(LuaJitProfileDiagnosticCodes.Incompatible, result.DiagnosticCode);
    }

    [Fact]
    public void ProfileImportRejectsVersionMismatchEvenWithValidChecksum()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            EnableTier2 = true,
        });
        var module = Compile("return 1");
        var payload = executor.ExportProfile(module);
        var magicLength = BinaryPrimitives.ReadInt32LittleEndian(payload);
        var schemaOffset = sizeof(int) + magicLength;
        BinaryPrimitives.WriteInt32LittleEndian(
            payload.AsSpan(schemaOffset, sizeof(int)),
            LuaJitProfileCodec.CurrentSchemaVersion + 1);
        const int footerLength = 8 + 32;
        SHA256.HashData(payload.AsSpan(0, payload.Length - footerLength)).CopyTo(
            payload.AsSpan(payload.Length - 32));

        var result = executor.ImportProfile(module, payload);

        Assert.Equal(LuaJitProfileImportStatus.Incompatible, result.Status);
        Assert.Equal(LuaJitProfileDiagnosticCodes.Incompatible, result.DiagnosticCode);
    }

    [Fact]
    public void ProfileFaultMatrixRejectsTruncationChecksumAndAbiMismatch()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            EnableTier2 = true,
        });
        var module = Compile("local value = ...; return value + 1");
        var payload = executor.ExportProfile(module);
        var truncationLengths = new[]
        {
            0,
            1,
            payload.Length / 2,
            payload.Length - 1,
        };

        foreach (var length in truncationLengths)
        {
            var result = executor.ImportProfile(module, payload.AsSpan(0, length));
            Assert.Equal(LuaJitProfileImportStatus.Rejected, result.Status);
            Assert.Equal(LuaJitProfileDiagnosticCodes.Malformed, result.DiagnosticCode);
        }

        var checksumMismatch = payload.ToArray();
        checksumMismatch[^1] ^= 0x80;
        var checksumResult = executor.ImportProfile(module, checksumMismatch);
        Assert.Equal(LuaJitProfileImportStatus.Rejected, checksumResult.Status);
        Assert.Equal(LuaJitProfileDiagnosticCodes.Malformed, checksumResult.DiagnosticCode);

        var abiMismatch = payload.ToArray();
        var magicLength = BinaryPrimitives.ReadInt32LittleEndian(abiMismatch);
        var abiOffset = sizeof(int) + magicLength + (2 * sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(
            abiMismatch.AsSpan(abiOffset, sizeof(int)),
            int.MaxValue);
        const int footerLength = 8 + 32;
        SHA256.HashData(abiMismatch.AsSpan(0, abiMismatch.Length - footerLength)).CopyTo(
            abiMismatch.AsSpan(abiMismatch.Length - 32));

        var abiResult = executor.ImportProfile(module, abiMismatch);
        Assert.Equal(LuaJitProfileImportStatus.Incompatible, abiResult.Status);
        Assert.Equal(LuaJitProfileDiagnosticCodes.Incompatible, abiResult.DiagnosticCode);
    }

    [Fact]
    public void ImportedProfilePrewarmsTier1AndTier2WithoutPersistingCode()
    {
        var module = Compile(
            "local total = 0; for i = 1, 5 do total = total + i end; return total");
        byte[] payload;
        using (var training = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            Tier2InvocationThreshold = int.MaxValue,
            Tier2BackedgeThreshold = int.MaxValue,
        }))
        {
            for (var iteration = 0; iteration < 3; iteration++)
            {
                AssertValues(ExecuteFresh(training, module), LuaValue.FromInteger(15));
            }

            payload = training.ExportProfile(module);
        }

        using var warmed = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = 1000,
            BackedgeThreshold = int.MaxValue,
            SynchronousCompilation = true,
            Tier2InvocationThreshold = 2,
            Tier2BackedgeThreshold = int.MaxValue,
        });
        Assert.True(warmed.ImportProfile(module, payload).Succeeded);

        AssertValues(ExecuteFresh(warmed, module), LuaValue.FromInteger(15));
        AssertValues(ExecuteFresh(warmed, module), LuaValue.FromInteger(15));

        Assert.Equal(LuaJitCompilationTier.Tier2, warmed.GetFunctionTier(module, 0));
        Assert.Equal(
            LuaJitTier2CodeKind.ExactNumericSpecializedCil,
            Assert.IsType<LuaJitTier2Plan>(warmed.GetTier2Plan(module, 0)).CodeKind);
        Assert.Equal(1, warmed.Statistics.CompilationCompleted);
        Assert.Equal(1, warmed.Statistics.Tier2CompilationCompleted);
    }

    [Fact]
    public void ImportedManagedProfileCannotBypassAutomaticTier2Eligibility()
    {
        var module = Compile("local target, key = ...; return target[key]");
        byte[] payload;
        using (var training = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            EnableTier2ManagedFallback = true,
            Tier2InvocationThreshold = int.MaxValue,
            Tier2BackedgeThreshold = int.MaxValue,
        }))
        {
            ExecuteTableLookup(training, module, extraKeys: 0, expected: LuaValue.Nil);
            payload = training.ExportProfile(module);
        }

        var compiler = new CountingTier2Compiler(ProfileGuidedLuaTier2Compiler.Instance);
        using var warmed = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.PreferJit,
                SynchronousCompilation = true,
                Tier2InvocationThreshold = 1,
                Tier2BackedgeThreshold = int.MaxValue,
            },
            tier2Compiler: compiler);
        Assert.True(warmed.ImportProfile(module, payload).Succeeded);

        ExecuteTableLookup(warmed, module, extraKeys: 0, expected: LuaValue.Nil);
        ExecuteTableLookup(warmed, module, extraKeys: 0, expected: LuaValue.Nil);

        Assert.Equal(0, compiler.CallCount);
        Assert.Equal(0, warmed.Statistics.Tier2CompilationQueued);
        Assert.True(warmed.Statistics.Tier2EligibilityRejected >= 1);
        Assert.Equal(
            LuaJitTier2EligibilityReason.ManagedSemanticBoundary,
            warmed.GetTier2PromotionEligibility(module, 0).Reason);
    }

    [Fact]
    public void ImportedProfileDoesNotRetainModuleOrLuaOwners()
    {
        using var warmed = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            EnableTier2 = true,
        });

        var references = ImportProfileAndReleaseOwners(warmed);
        for (var attempt = 0; attempt < 10 && references.Any(static item => item.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.All(references, static reference => Assert.False(reference.IsAlive));
    }

    [Fact]
    public void Tier2TablePicGuardsShapeAndPreservesLookupSemantics()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            EnableTier2 = true,
            EnableTier2ManagedFallback = true,
            Tier2InvocationThreshold = 1,
            Tier2BackedgeThreshold = int.MaxValue,
        });
        var module = Compile("local target, key = ...; return target[key]");

        ExecuteTableLookup(executor, module, extraKeys: 0, expected: LuaValue.Nil);
        ExecuteTableLookup(executor, module, extraKeys: 0, expected: LuaValue.Nil);

        var plan = Assert.IsType<LuaJitTier2Plan>(executor.GetTier2Plan(module, 0));
        Assert.Equal(LuaJitTier2CodeKind.ManagedProfileProgram, plan.CodeKind);
        Assert.Contains(
            plan.Optimizations,
            optimization => optimization.Kind == LuaJitOptimizationKind.TableGetPic);

        ExecuteTableLookup(
            executor,
            module,
            extraKeys: 1,
            expected: LuaValue.FromInteger(1));
        Assert.True(executor.Statistics.Tier2GuardFailures >= 1);
    }

    [Fact]
    public async Task ConcurrentTier2PromotionCompilesOnlyOnce()
    {
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var compiler = new BlockingTier2Compiler(
            ProfileGuidedLuaTier2Compiler.Instance,
            started,
            release);
        using var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.PreferJit,
                SynchronousCompilation = true,
                EnableTier2 = true,
                Tier2InvocationThreshold = 1,
                Tier2BackedgeThreshold = int.MaxValue,
            },
            tier2Compiler: compiler);
        var module = Compile("local value = ...; return value + 1");
        AssertValues(
            ExecuteFresh(executor, module, LuaValue.FromInteger(1)),
            LuaValue.FromInteger(2));

        var tasks = new List<Task<LuaExecutionResult>>
        {
            Task.Run(() => ExecuteFresh(executor, module, LuaValue.FromInteger(2))),
        };
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        tasks.AddRange(Enumerable.Range(0, 7).Select(index => Task.Run(() =>
            ExecuteFresh(executor, module, LuaValue.FromInteger(index + 3)))));
        release.Set();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(8, results.Length);
        Assert.Equal(1, compiler.CallCount);
        Assert.Equal(1, executor.Statistics.Tier2CompilationCompleted);
    }

    [Fact]
    public void Tier2FoldsPrimitiveConstantsAndDirectlyDispatchesKnownClosures()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            EnableTier2 = true,
            EnableTier2ManagedFallback = true,
            Tier2InvocationThreshold = 1,
            Tier2BackedgeThreshold = int.MaxValue,
        });
        var constants = Compile("return 2 + 3");
        AssertValues(ExecuteFresh(executor, constants), LuaValue.FromInteger(5));
        AssertValues(ExecuteFresh(executor, constants), LuaValue.FromInteger(5));
        Assert.Contains(
            Assert.IsType<LuaJitTier2Plan>(executor.GetTier2Plan(constants, 0)).Optimizations,
            optimization => optimization.Kind == LuaJitOptimizationKind.ConstantFold);

        var calls = Compile(
            "local function add(value) return value + 1 end; " +
            "local result = add(4); return result");
        AssertValues(ExecuteFresh(executor, calls), LuaValue.FromInteger(5));
        AssertValues(ExecuteFresh(executor, calls), LuaValue.FromInteger(5));
        var callPlan = Assert.IsType<LuaJitTier2Plan>(executor.GetTier2Plan(calls, 0));
        Assert.Contains(
            callPlan.Optimizations,
            optimization => optimization.Kind == LuaJitOptimizationKind.KnownClosureCall);
        Assert.Contains(
            callPlan.Optimizations,
            optimization => optimization.Kind ==
                LuaJitOptimizationKind.FixedResultWindowReuse);
    }

    [Fact]
    public void Tier2CacheDoesNotRetainModuleStateOrClosureOwners()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            EnableTier2 = true,
            Tier2InvocationThreshold = 1,
            Tier2BackedgeThreshold = int.MaxValue,
        });

        var references = CompileTier2AndReleaseOwners(executor);
        for (var attempt = 0; attempt < 10 && references.Any(static item => item.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.All(references, reference => Assert.False(reference.IsAlive));
        Assert.True(executor.Statistics.Tier2CompilationCompleted >= 1);
    }

    [Fact]
    public void RepeatedInjectedTier2GuardFailuresPreserveObservableSemantics()
    {
        var compiler = new AlternatingGuardFailureCompiler(
            ProfileGuidedLuaTier2Compiler.Instance);
        using var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.PreferJit,
                SynchronousCompilation = true,
                EnableTier2 = true,
                Tier2InvocationThreshold = 1,
                Tier2BackedgeThreshold = int.MaxValue,
                MaximumTier2GuardFailures = int.MaxValue,
            },
            tier2Compiler: compiler);
        var module = Compile(
            "local total = 0; for i = 1, 20 do total = total + i end; return total");

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(210));
        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(210));

        Assert.True(compiler.InjectedFailures > 0);
        Assert.Equal(compiler.InjectedFailures, executor.Statistics.Tier2GuardFailures);
        Assert.Equal(0, executor.Statistics.Tier2Invalidations);
    }

    [Fact]
    public void TieredCodeCacheEvictsTier1AndTier2AsOneLifecycleUnit()
    {
        using var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.PreferJit,
                SynchronousCompilation = true,
                EnableTier2 = true,
                EnableTier2ManagedFallback = true,
                Tier2InvocationThreshold = 1,
                Tier2BackedgeThreshold = int.MaxValue,
                MaximumCodeCacheBytes = 200,
            },
            compiler: new FixedSizeCompiler(
                ReflectionEmitLuaTier1Compiler.Instance,
                estimatedCodeBytes: 100),
            tier2Compiler: new FixedSizeTier2Compiler(
                ProfileGuidedLuaTier2Compiler.Instance,
                estimatedCodeBytes: 100));
        var first = Compile("return 1 + 2");
        var second = Compile("return 3 + 4");

        AssertValues(ExecuteFresh(executor, first), LuaValue.FromInteger(3));
        AssertValues(ExecuteFresh(executor, first), LuaValue.FromInteger(3));
        Assert.Equal(LuaJitCompilationTier.Tier2, executor.GetFunctionTier(first, 0));
        Assert.Equal(200, executor.Statistics.EstimatedCodeBytes);

        AssertValues(ExecuteFresh(executor, second), LuaValue.FromInteger(7));

        Assert.Equal(LuaJitFunctionState.Invalidated, executor.GetFunctionState(first, 0));
        Assert.Equal(LuaJitCompilationTier.Interpreter, executor.GetFunctionTier(first, 0));
        Assert.True(executor.Statistics.CacheEvictions >= 1);
    }

    [Fact]
    public void DefaultLoopOsrCompilesQualifiedExactNumericLoops()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = int.MaxValue,
            BackedgeThreshold = int.MaxValue,
            EnableTier2 = false,
            LoopOsrBackedgeThreshold = 1,
            SynchronousCompilation = true,
        });
        var events = new List<LuaJitEvent>();
        executor.EventOccurred += (_, jitEvent) => events.Add(jitEvent);
        var module = Compile("""
            local total, first, second, index = 0, 0, 1, 0
            while index < 200 do
                local next = first + second
                first = second
                second = next
                total = total + (next & 1023)
                index = index + 1
            end
            return total
            """);

        _ = ExecuteFresh(executor, module);

        var plan = Assert.Single(executor.GetLoopOsrPlans(module, 0));
        var eligibility = executor.GetLoopOsrEligibility(
            module,
            0,
            plan.HeaderProgramCounter,
            plan.BackedgeProgramCounter);
        Assert.True(eligibility.IsAutoEligible);
        Assert.Equal(
            LuaJitLoopOsrCodeKind.GuardedExactNumericCil,
            eligibility.ExpectedCodeKind);
        Assert.Equal(
            LuaJitOsrState.Ready,
            executor.GetLoopOsrState(
                module,
                0,
                plan.HeaderProgramCounter,
                plan.BackedgeProgramCounter));
        Assert.Contains(events, static jitEvent =>
            jitEvent.Kind == LuaJitEventKind.LoopOsrEligibilityAccepted);
        Assert.Contains(events, static jitEvent =>
            jitEvent.Kind == LuaJitEventKind.LoopOsrCompilerPrepared);
        var completed = Assert.Single(events, static jitEvent =>
            jitEvent.Kind == LuaJitEventKind.LoopOsrCompilationCompleted);
        Assert.Equal(
            LuaJitLoopOsrCodeKind.GuardedExactNumericCil,
            completed.LoopOsrCompilationMetrics?.CodeKind);
        Assert.True(executor.Statistics.LoopOsrRequests >= 1);
        Assert.True(executor.Statistics.LoopOsrEntries >= 1);
    }

    [Fact]
    public void ExplicitLoopOsrDisableRemainsCompleteOptOut()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = int.MaxValue,
            BackedgeThreshold = int.MaxValue,
            EnableTier2 = false,
            EnableLoopOsr = false,
            LoopOsrBackedgeThreshold = 1,
            SynchronousCompilation = true,
        });
        var events = new List<LuaJitEvent>();
        executor.EventOccurred += (_, jitEvent) => events.Add(jitEvent);
        var module = Compile(
            "local total = 0; for i = 1, 2000 do total = total + i end; return total");

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(2_001_000));

        var plan = Assert.Single(executor.GetLoopOsrPlans(module, 0));
        Assert.Equal(
            LuaJitOsrState.Disabled,
            executor.GetLoopOsrState(
                module,
                0,
                plan.HeaderProgramCounter,
                plan.BackedgeProgramCounter));
        Assert.DoesNotContain(events, static jitEvent => jitEvent.Kind is
            LuaJitEventKind.LoopOsrEligibilityAccepted or
            LuaJitEventKind.LoopOsrEligibilityRejected or
            LuaJitEventKind.LoopOsrCompilerPrepared or
            LuaJitEventKind.LoopOsrQueued or
            LuaJitEventKind.LoopOsrCompilationCompleted);
        Assert.Equal(0, executor.Statistics.LoopOsrEligibilityEvaluated);
        Assert.Equal(0, executor.Statistics.LoopOsrCompilationQueued);
        Assert.Equal(0, executor.Statistics.LoopOsrRequests);
        Assert.Equal(0, executor.Statistics.LoopOsrEntries);
    }

    [Fact]
    public void DefaultLoopOsrDoesNotRunWhenDynamicCodeIsUnavailable()
    {
        using var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.Auto,
                FunctionEntryThreshold = int.MaxValue,
                BackedgeThreshold = int.MaxValue,
                EnableTier2 = false,
                LoopOsrBackedgeThreshold = 1,
                SynchronousCompilation = true,
            },
            capabilities: new TestDynamicCodeCapabilities(false, false));
        var events = new List<LuaJitEvent>();
        executor.EventOccurred += (_, jitEvent) => events.Add(jitEvent);
        var module = Compile("local n=0; while n<4 do n=n+1 end; return n");

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(4));

        var plan = Assert.Single(executor.GetLoopOsrPlans(module, 0));
        Assert.Equal(
            LuaJitOsrState.Disabled,
            executor.GetLoopOsrState(
                module,
                0,
                plan.HeaderProgramCounter,
                plan.BackedgeProgramCounter));
        Assert.DoesNotContain(events, static jitEvent => jitEvent.Kind is
            LuaJitEventKind.LoopOsrEligibilityAccepted or
            LuaJitEventKind.LoopOsrEligibilityRejected or
            LuaJitEventKind.LoopOsrCompilerPrepared or
            LuaJitEventKind.LoopOsrQueued or
            LuaJitEventKind.LoopOsrCompilationCompleted);
        Assert.Equal(0, executor.Statistics.LoopOsrEligibilityEvaluated);
        Assert.Equal(0, executor.Statistics.LoopOsrCompilationQueued);
        Assert.Equal(0, executor.Statistics.LoopOsrRequests);
        Assert.Equal(0, executor.Statistics.LoopOsrEntries);
    }

    [Fact]
    public void LoopOsrDefersAnalysisUntilTheFunctionIsHot()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = int.MaxValue,
            BackedgeThreshold = int.MaxValue,
            EnableTier2 = false,
            EnableLoopOsr = true,
            LoopOsrBackedgeThreshold = 1_024,
            SynchronousCompilation = true,
        });
        var events = new List<LuaJitEvent>();
        executor.EventOccurred += (_, jitEvent) => events.Add(jitEvent);
        var module = Compile(
            "local total=0; for i=1,8 do total=total+i end; return total");

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(36));

        Assert.Equal(0, executor.Statistics.LoopOsrEligibilityEvaluated);
        Assert.Equal(0, executor.Statistics.LoopOsrCompilationQueued);
        Assert.Equal(0, executor.Statistics.LoopOsrEntries);
        Assert.DoesNotContain(events, static jitEvent => jitEvent.Kind is
            LuaJitEventKind.LoopOsrEligibilityAccepted or
            LuaJitEventKind.LoopOsrEligibilityRejected);
        Assert.DoesNotContain(events, static jitEvent =>
            jitEvent.Kind == LuaJitEventKind.LoopOsrCompilerPrepared);
    }

    [Fact]
    public void LoopOsrEligibilityWaitsForObservedExactNumericOperands()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = int.MaxValue,
            BackedgeThreshold = int.MaxValue,
            EnableTier2 = false,
            EnableLoopOsr = true,
            LoopOsrBackedgeThreshold = 1,
            SynchronousCompilation = true,
        });
        var module = Compile(
            "local total=0; for i=1,8 do total=total+i end; return total");
        var plan = Assert.Single(executor.GetLoopOsrPlans(module, 0));

        var pending = executor.GetLoopOsrEligibility(
            module,
            0,
            plan.HeaderProgramCounter,
            plan.BackedgeProgramCounter);

        Assert.False(pending.IsAutoEligible);
        Assert.Equal(
            LuaJitLoopOsrEligibilityReason.AwaitingExactNumericProfile,
            pending.Reason);
        Assert.Equal(0, executor.Statistics.LoopOsrEligibilityEvaluated);

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(36));

        var accepted = executor.GetLoopOsrEligibility(
            module,
            0,
            plan.HeaderProgramCounter,
            plan.BackedgeProgramCounter);
        Assert.True(accepted.IsAutoEligible);
        Assert.Equal(LuaJitLoopOsrEligibilityReason.Eligible, accepted.Reason);
        Assert.Equal(1, executor.Statistics.LoopOsrEligibilityAccepted);
    }

    [Fact]
    public void LoopOsrUsesGuardedExactNumericCilForQualifiedLoops()
    {
        using var executor = CreateLoopOsrExecutor();
        var events = new List<LuaJitEvent>();
        executor.EventOccurred += (_, jitEvent) => events.Add(jitEvent);
        var module = Compile("""
            local total, first, second, index = 0, 0, 1, 0
            while index < 200 do
                local next = first + second
                first = second
                second = next
                total = total + (next & 1023)
                index = index + 1
            end
            return total
            """);

        _ = ExecuteFresh(executor, module);

        var completed = Assert.Single(events, static jitEvent =>
            jitEvent.Kind == LuaJitEventKind.LoopOsrCompilationCompleted);
        var acceptedEvent = Assert.Single(events, static jitEvent =>
            jitEvent.Kind == LuaJitEventKind.LoopOsrEligibilityAccepted);
        var preparedEvent = Assert.Single(events, static jitEvent =>
            jitEvent.Kind == LuaJitEventKind.LoopOsrCompilerPrepared);
        Assert.True(preparedEvent.Duration >= TimeSpan.Zero);
        Assert.True(events.IndexOf(acceptedEvent) < events.IndexOf(preparedEvent));
        Assert.True(events.IndexOf(preparedEvent) < events.IndexOf(completed));
        var metrics = Assert.IsType<LuaJitLoopOsrCompilationMetrics>(
            completed.LoopOsrCompilationMetrics);
        Assert.Equal(LuaJitLoopOsrCodeKind.GuardedExactNumericCil, metrics.CodeKind);
        Assert.True(metrics.SpecializedInstructionCount >= 5);
        Assert.True(metrics.GuardCount >= 5);
        Assert.True(metrics.LivenessCacheHit);
        Assert.True(metrics.EstimatedCodeBytes > 0);
        Assert.True(metrics.AllocatedBytes > 0);
        var plan = Assert.Single(executor.GetLoopOsrPlans(module, 0));
        var eligibility = executor.GetLoopOsrEligibility(
            module,
            0,
            plan.HeaderProgramCounter,
            plan.BackedgeProgramCounter);
        Assert.True(eligibility.IsAutoEligible);
        Assert.Equal(
            LuaJitLoopOsrCodeKind.GuardedExactNumericCil,
            eligibility.ExpectedCodeKind);
        Assert.Equal(1, executor.Statistics.LoopOsrEligibilityAccepted);
        Assert.Equal(0, executor.Statistics.LoopOsrEligibilityRejected);
    }

    [Fact]
    public void DefaultLoopOsrRejectsManagedBoundaryWithoutCompilation()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = int.MaxValue,
            BackedgeThreshold = int.MaxValue,
            EnableTier2 = false,
            LoopOsrBackedgeThreshold = 1,
            SynchronousCompilation = true,
        });
        var events = new List<LuaJitEvent>();
        executor.EventOccurred += (_, jitEvent) => events.Add(jitEvent);
        var module = Compile("""
            local values = {}
            local index = 0
            while index < 8 do
                index = index + 1
                values[index] = index
            end
            return values[8]
            """);

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(8));

        var plan = Assert.Single(executor.GetLoopOsrPlans(module, 0));
        var eligibility = executor.GetLoopOsrEligibility(
            module,
            0,
            plan.HeaderProgramCounter,
            plan.BackedgeProgramCounter);
        Assert.False(eligibility.IsAutoEligible);
        Assert.Equal(
            LuaJitLoopOsrEligibilityReason.ManagedSemanticBoundary,
            eligibility.Reason);
        Assert.Equal(
            LuaJitLoopOsrDiagnosticCodes.ManagedSemanticBoundary,
            eligibility.DiagnosticCode);
        Assert.Equal(
            LuaJitOsrState.Ineligible,
            executor.GetLoopOsrState(
                module,
                0,
                plan.HeaderProgramCounter,
                plan.BackedgeProgramCounter));
        Assert.Contains(events, static jitEvent =>
            jitEvent.Kind == LuaJitEventKind.LoopOsrEligibilityRejected);
        Assert.DoesNotContain(events, static jitEvent =>
            jitEvent.Kind == LuaJitEventKind.LoopOsrCompilationCompleted);
        Assert.DoesNotContain(events, static jitEvent =>
            jitEvent.Kind == LuaJitEventKind.LoopOsrCompilerPrepared);
        Assert.Equal(1, executor.Statistics.LoopOsrEligibilityRejected);
        Assert.Equal(0, executor.Statistics.LoopOsrCompilationQueued);
    }

    [Fact]
    public void DefaultLoopOsrRejectsNonExactNumericProfileBeforeCompilation()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = int.MaxValue,
            BackedgeThreshold = int.MaxValue,
            EnableTier2 = false,
            LoopOsrBackedgeThreshold = 1,
            SynchronousCompilation = true,
        });
        var events = new List<LuaJitEvent>();
        executor.EventOccurred += (_, jitEvent) => events.Add(jitEvent);
        var module = Compile("""
            local value, limit = ...
            local count = 0
            while count < limit do
                value = value + 1
                count = count + 1
            end
            return count
            """);
        var state = new LuaState();
        var metatable = state.CreateTable();
        metatable.Set(
            LuaValue.FromString(state.Strings.GetOrCreate("__add"u8)),
            LuaValue.FromFunction(new LuaNativeFunction(
                "add",
                static (_, arguments) => [arguments[0]])));
        var value = state.CreateTable();
        value.SetMetatable(metatable);

        AssertValues(
            executor.Execute(
                state,
                state.CreateMainClosure(module),
                [LuaValue.FromTable(value), LuaValue.FromInteger(8)]),
            LuaValue.FromInteger(8));

        var plan = Assert.Single(executor.GetLoopOsrPlans(module, 0));
        var eligibility = executor.GetLoopOsrEligibility(
            module,
            0,
            plan.HeaderProgramCounter,
            plan.BackedgeProgramCounter);
        Assert.False(eligibility.IsAutoEligible);
        Assert.Equal(
            LuaJitLoopOsrEligibilityReason.NonExactNumericProfile,
            eligibility.Reason);
        Assert.Equal(
            LuaJitLoopOsrDiagnosticCodes.NonExactNumericProfile,
            eligibility.DiagnosticCode);
        Assert.Equal(
            LuaJitOsrState.Ineligible,
            executor.GetLoopOsrState(
                module,
                0,
                plan.HeaderProgramCounter,
                plan.BackedgeProgramCounter));
        Assert.Contains(events, static jitEvent =>
            jitEvent.Kind == LuaJitEventKind.LoopOsrEligibilityRejected &&
            jitEvent.DiagnosticCode == LuaJitLoopOsrDiagnosticCodes.NonExactNumericProfile);
        Assert.DoesNotContain(events, static jitEvent =>
            jitEvent.Kind == LuaJitEventKind.LoopOsrCompilationCompleted);
        Assert.DoesNotContain(events, static jitEvent =>
            jitEvent.Kind == LuaJitEventKind.LoopOsrCompilerPrepared);
        Assert.Equal(0, executor.Statistics.LoopOsrEligibilityAccepted);
        Assert.Equal(1, executor.Statistics.LoopOsrEligibilityRejected);
        Assert.Equal(0, executor.Statistics.LoopOsrCompilationQueued);
        Assert.Equal(0, executor.Statistics.LoopOsrGuardFailures);
    }

    [Fact]
    public void LoopOsrRetainsManagedFallbackForSemanticBoundaries()
    {
        using var executor = CreateLoopOsrExecutor();
        var events = new List<LuaJitEvent>();
        executor.EventOccurred += (_, jitEvent) => events.Add(jitEvent);
        var module = Compile("""
            local values = {}
            local index = 0
            while index < 8 do
                index = index + 1
                values[index] = index
            end
            return values[8]
            """);

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(8));

        var completed = Assert.Single(events, static jitEvent =>
            jitEvent.Kind == LuaJitEventKind.LoopOsrCompilationCompleted);
        Assert.Equal(
            LuaJitLoopOsrCodeKind.ManagedCanonicalProgram,
            completed.LoopOsrCompilationMetrics?.CodeKind);
        Assert.Equal(0, completed.LoopOsrCompilationMetrics?.SpecializedInstructionCount);
    }

    [Fact]
    public void LoopOsrWidensToManagedFallbackAfterSpecializedGuardFailure()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = int.MaxValue,
            BackedgeThreshold = int.MaxValue,
            EnableTier2 = false,
            EnableLoopOsr = true,
            EnableLoopOsrManagedFallback = true,
            LoopOsrBackedgeThreshold = 1,
            MaximumLoopOsrGuardFailures = 1,
            SynchronousCompilation = true,
        });
        var events = new List<LuaJitEvent>();
        executor.EventOccurred += (_, jitEvent) => events.Add(jitEvent);
        var module = Compile("""
            local value, limit = ...
            local count = 0
            while count < limit do
                value = value + 1
                count = count + 1
            end
            return count
            """);
        var numericState = new LuaState();
        AssertValues(
            executor.Execute(
                numericState,
                numericState.CreateMainClosure(module),
                [LuaValue.FromInteger(0), LuaValue.FromInteger(4)]),
            LuaValue.FromInteger(4));
        var managedState = new LuaState();
        var metamethodModule = Compile(
            "return function(left, right) return left end");
        var metamethod = Assert.Single(new LuaInterpreter().Execute(
            managedState,
            managedState.CreateMainClosure(metamethodModule)).Values);
        var metatable = managedState.CreateTable();
        metatable.Set(
            LuaValue.FromString(managedState.Strings.GetOrCreate("__add"u8)),
            metamethod);
        var managedValue = managedState.CreateTable();
        managedValue.SetMetatable(metatable);
        AssertValues(
            executor.Execute(
                managedState,
                managedState.CreateMainClosure(module),
                [
                    LuaValue.FromTable(managedValue),
                    LuaValue.FromInteger(4),
                ]),
            LuaValue.FromInteger(4));

        var codeKinds = events
            .Where(static jitEvent =>
                jitEvent.Kind == LuaJitEventKind.LoopOsrCompilationCompleted)
            .Select(static jitEvent => jitEvent.LoopOsrCompilationMetrics?.CodeKind)
            .ToArray();
        Assert.Equal(
            [
                LuaJitLoopOsrCodeKind.GuardedExactNumericCil,
                LuaJitLoopOsrCodeKind.ManagedCanonicalProgram,
            ],
            codeKinds);
        Assert.True(executor.Statistics.LoopOsrGuardFailures >= 1);
        Assert.True(executor.Statistics.LoopOsrInvalidations >= 1);
    }

    [Fact]
    public void LoopOsrRejectsUnexpectedManagedCompilerResult()
    {
        using var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.Auto,
                FunctionEntryThreshold = int.MaxValue,
                BackedgeThreshold = int.MaxValue,
                EnableTier2 = false,
                EnableLoopOsr = true,
                LoopOsrBackedgeThreshold = 1,
                SynchronousCompilation = true,
            },
            loopOsrCompiler: new ForcedManagedLoopOsrCompiler(
                CanonicalLuaLoopOsrCompiler.Instance));
        var events = new List<LuaJitEvent>();
        executor.EventOccurred += (_, jitEvent) => events.Add(jitEvent);
        var module = Compile(
            "local n=0; while n<8 do n=n+1 end; return n");

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(8));

        Assert.Contains(events, static jitEvent =>
            jitEvent.Kind == LuaJitEventKind.LoopOsrCompilationFailed &&
            jitEvent.DiagnosticCode == LuaJitLoopOsrDiagnosticCodes.UnexpectedCodeKind);
        Assert.Equal(0, executor.Statistics.LoopOsrCompilationCompleted);
        Assert.Equal(0, executor.Statistics.LoopOsrEntries);
    }

    [Theory]
    [InlineData(
        "local total=0; for i=1,10 do total=total+i end; return total",
        55)]
    [InlineData(
        "local i,total=0,0; while i<5 do i=i+1; total=total+i end; return total",
        15)]
    [InlineData(
        "local i,total=0,0; repeat i=i+1; total=total+i until i==4; return total",
        10)]
    [InlineData(
        "local function nextValue(limit,current) current=current+1; " +
        "if current<=limit then return current,current*2 end end; " +
        "local total=0; for _,value in nextValue,3,0 do total=total+value end; return total",
        12)]
    public void LoopOsrEntersOnlyAtVerifiedBackedgesAndPreservesLoopKinds(
        string source,
        long expected)
    {
        using var executor = CreateLoopOsrExecutor();
        var module = Compile(source);

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(expected));

        var plans = executor.GetLoopOsrPlans(module, 0);
        Assert.NotEmpty(plans);
        Assert.Contains(plans, plan =>
            executor.GetLoopOsrState(
                module,
                0,
                plan.HeaderProgramCounter,
                plan.BackedgeProgramCounter) == LuaJitOsrState.Ready);
        Assert.True(executor.Statistics.LoopOsrCompilationCompleted >= 1);
        Assert.True(executor.Statistics.LoopOsrEntries >= 1);
        if (expected == 15)
        {
            Assert.InRange(executor.Statistics.LoopOsrEntries, 1, 2);
            Assert.True(executor.Statistics.Backedges >= 5);
        }

        Assert.Equal(0, executor.Statistics.CompilationCompleted);
    }

    [Fact]
    public void LoopOsrKeepsCapturedOpenUpvaluesAndToBeClosedStateCanonical()
    {
        using var executor = CreateLoopOsrExecutor();
        var module = Compile("""
            local closer = ...
            local functions = {}
            local index = 0
            repeat
                local value <close> = closer
                local captured = index + 1
                functions[captured] = function() return captured end
                index = captured
            until index == 3
            return functions[1]() + functions[2]() + functions[3]()
            """);
        var state = new LuaState();
        var closed = 0;
        var closeMetatable = state.CreateTable();
        closeMetatable.Set(
            LuaValue.FromString(state.Strings.GetOrCreate("__close"u8)),
            LuaValue.FromFunction(new LuaNativeFunction(
                "close",
                (_, _) =>
                {
                    closed++;
                    return [];
                })));
        var closer = state.CreateTable();
        closer.SetMetatable(closeMetatable);

        var result = executor.Execute(
            state,
            state.CreateMainClosure(module),
            [LuaValue.FromTable(closer)]);

        AssertValues(result, LuaValue.FromInteger(6));
        Assert.Equal(3, closed);
        Assert.True(executor.Statistics.LoopOsrEntries >= 1);
        Assert.All(
            executor.GetLoopOsrPlans(module, 0),
            plan =>
            {
                Assert.True(plan.EntryMap.FrameTopMaterialized);
                Assert.True(plan.EntryMap.OpenUpvaluesMaterialized);
                Assert.True(plan.EntryMap.ToBeClosedStateMaterialized);
                Assert.All(
                    plan.EntryMap.Registers,
                    map => Assert.Equal(map.CanonicalRegister, map.CompiledSlot));
            });
    }

    [Fact]
    public void LoopOsrExitsThroughSchedulerForYieldingMetamethods()
    {
        using var executor = CreateLoopOsrExecutor();
        var module = Compile("""
            local value, limit = ...
            local count = 0
            while count < limit do
                value = value + 1
                count = count + 1
            end
            return count
            """);
        var state = new LuaState();
        var metamethodFactory = Compile("""
            local bridge = ...
            return function(left, right)
                bridge(1)
                return left
            end
            """);
        var metamethod = Assert.Single(new LuaInterpreter().Execute(
            state,
            state.CreateMainClosure(metamethodFactory),
            [LuaValue.FromFunction(new LuaNativeFunction("yield-bridge", YieldBridgeStep))])
            .Values);
        var metatable = state.CreateTable();
        metatable.Set(
            LuaValue.FromString(state.Strings.GetOrCreate("__add"u8)),
            metamethod);
        var value = state.CreateTable();
        value.SetMetatable(metatable);
        var thread = state.CreateThread(state.CreateMainClosure(module));

        var result = executor.Start(
            state,
            thread,
            [LuaValue.FromTable(value), LuaValue.FromInteger(3)]);
        var yields = 0;
        while (result.Signal == LuaVmSignal.Yielded)
        {
            yields++;
            result = executor.Resume(state, thread);
        }

        Assert.Equal(3, yields);
        AssertValues(result, LuaValue.FromInteger(3));
        Assert.True(executor.Statistics.LoopOsrEntries >= 1);
    }

    [Fact]
    public void LoopOsrGuardFailureResumesAtCanonicalHeaderWithoutRepeatingWork()
    {
        var compiler = new InjectedGuardFailureLoopOsrCompiler(
            CanonicalLuaLoopOsrCompiler.Instance);
        using var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.Auto,
                FunctionEntryThreshold = int.MaxValue,
                BackedgeThreshold = int.MaxValue,
                EnableTier2 = false,
                EnableLoopOsr = true,
                LoopOsrBackedgeThreshold = 1,
                MaximumLoopOsrGuardFailures = int.MaxValue,
                SynchronousCompilation = true,
            },
            loopOsrCompiler: compiler);
        var module = Compile(
            "local total=0; for i=1,20 do total=total+i end; return total");

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(210));

        Assert.Equal(1, compiler.InjectedFailures);
        Assert.Equal(1, executor.Statistics.LoopOsrGuardFailures);
        Assert.True(executor.Statistics.LoopOsrEntries >= 2);
    }

    [Fact]
    public async Task ConcurrentLoopOsrRequestsCompileEachLoopOnlyOnce()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var compiler = new BlockingLoopOsrCompiler(
            CanonicalLuaLoopOsrCompiler.Instance,
            started,
            release);
        using var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.Auto,
                FunctionEntryThreshold = int.MaxValue,
                BackedgeThreshold = int.MaxValue,
                EnableTier2 = false,
                EnableLoopOsr = true,
                LoopOsrBackedgeThreshold = 1,
                MaximumConcurrentCompilations = 2,
            },
            loopOsrCompiler: compiler);
        var module = Compile(
            "local total=0; for i=1,30 do total=total+i end; return total");
        var executions = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => ExecuteFresh(executor, module)))
            .ToArray();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        release.Set();

        var results = await Task.WhenAll(executions);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await executor.WaitForIdleAsync(timeout.Token);

        Assert.All(results, result => AssertValues(result, LuaValue.FromInteger(465)));
        Assert.Equal(1, compiler.CallCount);
        Assert.Equal(1, executor.Statistics.LoopOsrCompilationCompleted);
    }

    [Fact]
    public void LoopOsrHonorsBudgetDebugGcAndInvalidationGuards()
    {
        var module = Compile(
            "local total=0; for i=1,20 do local value={i}; total=total+value[1] end; return total");
        using (var budgetExecutor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = int.MaxValue,
            BackedgeThreshold = int.MaxValue,
            EnableTier2 = false,
            EnableLoopOsr = true,
            EnableLoopOsrManagedFallback = true,
            LoopOsrBackedgeThreshold = 1,
            SynchronousCompilation = true,
            Interpreter = LuaInterpreterOptions.Default with
            {
                MaximumInstructionCount = 25,
            },
        }))
        {
            var budgetState = new LuaState();
            Assert.Throws<LuaRuntimeException>(() => budgetExecutor.Execute(
                budgetState,
                budgetState.CreateMainClosure(module)));
            Assert.Equal(LuaThreadStatus.Error, budgetState.MainThread.Status);
        }

        using var executor = CreateLoopOsrExecutor();
        var debugState = new LuaState();
        LuaDebugApi.SetHook(
            debugState,
            debugState.MainThread,
            LuaValue.FromFunction(new LuaNativeFunction("hook", static (_, _) => [])),
            LuaDebugHookMask.Line,
            count: 0);
        AssertValues(
            executor.Execute(debugState, debugState.CreateMainClosure(module)),
            LuaValue.FromInteger(210));
        Assert.Equal(0, executor.Statistics.LoopOsrEntries);

        var gcState = new LuaState(new LuaStateOptions
        {
            Heap = LuaHeapOptions.Default with { StressEveryAllocation = true },
        });
        AssertValues(
            executor.Execute(gcState, gcState.CreateMainClosure(module)),
            LuaValue.FromInteger(210));
        Assert.True(executor.Statistics.LoopOsrEntries >= 1);
        var plan = Assert.Single(executor.GetLoopOsrPlans(module, 0));
        Assert.Equal(
            LuaJitOsrState.Ready,
            executor.GetLoopOsrState(
                module,
                0,
                plan.HeaderProgramCounter,
                plan.BackedgeProgramCounter));

        executor.Invalidate(module);

        Assert.Equal(
            LuaJitOsrState.Invalidated,
            executor.GetLoopOsrState(
                module,
                0,
                plan.HeaderProgramCounter,
                plan.BackedgeProgramCounter));
        Assert.Equal(0, executor.Statistics.EstimatedCodeBytes);
        Assert.True(executor.Statistics.LoopOsrInvalidations >= 1);
    }

    [Fact]
    public void LoopOsrParticipatesInTheSharedCodeByteLru()
    {
        using var executor = CreateExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.Auto,
                FunctionEntryThreshold = int.MaxValue,
                BackedgeThreshold = int.MaxValue,
                EnableTier2 = false,
                EnableLoopOsr = true,
                LoopOsrBackedgeThreshold = 1,
                MaximumCodeCacheBytes = 500,
                SynchronousCompilation = true,
            },
            loopOsrCompiler: new FixedSizeLoopOsrCompiler(
                CanonicalLuaLoopOsrCompiler.Instance,
                300));
        var first = Compile("local n=0; while n<4 do n=n+1 end; return n");
        var second = Compile("local n=0; repeat n=n+2 until n>=6; return n");

        AssertValues(ExecuteFresh(executor, first), LuaValue.FromInteger(4));
        var firstPlan = Assert.Single(executor.GetLoopOsrPlans(first, 0));
        Assert.Equal(
            LuaJitOsrState.Ready,
            executor.GetLoopOsrState(
                first,
                0,
                firstPlan.HeaderProgramCounter,
                firstPlan.BackedgeProgramCounter));

        AssertValues(ExecuteFresh(executor, second), LuaValue.FromInteger(6));

        Assert.Equal(
            LuaJitOsrState.Invalidated,
            executor.GetLoopOsrState(
                first,
                0,
                firstPlan.HeaderProgramCounter,
                firstPlan.BackedgeProgramCounter));
        Assert.Equal(300, executor.Statistics.EstimatedCodeBytes);
        Assert.True(executor.Statistics.CacheEvictions >= 1);
        Assert.True(executor.Statistics.LoopOsrInvalidations >= 1);
    }

    [Fact]
    public void LoopOsrCacheDoesNotRetainModuleStateOrClosureOwners()
    {
        using var executor = CreateLoopOsrExecutor();

        var references = CompileLoopOsrAndReleaseOwners(executor);
        for (var attempt = 0; attempt < 10 && references.Any(static item => item.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.All(references, reference => Assert.False(reference.IsAlive));
        Assert.True(executor.Statistics.LoopOsrCompilationCompleted >= 1);
    }

    private static LuaJitExecutor CreateExecutor(
        LuaJitExecutorOptions options,
        ILuaDynamicCodeCapabilities? capabilities = null,
        ILuaTier1Compiler? compiler = null,
        ILuaTier2Compiler? tier2Compiler = null,
        ILuaLoopOsrCompiler? loopOsrCompiler = null) =>
        new(
            options,
            capabilities ?? new TestDynamicCodeCapabilities(true, true),
            compiler ?? ReflectionEmitLuaTier1Compiler.Instance,
            tier2Compiler ?? ProfileGuidedLuaTier2Compiler.Instance,
            loopOsrCompiler ?? CanonicalLuaLoopOsrCompiler.Instance);

    private static LuaJitExecutor CreateLoopOsrExecutor() => CreateExecutor(
        LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = int.MaxValue,
            BackedgeThreshold = int.MaxValue,
            EnableTier2 = false,
            EnableLoopOsr = true,
            EnableLoopOsrManagedFallback = true,
            LoopOsrBackedgeThreshold = 1,
            SynchronousCompilation = true,
        });

    private static LuaNativeStep YieldBridgeStep(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values) => continuationId switch
        {
            0 => LuaNativeStep.Yielded(
                values.ToArray(),
                continuationId: 1,
                stateValues: values.ToArray()),
            1 => LuaNativeStep.Completed(context.InvocationState.ToArray()),
            _ => throw new InvalidOperationException(),
        };

    private static LuaExecutionResult ExecuteFresh(
        LuaJitExecutor executor,
        LuaIrModule module,
        params LuaValue[] arguments)
    {
        var state = new LuaState();
        return executor.Execute(state, state.CreateMainClosure(module), arguments);
    }

    private static void AssertValues(
        LuaExecutionResult result,
        params LuaValue[] expected) =>
        Assert.True(result.Values.SequenceEqual(expected));

    private static long MeasureCurrentThreadAllocation(int count, Action action)
    {
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < count; index++)
        {
            action();
        }

        return GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    }

    private static string FormatProfile(LuaJitFunctionProfile profile) => string.Join(
        "; ",
        profile.Sites.Select(site =>
            $"{site.ProgramCounter}:{site.Opcode}:{site.Samples}:" +
            $"{site.FirstOperandKinds}/{site.SecondOperandKinds}"));

    private static void ExecuteTableLookup(
        LuaJitExecutor executor,
        LuaIrModule module,
        int extraKeys,
        LuaValue expected)
    {
        var state = new LuaState();
        var table = state.CreateTable();
        for (var key = 1; key <= extraKeys; key++)
        {
            table.Set(LuaValue.FromInteger(key), LuaValue.FromInteger(key));
        }

        var result = executor.Execute(
            state,
            state.CreateMainClosure(module),
            [LuaValue.FromTable(table), LuaValue.FromInteger(1)]);
        AssertValues(result, expected);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CompileAndReleaseOwners(LuaJitExecutor executor)
    {
        var module = Compile("return 11");
        var state = new LuaState();
        var closure = state.CreateMainClosure(module);
        AssertValues(executor.Execute(state, closure), LuaValue.FromInteger(11));
        return [new WeakReference(module), new WeakReference(state), new WeakReference(closure)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] RouteRejectedFunctionAndReleaseOwners(
        LuaJitExecutor executor)
    {
        var module = Compile("""
            local values = {}
            local total = 0
            for index = 1, 20 do
                values[index] = index
                total = total + values[index]
            end
            return total
            """);
        var state = new LuaState();
        var closure = state.CreateMainClosure(module);
        AssertValues(executor.Execute(state, closure), LuaValue.FromInteger(210));
        Assert.Equal(
            LuaJitEligibilityReason.SlowPathDensityTooHigh,
            executor.GetFunctionEligibility(module, 0).Reason);
        return [new WeakReference(module), new WeakReference(state), new WeakReference(closure)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CompileTier2AndReleaseOwners(LuaJitExecutor executor)
    {
        var module = Compile("local value = 10; return value + 1");
        for (var invocation = 0; invocation < 2; invocation++)
        {
            var invocationState = new LuaState();
            AssertValues(
                executor.Execute(
                    invocationState,
                    invocationState.CreateMainClosure(module)),
                LuaValue.FromInteger(11));
        }

        var state = new LuaState();
        var closure = state.CreateMainClosure(module);
        return [new WeakReference(module), new WeakReference(state), new WeakReference(closure)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ImportProfileAndReleaseOwners(LuaJitExecutor warmed)
    {
        var module = Compile("local value = ...; return value + 1");
        var state = new LuaState();
        var closure = state.CreateMainClosure(module);
        byte[] payload;
        using (var training = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            EnableTier2 = true,
        }))
        {
            AssertValues(
                training.Execute(state, closure, [LuaValue.FromInteger(1)]),
                LuaValue.FromInteger(2));
            payload = training.ExportProfile(module);
        }

        Assert.True(warmed.ImportProfile(module, payload).Succeeded);
        return [new WeakReference(module), new WeakReference(state), new WeakReference(closure)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CompileLoopOsrAndReleaseOwners(LuaJitExecutor executor)
    {
        var module = Compile("local n=0; while n<5 do n=n+1 end; return n");
        var state = new LuaState();
        var closure = state.CreateMainClosure(module);
        AssertValues(executor.Execute(state, closure), LuaValue.FromInteger(5));
        return [new WeakReference(module), new WeakReference(state), new WeakReference(closure)];
    }

    private static LuaIrModule Compile(string source)
    {
        var parsing = LuaParser.Parse(SourceText.FromUtf8(source));
        var binding = LuaBinder.Bind(parsing);
        var lowering = LuaLowerer.Lower(binding);
        Assert.True(lowering.Succeeded, string.Join(
            "; ",
            lowering.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return Assert.IsType<LuaIrModule>(lowering.Module);
    }

    private sealed record TestDynamicCodeCapabilities(
        bool IsDynamicCodeSupported,
        bool IsDynamicCodeCompiled) : ILuaDynamicCodeCapabilities;

    private sealed class CountingCompiler(ILuaTier1Compiler inner) : ILuaTier1Compiler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public LuaTier1CompilationResult Compile(
            LuaIrModule module,
            int functionId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return inner.Compile(module, functionId, cancellationToken);
        }
    }

    private sealed class BlockingCompiler(
        ILuaTier1Compiler inner,
        ManualResetEventSlim started,
        ManualResetEventSlim release) : ILuaTier1Compiler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public LuaTier1CompilationResult Compile(
            LuaIrModule module,
            int functionId,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            return inner.Compile(module, functionId, cancellationToken);
        }
    }

    private sealed class FailingCompiler : ILuaTier1Compiler
    {
        public LuaTier1CompilationResult Compile(
            LuaIrModule module,
            int functionId,
            CancellationToken cancellationToken) =>
            new(null, 0, ["forced failure"]);
    }

    private sealed class FailOnceCompiler(ILuaTier1Compiler inner) : ILuaTier1Compiler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public LuaTier1CompilationResult Compile(
            LuaIrModule module,
            int functionId,
            CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _callCount) == 1
                ? new LuaTier1CompilationResult(null, 0, ["transient failure"])
                : inner.Compile(module, functionId, cancellationToken);
    }

    private sealed class FixedSizeCompiler(
        ILuaTier1Compiler inner,
        long estimatedCodeBytes) : ILuaTier1Compiler
    {
        public LuaTier1CompilationResult Compile(
            LuaIrModule module,
            int functionId,
            CancellationToken cancellationToken)
        {
            var result = inner.Compile(module, functionId, cancellationToken);
            return result.Succeeded
                ? result with { EstimatedCodeBytes = estimatedCodeBytes }
                : result;
        }
    }

    private sealed class FixedSizeTier2Compiler(
        ILuaTier2Compiler inner,
        long estimatedCodeBytes) : ILuaTier2Compiler
    {
        public LuaTier2CompilationResult Compile(
            LuaIrModule module,
            int functionId,
            LuaJitFunctionProfile profile,
            CancellationToken cancellationToken)
        {
            var result = inner.Compile(module, functionId, profile, cancellationToken);
            return result.Succeeded
                ? result with { EstimatedCodeBytes = estimatedCodeBytes }
                : result;
        }
    }

    private sealed class CountingTier2Compiler(ILuaTier2Compiler inner) : ILuaTier2Compiler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public LuaTier2CompilationResult Compile(
            LuaIrModule module,
            int functionId,
            LuaJitFunctionProfile profile,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return inner.Compile(module, functionId, profile, cancellationToken);
        }
    }

    private sealed class ManagedCodeKindTier2Compiler(ILuaTier2Compiler inner)
        : ILuaTier2Compiler
    {
        public LuaTier2CompilationResult Compile(
            LuaIrModule module,
            int functionId,
            LuaJitFunctionProfile profile,
            CancellationToken cancellationToken)
        {
            var result = inner.Compile(module, functionId, profile, cancellationToken);
            return result.Succeeded
                ? result with
                {
                    Plan = result.Plan! with
                    {
                        CodeKind = LuaJitTier2CodeKind.ManagedProfileProgram,
                    },
                }
                : result;
        }
    }

    private sealed class CancellableCompiler(
        ManualResetEventSlim started,
        ManualResetEventSlim canceled) : ILuaTier1Compiler
    {
        public LuaTier1CompilationResult Compile(
            LuaIrModule module,
            int functionId,
            CancellationToken cancellationToken)
        {
            started.Set();
            cancellationToken.WaitHandle.WaitOne();
            canceled.Set();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was not observed.");
        }
    }

    private sealed class CancellationIgnoringCompiler(
        ManualResetEventSlim started,
        ManualResetEventSlim returned) : ILuaTier1Compiler
    {
        public LuaTier1CompilationResult Compile(
            LuaIrModule module,
            int functionId,
            CancellationToken cancellationToken)
        {
            started.Set();
            cancellationToken.WaitHandle.WaitOne();
            returned.Set();
            return new LuaTier1CompilationResult(
                static (context, thread, frame) => LuaCompiledExit.Return(
                    frame.ProgramCounter,
                    context.InstructionsConsumed),
                64,
                []);
        }
    }

    private sealed class BlockingTier2Compiler(
        ILuaTier2Compiler inner,
        ManualResetEventSlim started,
        ManualResetEventSlim release) : ILuaTier2Compiler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public LuaTier2CompilationResult Compile(
            LuaIrModule module,
            int functionId,
            LuaJitFunctionProfile profile,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), cancellationToken));
            return inner.Compile(module, functionId, profile, cancellationToken);
        }
    }

    private sealed class AlternatingGuardFailureCompiler(ILuaTier2Compiler inner)
        : ILuaTier2Compiler
    {
        private int _entries;
        private long _injectedFailures;

        public long InjectedFailures => Interlocked.Read(ref _injectedFailures);

        public LuaTier2CompilationResult Compile(
            LuaIrModule module,
            int functionId,
            LuaJitFunctionProfile profile,
            CancellationToken cancellationToken)
        {
            var result = inner.Compile(module, functionId, profile, cancellationToken);
            if (!result.Succeeded)
            {
                return result;
            }

            var method = result.Method!;
            return result with
            {
                Method = (context, thread, frame) =>
                {
                    if ((Interlocked.Increment(ref _entries) & 1) != 0)
                    {
                        Interlocked.Increment(ref _injectedFailures);
                        return LuaCompiledExit.Deopt(
                            frame.ProgramCounter,
                            instructionsConsumed: 0,
                            LuaCompiledExitReason.GuardFailure);
                    }

                    return method(context, thread, frame);
                },
            };
        }
    }

    private sealed class BlockingLoopOsrCompiler(
        ILuaLoopOsrCompiler inner,
        ManualResetEventSlim started,
        ManualResetEventSlim release) : ILuaLoopOsrCompiler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public LuaLoopOsrCompilationResult Compile(
            LuaIrModule module,
            LuaJitLoopOsrPlan plan,
            bool allowSpecializedCil,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10), cancellationToken));
            return inner.Compile(module, plan, allowSpecializedCil, cancellationToken);
        }
    }

    private sealed class FixedSizeLoopOsrCompiler(
        ILuaLoopOsrCompiler inner,
        long estimatedCodeBytes) : ILuaLoopOsrCompiler
    {
        public LuaLoopOsrCompilationResult Compile(
            LuaIrModule module,
            LuaJitLoopOsrPlan plan,
            bool allowSpecializedCil,
            CancellationToken cancellationToken)
        {
            var result = inner.Compile(
                module,
                plan,
                allowSpecializedCil,
                cancellationToken);
            return result.Succeeded
                ? result with { EstimatedCodeBytes = estimatedCodeBytes }
                : result;
        }
    }

    private sealed class InjectedGuardFailureLoopOsrCompiler(ILuaLoopOsrCompiler inner)
        : ILuaLoopOsrCompiler
    {
        private int _injectedFailures;

        public int InjectedFailures => Volatile.Read(ref _injectedFailures);

        public LuaLoopOsrCompilationResult Compile(
            LuaIrModule module,
            LuaJitLoopOsrPlan plan,
            bool allowSpecializedCil,
            CancellationToken cancellationToken)
        {
            var result = inner.Compile(
                module,
                plan,
                allowSpecializedCil,
                cancellationToken);
            if (!result.Succeeded)
            {
                return result;
            }

            var method = result.Method!;
            return result with
            {
                Method = (context, thread, frame) =>
                {
                    if (Interlocked.Exchange(ref _injectedFailures, 1) == 0)
                    {
                        return LuaCompiledExit.Deopt(
                            frame.ProgramCounter,
                            instructionsConsumed: 0,
                            LuaCompiledExitReason.GuardFailure);
                    }

                    return method(context, thread, frame);
                },
            };
        }
    }

    private sealed class ForcedManagedLoopOsrCompiler(ILuaLoopOsrCompiler inner)
        : ILuaLoopOsrCompiler
    {
        public LuaLoopOsrCompilationResult Compile(
            LuaIrModule module,
            LuaJitLoopOsrPlan plan,
            bool allowSpecializedCil,
            CancellationToken cancellationToken) => inner.Compile(
                module,
                plan,
                allowSpecializedCil: false,
                cancellationToken);
    }
}
