using System.Collections.Immutable;

namespace Lunil.Hosting;

/// <summary>Participant signal applied atomically to a distributed rollout barrier.</summary>
public enum LuaPatchDistributedBarrierSignal : byte
{
    Observe,
    Prepared,
    PreparationFailed,
    Healthy,
    Unhealthy,
}

/// <summary>Durable decision for one rollout ring shared by multiple host processes.</summary>
public enum LuaPatchDistributedBarrierDecision : byte
{
    Waiting,
    Apply,
    Commit,
    Rollback,
}

/// <summary>
/// One idempotent participant update. The first accepted update pins the complete membership,
/// quorum, patch identity, and timeout policy for the rollout ring.
/// </summary>
public sealed record LuaPatchDistributedBarrierRequest
{
    public required string RolloutId { get; init; }

    public required string RingName { get; init; }

    public required string PatchId { get; init; }

    public required string TargetRevision { get; init; }

    /// <summary>SHA-256 identity of the canonical patch manifest.</summary>
    public required string PatchManifestIdentity { get; init; }

    public required string ParticipantId { get; init; }

    public ImmutableArray<string> Participants { get; init; } = [];

    public int RequiredParticipantCount { get; init; }

    public TimeSpan PreparationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public LuaPatchDistributedBarrierSignal Signal { get; init; }

    public string? Message { get; init; }
}

/// <summary>Current durable state of one distributed rollout ring.</summary>
public sealed record LuaPatchDistributedBarrierSnapshot
{
    public required string RolloutId { get; init; }

    public required string RingName { get; init; }

    public required string PatchId { get; init; }

    public required string TargetRevision { get; init; }

    public required string PatchManifestIdentity { get; init; }

    public ImmutableArray<string> Participants { get; init; } = [];

    public int RequiredParticipantCount { get; init; }

    public ImmutableArray<string> PreparedParticipants { get; init; } = [];

    /// <summary>
    /// Quorum members selected atomically when the barrier first transitions to Apply. Only these
    /// participants may publish the candidate generation.
    /// </summary>
    public ImmutableArray<string> SelectedParticipants { get; init; } = [];

    public ImmutableArray<string> HealthyParticipants { get; init; } = [];

    public ImmutableArray<string> FailedParticipants { get; init; } = [];

    public required LuaPatchDistributedBarrierDecision Decision { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public required DateTimeOffset PreparationDeadline { get; init; }

    public DateTimeOffset? HealthDeadline { get; init; }

    public string? Message { get; init; }

    public bool IsSelected(string participantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(participantId);
        return SelectedParticipants.Contains(participantId, StringComparer.Ordinal);
    }
}

/// <summary>
/// Atomic durable store used to coordinate a ring across processes. Implementations must make
/// duplicate requests idempotent and must never replace an existing terminal decision.
/// </summary>
public interface ILuaPatchDistributedBarrierStore
{
    LuaPatchDistributedBarrierSnapshot Advance(
        LuaPatchDistributedBarrierRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Cross-process barrier configuration for one coordinator participant.</summary>
public sealed record LuaPatchDistributedBarrierOptions
{
    public required ILuaPatchDistributedBarrierStore Store { get; init; }

    public required string ParticipantId { get; init; }

    public ImmutableArray<string> Participants { get; init; } = [];

    /// <summary>Number of pinned participants selected to publish. Zero requires all participants.</summary>
    public int RequiredParticipantCount { get; init; }

    public TimeSpan PreparationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(50);
}
