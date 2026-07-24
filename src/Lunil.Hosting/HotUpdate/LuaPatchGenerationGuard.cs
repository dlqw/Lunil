using Lunil.Runtime.Execution;

namespace Lunil.Hosting;

/// <summary>
/// One consistent view of generation-tracked asynchronous resources in a <see cref="LuaHost"/>.
/// </summary>
public sealed record LuaPatchGenerationSnapshot
{
    public required DateTimeOffset ObservedAt { get; init; }

    public required bool UpdateInProgress { get; init; }

    public required int ActiveCallbackCount { get; init; }

    public required int PendingCallbackCount { get; init; }

    public required int QuiescedCallbackCount { get; init; }

    public required int StaleCallbackCount { get; init; }

    public required int ActiveTaskCount { get; init; }

    public required int PendingTaskCount { get; init; }

    public required int QuiescedTaskCount { get; init; }

    public required int StaleTaskCount { get; init; }

    public required int ActiveTimerCount { get; init; }

    public required int PendingTimerCount { get; init; }

    public required int QuiescedTimerCount { get; init; }

    public required int StaleTimerCount { get; init; }

    public required int ActiveNativeContinuationCount { get; init; }

    public required int PendingNativeContinuationCount { get; init; }

    public required int QuiescedNativeContinuationCount { get; init; }

    public required int StaleNativeContinuationCount { get; init; }

    public long ActiveResourceCount => (long)ActiveCallbackCount + ActiveTaskCount +
        ActiveTimerCount + ActiveNativeContinuationCount;

    public long PendingResourceCount => (long)PendingCallbackCount + PendingTaskCount +
        PendingTimerCount + PendingNativeContinuationCount;

    public long QuiescedResourceCount => (long)QuiescedCallbackCount + QuiescedTaskCount +
        QuiescedTimerCount + QuiescedNativeContinuationCount;

    public long StaleResourceCount => (long)StaleCallbackCount + StaleTaskCount +
        StaleTimerCount + StaleNativeContinuationCount;

    public bool HasTransitionResidue => PendingResourceCount != 0 || QuiescedResourceCount != 0;

    public bool HasStaleResources => StaleResourceCount != 0;

    internal void Validate()
    {
        if (ActiveCallbackCount < 0 || PendingCallbackCount < 0 ||
            QuiescedCallbackCount < 0 || StaleCallbackCount < 0 ||
            ActiveTaskCount < 0 || PendingTaskCount < 0 ||
            QuiescedTaskCount < 0 || StaleTaskCount < 0 ||
            ActiveTimerCount < 0 || PendingTimerCount < 0 ||
            QuiescedTimerCount < 0 || StaleTimerCount < 0 ||
            ActiveNativeContinuationCount < 0 || PendingNativeContinuationCount < 0 ||
            QuiescedNativeContinuationCount < 0 || StaleNativeContinuationCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                "snapshot",
                "Patch generation resource counts cannot be negative.");
        }
    }
}

/// <summary>Result of evaluating one generation snapshot against a rollout guard.</summary>
public sealed record LuaPatchGenerationGuardResult(bool Accepted, string? Message);

/// <summary>
/// Optional rollout gate that prevents publication from exceeding retained-generation budgets.
/// </summary>
public sealed record LuaPatchGenerationGuardPolicy
{
    public static LuaPatchGenerationGuardPolicy Strict { get; } = new();

    public bool RejectTransitionResidue { get; init; } = true;

    public int MaximumStaleCallbackCount { get; init; }

    public int MaximumStaleTaskCount { get; init; }

    public int MaximumStaleTimerCount { get; init; }

    public int MaximumStaleNativeContinuationCount { get; init; }

    public LuaPatchGenerationGuardResult Evaluate(LuaPatchGenerationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Validate();
        snapshot.Validate();
        if (RejectTransitionResidue && snapshot.HasTransitionResidue)
        {
            return new LuaPatchGenerationGuardResult(
                false,
                $"The patch generation retained {snapshot.PendingResourceCount} pending and " +
                $"{snapshot.QuiescedResourceCount} quiesced resource(s).");
        }

        var violation = Check(
            "callback",
            snapshot.StaleCallbackCount,
            MaximumStaleCallbackCount) ?? Check(
            "task",
            snapshot.StaleTaskCount,
            MaximumStaleTaskCount) ?? Check(
            "timer",
            snapshot.StaleTimerCount,
            MaximumStaleTimerCount) ?? Check(
            "native continuation",
            snapshot.StaleNativeContinuationCount,
            MaximumStaleNativeContinuationCount);
        return violation is null
            ? new LuaPatchGenerationGuardResult(true, null)
            : new LuaPatchGenerationGuardResult(false, violation);
    }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumStaleCallbackCount);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumStaleTaskCount);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumStaleTimerCount);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumStaleNativeContinuationCount);
    }

    private static string? Check(string kind, int observed, int maximum) => observed > maximum
        ? $"The patch generation retained {observed} stale {kind} resource(s); the limit is {maximum}."
        : null;
}

