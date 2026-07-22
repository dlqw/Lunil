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
    public bool Accepted => Status == LuaPatchAcceptanceStatus.Accepted;
}

public delegate bool LuaPatchReplayLookup(string patchId, string nonce);

/// <summary>
/// Atomically records accepted patch identities. Implementations must make the check-and-record
/// operation indivisible across every host that shares the replay domain and durably publish a
/// successful acceptance before returning <see langword="true"/>.
/// </summary>
public interface ILuaPatchReplayStore
{
    bool TryAccept(string patchId, string nonce, DateTimeOffset acceptedAt);
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
    /// Evaluates manifest policy and then atomically records the patch identity. A false store
    /// result is reported as a replay; the non-atomic <see cref="ReplayLookup"/> alone is not a
    /// substitute for this operation.
    /// </summary>
    public LuaPatchAcceptanceResult TryAccept(
        LuaPatchManifest manifest,
        ILuaPatchReplayStore replayStore,
        DateTimeOffset? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(replayStore);
        var now = utcNow ?? DateTimeOffset.UtcNow;
        var evaluation = Evaluate(manifest, now);
        if (!evaluation.Accepted)
        {
            return evaluation;
        }

        return replayStore.TryAccept(manifest.PatchId, manifest.Nonce, now)
            ? evaluation
            : Rejected(
                LuaPatchAcceptanceStatus.ReplayDetected,
                "The patch id and nonce were already accepted.");
    }

    private static LuaPatchAcceptanceResult Rejected(
        LuaPatchAcceptanceStatus status,
        string message) => new(status, message);
}
