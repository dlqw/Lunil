using System.Collections.Immutable;

namespace Lunil.Hosting;

public enum LuaPatchAcceptanceStatus : byte
{
    Accepted,
    TargetBuildMismatch,
    BaseRevisionMismatch,
    RuntimeAbiMismatch,
    ChannelNotAllowed,
    CreatedInFuture,
    CreatedBeforeMinimum,
    ReplayDetected,
    Expired,
}

public sealed record LuaPatchAcceptanceResult(
    LuaPatchAcceptanceStatus Status,
    string? Message)
{
    public LuaPatchReplayReservation? ReplayReservation { get; init; }

    public bool Accepted => Status == LuaPatchAcceptanceStatus.Accepted;
}

public delegate bool LuaPatchReplayLookup(string patchId, string nonce);

/// <summary>
/// Atomically reserves scoped patch identities and serializes their commit attempts. A durable
/// reservation is resumable after a process crash; a commit lease must be released automatically
/// when its owning process exits.
/// </summary>
public interface ILuaPatchReplayStore
{
    LuaPatchReplayReservationResult TryReserve(
        string scope,
        string patchId,
        string nonce,
        DateTimeOffset reservedAt);

    ILuaPatchReplayCommitLease? TryAcquireCommit(
        LuaPatchReplayReservation reservation,
        DateTimeOffset acquiredAt);
}

public enum LuaPatchReplayReservationStatus : byte
{
    Reserved,
    ReplayDetected,
}

/// <summary>A durable, target-scoped identity reservation returned by a replay store.</summary>
public sealed record LuaPatchReplayReservation(
    string Scope,
    string PatchId,
    string Nonce,
    string ReservationId,
    DateTimeOffset ReservedAt);

public sealed record LuaPatchReplayReservationResult(
    LuaPatchReplayReservationStatus Status,
    LuaPatchReplayReservation? Reservation,
    string? Message)
{
    public bool Reserved => Status == LuaPatchReplayReservationStatus.Reserved &&
        Reservation is not null;
}

/// <summary>
/// Exclusive commit ownership for one reservation. Disposing an incomplete lease makes the
/// reservation available for a crash-safe retry; completing it durably rejects later retries.
/// </summary>
public interface ILuaPatchReplayCommitLease : IDisposable
{
    LuaPatchReplayReservation Reservation { get; }

    bool IsCompleted { get; }

    void Complete(DateTimeOffset committedAt);

    /// <summary>Compensates a completed lease while ownership is still held after rollback.</summary>
    void Reopen(DateTimeOffset reopenedAt);
}

public sealed record LuaPatchAcceptancePolicy
{
    public required string TargetBuild { get; init; }

    public required string CurrentRevision { get; init; }

    public required string RuntimeAbi { get; init; }

    public ImmutableArray<string> AllowedChannels { get; init; } = [];

    public DateTimeOffset? MinimumCreatedAt { get; init; }

    public TimeSpan MaximumFutureSkew { get; init; } = TimeSpan.FromMinutes(5);

    public LuaPatchReplayLookup? ReplayLookup { get; init; }

