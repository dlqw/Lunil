using Lunil.IR.Canonical;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;

namespace Lunil.Hosting;

/// <summary>Controls how a periodic CLR timer handles elapsed periods between dispatches.</summary>
public enum LuaClrTimerCatchUpPolicy : byte
{
    /// <summary>Runs once and schedules the next tick relative to the dispatch time.</summary>
    Skip,

    /// <summary>Runs once, reports missed periods, and preserves the original phase.</summary>
    Coalesce,

    /// <summary>Runs overdue ticks individually, subject to the configured catch-up bound.</summary>
    CatchUp,
}

/// <summary>Configuration for a host-polled Lua timer.</summary>
public sealed record LuaClrTimerOptions
{
    /// <summary>Gets a one-shot timer configuration that is due immediately.</summary>
    public static LuaClrTimerOptions Default { get; } = new();

    /// <summary>Gets the delay before the first tick.</summary>
    public TimeSpan DueTime { get; init; }

    /// <summary>Gets the periodic interval, or <see cref="Timeout.InfiniteTimeSpan"/> for one shot.</summary>
    public TimeSpan Period { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>Gets the overdue-period policy.</summary>
    public LuaClrTimerCatchUpPolicy CatchUpPolicy { get; init; } =
        LuaClrTimerCatchUpPolicy.Coalesce;

    /// <summary>Gets the maximum overdue ticks dispatched per poll for a catch-up timer.</summary>
    public int MaximumCatchUpTicks { get; init; } = 8;
}

internal sealed class LuaClrTimerRegistration
{
    private int _state;

    public LuaClrTimerRegistration(
        LuaValue callback,
        LuaIrModule? module,
        LuaClrGenerationState state,
        long order)
    {
        Callback = callback;
        Module = module;
        _state = (int)state;
        Order = order;
    }

    public LuaValue Callback { get; }

    public LuaIrModule? Module { get; }

    public LuaClrGenerationState State
    {
        get => (LuaClrGenerationState)Volatile.Read(ref _state);
        set => Volatile.Write(ref _state, (int)value);
    }

    public long Order { get; }

    public LuaClrTimer Timer { get; set; } = null!;
}

internal readonly record struct LuaClrTimerScheduleSnapshot(
    bool WasActivated,
    bool WasScheduled,
    TimeSpan Remaining,
    long DispatchedTickCount,
    long MissedTickCount);

/// <summary>
/// A bounded, host-polled timer that never enters Lua from a background thread.
/// </summary>
public sealed class LuaClrTimer : IDisposable
{
    private readonly object _gate = new();
    private readonly LuaClrBridge _bridge;
    private readonly LuaClrTimerRegistration _registration;
    private LuaHandle? _callbackHandle;
    private long _nextDueTimestamp;
    private TimeSpan _pausedRemaining;
    private bool _scheduled;
    private bool _activated;
    private bool _resumeAfterPatch;
    private int _disposed;
    private long _dispatchedTickCount;
    private long _missedTickCount;

    internal LuaClrTimer(
        LuaClrBridge bridge,
        LuaClrTimerRegistration registration,
        LuaClrTimerOptions options,
        LuaHandle callbackHandle)
    {
        _bridge = bridge;
        _registration = registration;
        Options = options;
        Callback = registration.Callback;
        _callbackHandle = callbackHandle;
    }

    /// <summary>Gets the bridge that owns this timer.</summary>
    public LuaClrBridge Bridge => _bridge;

    /// <summary>Gets the Lua callback retained by this timer.</summary>
    public LuaValue Callback { get; }

    /// <summary>Gets the validated immutable scheduling options.</summary>
    public LuaClrTimerOptions Options { get; }

    /// <summary>Gets whether the timer belongs to the active patch generation.</summary>
    public bool IsActive => !IsDisposed && _bridge.IsTimerActive(_registration);

    /// <summary>Gets whether the timer is currently waiting for a tick.</summary>
    public bool IsScheduled
    {
        get
        {
            lock (_gate)
            {
                return _scheduled && !IsDisposed;
            }
        }
    }

    /// <summary>Gets whether the timer was cancelled or disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Gets the number of callback ticks dispatched by this timer.</summary>
    public long DispatchedTickCount => Interlocked.Read(ref _dispatchedTickCount);

    /// <summary>Gets the number of overdue periodic ticks skipped or coalesced.</summary>
    public long MissedTickCount => Interlocked.Read(ref _missedTickCount);

    /// <summary>Cancels the timer and releases its retained Lua callback.</summary>
    public void Cancel() => Dispose();

    /// <summary>Cancels the timer at most once.</summary>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        _bridge.CloseTimerRegistration(_registration);
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            _scheduled = false;
        }