public sealed partial class LuaHost
{
    /// <summary>
    /// Captures generation-tracked asynchronous resource counts under the host execution gate.
    /// </summary>
    public LuaPatchGenerationSnapshot CapturePatchGenerationSnapshot()
    {
        lock (_executionGate)
        {
            ThrowIfDisposed();
            return ClrBridge.CapturePatchGenerationCounts(counts =>
                new LuaPatchGenerationSnapshot
                {
                    ObservedAt = ClrBridge.Options.TimeProvider.GetUtcNow(),
                    UpdateInProgress = _activePatchUpdateWindow is not null,
                    ActiveCallbackCount = counts.ActiveCallbacks,
                    PendingCallbackCount = counts.PendingCallbacks,
                    QuiescedCallbackCount = counts.QuiescedCallbacks,
                    StaleCallbackCount = counts.StaleCallbacks,
                    ActiveTaskCount = counts.ActiveTasks,
                    PendingTaskCount = counts.PendingTasks,
                    QuiescedTaskCount = counts.QuiescedTasks,
                    StaleTaskCount = counts.StaleTasks,
                    ActiveTimerCount = counts.ActiveTimers,
                    PendingTimerCount = counts.PendingTimers,
                    QuiescedTimerCount = counts.QuiescedTimers,
                    StaleTimerCount = counts.StaleTimers,
                    ActiveNativeContinuationCount = CountNativeContinuations(
                        LuaThreadPatchGenerationState.Unmanaged,
                        LuaThreadPatchGenerationState.Active),
                    PendingNativeContinuationCount = CountNativeContinuations(
                        LuaThreadPatchGenerationState.Pending),
                    QuiescedNativeContinuationCount = CountNativeContinuations(
                        LuaThreadPatchGenerationState.Quiesced),
                    StaleNativeContinuationCount = CountNativeContinuations(
                        LuaThreadPatchGenerationState.Stale),
                });
        }
    }
}

public sealed partial class LuaClrBridge
{
    internal TResult CapturePatchGenerationCounts<TResult>(
        Func<PatchGenerationCounts, TResult> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        lock (_callbackGate)
        {
            PruneGenerationRegistrations();
            return capture(new PatchGenerationCounts(
                CountCallbacksUnsafe(LuaClrGenerationState.Active),
                CountCallbacksUnsafe(LuaClrGenerationState.Pending),
                CountCallbacksUnsafe(LuaClrGenerationState.Quiesced),
                CountCallbacksUnsafe(LuaClrGenerationState.Stale),
                CountTasksUnsafe(LuaClrGenerationState.Active),
                CountTasksUnsafe(LuaClrGenerationState.Pending),
                CountTasksUnsafe(LuaClrGenerationState.Quiesced),
                CountTasksUnsafe(LuaClrGenerationState.Stale),
                CountTimersUnsafe(LuaClrGenerationState.Active),
                CountTimersUnsafe(LuaClrGenerationState.Pending),
                CountTimersUnsafe(LuaClrGenerationState.Quiesced),
                CountTimersUnsafe(LuaClrGenerationState.Stale)));
        }
    }

    private int CountCallbacksUnsafe(LuaClrGenerationState state) =>
        _callbackRegistrations.Count(reference =>
            reference.TryGetTarget(out var registration) && registration.State == state);

    private int CountTasksUnsafe(LuaClrGenerationState state) =>
        _taskRegistrations.Count(reference =>
            reference.TryGetTarget(out var registration) && registration.State == state);

    private int CountTimersUnsafe(LuaClrGenerationState state) =>
        _timerRegistrations.Count(registration => registration.State == state);

    internal readonly record struct PatchGenerationCounts(
        int ActiveCallbacks,
        int PendingCallbacks,
        int QuiescedCallbacks,
        int StaleCallbacks,
        int ActiveTasks,
        int PendingTasks,
        int QuiescedTasks,
        int StaleTasks,
        int ActiveTimers,
        int PendingTimers,
        int QuiescedTimers,
        int StaleTimers);
}
