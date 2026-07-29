using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Lunil.Compiler;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;
using Lunil.StandardLibrary;

namespace Lunil.Hosting;

/// <summary>
/// Engine-neutral owner-thread scheduler for frame callbacks, Lua coroutines, CLR continuations,
/// timers, cancellation, and frame-boundary publication.
/// </summary>
public sealed class LuaGameLoopHost : IDisposable
{
    private readonly object _gate = new();
    private readonly ConcurrentQueue<QueuedWork> _work = new();
    private readonly ConcurrentQueue<BoundaryPublication> _publications = new();
    private readonly Dictionary<long, LuaGameLoopOperation> _operations = [];
    private readonly ConditionalWeakTable<LuaClrCallbackRegistration, CallbackGeneration>
        _callbackGenerations = new();
    private readonly object _callbackGenerationGate = new();
    private readonly ILuaGameLoopDispatcher _dispatcher;
    private readonly CallbackSchedulerAdapter _callbackScheduler;
    private readonly int _ownerThreadId;
    private readonly bool _ownsHost;
    private long _nextSequence;
    private long _nextOperationId;
    private long _generation = 1;
    private long _frameNumber;
    private int _queuedWorkCount;
    private int _disposed;
    private bool _isTicking;

    public LuaGameLoopHost(LuaGameLoopHostOptions? options = null)
        : this(CreateOwnedHost(options ?? LuaGameLoopHostOptions.Default), ownsHost: true)
    {
    }

    public LuaGameLoopHost(
        LuaHost host,
        LuaGameLoopHostOptions? options = null,
        bool ownsHost = false)
        : this(new HostCreation(host, options ?? LuaGameLoopHostOptions.Default), ownsHost)
    {
    }

    private LuaGameLoopHost(HostCreation creation, bool ownsHost)
    {
        Host = creation.Host;
        _ownsHost = ownsHost;
        try
        {
            Options = ValidateOptions(creation.Options, Host);
        }
        catch
        {
            if (ownsHost)
            {
                Host.Dispose();
            }

            throw;
        }

        _ownerThreadId = Environment.CurrentManagedThreadId;
        _dispatcher = Options.Dispatcher ?? new InlineDispatcher(_ownerThreadId);
        _callbackScheduler = new CallbackSchedulerAdapter(this);
        if (!_dispatcher.CheckAccess())
        {
            throw new ArgumentException(
                "The game-loop dispatcher must recognize the construction thread as its owner.",
                nameof(creation));
        }

        Host.ClrBridge.AttachCallbackScheduler(_callbackScheduler);
    }

    public LuaGameLoopHostOptions Options { get; }

    public LuaHost Host { get; }

    public TimeProvider TimeProvider => Options.TimeProvider;

    public ILuaGameLoopAssetResolver? AssetResolver => Options.AssetResolver;

    public ILuaGameLoopPersistentStore? PersistentStore => Options.PersistentStore;

    public int OwnerThreadId => _ownerThreadId;

    public long FrameNumber => Interlocked.Read(ref _frameNumber);

    public int PendingWorkCount => Volatile.Read(ref _queuedWorkCount);

    public int ActiveOperationCount
    {
        get
        {
            lock (_gate)
            {
                return _operations.Count;
            }
        }
    }

    public LuaGameLoopOperation Start(
        LuaCompilationResult compilation,
        ReadOnlySpan<LuaValue> arguments = default,
        LuaGameLoopStartOptions? options = null)
    {
        LunilGuard.NotNull(compilation);
        if (!compilation.Succeeded || compilation.Module is null)
        {
            throw new ArgumentException(
                "The compilation must succeed before it can be scheduled.",
                nameof(compilation));
        }

        return Start(compilation.Module, arguments, options);
    }

    public LuaGameLoopOperation Start(
        LuaIrModule module,
        ReadOnlySpan<LuaValue> arguments = default,
        LuaGameLoopStartOptions? options = null)
    {
        LunilGuard.NotNull(module);
        EnsureOwnerThread();
        ThrowIfDisposed();
        return CreateAndQueueOperation(
            LuaValue.FromFunction(Host.State.CreateMainClosure(module)),
            arguments,
            options ?? LuaGameLoopStartOptions.Default);
    }