    public LuaPatchAcceptanceResult Evaluate(
        LuaPatchManifest manifest,
        DateTimeOffset? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetBuild);
        ArgumentException.ThrowIfNullOrWhiteSpace(CurrentRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(RuntimeAbi);
        if (MaximumFutureSkew < TimeSpan.Zero)
        {
            throw new InvalidOperationException("MaximumFutureSkew cannot be negative.");
        }

        if (!string.Equals(manifest.TargetBuild, TargetBuild, StringComparison.Ordinal))
        {
            return Rejected(
                LuaPatchAcceptanceStatus.TargetBuildMismatch,
                "The patch targets a different application build.");
        }

        if (!string.Equals(manifest.BaseRevision, CurrentRevision, StringComparison.Ordinal))
        {
            return Rejected(
                LuaPatchAcceptanceStatus.BaseRevisionMismatch,
                "The patch base revision does not match the current revision.");
        }

        if (!string.Equals(manifest.RuntimeAbi, RuntimeAbi, StringComparison.Ordinal))
        {
            return Rejected(
                LuaPatchAcceptanceStatus.RuntimeAbiMismatch,
                "The patch runtime ABI does not match the host.");
        }

        if (!AllowedChannels.IsDefaultOrEmpty &&
            !AllowedChannels.Contains(manifest.Channel, StringComparer.Ordinal))
        {
            return Rejected(
                LuaPatchAcceptanceStatus.ChannelNotAllowed,
                "The patch channel is not allowed by this host.");
        }

        var now = utcNow ?? DateTimeOffset.UtcNow;
        if (manifest.CreatedAt > now + MaximumFutureSkew)
        {
            return Rejected(
                LuaPatchAcceptanceStatus.CreatedInFuture,
                "The patch creation time is beyond the configured clock skew.");
        }

        if (MinimumCreatedAt is { } minimum && manifest.CreatedAt < minimum)
        {
            return Rejected(
                LuaPatchAcceptanceStatus.CreatedBeforeMinimum,
                "The patch predates the minimum accepted release time.");
        }

        if (manifest.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            return Rejected(
                LuaPatchAcceptanceStatus.Expired,
                "The patch has expired.");
        }

        if (ReplayLookup?.Invoke(manifest.PatchId, manifest.Nonce) == true)
        {
            return Rejected(
                LuaPatchAcceptanceStatus.ReplayDetected,
                "The patch id and nonce were already accepted.");
        }

        return new LuaPatchAcceptanceResult(LuaPatchAcceptanceStatus.Accepted, null);
    }

    /// <summary>
    /// Evaluates manifest policy and then durably creates or resumes its scoped reservation.
    /// The non-atomic <see cref="ReplayLookup"/> is excluded from this operation and cannot
    /// substitute for the store lifecycle.
    /// </summary>
    public LuaPatchAcceptanceResult TryReserve(
        LuaPatchManifest manifest,
        string scope,
        ILuaPatchReplayStore replayStore,
        DateTimeOffset? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(replayStore);
        var now = utcNow ?? DateTimeOffset.UtcNow;
        // The durable store owns replay state for this path. A legacy lookup is intentionally
        // excluded so an incomplete reservation can be rebound after a process restart.
        var evaluation = ReplayLookup is null
            ? Evaluate(manifest, now)
            : (this with { ReplayLookup = null }).Evaluate(manifest, now);
        if (!evaluation.Accepted)
        {
            return evaluation;
        }

        var reservation = replayStore.TryReserve(
            scope,
            manifest.PatchId,
            manifest.Nonce,
            now);
        if (!Enum.IsDefined(reservation.Status) ||
            (reservation.Status == LuaPatchReplayReservationStatus.Reserved) !=
            (reservation.Reservation is not null))
        {
            throw new InvalidOperationException(
                "The replay store returned an inconsistent reservation result.");
        }

        if (reservation.Reserved &&
            (!string.Equals(reservation.Reservation!.Scope, scope, StringComparison.Ordinal) ||
                !string.Equals(
                    reservation.Reservation.PatchId,
                    manifest.PatchId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    reservation.Reservation.Nonce,
                    manifest.Nonce,
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(reservation.Reservation.ReservationId) ||
                reservation.Reservation.ReservedAt == default))
        {
            throw new InvalidOperationException(
                "The replay store returned a reservation for another scoped identity.");
        }

        return reservation.Reserved
            ? evaluation with { ReplayReservation = reservation.Reservation }
            : Rejected(
                LuaPatchAcceptanceStatus.ReplayDetected,
                reservation.Message ??
                    "The scoped patch identity is committed or conflicts with another reservation.");
    }

    private static LuaPatchAcceptanceResult Rejected(
        LuaPatchAcceptanceStatus status,
        string message) => new(status, message);
}
