using Lunil.IR.Canonical;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.Hosting;

internal enum LuaClrGenerationState : byte
{
    Active,
    Pending,
    Quiesced,
    Stale,
    Closed,
}

internal sealed class LuaClrCallbackRegistration
{
    private readonly object _gate = new();
    private Action? _subscribe;
    private Action? _unsubscribe;
    private bool _attached;
    private bool _attachmentFaulted;
    private int _state;

    public LuaClrCallbackRegistration(
        LuaValue callback,
        LuaIrModule? module,
        LuaClrGenerationState state)
    {
        Callback = callback;
        Module = module;
        _state = (int)state;
    }

    public LuaValue Callback { get; }

    public LuaIrModule? Module { get; }

    public LuaClrGenerationState State
    {
        get => (LuaClrGenerationState)Volatile.Read(ref _state);
        set => Volatile.Write(ref _state, (int)value);
    }

    public bool IsSubscriptionActive
    {
        get
        {
            lock (_gate)
            {
                return _attached && !_attachmentFaulted &&
                    State == LuaClrGenerationState.Active;
            }
        }
    }

    public LuaValue GetActiveCallback()
    {
        lock (_gate)
        {
            if (State != LuaClrGenerationState.Active)
            {
                throw new LuaClrException(
                    LuaClrErrorCode.SubscriptionClosed,
                    "The Lua callback belongs to an inactive patch generation.");
            }

            return Callback;
        }
    }

    public void AttachSubscription(Action subscribe, Action unsubscribe)
    {
        ArgumentNullException.ThrowIfNull(subscribe);
        ArgumentNullException.ThrowIfNull(unsubscribe);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(
                State == LuaClrGenerationState.Closed,
                this);

            _subscribe = subscribe;
            _unsubscribe = unsubscribe;
            _attached = true;
            _attachmentFaulted = false;
        }
    }

    public void Suspend()
    {
        lock (_gate)
        {
            if (!_attached || State == LuaClrGenerationState.Closed)
            {
                return;
            }

            try
            {
                _unsubscribe!();
                _attached = false;
            }
            catch
            {
                _attachmentFaulted = true;
                throw;
            }
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (State == LuaClrGenerationState.Closed || _subscribe is null)
            {
                return;
            }

            if (_attachmentFaulted)
            {
                throw new InvalidOperationException(
                    "The CLR event subscription attachment state is indeterminate.");
            }

            if (!_attached)
            {
                try
                {
                    _subscribe();
                    _attached = true;
                }
                catch
                {
                    _attachmentFaulted = true;
                    throw;
                }
            }
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            if (State == LuaClrGenerationState.Closed)
            {
                return;
            }

            try
            {
                if (_attached)
                {
                    try
                    {
                        _unsubscribe!();
                        _attached = false;
                    }
                    catch
                    {
                        _attachmentFaulted = true;
                        throw;
                    }
                }
            }
            finally
            {
                State = LuaClrGenerationState.Closed;
            }
        }
    }
}

internal sealed class LuaClrTaskRegistration
{
    private int _state;

    public LuaClrTaskRegistration(
        LuaIrModule? module,
        LuaClrGenerationState state)
    {
        Module = module;
        _state = (int)state;
    }

    public LuaIrModule? Module { get; }

    public LuaClrGenerationState State
    {
        get => (LuaClrGenerationState)Volatile.Read(ref _state);
        set => Volatile.Write(ref _state, (int)value);
    }

    public bool IsActive => State == LuaClrGenerationState.Active;

    public void EnsureActive()
    {
        if (!IsActive)
        {
            throw new LuaClrException(
                LuaClrErrorCode.AsyncGenerationClosed,
                "The CLR task belongs to an inactive patch generation.");
        }
    }

    public void Close() => State = LuaClrGenerationState.Closed;
}

public sealed partial class LuaClrBridge
{
    private readonly List<WeakReference<LuaClrCallbackRegistration>> _callbackRegistrations = [];
    private readonly List<WeakReference<LuaClrTaskRegistration>> _taskRegistrations = [];
    private LuaClrGenerationUpdate? _generationUpdate;

