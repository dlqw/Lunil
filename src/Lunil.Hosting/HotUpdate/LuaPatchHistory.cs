using System.Collections.Immutable;

namespace Lunil.Hosting;

/// <summary>Heap-independent summary of one target in a completed rollout ring.</summary>
public sealed record LuaPatchHistoryTargetEntry(
    string TargetId,
    LuaPatchCommitStatus CommitStatus,
    LuaPatchTargetLifecycleStatus LifecycleStatus,
    bool SideEffectsMayHaveOccurred,
    TimeSpan PauseDuration)
{
    /// <summary>Decision-time generation snapshot when a generation guard was configured.</summary>
    public LuaPatchGenerationSnapshot? GenerationSnapshot { get; init; }

    /// <summary>Lifecycle stage that failed before final restoration, when present.</summary>
    public LuaPatchTargetLifecycleStatus? LifecycleFailure { get; init; }
}

/// <summary>Bounded operational summary of one completed rollout ring.</summary>
public sealed record LuaPatchHistoryEntry(
    long Sequence,
    DateTimeOffset RecordedAt,
    string RolloutId,
    string RingName,
    string TransactionId,
    string PatchId,
    string TargetRevision,
    LuaPatchRingCommitStatus Status,
    TimeSpan Duration,
    ImmutableArray<LuaPatchHistoryTargetEntry> Targets)
{
    public LuaPatchDistributedBarrierDecision? DistributedDecision { get; init; }
}

/// <summary>One consistent read of bounded patch history and its health summary.</summary>
public sealed record LuaPatchHistorySnapshot(
    int MaximumEntryCount,
    long TotalRecordedCount,
    long DroppedEntryCount,
    long RecordingFailureCount,
    int ConsecutiveUnsuccessfulCount,
    DateTimeOffset? LastCommittedAt,
    DateTimeOffset? LastUnsuccessfulAt,
    ImmutableArray<LuaPatchHistoryEntry> Entries);

/// <summary>
/// Retains bounded, heap-independent rollout summaries for operational health endpoints.
/// </summary>
public sealed class LuaPatchHistory
{
    private const int MaximumAllowedEntryCount = 10_000;
    private readonly object _gate = new();
    private readonly Queue<LuaPatchHistoryEntry> _entries;
    private long _totalRecordedCount;
    private long _droppedEntryCount;
    private long _recordingFailureCount;
    private int _consecutiveUnsuccessfulCount;
    private DateTimeOffset? _lastCommittedAt;
    private DateTimeOffset? _lastUnsuccessfulAt;

    public LuaPatchHistory(int maximumEntryCount = 256)
    {
        if (maximumEntryCount is < 1 or > MaximumAllowedEntryCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumEntryCount),
                maximumEntryCount,
                $"Patch history capacity must be between 1 and {MaximumAllowedEntryCount}.");
        }

        MaximumEntryCount = maximumEntryCount;
        _entries = new Queue<LuaPatchHistoryEntry>(maximumEntryCount);
    }

    public int MaximumEntryCount { get; }

    /// <summary>Captures entries in oldest-to-newest order with bounded health counters.</summary>
    public LuaPatchHistorySnapshot CaptureSnapshot()
    {
        lock (_gate)
        {
            return new LuaPatchHistorySnapshot(
                MaximumEntryCount,
                _totalRecordedCount,
                _droppedEntryCount,
                _recordingFailureCount,
                _consecutiveUnsuccessfulCount,
                _lastCommittedAt,
                _lastUnsuccessfulAt,
                _entries.ToImmutableArray());
        }
    }

    internal void Record(
        LuaPatchRingCommitResult result,
        string patchId,
        string targetRevision,
        DateTimeOffset recordedAt,
        TimeSpan duration)
    {
        LunilGuard.NotNull(result);
        LunilGuard.NotNullOrWhiteSpace(patchId);
        LunilGuard.NotNullOrWhiteSpace(targetRevision);
        var targets = result.Targets.Select(static target => new LuaPatchHistoryTargetEntry(
            target.TargetId,
            target.Commit.Status,
            target.Lifecycle.Status,
            target.Commit.SideEffectsMayHaveOccurred,
            target.Commit.PauseDuration)
        {
            GenerationSnapshot = target.GenerationSnapshot,
            LifecycleFailure = target.Lifecycle.Failure,
        }).ToImmutableArray();

        lock (_gate)
        {
            if (_entries.Count == MaximumEntryCount)
            {
                _entries.Dequeue();
                _droppedEntryCount = SaturatingIncrement(_droppedEntryCount);
            }

            _totalRecordedCount = SaturatingIncrement(_totalRecordedCount);
            var entry = new LuaPatchHistoryEntry(
                _totalRecordedCount,
                recordedAt,
                result.RolloutId,
                result.RingName,
                result.TransactionId,
                patchId,
                targetRevision,
                result.Status,
                duration,
                targets)
            {
                DistributedDecision = result.DistributedBarrier?.Decision,
            };
            _entries.Enqueue(entry);

            if (result.Succeeded)
            {
                _consecutiveUnsuccessfulCount = 0;
                _lastCommittedAt = recordedAt;
            }
            else
            {
                _consecutiveUnsuccessfulCount = _consecutiveUnsuccessfulCount == int.MaxValue
                    ? int.MaxValue
                    : _consecutiveUnsuccessfulCount + 1;
                _lastUnsuccessfulAt = recordedAt;
            }
        }
    }

    internal void RecordFailure()
    {
        lock (_gate)
        {
            _recordingFailureCount = SaturatingIncrement(_recordingFailureCount);
        }
    }

    private static long SaturatingIncrement(long value) => value == long.MaxValue
        ? long.MaxValue
        : value + 1;
}
