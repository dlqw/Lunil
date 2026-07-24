using Lunil.IR.Canonical;
using Lunil.Runtime.Values;

namespace Lunil.Hosting;

internal enum LuaClrCallbackState : byte
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
        LuaClrCallbackState state)
    {
        Callback = callback;
        Module = module;
        _state = (int)state;
    }

    public LuaValue Callback { get; }

    public LuaIrModule? Module { get; }

    public LuaClrCallbackState State
    {
        get => (LuaClrCallbackState)Volatile.Read(ref _state);
        set => Volatile.Write(ref _state, (int)value);
    }

    public bool IsSubscriptionActive
    {
        get
        {
            lock (_gate)
            {
                return _attached && !_attachmentFaulted &&
                    State == LuaClrCallbackState.Active;
            }
        }
    }

    public LuaValue GetActiveCallback()
    {
        lock (_gate)
        {
            if (State != LuaClrCallbackState.Active)
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
                State == LuaClrCallbackState.Closed,
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
            if (!_attached || State == LuaClrCallbackState.Closed)
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
            if (State == LuaClrCallbackState.Closed || _subscribe is null)
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
            if (State == LuaClrCallbackState.Closed)
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
                State = LuaClrCallbackState.Closed;
            }
        }
    }
}

public sealed partial class LuaClrBridge
{
    private readonly List<WeakReference<LuaClrCallbackRegistration>> _callbackRegistrations = [];
    private LuaClrCallbackUpdate? _callbackUpdate;

    /// <summary>Gets the number of live delegate registrations admitted by the current generation.</summary>
    public int ActiveCallbackCount => CountCallbacks(LuaClrCallbackState.Active);

    /// <summary>Gets the number of candidate callbacks waiting for atomic patch publication.</summary>
    public int PendingCallbackCount => CountCallbacks(LuaClrCallbackState.Pending);

    /// <summary>Gets the number of previous-generation callbacks blocked by an active patch barrier.</summary>
    public int QuiescedCallbackCount => CountCallbacks(LuaClrCallbackState.Quiesced);

    /// <summary>Gets the number of still-referenced delegates rejected as stale.</summary>
    public int StaleCallbackCount => CountCallbacks(LuaClrCallbackState.Stale);

    private LuaClrCallbackRegistration CreateCallbackRegistration(LuaValue callback)
    {
        var module = callback.TryGetClosure()?.Module;
        lock (_callbackGate)
        {
            PruneCallbackRegistrations();
            var state = module is not null && _callbackUpdate?.IsCandidateModule(module) == true
                ? LuaClrCallbackState.Pending
                : LuaClrCallbackState.Active;
            var registration = new LuaClrCallbackRegistration(callback, module, state);
            _callbackRegistrations.Add(new WeakReference<LuaClrCallbackRegistration>(registration));
            _callbackUpdate?.Track(registration);
            return registration;
        }
    }

    internal LuaClrCallbackUpdate BeginCallbackUpdate(
        IEnumerable<LuaIrModule> previousModules,
        IEnumerable<LuaIrModule> candidateModules)
    {
        ArgumentNullException.ThrowIfNull(previousModules);
        ArgumentNullException.ThrowIfNull(candidateModules);
        lock (_callbackGate)
        {
            if (_callbackUpdate is not null)
            {
                throw new InvalidOperationException("A CLR callback generation update is already active.");
            }

            var update = new LuaClrCallbackUpdate(this, previousModules, candidateModules);
            _callbackUpdate = update;
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
                catch (Exception rollbackFailure) when (LuaClrCallbackUpdate.IsRecoverable(
                    rollbackFailure))
                {
                    throw new InvalidOperationException(
                        "CLR callback generation staging failed and rollback was incomplete.",
                        new AggregateException(stagingFailure, rollbackFailure));
                }

                throw;
            }
        }
    }

    private void EndCallbackUpdate(LuaClrCallbackUpdate update)
    {
        if (!ReferenceEquals(_callbackUpdate, update))
        {
            throw new InvalidOperationException("The CLR callback generation update is not active.");
        }

        _callbackUpdate = null;
        PruneCallbackRegistrations();
    }

    private void PruneCallbackRegistrations() => _callbackRegistrations.RemoveAll(
        static reference => !reference.TryGetTarget(out _));

    private int CountCallbacks(LuaClrCallbackState state)
    {
        lock (_callbackGate)
        {
            PruneCallbackRegistrations();
            return _callbackRegistrations.Count(reference =>
                reference.TryGetTarget(out var registration) && registration.State == state);
        }
    }

    internal sealed class LuaClrCallbackUpdate : IDisposable
    {
        private readonly LuaClrBridge _bridge;
        private readonly HashSet<LuaIrModule> _previousModules;
        private readonly HashSet<LuaIrModule> _candidateModules;
        private readonly List<LuaClrCallbackRegistration> _pending = [];
        private readonly List<LuaClrCallbackRegistration> _retired = [];
        private bool _applied;
        private bool _completed;

        public LuaClrCallbackUpdate(
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
        }

        public bool IsCandidateModule(LuaIrModule module) => _candidateModules.Contains(module);

        public void Track(LuaClrCallbackRegistration registration)
        {
            if (registration.State == LuaClrCallbackState.Pending)
            {
                _pending.Add(registration);
            }
        }

        public void StagePreviousRegistrations()
        {
            foreach (var reference in _bridge._callbackRegistrations)
            {
                if (reference.TryGetTarget(out var registration) &&
                    registration.State == LuaClrCallbackState.Active &&
                    registration.Module is { } module && _previousModules.Contains(module))
                {
                    registration.State = LuaClrCallbackState.Quiesced;
                    _retired.Add(registration);
                    registration.Suspend();
                }
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
                    if (registration.State == LuaClrCallbackState.Pending)
                    {
                        registration.State = LuaClrCallbackState.Active;
                    }
                }

                foreach (var registration in _retired)
                {
                    if (registration.State == LuaClrCallbackState.Quiesced)
                    {
                        registration.State = LuaClrCallbackState.Stale;
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
                        if (registration.State != LuaClrCallbackState.Closed)
                        {
                            registration.State = LuaClrCallbackState.Stale;
                        }
                    }
                }

                foreach (var registration in _retired)
                {
                    if (registration.State is LuaClrCallbackState.Quiesced or
                        LuaClrCallbackState.Stale)
                    {
                        try
                        {
                            registration.Resume();
                            if (registration.State != LuaClrCallbackState.Closed)
                            {
                                registration.State = LuaClrCallbackState.Active;
                            }
                        }
                        catch (Exception exception) when (IsRecoverable(exception))
                        {
                            if (registration.State != LuaClrCallbackState.Closed)
                            {
                                registration.State = LuaClrCallbackState.Stale;
                            }

                            failure ??= exception;
                        }
                    }
                }

                _completed = true;
                _bridge.EndCallbackUpdate(this);
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
                        "The CLR callback generation update was not published.");
                }

                _completed = true;
                _bridge.EndCallbackUpdate(this);
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

        internal static bool IsRecoverable(Exception exception) => exception is not
            OutOfMemoryException and not StackOverflowException and not AccessViolationException;
    }
}