    /// <summary>Gets the number of live delegate registrations admitted by the current generation.</summary>
    public int ActiveCallbackCount => CountCallbacks(LuaClrGenerationState.Active);

    /// <summary>Gets the number of candidate callbacks waiting for atomic patch publication.</summary>
    public int PendingCallbackCount => CountCallbacks(LuaClrGenerationState.Pending);

    /// <summary>Gets the number of previous-generation callbacks blocked by an active patch barrier.</summary>
    public int QuiescedCallbackCount => CountCallbacks(LuaClrGenerationState.Quiesced);

    /// <summary>Gets the number of still-referenced delegates rejected as stale.</summary>
    public int StaleCallbackCount => CountCallbacks(LuaClrGenerationState.Stale);

    /// <summary>Gets the number of live CLR task wrappers admitted by the current generation.</summary>
    public int ActiveTaskCount => CountTasks(LuaClrGenerationState.Active);

    /// <summary>Gets the number of candidate task wrappers waiting for atomic patch publication.</summary>
    public int PendingTaskCount => CountTasks(LuaClrGenerationState.Pending);

    /// <summary>Gets the number of previous-generation task wrappers blocked by a patch barrier.</summary>
    public int QuiescedTaskCount => CountTasks(LuaClrGenerationState.Quiesced);

    /// <summary>Gets the number of still-referenced task wrappers rejected as stale.</summary>
    public int StaleTaskCount => CountTasks(LuaClrGenerationState.Stale);

    private LuaClrCallbackRegistration CreateCallbackRegistration(LuaValue callback)
    {
        var module = callback.TryGetClosure()?.Module;
        lock (_callbackGate)
        {
            PruneGenerationRegistrations();
            var state = module is not null && _generationUpdate?.IsCandidateModule(module) == true
                ? LuaClrGenerationState.Pending
                : LuaClrGenerationState.Active;
            var registration = new LuaClrCallbackRegistration(callback, module, state);
            _callbackRegistrations.Add(new WeakReference<LuaClrCallbackRegistration>(registration));
            _generationUpdate?.Track(registration);
            return registration;
        }
    }

    internal LuaClrTaskRegistration CreateTaskRegistration()
    {
        var thread = _state.RunningThread;
        var module = thread is { Frames.Count: > 0 }
            ? thread.Frames[^1].Closure.Module
            : null;
        lock (_callbackGate)
        {
            PruneGenerationRegistrations();
            var state = module is not null && _generationUpdate?.IsCandidateModule(module) == true
                ? LuaClrGenerationState.Pending
                : LuaClrGenerationState.Active;
            var registration = new LuaClrTaskRegistration(module, state);
            _taskRegistrations.Add(new WeakReference<LuaClrTaskRegistration>(registration));
            _generationUpdate?.Track(registration);
            return registration;
        }
    }

    internal void CloseCallbackRegistration(LuaClrCallbackRegistration registration)
    {
        lock (_callbackGate)
        {
            registration.Close();
        }
    }

    internal bool IsCallbackActive(LuaClrCallbackRegistration registration)
    {
        lock (_callbackGate)
        {
            return registration.IsSubscriptionActive;
        }
    }

    internal void EnsureTaskConsumable(LuaClrTaskRegistration registration)
    {
        lock (_callbackGate)
        {
            if (registration.IsActive)
            {
                return;
            }

            var thread = _state.RunningThread;
            var runningModule = thread is { Frames.Count: > 0 }
                ? thread.Frames[^1].Closure.Module
                : null;
            if (registration.State == LuaClrGenerationState.Pending &&
                registration.Module is { } module &&
                ReferenceEquals(runningModule, module) &&
                _generationUpdate?.IsCandidateModule(module) == true)
            {
                return;
            }

            registration.EnsureActive();
        }
    }

    internal bool IsTaskActive(LuaClrTaskRegistration registration)
    {
        lock (_callbackGate)
        {
            return registration.IsActive;
        }
    }

