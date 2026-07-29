using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.Hosting.Tests;

public sealed class LuaGameLoopHostTests
{
    private static TaskCompletionSource<int>? _pendingTask;

    [Fact]
    public void ContractsAndReadResultsExposeTheirStableValues()
    {
        var inner = new InvalidOperationException("inner");
        var exception = new LuaGameLoopException(
            LuaGameLoopErrorCode.QueueLimitExceeded,
            "outer",
            inner);
        var missing = LuaGameLoopReadResult.Missing;
        var present = LuaGameLoopReadResult.FromValue(new byte[] { 1, 2, 3 });
        var failure = new LuaGameLoopFailure(null, exception.Code, exception.Message, exception);
        var tick = new LuaGameLoopTickResult(
            LuaGameLoopPhase.Update, 7, 3, 11, 1, 1, 1, [failure], 2);
        var drain = new LuaGameLoopDrainResult(false, 4, 2, 1);

        Assert.Same(inner, exception.InnerException);
        Assert.Equal(LuaGameLoopErrorCode.QueueLimitExceeded, exception.Code);
        Assert.False(missing.Found);
        Assert.True(missing.Value.IsEmpty);
        Assert.True(present.Found);
        Assert.Equal([1, 2, 3], present.Value.ToArray());
        Assert.Same(exception, failure.Exception);
        Assert.False(tick.Succeeded);
        Assert.Equal(7, tick.FrameNumber);
        Assert.False(drain.Completed);
        Assert.NotNull(LuaGameLoopStartOptions.Default);
        Assert.NotNull(LuaGameLoopHostOptions.Default);
    }