        Interlocked.Exchange(ref _callbackHandle, null)?.Dispose();
    }

    internal long NextDueTimestamp
    {
        get
        {
            lock (_gate)
            {
                return _nextDueTimestamp;
            }
        }
    }

    internal LuaClrTimerRegistration Registration => _registration;

    internal bool IsDue(long now)
    {
        lock (_gate)
        {
            return _scheduled && !IsDisposed && _nextDueTimestamp <= now;
        }
    }

    internal bool HasLiveSchedule
    {
        get
        {
            lock (_gate)
            {
                return !IsDisposed && (_scheduled ||
                    _activated && _registration.State == LuaClrGenerationState.Quiesced);
            }
        }
    }

    internal bool TryPrepareDispatch(
        long now,
        TimeProvider timeProvider,
        int catchUpTicksDispatched,
        out long tick,
        out long missed)
    {
        lock (_gate)
        {
            tick = 0;
            missed = 0;
            if (!_scheduled || IsDisposed || _registration.State != LuaClrGenerationState.Active ||
                _nextDueTimestamp > now)
            {
                return false;
            }

            var period = Options.Period;
            if (period == Timeout.InfiniteTimeSpan)
            {
                _scheduled = false;
            }
            else
            {
                var elapsed = timeProvider.GetElapsedTime(_nextDueTimestamp, now);
                var elapsedPeriods = 1L + Math.Max(0L, elapsed.Ticks / period.Ticks);
                switch (Options.CatchUpPolicy)
                {
                    case LuaClrTimerCatchUpPolicy.Skip:
                        missed = elapsedPeriods - 1;
                        _nextDueTimestamp = AddSaturated(now, period, timeProvider);
                        break;
                    case LuaClrTimerCatchUpPolicy.Coalesce:
                        missed = elapsedPeriods - 1;
                        _nextDueTimestamp = AddPeriodsSaturated(
                            _nextDueTimestamp,
                            period,
                            elapsedPeriods,
                            timeProvider);
                        break;
                    case LuaClrTimerCatchUpPolicy.CatchUp:
                        if (catchUpTicksDispatched >= Options.MaximumCatchUpTicks)
                        {
                            return false;
                        }

                        _nextDueTimestamp = AddSaturated(
                            _nextDueTimestamp,
                            period,
                            timeProvider);
                        break;
                    default:
                        throw new InvalidOperationException("The CLR timer catch-up policy is invalid.");
                }
            }

            tick = Interlocked.Increment(ref _dispatchedTickCount);
            if (missed != 0)
            {
                Interlocked.Add(ref _missedTickCount, missed);
            }

            return true;
        }
    }

    internal void Activate(long now, TimeProvider timeProvider)
    {
        lock (_gate)
        {
            if (IsDisposed)
            {
                return;
            }

            if (!_activated)
            {
                _activated = true;
                _scheduled = true;
                _nextDueTimestamp = AddSaturated(now, Options.DueTime, timeProvider);
            }
            else if (_resumeAfterPatch)
            {
                _resumeAfterPatch = false;
                _scheduled = true;
                _nextDueTimestamp = AddSaturated(now, _pausedRemaining, timeProvider);
            }
        }
    }

    internal void Suspend(long now, TimeProvider timeProvider)
    {
        lock (_gate)
        {
            if (IsDisposed || !_scheduled)
            {
                return;
            }

            _pausedRemaining = _nextDueTimestamp <= now
                ? TimeSpan.Zero
                : timeProvider.GetElapsedTime(now, _nextDueTimestamp);
            _scheduled = false;
            _resumeAfterPatch = true;
        }
    }

    internal void Retire()
    {
        lock (_gate)
        {
            _scheduled = false;
        }
    }

    internal LuaClrTimerScheduleSnapshot CaptureScheduleForMigration()
    {
        lock (_gate)
        {
            return new LuaClrTimerScheduleSnapshot(
                _activated,
                _resumeAfterPatch,
                _pausedRemaining,
                Interlocked.Read(ref _dispatchedTickCount),
                Interlocked.Read(ref _missedTickCount));
        }
    }

    internal void AdoptScheduleForMigration(LuaClrTimerScheduleSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_activated || _scheduled || IsDisposed)
            {
                throw new InvalidOperationException(
                    "Only an unpublished candidate timer can adopt a previous schedule.");
            }

            _activated = snapshot.WasActivated;
            _resumeAfterPatch = snapshot.WasScheduled;
            _pausedRemaining = snapshot.Remaining;
            Interlocked.Exchange(ref _dispatchedTickCount, snapshot.DispatchedTickCount);
            Interlocked.Exchange(ref _missedTickCount, snapshot.MissedTickCount);
        }
    }

    private static long AddPeriodsSaturated(
        long value,
        TimeSpan period,
        long count,
        TimeProvider timeProvider)
    {
        if (count <= 0)
        {
            return value;
        }

        if (period.Ticks > 0 && count > long.MaxValue / period.Ticks)
        {
            return long.MaxValue;
        }

        return AddSaturated(
            value,
            TimeSpan.FromTicks(period.Ticks * count),
            timeProvider);
    }

    private static long AddSaturated(long value, TimeSpan delay, TimeProvider timeProvider)
    {
        if (delay <= TimeSpan.Zero)
        {
            return value;
        }

        var timestampDelta = Math.Ceiling(
            delay.TotalSeconds * timeProvider.TimestampFrequency);
        if (timestampDelta >= long.MaxValue || value > long.MaxValue - (long)timestampDelta)
        {
            return long.MaxValue;
        }

        return value + (long)timestampDelta;
    }
}