    internal void CloseTaskRegistration(LuaClrTaskRegistration registration)
    {
        lock (_callbackGate)
        {
            registration.Close();
        }
    }

    internal LuaClrGenerationUpdate BeginGenerationUpdate(
        IEnumerable<LuaIrModule> previousModules,
        IEnumerable<LuaIrModule> candidateModules)
    {
        ArgumentNullException.ThrowIfNull(previousModules);
        ArgumentNullException.ThrowIfNull(candidateModules);
        lock (_callbackGate)
        {
            if (_generationUpdate is not null)
            {
                throw new InvalidOperationException("A CLR generation update is already active.");
            }

            var update = new LuaClrGenerationUpdate(this, previousModules, candidateModules);
            _generationUpdate = update;
            try
            {
                update.StagePreviousRegistrations();
                return update;
            }
            catch (Exception stagingFailure)
            {
                try
                {
                    update.Rollback();
                }
                catch (Exception rollbackFailure) when (LuaClrGenerationUpdate.IsRecoverable(
                    rollbackFailure))
                {
                    throw new InvalidOperationException(
                        "CLR generation staging failed and rollback was incomplete.",
                        new AggregateException(stagingFailure, rollbackFailure));
                }

                throw;
            }
        }
    }

    private void EndGenerationUpdate(LuaClrGenerationUpdate update)
    {
        if (!ReferenceEquals(_generationUpdate, update))
        {
            throw new InvalidOperationException("The CLR generation update is not active.");
        }

        update.StopTrackingThreads();
        _generationUpdate = null;
        PruneGenerationRegistrations();
    }

    private void PruneGenerationRegistrations()
    {
        _callbackRegistrations.RemoveAll(static reference => !reference.TryGetTarget(out _));
        _taskRegistrations.RemoveAll(static reference => !reference.TryGetTarget(out _));
    }

    private int CountCallbacks(LuaClrGenerationState state)
    {
        lock (_callbackGate)
        {
            PruneGenerationRegistrations();
            return _callbackRegistrations.Count(reference =>
                reference.TryGetTarget(out var registration) && registration.State == state);
        }
    }

    private int CountTasks(LuaClrGenerationState state)
    {
        lock (_callbackGate)
        {
            PruneGenerationRegistrations();
            return _taskRegistrations.Count(reference =>
                reference.TryGetTarget(out var registration) && registration.State == state);
        }
    }

    internal sealed class LuaClrGenerationUpdate : IDisposable
    {
        private readonly LuaClrBridge _bridge;
        private readonly HashSet<LuaIrModule> _previousModules;
        private readonly HashSet<LuaIrModule> _candidateModules;
        private readonly List<LuaClrCallbackRegistration> _pending = [];
        private readonly List<LuaClrCallbackRegistration> _retired = [];
        private readonly List<LuaClrTaskRegistration> _pendingTasks = [];
        private readonly List<LuaClrTaskRegistration> _retiredTasks = [];
        private readonly List<LuaClrTimerRegistration> _pendingTimers = [];
        private readonly List<LuaClrTimerRegistration> _retiredTimers = [];
        private readonly HashSet<LuaThread> _pendingThreads =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<LuaThread> _retiredThreads =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<LuaThread, LuaIrModule> _transferredThreads =
            new(ReferenceEqualityComparer.Instance);
        private bool _applied;
        private bool _completed;

        public LuaClrGenerationUpdate(
            LuaClrBridge bridge,
            IEnumerable<LuaIrModule> previousModules,
            IEnumerable<LuaIrModule> candidateModules)
        {
            _bridge = bridge;
            _previousModules = new HashSet<LuaIrModule>(
                previousModules,
                ReferenceEqualityComparer.Instance);
            _candidateModules = new HashSet<LuaIrModule>(
                candidateModules,
                ReferenceEqualityComparer.Instance);
            _bridge._state.ThreadCreated += TrackCreatedThread;
        }

        public bool IsCandidateModule(LuaIrModule module) => _candidateModules.Contains(module);