    [Fact]
    public void ConstructorRejectsEveryInvalidPortableSchedulingLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuaGameLoopHost(
            LuaGameLoopHostOptions.Default with { TimeProvider = new InvalidTimeProvider() }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuaGameLoopHost(
            LuaGameLoopHostOptions.Default with { MaximumCallbacksPerTick = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuaGameLoopHost(
            LuaGameLoopHostOptions.Default with { MaximumCallbacksPerTick = 1_000_001 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuaGameLoopHost(
            LuaGameLoopHostOptions.Default with { MaximumInstructionsPerTick = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuaGameLoopHost(
            LuaGameLoopHostOptions.Default with
            {
                MaximumInstructionsPerTick =
                    LuaInterpreterOptions.Default.MaximumInstructionCount + 1,
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuaGameLoopHost(
            LuaGameLoopHostOptions.Default with { MaximumQueuedWork = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuaGameLoopHost(
            LuaGameLoopHostOptions.Default with { MaximumQueuedWork = 1_000_001 }));
    }

    [Fact]
    public void ConstructorRejectsDispatcherOwnershipAndMismatchedTimerClock()
    {
        using var plainHost = new LuaHost();
        Assert.Throws<ArgumentException>(() => new LuaGameLoopHost(
            plainHost,
            LuaGameLoopHostOptions.Default with { Dispatcher = new RejectingDispatcher() }));

        var timerClock = new ManualTimeProvider();
        using var timerHost = new LuaHost(new LuaHostOptions
        {
            Clr = new LuaClrOptions
            {
                Capabilities = LuaClrCapabilities.Timers,
                TimeProvider = timerClock,
            },
        });
        Assert.Throws<ArgumentException>(() => new LuaGameLoopHost(
            timerHost,
            LuaGameLoopHostOptions.Default with { TimeProvider = new ManualTimeProvider() }));
    }

    [Fact]
    public void InvalidCompilationAndRawCallbackHaveBoundedFailures()
    {
        using var game = CreateGameLoop();
        var invalid = game.Host.CompileUtf8("local =");

        Assert.Throws<ArgumentException>(() => game.Start(invalid));
        Assert.Throws<ArgumentException>(() => game.StartCallback(LuaValue.Nil));
    }

    [Fact]
    public void StartRejectsUnknownPhaseAndResumePolicyValues()
    {
        using var game = CreateGameLoop();
        var compilation = game.Host.CompileUtf8("return true");

        Assert.Throws<ArgumentOutOfRangeException>(() => game.Start(
            compilation,
            options: new LuaGameLoopStartOptions { Phase = (LuaGameLoopPhase)byte.MaxValue }));
        Assert.Throws<ArgumentOutOfRangeException>(() => game.Start(
            compilation,
            options: new LuaGameLoopStartOptions
            {
                ResumePolicy = (LuaGameLoopResumePolicy)byte.MaxValue,
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            game.Tick((LuaGameLoopPhase)byte.MaxValue));
    }

    [Fact]
    public void ReentrantCancellationAndDisposalAreReportedWithoutMutatingTheHost()
    {
        using var game = CreateGameLoop();
        game.PublishAtFrameBoundary(_ => game.CancelAll());
        game.PublishAtFrameBoundary(_ => game.Dispose());

        var tick = game.Tick();

        Assert.Equal(2, tick.Failures.Length);
        Assert.All(tick.Failures, failure =>
            Assert.Equal(LuaGameLoopErrorCode.ReentrantTick, failure.Code));
        Assert.Equal(1, game.FrameNumber);
        Assert.Equal(0, game.PendingWorkCount);
    }

    [Fact]
    public void YieldedRootResumesOnLaterTicksAndReleasesItsHandle()
    {
        using var game = CreateGameLoop();
        var baselineHandles = game.Host.State.Heap.HandleCount;
        var compilation = game.Host.CompileUtf8(
            "trace=''; for i=1,3 do trace=trace..i; coroutine.yield(i) end; return 4");

        var operation = game.Start(compilation);

        Assert.Equal(LuaGameLoopOperationStatus.Pending, operation.Status);
        Assert.Equal(LuaGameLoopOperationStatus.Suspended, Tick(game, "1").Status);
        Assert.Equal(1L, operation.Values[0].AsInteger());
        Assert.Equal(LuaGameLoopOperationStatus.Suspended, Tick(game, "12").Status);
        Assert.Equal(LuaGameLoopOperationStatus.Suspended, Tick(game, "123").Status);
        Assert.Equal(LuaGameLoopOperationStatus.Completed, Tick(game, "123").Status);
        Assert.Equal(4L, operation.Values[0].AsInteger());
        Assert.Equal(baselineHandles, game.Host.State.Heap.HandleCount);

        LuaGameLoopOperation Tick(LuaGameLoopHost host, string expectedTrace)
        {
            var result = host.Tick();
            Assert.True(result.Succeeded, string.Join("; ", result.Failures.Select(f => f.Message)));
            Assert.Equal(expectedTrace, host.Host.State.GetGlobal("trace").AsString().ToString());
            return operation;
        }
    }

    [Fact]
    public void SelectedJitBackendUsesTheSameStartResumeAndCloseBoundary()
    {
        using var game = new LuaGameLoopHost(new LuaGameLoopHostOptions
        {
            HostOptions = new LuaHostOptions
            {
                ExecutionBackend = LuaHostExecutionBackend.Jit,
                Jit = LuaHostJitOptions.Default with
                {
                    SynchronousCompilation = true,
                    FunctionEntryThreshold = 1,
                },
            },
        });
        var operation = game.Start(game.Host.CompileUtf8(
            "local value=40; coroutine.yield(value); return value+2"));

        var yielded = game.Tick();
        var completed = game.Tick();

        Assert.True(yielded.Succeeded);
        Assert.True(completed.Succeeded);
        Assert.Equal(LuaHostExecutionBackend.Jit, game.Host.SelectedExecutionBackend);
        Assert.Equal(LuaGameLoopOperationStatus.Completed, operation.Status);
        Assert.Equal(42L, operation.Values[0].AsInteger());
    }

    [Fact]
    public void ManualResumeDeliversValuesAndCannotBeQueuedTwice()
    {
        using var game = CreateGameLoop();
        var operation = game.Start(
            game.Host.CompileUtf8("local value=coroutine.yield('waiting'); return value"),
            options: new LuaGameLoopStartOptions
            {
                ResumePolicy = LuaGameLoopResumePolicy.Manual,
            });

        game.Tick();
        Assert.Equal(LuaGameLoopOperationStatus.Suspended, operation.Status);
        game.Resume(operation, [LuaValue.FromInteger(42)]);
        Assert.Throws<LuaGameLoopException>(() => game.Resume(operation));

        var tick = game.Tick();

        Assert.True(tick.Succeeded);
        Assert.Equal(LuaGameLoopOperationStatus.Completed, operation.Status);
        Assert.Equal(42L, operation.Values[0].AsInteger());
    }

    [Fact]
    public void CallbackAndInstructionBudgetsBoundEachTick()
    {
        using var game = CreateGameLoop(maximumCallbacks: 1, maximumInstructions: 32);
        var first = game.Start(game.Host.CompileUtf8("first=true"));
        var second = game.Start(game.Host.CompileUtf8("second=true"));

        var tick1 = game.Tick();

        Assert.Equal(1, tick1.CallbackCount);
        Assert.InRange(tick1.ExecutedInstructionCount, 1, 32);
        Assert.Equal(1, tick1.RemainingQueuedWork);
        Assert.Equal(LuaGameLoopOperationStatus.Completed, first.Status);
        Assert.Equal(LuaGameLoopOperationStatus.Pending, second.Status);

        var tick2 = game.Tick();
        Assert.Equal(1, tick2.CallbackCount);
        Assert.Equal(LuaGameLoopOperationStatus.Completed, second.Status);
    }

    [Fact]
    public void InstructionBudgetFaultsRunawayWorkWithoutEscapingTheFrameLimit()
    {
        using var game = CreateGameLoop(maximumInstructions: 8);
        var operation = game.Start(game.Host.CompileUtf8("local x=0; while true do x=x+1 end"));

        var tick = game.Tick();

        Assert.Equal(1, tick.CallbackCount);
        Assert.InRange(tick.ExecutedInstructionCount, 1, 8);
        Assert.Single(tick.Failures);
        Assert.Equal(LuaGameLoopOperationStatus.Faulted, operation.Status);
        Assert.Contains("instruction budget", tick.Failures[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CallbackFailureAndReentrantTickDoNotAbortRemainingFrameWork()
    {
        using var game = CreateGameLoop();
        var failed = game.Start(game.Host.CompileUtf8("error('expected')"));
        var completed = game.Start(game.Host.CompileUtf8("survived=true"));
        game.PublishAtFrameBoundary(_ => game.Tick());

        var tick = game.Tick();

        Assert.Equal(2, tick.Failures.Length);
        Assert.Contains(tick.Failures, failure =>
            failure.Code == LuaGameLoopErrorCode.ReentrantTick);
        Assert.Equal(LuaGameLoopOperationStatus.Faulted, failed.Status);
        Assert.Equal(LuaGameLoopOperationStatus.Completed, completed.Status);
        Assert.True(game.Host.State.GetGlobal("survived").AsBoolean());
    }

    [Fact]
    public void FixedUpdateWorkRunsOnlyOnTheFixedBoundaryAndDrainIsBounded()
    {
        using var game = CreateGameLoop();
        var fixedOperation = game.Start(
            game.Host.CompileUtf8("fixed_ran=true"),
            options: new LuaGameLoopStartOptions
            {
                Phase = LuaGameLoopPhase.FixedUpdate,
            });

        Assert.Equal(0, game.Tick().CallbackCount);
        Assert.Equal(LuaGameLoopOperationStatus.Pending, fixedOperation.Status);
        Assert.Equal(1, game.TickFixed().CallbackCount);
        Assert.Equal(LuaGameLoopOperationStatus.Completed, fixedOperation.Status);

        var suspended = game.Start(
            game.Host.CompileUtf8("coroutine.yield()"),
            options: new LuaGameLoopStartOptions
            {
                ResumePolicy = LuaGameLoopResumePolicy.Manual,
            });
        game.Tick();
        var drain = game.Drain(3);

        Assert.False(drain.Completed);
        Assert.Equal(3, drain.TickCount);
        Assert.Equal(1, drain.ActiveOperationCount);
        game.Cancel(suspended);
        Assert.True(game.Drain(2).Completed);
    }

    [Fact]
    public void CancellationClosesSuspendedWorkAndQueuedResumesCannotReviveIt()
    {
        using var cancellation = new CancellationTokenSource();
        using var game = CreateGameLoop();
        var operation = game.Start(
            game.Host.CompileUtf8(
                "count=0; while true do count=count+1; coroutine.yield() end"),
            options: new LuaGameLoopStartOptions
            {
                CancellationToken = cancellation.Token,
            });
        game.Tick();
        Assert.Equal(1L, game.Host.State.GetGlobal("count").AsInteger());

        cancellation.Cancel();
        var cancellationTick = game.Tick();
        var afterCancellation = game.Tick();

        Assert.Equal(LuaGameLoopOperationStatus.Cancelled, operation.Status);
        Assert.Equal(1L, game.Host.State.GetGlobal("count").AsInteger());
        Assert.True(cancellationTick.CancelledOperationCount > 0);
        Assert.Equal(0, afterCancellation.CallbackCount);
    }

    [Fact]
    public void OwnerThreadViolationsHaveAStableErrorCode()
    {
        using var game = CreateGameLoop();

        LuaGameLoopException? exception = null;
        var thread = new Thread(() =>
            exception = Assert.Throws<LuaGameLoopException>(() => game.Tick()));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));

        Assert.Equal(LuaGameLoopErrorCode.WrongThread, exception!.Code);
    }

    [Fact]
    public void FrameBoundaryPublicationIsVisibleBeforeScheduledLuaWork()
    {
        using var game = CreateGameLoop();
        game.Host.State.SetGlobal("revision", LuaValue.FromInteger(1));
        var operation = game.Start(game.Host.CompileUtf8("return revision"));
        game.PublishAtFrameBoundary(host =>
            host.State.SetGlobal("revision", LuaValue.FromInteger(2)));

        Assert.Equal(1L, game.Host.State.GetGlobal("revision").AsInteger());
        var tick = game.Tick();

        Assert.True(tick.Succeeded);
        Assert.Equal(2L, operation.Values[0].AsInteger());
    }

    [Fact]
    public void DueTimerCallbackMayYieldAndResumeOnTheNextFrame()
    {
        var time = new ManualTimeProvider();
        using var game = CreateGameLoop(
            timeProvider: time,
            capabilities: LuaClrCapabilities.Timers,
            installClrModule: true);
        var setup = game.Start(game.Host.CompileUtf8(
            "trace=''; timer=clr.timer(function() " +
            "trace=trace..'a'; coroutine.yield(); trace=trace..'b' end,0)"));
        game.Tick();
        Assert.Equal(LuaGameLoopOperationStatus.Completed, setup.Status);

        var timerTick = game.Tick();
        Assert.True(timerTick.Succeeded, string.Join("; ", timerTick.Failures.Select(f => f.Message)));
        Assert.Equal("a", game.Host.State.GetGlobal("trace").AsString().ToString());

        var resumeTick = game.Tick();
        Assert.True(resumeTick.Succeeded);
        Assert.Equal("ab", game.Host.State.GetGlobal("trace").AsString().ToString());
    }

    [Fact]
    public void VoidClrDelegateCallbacksAreMarshalledAndMayYieldAcrossFrames()
    {
        var delegateName = typeof(Action<int>).FullName!;
        using var host = new LuaHost(new LuaHostOptions
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            Clr = new LuaClrOptions
            {
                Capabilities = LuaClrCapabilities.DelegateConversion,
                AllowedAssemblyNames = [typeof(Action<int>).Assembly.GetName().Name!],
                AllowedTypeNames = [delegateName],
                AllowedDelegateTypeNames = [delegateName],
                ThreadPolicy = LuaClrThreadPolicy.OwnerThreadOnly,
            },
        });
        using var game = new LuaGameLoopHost(host, new LuaGameLoopHostOptions
        {
            TimeProvider = host.ClrBridge.Options.TimeProvider,
        });
        var callbackValue = host.RunUtf8(
            "seen=0; return function(value) seen=value; coroutine.yield(); seen=seen+1 end")
            .Execution!.Values[0];
        var callback = (Action<int>)host.ClrBridge.CreateDelegate(callbackValue, delegateName);

        var thread = new Thread(() => callback(41));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(0L, host.State.GetGlobal("seen").AsInteger());

        var firstTick = game.Tick();
        Assert.True(firstTick.Succeeded, string.Join("; ", firstTick.Failures.Select(f => f.Message)));
        Assert.Equal(41L, host.State.GetGlobal("seen").AsInteger());
        Assert.Equal(1, game.PendingWorkCount);
        var secondTick = game.Tick();
        Assert.True(secondTick.Succeeded, string.Join("; ", secondTick.Failures.Select(f => f.Message)));
        Assert.Equal(1, secondTick.CallbackCount);
        Assert.Equal(42L, host.State.GetGlobal("seen").AsInteger());
    }

    [Fact]
    public void CompletedClrTaskResumesThroughTheUnifiedQueue()
    {
        var typeName = typeof(LuaGameLoopHostTests).FullName!;
        using var host = new LuaHost(new LuaHostOptions
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            Clr = new LuaClrOptions
            {
                Capabilities = LuaClrCapabilities.MemberAccess | LuaClrCapabilities.Async,
                AllowedAssemblyNames = [typeof(LuaGameLoopHostTests).Assembly.GetName().Name!],
                AllowedTypeNames = [typeName],
                AllowedMemberNames = [$"{typeName}.{nameof(GetPendingTask)}"],
            },
        });
        using var game = new LuaGameLoopHost(host, new LuaGameLoopHostOptions
        {
            TimeProvider = host.ClrBridge.Options.TimeProvider,
        });
        _pendingTask = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var taskValue = host.ClrBridge.InvokeStatic(typeName, nameof(GetPendingTask)).ReturnValue;
        var task = Assert.IsType<LuaClrTask>(taskValue.AsUserdata().Payload);
        var callback = host.RunUtf8(
            "result=0; return function(value) result=value; coroutine.yield(); result=result+1 end")
            .Execution!.Values[0];
        var operation = game.ContinueWith(task, callback);

        _pendingTask.SetResult(73);
        Assert.True(SpinWait.SpinUntil(() => game.PendingWorkCount != 0, TimeSpan.FromSeconds(5)));
        var tick = game.Tick();

        Assert.True(tick.Succeeded, string.Join("; ", tick.Failures.Select(f => f.Message)));
        Assert.Equal(LuaGameLoopOperationStatus.Suspended, operation.Status);
        Assert.Equal(73L, host.State.GetGlobal("result").AsInteger());
        var resume = game.Tick();
        Assert.True(resume.Succeeded);
        Assert.Equal(LuaGameLoopOperationStatus.Completed, operation.Status);
        Assert.Equal(74L, host.State.GetGlobal("result").AsInteger());
    }

    [Fact]
    public void CancellingTaskContinuationCancelsItsLinkedSourceAndDropsLateCompletion()
    {
        var typeName = typeof(LuaGameLoopHostTests).FullName!;
        using var host = CreateAsyncHost(typeName);
        using var game = new LuaGameLoopHost(host, new LuaGameLoopHostOptions
        {
            TimeProvider = host.ClrBridge.Options.TimeProvider,
        });
        using var cancellation = new LuaClrCancellation();
        _pendingTask = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var task = Assert.IsType<LuaClrTask>(
            host.ClrBridge.InvokeStatic(typeName, nameof(GetPendingTask))
                .ReturnValue.AsUserdata().Payload);
        var callback = host.RunUtf8("late=false; return function() late=true end")
            .Execution!.Values[0];
        var operation = game.ContinueWith(task, callback, cancellation);

        game.Cancel(operation);
        game.Tick();
        _pendingTask.SetResult(1);
        Assert.True(SpinWait.SpinUntil(() => task.IsCompleted, TimeSpan.FromSeconds(5)));
        game.Tick();

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(LuaGameLoopOperationStatus.Cancelled, operation.Status);
        Assert.False(host.State.GetGlobal("late").AsBoolean());
    }

    [Fact]
    public void InjectedEngineServicesRemainTheAuthoritativeInstances()
    {
        var time = new ManualTimeProvider();
        var assets = new TestAssetResolver();
        var store = new TestPersistentStore();
        var console = new LuaBufferedConsole();
        using var game = new LuaGameLoopHost(new LuaGameLoopHostOptions
        {
            TimeProvider = time,
            AssetResolver = assets,
            PersistentStore = store,
            Console = console,
            HostOptions = new LuaHostOptions
            {
                ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            },
        });

        Assert.Same(time, game.TimeProvider);
        Assert.Same(assets, game.AssetResolver);
        Assert.Same(store, game.PersistentStore);
        Assert.Same(console, game.Host.StandardLibraryOptions!.Console);
    }

    [Fact]
    public void OneHundredThousandTicksProduceTheSameDeterministicTrace()
    {
        const int tickCount = 100_000;
        using var first = CreateGameLoop();
        using var second = CreateGameLoop();
        var firstHandles = first.Host.State.Heap.HandleCount;
        var secondHandles = second.Host.State.Heap.HandleCount;
        const string source =
            "local value=0; for i=1,100000 do " +
            "value=(value*33+i)%1000000007; coroutine.yield(value) end; return value";
        var firstOperation = first.Start(first.Host.CompileUtf8(source));
        var secondOperation = second.Start(second.Host.CompileUtf8(source));
        long trace = 17;

        for (var index = 0; index < tickCount; index++)
        {
            var firstTick = first.Tick();
            var secondTick = second.Tick();
            Assert.True(firstTick.Succeeded);
            Assert.True(secondTick.Succeeded);
            var firstValue = firstOperation.Values[0].AsInteger();
            var secondValue = secondOperation.Values[0].AsInteger();
            Assert.Equal(firstValue, secondValue);
            trace = unchecked(trace * 31 + firstValue);
        }

        first.Tick();
        second.Tick();
        Assert.Equal(LuaGameLoopOperationStatus.Completed, firstOperation.Status);
        Assert.Equal(LuaGameLoopOperationStatus.Completed, secondOperation.Status);
        Assert.Equal(firstOperation.Values[0], secondOperation.Values[0]);
        Assert.Equal(firstHandles, first.Host.State.Heap.HandleCount);
        Assert.Equal(secondHandles, second.Host.State.Heap.HandleCount);
        Assert.NotEqual(17, trace);
    }

    [Fact]
    public void CancelAllInvalidatesPendingTaskCompletionsAndDisposeIsIdempotent()
    {
        var game = CreateGameLoop();
        var operation = game.Start(game.Host.CompileUtf8("coroutine.yield(); revived=true"));
        game.Tick();

        game.CancelAll();
        game.Tick();
        game.Dispose();
        game.Dispose();

        Assert.Equal(LuaGameLoopOperationStatus.Cancelled, operation.Status);
        Assert.True(game.Host.State.GetGlobal("revived").IsNil);
    }

    [Fact]
    public void CancelAllDisposesTimersAndRejectsCallbacksRegisteredByThePreviousScene()
    {
        var delegateName = typeof(Action).FullName!;
        var time = new ManualTimeProvider();
        using var host = new LuaHost(new LuaHostOptions
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            Clr = new LuaClrOptions
            {
                Capabilities = LuaClrCapabilities.DelegateConversion | LuaClrCapabilities.Timers,
                AllowedAssemblyNames = [typeof(Action).Assembly.GetName().Name!],
                AllowedTypeNames = [delegateName],
                AllowedDelegateTypeNames = [delegateName],
                TimeProvider = time,
            },
        });
        using var game = new LuaGameLoopHost(host, new LuaGameLoopHostOptions
        {
            TimeProvider = time,
        });
        var oldValue = host.RunUtf8("old_count=0; return function() old_count=old_count+1 end")
            .Execution!.Values[0];
        var oldCallback = (Action)host.ClrBridge.CreateDelegate(oldValue, delegateName);
        var timer = host.ClrBridge.ScheduleTimer(oldValue, new LuaClrTimerOptions
        {
            DueTime = TimeSpan.FromDays(1),
        });

        game.CancelAll();
        oldCallback();
        game.Tick();

        Assert.True(timer.IsDisposed);
        Assert.Equal(0L, host.State.GetGlobal("old_count").AsInteger());

        var newValue = host.RunUtf8("return function() old_count=old_count+10 end")
            .Execution!.Values[0];
        var newCallback = (Action)host.ClrBridge.CreateDelegate(newValue, delegateName);
        newCallback();
        game.Tick();
        Assert.Equal(10L, host.State.GetGlobal("old_count").AsInteger());
    }

    public static Task<int> GetPendingTask() => _pendingTask!.Task;

    private static LuaHost CreateAsyncHost(string typeName) => new(new LuaHostOptions
    {
        ExecutionBackend = LuaHostExecutionBackend.Interpreter,
        Clr = new LuaClrOptions
        {
            Capabilities = LuaClrCapabilities.MemberAccess | LuaClrCapabilities.Async,
            AllowedAssemblyNames = [typeof(LuaGameLoopHostTests).Assembly.GetName().Name!],
            AllowedTypeNames = [typeName],
            AllowedMemberNames = [$"{typeName}.{nameof(GetPendingTask)}"],
        },
    });

    private static LuaGameLoopHost CreateGameLoop(
        int maximumCallbacks = 32,
        long maximumInstructions = 100_000,
        TimeProvider? timeProvider = null,
        LuaClrCapabilities capabilities = LuaClrCapabilities.None,
        bool installClrModule = false)
    {
        timeProvider ??= TimeProvider.System;
        return new LuaGameLoopHost(new LuaGameLoopHostOptions
        {
            TimeProvider = timeProvider,
            MaximumCallbacksPerTick = maximumCallbacks,
            MaximumInstructionsPerTick = maximumInstructions,
            HostOptions = new LuaHostOptions
            {
                ExecutionBackend = LuaHostExecutionBackend.Interpreter,
                Execution = LuaInterpreterOptions.Default with
                {
                    MaximumInstructionCount = Math.Max(maximumInstructions, 100_000),
                },
                Clr = new LuaClrOptions
                {
                    Capabilities = capabilities,
                    InstallGlobalModule = installClrModule,
                    TimeProvider = timeProvider,
                    MaximumTimerDispatchCount = maximumCallbacks,
                },
            },
        });
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan value) => _timestamp += value.Ticks;
    }

    private sealed class InvalidTimeProvider : TimeProvider
    {
        public override long TimestampFrequency => 0;
    }

    private sealed class RejectingDispatcher : ILuaGameLoopDispatcher
    {
        public bool CheckAccess() => false;

        public void Post(Action callback) => throw new InvalidOperationException();
    }

    private sealed class TestAssetResolver : ILuaGameLoopAssetResolver
    {
        public ValueTask<LuaGameLoopReadResult> ResolveAsync(
            string assetId,
            CancellationToken cancellationToken = default) =>
            new(LuaGameLoopReadResult.Missing);
    }

    private sealed class TestPersistentStore : ILuaGameLoopPersistentStore
    {
        public ValueTask<LuaGameLoopReadResult> ReadAsync(
            string key,
            CancellationToken cancellationToken = default) =>
            new(LuaGameLoopReadResult.Missing);

        public ValueTask WriteAsync(
            string key,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