    public LuaGameLoopOperation StartCallback(
        LuaValue callback,
        ReadOnlySpan<LuaValue> arguments = default,
        LuaGameLoopStartOptions? options = null)
    {
        EnsureOwnerThread();
        ThrowIfDisposed();
        return CreateAndQueueOperation(
            callback,
            arguments,
            options ?? LuaGameLoopStartOptions.Default);
    }

    public LuaGameLoopOperation ContinueWith(
        LuaClrTask task,
        LuaValue callback,
        LuaClrCancellation? cancellation = null,
        LuaGameLoopStartOptions? options = null)
    {
        LunilGuard.NotNull(task);
        EnsureOwnerThread();
        ThrowIfDisposed();
        if (!ReferenceEquals(task.Bridge, Host.ClrBridge))
        {
            throw new ArgumentException(
                "The CLR task belongs to a different Lua host.",
                nameof(task));
        }

        var configured = options ?? LuaGameLoopStartOptions.Default;
        ValidateStartOptions(configured);
        var operation = CreateOperation(callback, configured);
        operation.LinkedCancellation = cancellation;
        RegisterCancellation(operation, configured.CancellationToken);
        _ = task.Task.ContinueWith(
            static (_, state) =>
            {
                var continuation = (TaskContinuationState)state!;
                continuation.Host.QueueThroughDispatcher(
                    new QueuedWork(
                        continuation.Host.NextSequence(),
                        QueuedWorkKind.TaskCompletion,
                        continuation.Operation.Phase,
                        continuation.Operation.Generation,
                        continuation.Operation,
                        [],
                        [],
                        Task: continuation.Task));
            },
            new TaskContinuationState(this, operation, task),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return operation;
    }

    public void Resume(
        LuaGameLoopOperation operation,
        ReadOnlySpan<LuaValue> arguments = default)
    {
        LunilGuard.NotNull(operation);
        EnsureOwnerThread();
        ThrowIfDisposed();
        EnsureOwnedOperation(operation);
        if (operation.Status != LuaGameLoopOperationStatus.Suspended)
        {
            throw new LuaGameLoopException(
                LuaGameLoopErrorCode.InvalidOperationState,
                "Only a suspended game-loop operation can be resumed.");
        }

        QueueOperationExecution(operation, QueuedWorkKind.Resume, arguments);
    }

    public void Cancel(LuaGameLoopOperation operation)
    {
        LunilGuard.NotNull(operation);
        EnsureOwnerThread();
        ThrowIfDisposed();
        EnsureOwnedOperation(operation);
        RequestCancellation(operation);
    }

    /// <summary>Cancels all current scene work and invalidates queued completions.</summary>
    public void CancelAll()
    {
        EnsureOwnerThread();
        ThrowIfDisposed();
        lock (_gate)
        {
            if (_isTicking)
            {
                throw new LuaGameLoopException(
                    LuaGameLoopErrorCode.ReentrantTick,
                    "All game-loop work cannot be cancelled from inside an active tick.");
            }

            _generation = checked(_generation + 1);
            foreach (var operation in _operations.Values.ToArray())
            {
                operation.RequestCancellation();
                CloseOperation(operation, LuaGameLoopOperationStatus.Cancelled, null);
            }

            ClearQueuedWork();
            Host.ClrBridge.DisposeTimers();
            while (_publications.TryDequeue(out _))
            {
            }
        }
    }

    /// <summary>Queues an existing patch prepare/commit action for the next frame boundary.</summary>
    public void PublishAtFrameBoundary(Action<LuaHost> publication)
    {
        LunilGuard.NotNull(publication);
        ThrowIfDisposed();
        var item = new BoundaryPublication(
            NextSequence(),
            Volatile.Read(ref _generation),
            publication);
        if (_dispatcher.CheckAccess())
        {
            _publications.Enqueue(item);
        }
        else
        {
            _dispatcher.Post(() =>
            {
                if (Volatile.Read(ref _disposed) == 0 &&
                    item.Generation == Volatile.Read(ref _generation))
                {
                    _publications.Enqueue(item);
                }
            });
        }
    }

    public LuaGameLoopTickResult Tick() => Tick(LuaGameLoopPhase.Update);

    public LuaGameLoopTickResult TickFixed() => Tick(LuaGameLoopPhase.FixedUpdate);

    public LuaGameLoopTickResult Tick(LuaGameLoopPhase phase)
    {
        EnsureOwnerThread();
        ThrowIfDisposed();
        ValidatePhase(phase);
        lock (_gate)
        {
            if (_isTicking)
            {
                throw new LuaGameLoopException(
                    LuaGameLoopErrorCode.ReentrantTick,
                    "A game-loop tick cannot re-enter the same host.");
            }

            _isTicking = true;
            try
            {
                var frame = checked(++_frameNumber);
                var failures = ImmutableArray.CreateBuilder<LuaGameLoopFailure>();
                PublishBoundary(failures);
                if (phase == LuaGameLoopPhase.Update &&
                    (Host.ClrBridge.Options.Capabilities & LuaClrCapabilities.Timers) != 0)
                {
                    try
                    {
                        Host.ClrBridge.DispatchTimersForGameLoop(
                            Math.Min(
                                Options.MaximumCallbacksPerTick,
                                Host.ClrBridge.Options.MaximumTimerDispatchCount),
                            (callback, arguments) => QueueMaterializedCallback(
                                callback,
                                arguments,
                                LuaGameLoopPhase.Update));
                    }
                    catch (Exception exception) when (IsRecoverable(exception))
                    {
                        failures.Add(new LuaGameLoopFailure(
                            null,
                            null,
                            exception.Message,
                            exception));
                    }
                }

                var batch = TakeWorkSnapshot();
                var callbacks = 0;
                var instructions = 0L;
                var completed = 0;
                var suspended = 0;
                var cancelled = 0;
                for (var index = 0; index < batch.Count; index++)
                {
                    var item = batch[index];
                    if (item.Phase != phase)
                    {
                        Requeue(item);
                        continue;
                    }

                    if (item.Kind != QueuedWorkKind.Cancel &&
                        (callbacks >= Options.MaximumCallbacksPerTick ||
                            instructions >= Options.MaximumInstructionsPerTick))
                    {
                        Requeue(item);
                        for (var remaining = index + 1; remaining < batch.Count; remaining++)
                        {
                            Requeue(batch[remaining]);
                        }

                        break;
                    }

                    ProcessWork(
                        item,
                        Options.MaximumInstructionsPerTick - instructions,
                        failures,
                        ref callbacks,
                        ref instructions,
                        ref completed,
                        ref suspended,
                        ref cancelled);
                }

                return new LuaGameLoopTickResult(
                    phase,
                    frame,
                    callbacks,
                    instructions,
                    completed,
                    suspended,
                    cancelled,
                    failures.ToImmutable(),
                    PendingWorkCount);
            }
            finally
            {
                _isTicking = false;
            }
        }
    }

    public LuaGameLoopDrainResult Drain(int maximumTicks = 1_024)
    {
        EnsureOwnerThread();
        ThrowIfDisposed();
        LunilGuard.Positive(maximumTicks);

        var ticks = 0;
        while (ticks < maximumTicks && (PendingWorkCount != 0 || ActiveOperationCount != 0))
        {
            Tick(LuaGameLoopPhase.Update);
            ticks++;
            if (PendingWorkCount != 0 && ticks < maximumTicks)
            {
                Tick(LuaGameLoopPhase.FixedUpdate);
                ticks++;
            }
        }

        return new LuaGameLoopDrainResult(
            PendingWorkCount == 0 && ActiveOperationCount == 0,
            ticks,
            PendingWorkCount,
            ActiveOperationCount);
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        EnsureOwnerThread();
        lock (_gate)
        {
            if (_isTicking)
            {
                throw new LuaGameLoopException(
                    LuaGameLoopErrorCode.ReentrantTick,
                    "A game-loop host cannot be disposed from inside an active tick.");
            }

            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Host.ClrBridge.DetachCallbackScheduler(_callbackScheduler);
            _generation = checked(_generation + 1);
            foreach (var operation in _operations.Values.ToArray())
            {
                operation.RequestCancellation();
                CloseOperation(operation, LuaGameLoopOperationStatus.Cancelled, null);
            }

            ClearQueuedWork();
            Host.ClrBridge.DisposeTimers();
            while (_publications.TryDequeue(out _))
            {
            }

            if (_ownsHost)
            {
                Host.Dispose();
            }
        }
    }

    private void RegisterCallback(LuaClrCallbackRegistration registration)
    {
        lock (_callbackGenerationGate)
        {
            _callbackGenerations.Remove(registration);
            _callbackGenerations.Add(
                registration,
                new CallbackGeneration(Volatile.Read(ref _generation)));
        }
    }

    private void ScheduleCallback(
        LuaClrCallbackRegistration registration,
        object?[] arguments)
    {
        long generation;
        lock (_callbackGenerationGate)
        {
            if (!_callbackGenerations.TryGetValue(registration, out var registered))
            {
                registered = new CallbackGeneration(Volatile.Read(ref _generation));
                _callbackGenerations.Add(registration, registered);
            }

            generation = registered.Value;
        }

        var item = new QueuedWork(
            NextSequence(),
            QueuedWorkKind.ClrCallback,
            LuaGameLoopPhase.Update,
            generation,
            Operation: null,
            Arguments: [],
            Roots: [],
            CallbackRegistration: registration,
            CallbackArguments: arguments);
        QueueThroughDispatcher(item);
    }

    private static HostCreation CreateOwnedHost(LuaGameLoopHostOptions options)
    {
        LunilGuard.NotNull(options);
        LunilGuard.NotNull(options.HostOptions);
        LunilGuard.NotNull(options.TimeProvider);
        var hostOptions = options.HostOptions with
        {
            ModuleResolver = options.ModuleResolver ?? options.HostOptions.ModuleResolver,
            Clr = options.HostOptions.Clr with { TimeProvider = options.TimeProvider },
        };
        if (options.Console is not null && hostOptions.InstallStandardLibrary)
        {
            hostOptions = hostOptions with
            {
                StandardLibrary = (hostOptions.StandardLibrary ??
                    LuaHostCapabilityProfiles.Create(hostOptions.Profile)) with
                {
                    Console = options.Console,
                },
            };
        }

        return new HostCreation(new LuaHost(hostOptions), options);
    }

    private static LuaGameLoopHostOptions ValidateOptions(
        LuaGameLoopHostOptions options,
        LuaHost host)
    {
        LunilGuard.NotNull(options);
        LunilGuard.NotNull(options.TimeProvider);
        if (options.TimeProvider.TimestampFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The monotonic time provider must expose a positive timestamp frequency.");
        }

        if (options.MaximumCallbacksPerTick is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The per-tick callback limit must be between 1 and 1000000.");
        }

        if (options.MaximumInstructionsPerTick < 1 ||
            options.MaximumInstructionsPerTick > host.Options.Execution.MaximumInstructionCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The per-tick instruction limit must be positive and no greater than the " +
                "Lua host execution limit.");
        }

        if (options.MaximumQueuedWork is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The queued-work limit must be between 1 and 1000000.");
        }

        if ((host.ClrBridge.Options.Capabilities & LuaClrCapabilities.Timers) != 0 &&
            !ReferenceEquals(options.TimeProvider, host.ClrBridge.Options.TimeProvider))
        {
            throw new ArgumentException(
                "The game-loop and CLR timer clock must be the same TimeProvider instance.",
                nameof(options));
        }

        return options;
    }