        public void Track(LuaClrCallbackRegistration registration)
        {
            if (registration.State == LuaClrGenerationState.Pending)
            {
                _pending.Add(registration);
            }
        }

        public void Track(LuaClrTaskRegistration registration)
        {
            if (registration.State == LuaClrGenerationState.Pending)
            {
                _pendingTasks.Add(registration);
            }
        }

        public void Track(LuaClrTimerRegistration registration)
        {
            if (registration.State == LuaClrGenerationState.Pending)
            {
                _pendingTimers.Add(registration);
            }
        }

        public void TransferTimerSchedule(
            LuaClrTimer previousTimer,
            LuaClrTimer candidateTimer,
            LuaIrModule candidateModule)
        {
            ArgumentNullException.ThrowIfNull(previousTimer);
            ArgumentNullException.ThrowIfNull(candidateTimer);
            ArgumentNullException.ThrowIfNull(candidateModule);
            lock (_bridge._callbackGate)
            {
                ThrowIfCompleted();
                if (!ReferenceEquals(previousTimer.Bridge, _bridge) ||
                    !ReferenceEquals(candidateTimer.Bridge, _bridge) ||
                    !_candidateModules.Contains(candidateModule))
                {
                    throw new InvalidOperationException(
                        "A timer schedule can be transferred only within its owning state.");
                }

                var previous = previousTimer.Registration;
                var candidate = candidateTimer.Registration;
                if (!_retiredTimers.Contains(previous) ||
                    previous.State != LuaClrGenerationState.Quiesced ||
                    !_pendingTimers.Contains(candidate) ||
                    candidate.State != LuaClrGenerationState.Pending ||
                    !ReferenceEquals(candidate.Module, candidateModule))
                {
                    throw new InvalidOperationException(
                        "Timer migration requires quiesced previous and pending candidate timers.");
                }

                candidateTimer.AdoptScheduleForMigration(
                    previousTimer.CaptureScheduleForMigration());
            }
        }

        public void TransferCoroutine(LuaThread thread, LuaIrModule candidateModule)
        {
            ArgumentNullException.ThrowIfNull(thread);
            ArgumentNullException.ThrowIfNull(candidateModule);
            lock (_bridge._callbackGate)
            {
                ThrowIfCompleted();
                if (!_candidateModules.Contains(candidateModule))
                {
                    throw new InvalidOperationException(
                        "A coroutine can be transferred only to a candidate module.");
                }

                if (thread.Status is LuaThreadStatus.Dead or LuaThreadStatus.Error)
                {
                    return;
                }

                if (_transferredThreads.ContainsKey(thread) &&
                    thread.PatchGenerationState == LuaThreadPatchGenerationState.Pending &&
                    ReferenceEquals(thread.PatchGenerationOwnerModule, candidateModule))
                {
                    return;
                }

                if (!_retiredThreads.Contains(thread) ||
                    thread.PatchGenerationState != LuaThreadPatchGenerationState.Quiesced ||
                    thread.PatchGenerationOwnerModule is not { } previousModule)
                {
                    throw new InvalidOperationException(
                        "The coroutine does not belong to the quiesced previous generation.");
                }

                _transferredThreads.Add(thread, previousModule);
                thread.PatchGenerationOwnerModule = candidateModule;
                thread.PatchGenerationState = LuaThreadPatchGenerationState.Pending;
                _pendingThreads.Add(thread);
            }
        }