public sealed partial class LuaClrBridge
{
    private static readonly TimeSpan MaximumTimerDuration = TimeSpan.FromDays(3650);
    private readonly List<LuaClrTimerRegistration> _timerRegistrations = [];
    private readonly List<LuaClrTimerRegistration> _dueTimerBuffer = [];
    private readonly Dictionary<LuaClrTimerRegistration, int> _timerCatchUpCounts =
        new(ReferenceEqualityComparer.Instance);
    private long _nextTimerOrder;

    /// <summary>Gets the number of live timers admitted by the current generation.</summary>
    public int ActiveTimerCount => CountTimers(LuaClrGenerationState.Active);

    /// <summary>Gets the number of candidate timers waiting for atomic patch publication.</summary>
    public int PendingTimerCount => CountTimers(LuaClrGenerationState.Pending);

    /// <summary>Gets the number of previous-generation timers paused by a patch barrier.</summary>
    public int QuiescedTimerCount => CountTimers(LuaClrGenerationState.Quiesced);

    /// <summary>Gets the number of still-referenced timers rejected as stale.</summary>
    public int StaleTimerCount => CountTimers(LuaClrGenerationState.Stale);

    /// <summary>Creates a bounded timer whose callback runs only during explicit dispatch.</summary>
    public LuaClrTimer ScheduleTimer(LuaValue callback, LuaClrTimerOptions options)
    {
        RequireCapability(LuaClrCapabilities.Timers);
        ArgumentNullException.ThrowIfNull(options);
        ValidateTimerOptions(options);
        if (callback.Kind != LuaValueKind.Function || callback.TryGetClosure() is null)
        {
            throw new LuaClrException(
                LuaClrErrorCode.InvalidDelegate,
                "A Lua closure is required for a CLR timer callback.");
        }

        _state.Heap.ValidateValue(callback);
        var handle = _state.CreateHandle(callback);
        LuaClrTimerRegistration? createdRegistration = null;
        try
        {
            lock (_callbackGate)
            {
                PruneGenerationRegistrations();
                var liveCount = _timerRegistrations.Count(existing => existing.State is
                        (LuaClrGenerationState.Active or LuaClrGenerationState.Pending));
                if (liveCount >= _options.MaximumTimerCount)
                {
                    throw new LuaClrException(
                        LuaClrErrorCode.InvocationFailed,
                        "The CLR timer count limit was reached.");
                }

                var module = callback.TryGetClosure()!.Module;
                var state = module is not null && _generationUpdate?.IsCandidateModule(module) == true
                    ? LuaClrGenerationState.Pending
                    : LuaClrGenerationState.Active;
                var registration = new LuaClrTimerRegistration(
                    callback,
                    module,
                    state,
                    checked(++_nextTimerOrder));
                createdRegistration = registration;
                var timer = new LuaClrTimer(this, registration, options, handle);
                registration.Timer = timer;
                _timerRegistrations.Add(registration);
                _generationUpdate?.Track(registration);
                if (state == LuaClrGenerationState.Active)
                {
                    timer.Activate(_options.TimeProvider.GetTimestamp(), _options.TimeProvider);
                }

                return timer;
            }
        }
        catch (Exception exception) when (LuaClrGenerationUpdate.IsRecoverable(exception))
        {
            lock (_callbackGate)
            {
                if (createdRegistration is not null)
                {
                    createdRegistration.State = LuaClrGenerationState.Closed;
                    createdRegistration.Timer?.Retire();
                    _timerRegistrations.Remove(createdRegistration);
                }
            }

            handle.Dispose();
            throw;
        }
    }

    /// <summary>Dispatches due timers up to the configured per-call limit.</summary>
    public int DispatchTimers() => DispatchTimers(_options.MaximumTimerDispatchCount);