    private LuaGameLoopOperation CreateAndQueueOperation(
        LuaValue callback,
        ReadOnlySpan<LuaValue> arguments,
        LuaGameLoopStartOptions options)
    {
        ValidateStartOptions(options);
        var operation = CreateOperation(callback, options);
        RegisterCancellation(operation, options.CancellationToken);
        try
        {
            QueueOperationExecution(operation, QueuedWorkKind.Start, arguments);
        }
        catch
        {
            CloseOperation(operation, LuaGameLoopOperationStatus.Faulted, null);
            throw;
        }

        return operation;
    }

    private LuaGameLoopOperation CreateOperation(
        LuaValue callback,
        LuaGameLoopStartOptions options)
    {
        var closure = callback.TryGetClosure() ?? throw new ArgumentException(
            "A Lua closure is required for game-loop work.",
            nameof(callback));
        Host.State.Heap.ValidateValue(callback);
        var operation = new LuaGameLoopOperation(
            checked(++_nextOperationId),
            _generation,
            options.Phase,
            options.ResumePolicy);
        operation.Thread = Host.State.CreateThread(closure);
        operation.ThreadHandle = Host.State.CreateHandle(LuaValue.FromThread(operation.Thread));
        _operations.Add(operation.Id, operation);
        return operation;
    }