        public void StagePreviousRegistrations()
        {
            foreach (var reference in _bridge._callbackRegistrations)
            {
                if (reference.TryGetTarget(out var registration) &&
                    registration.State == LuaClrGenerationState.Active &&
                    registration.Module is { } module && _previousModules.Contains(module))
                {
                    registration.State = LuaClrGenerationState.Quiesced;
                    _retired.Add(registration);
                    registration.Suspend();
                }
            }

            foreach (var reference in _bridge._taskRegistrations)
            {
                if (reference.TryGetTarget(out var registration) &&
                    registration.State == LuaClrGenerationState.Active &&
                    registration.Module is { } module && _previousModules.Contains(module))
                {
                    registration.State = LuaClrGenerationState.Quiesced;
                    _retiredTasks.Add(registration);
                }
            }

            var now = _bridge._options.TimeProvider.GetTimestamp();
            foreach (var registration in _bridge._timerRegistrations)
            {
                if (registration.State == LuaClrGenerationState.Active &&
                    registration.Module is { } module && _previousModules.Contains(module))
                {
                    registration.Timer.Suspend(now, _bridge._options.TimeProvider);
                    registration.State = LuaClrGenerationState.Quiesced;
                    _retiredTimers.Add(registration);
                }
            }

            foreach (var thread in _bridge._state.Heap.Objects.OfType<LuaThread>())
            {
                StageThread(thread);
            }
        }

        public void Apply()
        {
            lock (_bridge._callbackGate)
            {
                ThrowIfCompleted();
                if (_applied)
                {
                    return;
                }

                foreach (var registration in _pending)
                {
                    if (registration.State == LuaClrGenerationState.Pending)
                    {
                        registration.State = LuaClrGenerationState.Active;
                    }
                }

                foreach (var registration in _retired)
                {
                    if (registration.State == LuaClrGenerationState.Quiesced)
                    {
                        registration.State = LuaClrGenerationState.Stale;
                    }
                }

                foreach (var registration in _pendingTasks)
                {
                    if (registration.State == LuaClrGenerationState.Pending)
                    {
                        registration.State = LuaClrGenerationState.Active;
                    }
                }

                foreach (var registration in _retiredTasks)
                {
                    if (registration.State == LuaClrGenerationState.Quiesced)
                    {
                        registration.State = LuaClrGenerationState.Stale;
                    }
                }

                var now = _bridge._options.TimeProvider.GetTimestamp();
                foreach (var registration in _pendingTimers)
                {
                    if (registration.State == LuaClrGenerationState.Pending)
                    {
                        registration.State = LuaClrGenerationState.Active;
                        registration.Timer.Activate(now, _bridge._options.TimeProvider);
                    }
                }

                foreach (var registration in _retiredTimers)
                {
                    if (registration.State == LuaClrGenerationState.Quiesced)
                    {
                        registration.State = LuaClrGenerationState.Stale;
                        registration.Timer.Retire();
                    }
                }

                foreach (var thread in _pendingThreads)
                {
                    if (thread.PatchGenerationState == LuaThreadPatchGenerationState.Pending)
                    {
                        thread.PatchGenerationState = LuaThreadPatchGenerationState.Active;
                    }
                }

                foreach (var thread in _retiredThreads)
                {
                    if (thread.PatchGenerationState == LuaThreadPatchGenerationState.Quiesced)
                    {
                        thread.PatchGenerationState = LuaThreadPatchGenerationState.Stale;
                    }
                }

                _applied = true;
            }
        }