    /// <summary>Dispatches at most <paramref name="maximumCallbacks"/> due timer callbacks.</summary>
    public int DispatchTimers(int maximumCallbacks)
    {
        RequireCapability(LuaClrCapabilities.Timers);
        if (maximumCallbacks is < 1 || maximumCallbacks > _options.MaximumTimerDispatchCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCallbacks),
                maximumCallbacks,
                $"The timer dispatch count must be between 1 and {_options.MaximumTimerDispatchCount}.");
        }

        lock (_callbackGate)
        {
            EnsureThread();
            if (_state.RunningThread is not null)
            {
                throw new LuaClrException(
                    LuaClrErrorCode.ThreadDenied,
                    "CLR timers can be dispatched only while the Lua state is idle.");
            }

            PruneGenerationRegistrations();
            var now = _options.TimeProvider.GetTimestamp();
            foreach (var registration in _timerRegistrations)
            {
                if (registration.State == LuaClrGenerationState.Active &&
                    registration.Timer.IsDue(now))
                {
                    _dueTimerBuffer.Add(registration);
                }
            }

            _dueTimerBuffer.Sort(static (left, right) =>
            {
                var dueComparison = left.Timer.NextDueTimestamp.CompareTo(
                    right.Timer.NextDueTimestamp);
                return dueComparison != 0 ? dueComparison : left.Order.CompareTo(right.Order);
            });
            var dispatched = 0;
            try
            {
                while (_dueTimerBuffer.Count != 0 && dispatched < maximumCallbacks)
                {
                    var madeProgress = false;
                    foreach (var registration in _dueTimerBuffer)
                    {
                        if (dispatched >= maximumCallbacks)
                        {
                            break;
                        }

                        _timerCatchUpCounts.TryGetValue(registration, out var catchUpCount);
                        if (!registration.Timer.TryPrepareDispatch(
                            now,
                            _options.TimeProvider,
                            catchUpCount,
                            out var tick,
                            out var missed))
                        {
                            continue;
                        }

                        madeProgress = true;
                        if (registration.Timer.Options.CatchUpPolicy ==
                            LuaClrTimerCatchUpPolicy.CatchUp)
                        {
                            _timerCatchUpCounts[registration] = catchUpCount + 1;
                        }

                        InvokeLuaCallback(
                            registration.Callback,
                            [LuaValue.FromInteger(tick), LuaValue.FromInteger(missed)],
                            _timerExecutionOptions);
                        dispatched++;
                    }

                    if (!madeProgress)
                    {
                        break;
                    }
                }
            }
            finally
            {
                _dueTimerBuffer.Clear();
                _timerCatchUpCounts.Clear();
            }

            return dispatched;
        }
    }

    internal bool IsTimerActive(LuaClrTimerRegistration registration)
    {
        lock (_callbackGate)
        {
            return registration.State == LuaClrGenerationState.Active;
        }
    }

    internal void CloseTimerRegistration(LuaClrTimerRegistration registration)
    {
        lock (_callbackGate)
        {
            if (registration.State == LuaClrGenerationState.Quiesced)
            {
                throw new LuaClrException(
                    LuaClrErrorCode.TimerGenerationClosed,
                    "A quiesced CLR timer cannot be cancelled before patch publication.");
            }

            registration.State = LuaClrGenerationState.Closed;
            registration.Timer.Retire();
            _timerRegistrations.Remove(registration);
        }
    }

    internal static LuaClrTimer RequireTimer(LuaValue value, LuaClrBridge bridge)
    {
        if (value.Kind != LuaValueKind.Userdata ||
            value.AsUserdata().Payload is not LuaClrTimer timer ||
            !ReferenceEquals(timer.Bridge, bridge))
        {
            throw new LuaClrException(
                LuaClrErrorCode.InvocationFailed,
                "A CLR timer owned by this Lua state is required.");
        }

        return timer;
    }

    internal void DisposeTimers()
    {
        lock (_callbackGate)
        {
            var timers = _timerRegistrations
                .Select(static registration => registration.Timer)
                .ToArray();
            foreach (var timer in timers)
            {
                timer.Dispose();
            }

            PruneGenerationRegistrations();
        }
    }

    private int CountTimers(LuaClrGenerationState state)
    {
        lock (_callbackGate)
        {
            PruneGenerationRegistrations();
            return _timerRegistrations.Count(registration => registration.State == state);
        }
    }

    private static void ValidateTimerOptions(LuaClrTimerOptions options)
    {
        if (options.DueTime < TimeSpan.Zero || options.DueTime > MaximumTimerDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.DueTime,
                "The CLR timer due time must be between zero and 3650 days.");
        }

        if (options.Period != Timeout.InfiniteTimeSpan &&
            (options.Period <= TimeSpan.Zero || options.Period > MaximumTimerDuration))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Period,
                "The CLR timer period must be positive, infinite, or no more than 3650 days.");
        }

        if (!Enum.IsDefined(options.CatchUpPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The timer catch-up policy is invalid.");
        }

        if (options.MaximumCatchUpTicks is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaximumCatchUpTicks,
                "The maximum catch-up tick count must be between 1 and 10000.");
        }
    }
}