    private void RegisterCancellation(
        LuaGameLoopOperation operation,
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return;
        }

        operation.CancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var cancellation = (CancellationState)state!;
                cancellation.Operation.RequestCancellation();
                cancellation.Host.QueueCancellationFromAnyThread(cancellation.Operation);
            },
            new CancellationState(this, operation));
    }

    private void QueueOperationExecution(
        LuaGameLoopOperation operation,
        QueuedWorkKind kind,
        ReadOnlySpan<LuaValue> arguments)
    {
        if (!operation.TryQueueExecution())
        {
            throw new LuaGameLoopException(
                LuaGameLoopErrorCode.InvalidOperationState,
                "The operation already has a queued execution turn.");
        }

        var values = arguments.ToArray();
        LuaHandle[] roots;
        try
        {
            roots = values.Select(Host.State.CreateHandle).ToArray();
        }
        catch
        {
            operation.ClearQueuedExecution();
            throw;
        }

        var item = new QueuedWork(
            NextSequence(),
            kind,
            operation.Phase,
            operation.Generation,
            operation,
            values,
            roots);
        try
        {
            Enqueue(item, enforceLimit: true);
        }
        catch
        {
            DisposeRoots(roots);
            operation.ClearQueuedExecution();
            throw;
        }
    }

    private void QueueMaterializedCallback(
        LuaValue callback,
        ReadOnlySpan<LuaValue> arguments,
        LuaGameLoopPhase phase)
    {
        var operation = CreateOperation(
            callback,
            new LuaGameLoopStartOptions
            {
                Phase = phase,
                ResumePolicy = LuaGameLoopResumePolicy.NextTick,
            });
        try
        {
            QueueOperationExecution(operation, QueuedWorkKind.Start, arguments);
        }
        catch
        {
            CloseOperation(operation, LuaGameLoopOperationStatus.Faulted, null);
            throw;
        }
    }

    private void QueueThroughDispatcher(QueuedWork item)
    {
        if (_dispatcher.CheckAccess())
        {
            if (Volatile.Read(ref _disposed) == 0 &&
                item.Generation == Volatile.Read(ref _generation))
            {
                Enqueue(item, enforceLimit: true);
            }

            return;
        }

        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) == 0 &&
                item.Generation == Volatile.Read(ref _generation) &&
                item.Operation?.IsTerminal != true)
            {
                Enqueue(item, enforceLimit: true);
            }
        });
    }

    private void QueueCancellationFromAnyThread(LuaGameLoopOperation operation)
    {
        var item = new QueuedWork(
            NextSequence(),
            QueuedWorkKind.Cancel,
            operation.Phase,
            operation.Generation,
            operation,
            [],
            []);
        if (_dispatcher.CheckAccess())
        {
            if (Volatile.Read(ref _disposed) == 0 &&
                operation.Generation == Volatile.Read(ref _generation) &&
                !operation.IsTerminal)
            {
                Enqueue(item, enforceLimit: false);
            }
        }
        else
        {
            _dispatcher.Post(() =>
            {
                if (Volatile.Read(ref _disposed) == 0 &&
                    operation.Generation == Volatile.Read(ref _generation) &&
                    !operation.IsTerminal)
                {
                    Enqueue(item, enforceLimit: false);
                }
            });
        }
    }

    private void RequestCancellation(LuaGameLoopOperation operation)
    {
        operation.RequestCancellation();
        QueueCancellationFromAnyThread(operation);
    }

    private void Enqueue(QueuedWork item, bool enforceLimit)
    {
        var count = Interlocked.Increment(ref _queuedWorkCount);
        if (enforceLimit && count > Options.MaximumQueuedWork)
        {
            Interlocked.Decrement(ref _queuedWorkCount);
            throw new LuaGameLoopException(
                LuaGameLoopErrorCode.QueueLimitExceeded,
                "The game-loop queued-work limit was reached.");
        }

        _work.Enqueue(item);
    }

    private List<QueuedWork> TakeWorkSnapshot()
    {
        var count = Volatile.Read(ref _queuedWorkCount);
        var batch = new List<QueuedWork>(Math.Min(count, Options.MaximumQueuedWork));
        for (var index = 0; index < count && _work.TryDequeue(out var item); index++)
        {
            Interlocked.Decrement(ref _queuedWorkCount);
            batch.Add(item);
        }

        batch.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
        return batch;
    }

    private void Requeue(QueuedWork item)
    {
        Interlocked.Increment(ref _queuedWorkCount);
        _work.Enqueue(item);
    }

    private void ProcessWork(
        QueuedWork item,
        long remainingInstructionBudget,
        ImmutableArray<LuaGameLoopFailure>.Builder failures,
        ref int callbacks,
        ref long instructions,
        ref int completed,
        ref int suspended,
        ref int cancelled)
    {
        var operation = item.Operation;
        try
        {
            if (item.Generation != _generation)
            {
                if (operation is { IsTerminal: false })
                {
                    CloseOperation(operation, LuaGameLoopOperationStatus.Stale, null);
                }

                return;
            }

            if (item.Kind == QueuedWorkKind.ClrCallback)
            {
                var materialized = Host.ClrBridge.MaterializeScheduledCallback(
                    item.CallbackRegistration!,
                    item.CallbackArguments!);
                operation = CreateOperation(
                    materialized.Callback,
                    new LuaGameLoopStartOptions
                    {
                        Phase = item.Phase,
                        ResumePolicy = LuaGameLoopResumePolicy.NextTick,
                    });
                ExecuteOperation(
                    operation,
                    start: true,
                    materialized.Arguments,
                    remainingInstructionBudget,
                    failures,
                    ref callbacks,
                    ref instructions,
                    ref completed,
                    ref suspended,
                    ref cancelled);
                return;
            }

            if (operation is null || operation.IsTerminal)
            {
                return;
            }

            if (item.Kind == QueuedWorkKind.Cancel || operation.IsCancellationRequested)
            {
                operation.ClearQueuedExecution();
                CloseOperation(operation, LuaGameLoopOperationStatus.Cancelled, null);
                cancelled++;
                return;
            }

            if (item.Kind == QueuedWorkKind.TaskCompletion)
            {
                var result = Host.ClrBridge.GetCompletedTaskResult(item.Task!);
                ExecuteOperation(
                    operation,
                    start: true,
                    [result],
                    remainingInstructionBudget,
                    failures,
                    ref callbacks,
                    ref instructions,
                    ref completed,
                    ref suspended,
                    ref cancelled);
                return;
            }

            operation.ClearQueuedExecution();
            ExecuteOperation(
                operation,
                item.Kind == QueuedWorkKind.Start,
                item.Arguments,
                remainingInstructionBudget,
                failures,
                ref callbacks,
                ref instructions,
                ref completed,
                ref suspended,
                ref cancelled);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            if (operation is { IsTerminal: false })
            {
                CloseOperation(operation, LuaGameLoopOperationStatus.Faulted, exception);
            }

            failures.Add(new LuaGameLoopFailure(
                operation,
                exception is LuaGameLoopException gameLoop ? gameLoop.Code : null,
                exception.Message,
                exception));
        }
        finally
        {
            DisposeRoots(item.Roots);
        }
    }

    private void ExecuteOperation(
        LuaGameLoopOperation operation,
        bool start,
        ReadOnlySpan<LuaValue> arguments,
        long instructionBudget,
        ImmutableArray<LuaGameLoopFailure>.Builder failures,
        ref int callbacks,
        ref long instructions,
        ref int completed,
        ref int suspended,
        ref int cancelled)
    {
        if (operation.IsCancellationRequested)
        {
            CloseOperation(operation, LuaGameLoopOperationStatus.Cancelled, null);
            cancelled++;
            return;
        }

        if (!operation.Thread.IsPatchGenerationActive)
        {
            var exception = new LuaGameLoopException(
                LuaGameLoopErrorCode.StaleGeneration,
                "The Lua coroutine belongs to an inactive patch generation.");
            CloseOperation(operation, LuaGameLoopOperationStatus.Stale, exception);
            failures.Add(new LuaGameLoopFailure(
                operation,
                exception.Code,
                exception.Message,
                exception));
            return;
        }

        operation.SetStatus(LuaGameLoopOperationStatus.Running);
        var result = start
            ? Host.StartThread(operation.Thread, instructionBudget, arguments)
            : Host.ResumeThread(operation.Thread, instructionBudget, arguments);
        callbacks++;
        instructions = checked(instructions + result.ExecutedInstructionCount);
        operation.SetValues(result.Values);
        switch (result.Signal)
        {
            case LuaVmSignal.Completed:
                CloseOperation(operation, LuaGameLoopOperationStatus.Completed, null, closeThread: false);
                completed++;
                break;
            case LuaVmSignal.Yielded:
                operation.SetStatus(LuaGameLoopOperationStatus.Suspended);
                suspended++;
                if (operation.ResumePolicy == LuaGameLoopResumePolicy.NextTick)
                {
                    QueueOperationExecution(operation, QueuedWorkKind.Resume, []);
                }
                break;
            case LuaVmSignal.Error:
                var exception = new LuaRuntimeException(
                    result.Values.IsEmpty ? LuaValue.Nil : result.Values[0]);
                CloseOperation(operation, LuaGameLoopOperationStatus.Faulted, exception);
                failures.Add(new LuaGameLoopFailure(
                    operation,
                    null,
                    exception.Message,
                    exception));
                break;
            default:
                throw new InvalidOperationException("The Lua VM signal is invalid.");
        }
    }

    private void CloseOperation(
        LuaGameLoopOperation operation,
        LuaGameLoopOperationStatus status,
        Exception? exception,
        bool closeThread = true)
    {
        if (operation.IsTerminal)
        {
            return;
        }

        Exception? closeFailure = null;
        if (closeThread && operation.HasThread &&
            operation.Thread.Status is not LuaThreadStatus.Dead)
        {
            try
            {
                var close = Host.CloseThread(operation.Thread);
                if (close.Signal == LuaVmSignal.Error)
                {
                    closeFailure = new LuaRuntimeException(
                        close.Values.IsEmpty ? LuaValue.Nil : close.Values[0]);
                }
            }
            catch (Exception candidate) when (IsRecoverable(candidate))
            {
                closeFailure = candidate;
            }
        }

        operation.Exception = exception ?? closeFailure;
        operation.SetStatus(closeFailure is not null && status != LuaGameLoopOperationStatus.Stale
            ? LuaGameLoopOperationStatus.Faulted
            : status);
        operation.CancellationRegistration.Dispose();
        operation.ThreadHandle?.Dispose();
        _operations.Remove(operation.Id);
    }

    private void PublishBoundary(ImmutableArray<LuaGameLoopFailure>.Builder failures)
    {
        var publications = new List<BoundaryPublication>();
        while (_publications.TryDequeue(out var publication))
        {
            publications.Add(publication);
        }

        publications.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
        foreach (var publication in publications)
        {
            if (publication.Generation != _generation)
            {
                continue;
            }

            try
            {
                publication.Action(Host);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                failures.Add(new LuaGameLoopFailure(
                    null,
                    exception is LuaGameLoopException gameLoop ? gameLoop.Code : null,
                    exception.Message,
                    exception));
            }
        }
    }

    private void EnsureOwnedOperation(LuaGameLoopOperation operation)
    {
        if (operation.Generation != _generation || !_operations.ContainsKey(operation.Id))
        {
            throw new LuaGameLoopException(
                LuaGameLoopErrorCode.InvalidOperationState,
                "The operation is not active in this game-loop generation.");
        }
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId || !_dispatcher.CheckAccess())
        {
            throw new LuaGameLoopException(
                LuaGameLoopErrorCode.WrongThread,
                "Game-loop state may only be advanced from its owner thread.");
        }
    }

    private void ThrowIfDisposed() =>
        LunilGuard.NotDisposed(Volatile.Read(ref _disposed) != 0, this);

    private long NextSequence() => Interlocked.Increment(ref _nextSequence);

    private void ClearQueuedWork()
    {
        while (_work.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _queuedWorkCount);
            DisposeRoots(item.Roots);
        }
    }

    private static void DisposeRoots(IEnumerable<LuaHandle> roots)
    {
        foreach (var root in roots)
        {
            root.Dispose();
        }
    }

    private static void ValidateStartOptions(LuaGameLoopStartOptions options)
    {
        LunilGuard.NotNull(options);
        ValidatePhase(options.Phase);
        if (!LunilEnum.IsDefined(options.ResumePolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The resume policy is invalid.");
        }
    }

    private static void ValidatePhase(LuaGameLoopPhase phase)
    {
        if (!LunilEnum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }
    }

    private static bool IsRecoverable(Exception exception) => exception is not
        OutOfMemoryException and not StackOverflowException and not AccessViolationException;

    private sealed class InlineDispatcher : ILuaGameLoopDispatcher
    {
        private readonly int _ownerThreadId;

        public InlineDispatcher(int ownerThreadId) => _ownerThreadId = ownerThreadId;

        public bool CheckAccess() => Environment.CurrentManagedThreadId == _ownerThreadId;

        public void Post(Action callback)
        {
            LunilGuard.NotNull(callback);
            callback();
        }
    }

    private sealed class CallbackSchedulerAdapter : ILuaClrCallbackScheduler
    {
        private readonly LuaGameLoopHost _host;

        public CallbackSchedulerAdapter(LuaGameLoopHost host) => _host = host;

        public void Register(LuaClrCallbackRegistration registration) =>
            _host.RegisterCallback(registration);

        public void Schedule(LuaClrCallbackRegistration registration, object?[] arguments) =>
            _host.ScheduleCallback(registration, arguments);
    }

    private enum QueuedWorkKind : byte
    {
        Start,
        Resume,
        ClrCallback,
        TaskCompletion,
        Cancel,
    }

    private sealed record QueuedWork(
        long Sequence,
        QueuedWorkKind Kind,
        LuaGameLoopPhase Phase,
        long Generation,
        LuaGameLoopOperation? Operation,
        LuaValue[] Arguments,
        LuaHandle[] Roots,
        LuaClrCallbackRegistration? CallbackRegistration = null,
        object?[]? CallbackArguments = null,
        LuaClrTask? Task = null);

    private sealed record BoundaryPublication(
        long Sequence,
        long Generation,
        Action<LuaHost> Action);

    private sealed record HostCreation(LuaHost Host, LuaGameLoopHostOptions Options);

    private sealed record CancellationState(
        LuaGameLoopHost Host,
        LuaGameLoopOperation Operation);

    private sealed record TaskContinuationState(
        LuaGameLoopHost Host,
        LuaGameLoopOperation Operation,
        LuaClrTask Task);

    private sealed record CallbackGeneration(long Value);
}