        public void Rollback()
        {
            lock (_bridge._callbackGate)
            {
                if (_completed)
                {
                    return;
                }

                Exception? failure = null;
                foreach (var registration in _pending)
                {
                    try
                    {
                        registration.Suspend();
                    }
                    catch (Exception exception) when (IsRecoverable(exception))
                    {
                        failure ??= exception;
                    }
                    finally
                    {
                        if (registration.State != LuaClrGenerationState.Closed)
                        {
                            registration.State = LuaClrGenerationState.Stale;
                        }
                    }
                }

                foreach (var registration in _retired)
                {
                    if (registration.State is LuaClrGenerationState.Quiesced or
                        LuaClrGenerationState.Stale)
                    {
                        try
                        {
                            registration.Resume();
                            if (registration.State != LuaClrGenerationState.Closed)
                            {
                                registration.State = LuaClrGenerationState.Active;
                            }
                        }
                        catch (Exception exception) when (IsRecoverable(exception))
                        {
                            if (registration.State != LuaClrGenerationState.Closed)
                            {
                                registration.State = LuaClrGenerationState.Stale;
                            }

                            failure ??= exception;
                        }
                    }
                }

                foreach (var registration in _pendingTasks)
                {
                    if (registration.State != LuaClrGenerationState.Closed)
                    {
                        registration.State = LuaClrGenerationState.Stale;
                    }
                }

                foreach (var registration in _retiredTasks)
                {
                    if (registration.State is LuaClrGenerationState.Quiesced or
                        LuaClrGenerationState.Stale)
                    {
                        registration.State = LuaClrGenerationState.Active;
                    }
                }

                var now = _bridge._options.TimeProvider.GetTimestamp();
                foreach (var registration in _pendingTimers)
                {
                    if (registration.State != LuaClrGenerationState.Closed)
                    {
                        registration.State = LuaClrGenerationState.Stale;
                        registration.Timer.Retire();
                    }
                }

                foreach (var registration in _retiredTimers)
                {
                    if (registration.State is LuaClrGenerationState.Quiesced or
                            LuaClrGenerationState.Stale)
                    {
                        registration.State = LuaClrGenerationState.Active;
                        registration.Timer.Activate(now, _bridge._options.TimeProvider);
                    }
                }

                foreach (var thread in _pendingThreads)
                {
                    if (_transferredThreads.TryGetValue(thread, out var previousModule))
                    {
                        thread.PatchGenerationOwnerModule = previousModule;
                        thread.PatchGenerationState = LuaThreadPatchGenerationState.Active;
                    }
                    else if (thread.PatchGenerationState != LuaThreadPatchGenerationState.Stale)
                    {
                        thread.PatchGenerationState = LuaThreadPatchGenerationState.Stale;
                    }
                }

                foreach (var thread in _retiredThreads)
                {
                    if (!_transferredThreads.ContainsKey(thread) &&
                        thread.PatchGenerationState is LuaThreadPatchGenerationState.Quiesced or
                            LuaThreadPatchGenerationState.Stale)
                    {
                        thread.PatchGenerationState = LuaThreadPatchGenerationState.Active;
                    }
                }

                _completed = true;
                _bridge.EndGenerationUpdate(this);
                if (failure is not null)
                {
                    throw new InvalidOperationException(
                        "One or more CLR callback subscriptions could not be restored.",
                        failure);
                }
            }
        }

        public void Complete()
        {
            lock (_bridge._callbackGate)
            {
                ThrowIfCompleted();
                if (!_applied)
                {
                    throw new InvalidOperationException(
                        "The CLR generation update was not published.");
                }

                _completed = true;
                _bridge.EndGenerationUpdate(this);
            }
        }

        public void Dispose()
        {
            if (_completed)
            {
                return;
            }

            if (_applied)
            {
                Complete();
            }
            else
            {
                Rollback();
            }
        }

        private void ThrowIfCompleted() => ObjectDisposedException.ThrowIf(_completed, this);

        public void StopTrackingThreads() =>
            _bridge._state.ThreadCreated -= TrackCreatedThread;

        private void TrackCreatedThread(LuaThread thread)
        {
            lock (_bridge._callbackGate)
            {
                if (_completed)
                {
                    return;
                }

                StageThread(thread);
            }
        }

        private void StageThread(LuaThread thread)
        {
            if (thread.Status is LuaThreadStatus.Dead or LuaThreadStatus.Error ||
                thread.PatchGenerationOwnerModule is not { } module)
            {
                return;
            }

            if (_candidateModules.Contains(module) &&
                thread.PatchGenerationState == LuaThreadPatchGenerationState.Unmanaged)
            {
                thread.PatchGenerationState = LuaThreadPatchGenerationState.Pending;
                _pendingThreads.Add(thread);
                return;
            }

            if (_previousModules.Contains(module) &&
                thread.PatchGenerationState is LuaThreadPatchGenerationState.Unmanaged or
                    LuaThreadPatchGenerationState.Active)
            {
                thread.PatchGenerationState = LuaThreadPatchGenerationState.Quiesced;
                _retiredThreads.Add(thread);
            }
        }

        internal static bool IsRecoverable(Exception exception) => exception is not
            OutOfMemoryException and not StackOverflowException and not AccessViolationException;
    }
}
