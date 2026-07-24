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
    UpdateIntentMismatch,
    RollbackNotAuthorized,
    CapabilityDenied,
    TargetSelectorMismatch,
}

public sealed record LuaPatchAcceptanceResult(
    LuaPatchAcceptanceStatus Status,
    string? Message)
{
    public LuaPatchReplayReservation? ReplayReservation { get; init; }

    public bool Accepted => Status == LuaPatchAcceptanceStatus.Accepted;
}

public delegate bool LuaPatchReplayLookup(string patchId, string nonce);

public sealed record LuaPatchSignerIdentity(string Algorithm, string KeyId);

public delegate LuaPatchUpdateIntent LuaPatchRevisionClassifier(
    string baseRevision,
    string targetRevision);

public delegate bool LuaPatchRollbackAuthorizer(
    LuaPatchManifest manifest,
    LuaPatchSignerIdentity signer);

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

    /// <summary>
    /// Case-sensitive capabilities already granted by deployment policy. Matching a request only
    /// admits the patch; it does not alter the host's runtime capability configuration.
    /// </summary>
    public ImmutableArray<string> GrantedCapabilities { get; init; } = [];

    /// <summary>
    /// Case-sensitive labels assigned by the deployment control plane to this target.
    /// Every signed required target label must match exactly.
    /// </summary>
    public ImmutableArray<LuaPatchTargetLabel> TargetLabels { get; init; } = [];

    /// <summary>
    /// Optional host revision-ledger classifier. When present, its result must match the signed
    /// manifest intent, preventing a downgrade from being labelled as a forward update.
    /// </summary>
    public LuaPatchRevisionClassifier? RevisionClassifier { get; init; }

    /// <summary>
    /// Explicit authorization required for every rollback. It receives the verified bundle signer
    /// identity so hosts can enforce separate release and rollback key roles.
    /// </summary>
    public LuaPatchRollbackAuthorizer? RollbackAuthorizer { get; init; }

    public DateTimeOffset? MinimumCreatedAt { get; init; }

    public TimeSpan MaximumFutureSkew { get; init; } = TimeSpan.FromMinutes(5);

    public LuaPatchReplayLookup? ReplayLookup { get; init; }

    public LuaPatchAcceptanceResult Evaluate(
        LuaPatchManifest manifest,
        DateTimeOffset? utcNow = null)
        => EvaluateCore(manifest, null, utcNow);

    public LuaPatchAcceptanceResult Evaluate(
        LuaPatchManifest manifest,
        LuaPatchSignerIdentity verifiedSigner,
        DateTimeOffset? utcNow)
    {
        ArgumentNullException.ThrowIfNull(verifiedSigner);
        return EvaluateCore(manifest, verifiedSigner, utcNow);
    }

    private LuaPatchAcceptanceResult EvaluateCore(
        LuaPatchManifest manifest,
        LuaPatchSignerIdentity? verifiedSigner,
        DateTimeOffset? utcNow)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetBuild);
        ArgumentException.ThrowIfNullOrWhiteSpace(CurrentRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(RuntimeAbi);
        if (MaximumFutureSkew < TimeSpan.Zero)
        {
            throw new InvalidOperationException("MaximumFutureSkew cannot be negative.");
        }

        if (!Enum.IsDefined(manifest.UpdateIntent))
        {
            throw new ArgumentException("The patch update intent is invalid.", nameof(manifest));
        }

        var grantedCapabilities = ValidateGrantedCapabilities();
        var targetLabels = ValidateTargetLabels();

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

        if (RevisionClassifier is not null)
        {
            var classifiedIntent = RevisionClassifier(
                manifest.BaseRevision,
                manifest.TargetRevision);
            if (!Enum.IsDefined(classifiedIntent))
            {
                throw new InvalidOperationException(
                    "RevisionClassifier returned an invalid update intent.");
            }

            if (classifiedIntent != manifest.UpdateIntent)
            {
                return Rejected(
                    LuaPatchAcceptanceStatus.UpdateIntentMismatch,
                    "The signed patch intent does not match the host revision ledger.");
            }
        }

        if (manifest.UpdateIntent == LuaPatchUpdateIntent.Rollback &&
            (verifiedSigner is null || string.IsNullOrWhiteSpace(verifiedSigner.Algorithm) ||
                string.IsNullOrWhiteSpace(verifiedSigner.KeyId) ||
                RollbackAuthorizer?.Invoke(manifest, verifiedSigner) != true))
        {
            return Rejected(
                LuaPatchAcceptanceStatus.RollbackNotAuthorized,
                "The rollback is not authorized for the verified patch signer and target revision.");
        }

        if (!manifest.RequiredCapabilities.IsDefaultOrEmpty)
        {
            foreach (var capability in manifest.RequiredCapabilities)
            {
                if (!grantedCapabilities.Contains(capability))
                {
                    return Rejected(
                        LuaPatchAcceptanceStatus.CapabilityDenied,
                        $"Required patch capability '{capability}' is not granted by the host policy.");
                }
            }
        }

        if (!manifest.RequiredTargetLabels.IsDefaultOrEmpty)
        {
            var requiredNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var requiredLabel in manifest.RequiredTargetLabels)
            {
                if (requiredLabel is null || string.IsNullOrWhiteSpace(requiredLabel.Name) ||
                    !string.Equals(
                        requiredLabel.Name,
                        requiredLabel.Name.Trim(),
                        StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(requiredLabel.Value) ||
                    !string.Equals(
                        requiredLabel.Value,
                        requiredLabel.Value.Trim(),
                        StringComparison.Ordinal) ||
                    !requiredNames.Add(requiredLabel.Name))
                {
                    throw new ArgumentException(
                        "RequiredTargetLabels must contain unique, non-blank canonical names and values.",
                        nameof(manifest));
                }

                if (!targetLabels.TryGetValue(requiredLabel.Name, out var actualValue) ||
                    !string.Equals(actualValue, requiredLabel.Value, StringComparison.Ordinal))
                {
                    return Rejected(
                        LuaPatchAcceptanceStatus.TargetSelectorMismatch,
                        $"Required target label '{requiredLabel.Name}' does not match this deployment target.");
                }
            }
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
        => TryReserveCore(manifest, null, scope, replayStore, utcNow);

    public LuaPatchAcceptanceResult TryReserve(
        LuaPatchManifest manifest,
        LuaPatchSignerIdentity verifiedSigner,
        string scope,
        ILuaPatchReplayStore replayStore,
        DateTimeOffset? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(verifiedSigner);
        return TryReserveCore(manifest, verifiedSigner, scope, replayStore, utcNow);
    }

    private LuaPatchAcceptanceResult TryReserveCore(
        LuaPatchManifest manifest,
        LuaPatchSignerIdentity? verifiedSigner,
        string scope,
        ILuaPatchReplayStore replayStore,
        DateTimeOffset? utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(replayStore);
        var now = utcNow ?? DateTimeOffset.UtcNow;
        // The durable store owns replay state for this path. A legacy lookup is intentionally
        // excluded so an incomplete reservation can be rebound after a process restart.
        var evaluation = ReplayLookup is null
            ? EvaluateForSigner(manifest, verifiedSigner, now)
            : (this with { ReplayLookup = null }).EvaluateForSigner(
                manifest,
                verifiedSigner,
                now);
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

    private LuaPatchAcceptanceResult EvaluateForSigner(
        LuaPatchManifest manifest,
        LuaPatchSignerIdentity? verifiedSigner,
        DateTimeOffset utcNow) => verifiedSigner is null
            ? Evaluate(manifest, utcNow)
            : Evaluate(manifest, verifiedSigner, utcNow);

    private HashSet<string> ValidateGrantedCapabilities()
    {
        var granted = new HashSet<string>(StringComparer.Ordinal);
        if (GrantedCapabilities.IsDefaultOrEmpty)
        {
            return granted;
        }

        foreach (var capability in GrantedCapabilities)
        {
            if (string.IsNullOrWhiteSpace(capability) ||
                !string.Equals(capability, capability.Trim(), StringComparison.Ordinal) ||
                !granted.Add(capability))
            {
                throw new InvalidOperationException(
                    "GrantedCapabilities must contain unique, non-blank canonical names.");
            }
        }

        return granted;
    }

    private Dictionary<string, string> ValidateTargetLabels()
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TargetLabels.IsDefaultOrEmpty)
        {
            return labels;
        }

        foreach (var label in TargetLabels)
        {
            if (label is null || string.IsNullOrWhiteSpace(label.Name) ||
                !string.Equals(label.Name, label.Name.Trim(), StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(label.Value) ||
                !string.Equals(label.Value, label.Value.Trim(), StringComparison.Ordinal) ||
                !labels.TryAdd(label.Name, label.Value))
            {
                throw new InvalidOperationException(
                    "TargetLabels must contain unique, non-blank canonical names and values.");
            }
        }

        return labels;
    }
}
