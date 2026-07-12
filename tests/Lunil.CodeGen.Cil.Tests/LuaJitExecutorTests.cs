using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Lunil.CodeGen.Cil.Emission;
using Lunil.CodeGen.Cil.Jit;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.CodeGen.Cil.Tests;

public sealed class LuaJitExecutorTests
{
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
        Assert.Contains(events, jitEvent => jitEvent.Kind == LuaJitEventKind.Queued);
        Assert.Contains(events, jitEvent =>
            jitEvent.Kind == LuaJitEventKind.CompilationCompleted);
    }

    [Fact]
    public async Task AutoPolicyCompilesAsynchronouslyAfterTheHotThreshold()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
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
        var capabilities = new TestDynamicCodeCapabilities(false, false);
        var options = LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = 1,
            SynchronousCompilation = true,
        };
        using var executor = CreateExecutor(options, capabilities, compiler);
        var module = Compile("return 7");
        var state = new LuaState();

        var result = executor.Execute(state, state.CreateMainClosure(module));

        AssertValues(result, LuaValue.FromInteger(7));
        Assert.Equal(0, compiler.CallCount);
        Assert.False(executor.IsDynamicCodeAvailable);
        Assert.Equal(LuaJitFunctionState.Failed, executor.GetFunctionState(module, 0));
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
    public void HotTier1FunctionPromotesToProfileGuidedTier2()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            Tier2InvocationThreshold = 1,
            Tier2BackedgeThreshold = int.MaxValue,
        });
        var module = Compile(
            "local total = 0; for i = 1, 5 do total = total + i end; return total");

        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(15));
        AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(15));

        Assert.Equal(LuaJitCompilationTier.Tier2, executor.GetFunctionTier(module, 0));
        Assert.Equal(LuaJitTier2State.Ready, executor.GetTier2State(module, 0));
        var tier2Plan = Assert.IsType<LuaJitTier2Plan>(executor.GetTier2Plan(module, 0));
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
    }

    [Fact]
    public void Tier2GuardFailureRestoresCanonicalExecutionAndReprofiles()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            Tier2InvocationThreshold = 1,
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
    public void Tier2TablePicGuardsShapeAndPreservesLookupSemantics()
    {
        using var executor = CreateExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            Tier2InvocationThreshold = 1,
            Tier2BackedgeThreshold = int.MaxValue,
        });
        var module = Compile("local target, key = ...; return target[key]");

        ExecuteTableLookup(executor, module, extraKeys: 0, expected: LuaValue.Nil);
        ExecuteTableLookup(executor, module, extraKeys: 0, expected: LuaValue.Nil);

        var plan = Assert.IsType<LuaJitTier2Plan>(executor.GetTier2Plan(module, 0));
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

    private static LuaJitExecutor CreateExecutor(
        LuaJitExecutorOptions options,
        ILuaDynamicCodeCapabilities? capabilities = null,
        ILuaTier1Compiler? compiler = null,
        ILuaTier2Compiler? tier2Compiler = null) =>
        new(
            options,
            capabilities ?? new TestDynamicCodeCapabilities(true, true),
            compiler ?? ReflectionEmitLuaTier1Compiler.Instance,
            tier2Compiler ?? ProfileGuidedLuaTier2Compiler.Instance);

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
}
