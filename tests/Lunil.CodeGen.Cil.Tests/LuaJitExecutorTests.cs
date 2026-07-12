using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Lunil.CodeGen.Cil.Emission;
using Lunil.CodeGen.Cil.Jit;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime;
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

    private static LuaJitExecutor CreateExecutor(
        LuaJitExecutorOptions options,
        ILuaDynamicCodeCapabilities? capabilities = null,
        ILuaTier1Compiler? compiler = null) =>
        new(
            options,
            capabilities ?? new TestDynamicCodeCapabilities(true, true),
            compiler ?? ReflectionEmitLuaTier1Compiler.Instance);

    private static LuaExecutionResult ExecuteFresh(
        LuaJitExecutor executor,
        LuaIrModule module)
    {
        var state = new LuaState();
        return executor.Execute(state, state.CreateMainClosure(module));
    }

    private static void AssertValues(
        LuaExecutionResult result,
        params LuaValue[] expected) =>
        Assert.True(result.Values.SequenceEqual(expected));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CompileAndReleaseOwners(LuaJitExecutor executor)
    {
        var module = Compile("return 11");
        var state = new LuaState();
        var closure = state.CreateMainClosure(module);
        AssertValues(executor.Execute(state, closure), LuaValue.FromInteger(11));
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
}
