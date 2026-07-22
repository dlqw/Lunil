using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Lunil.Hosting;

public sealed record LuaPatchDeploymentTarget(
    string TargetId,
    LuaHost Host,
    LuaPreparedPatch PreparedPatch);

public sealed record LuaPatchRolloutRing
{
    public required string Name { get; init; }

    public ImmutableArray<LuaPatchDeploymentTarget> Targets { get; init; } = [];
}

public sealed record LuaPatchRolloutPlan
{
    public required string RolloutId { get; init; }

    public ImmutableArray<LuaPatchRolloutRing> Rings { get; init; } = [];
}

public enum LuaPatchRingHealthDecision : byte
{
    Accept,
    Rollback,
}

public sealed record LuaPatchRingHealthContext(
    string RolloutId,
    string RingName,
    ImmutableArray<LuaPatchTargetCommitResult> Targets);

public delegate LuaPatchRingHealthDecision LuaPatchRingHealthCallback(
    LuaPatchRingHealthContext context);

public sealed record LuaPatchCoordinatorOptions
{
    public static LuaPatchCoordinatorOptions Default { get; } = new();

    public LuaPatchUpdateWindowOptions UpdateWindow { get; init; } =
        LuaPatchUpdateWindowOptions.Default;

    public LuaPatchCommitOptions Commit { get; init; } = LuaPatchCommitOptions.Default;

    public LuaPatchRingHealthCallback? HealthCheck { get; init; }

    public ILuaPatchDeploymentJournal? Journal { get; init; }

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public LuaPatchResourceLimits ResourceLimits { get; init; } = LuaPatchResourceLimits.Default;
}

public enum LuaPatchRingCommitStatus : byte
{
    Committed,
    Deferred,
    Cancelled,
    PrepareFailed,
    PublishFailed,
    HealthRejected,
    JournalFailed,
}

public sealed record LuaPatchTargetCommitResult(
    string TargetId,
    LuaPatchCommitResult Commit);

public sealed record LuaPatchRingCommitResult(
    string RolloutId,
    string RingName,
    string TransactionId,
    LuaPatchRingCommitStatus Status,
    ImmutableArray<LuaPatchTargetCommitResult> Targets,
    string? Message)
{
    public bool Succeeded => Status == LuaPatchRingCommitStatus.Committed;
}

public sealed record LuaPatchRolloutResult(
    string RolloutId,
    ImmutableArray<LuaPatchRingCommitResult> Rings)
{
    public bool Succeeded => !Rings.IsEmpty && Rings.All(static ring => ring.Succeeded);
}

[JsonConverter(typeof(JsonStringEnumConverter<LuaPatchJournalPhase>))]
public enum LuaPatchJournalPhase : byte
{
    Started,
    Prepared,
    Publishing,
    Committed,
    RolledBack,
    Failed,
    RecoveredCommitted,
    RecoveredRolledBack,
}

public sealed record LuaPatchJournalEntry
{
    public long Sequence { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string TransactionId { get; init; }

    public required string RolloutId { get; init; }

    public required string RingName { get; init; }

    public required string PatchId { get; init; }

    public required string TargetRevision { get; init; }

    public required LuaPatchJournalPhase Phase { get; init; }

    public ImmutableArray<string> TargetIds { get; init; } = [];

    public string? Message { get; init; }

    public string? PreviousHash { get; init; }

    public string? Hash { get; init; }
}

public interface ILuaPatchDeploymentJournal
{
    void Append(LuaPatchJournalEntry entry);

    ImmutableArray<LuaPatchJournalEntry> ReadAll();
}

/// <summary>Coordinates barrier commits and ordered canary/ring rollout inside one process.</summary>
public sealed class LuaPatchCoordinator
{
    private static readonly object ProcessGate = new();
    private static bool _operationActive;
    private readonly object _gate = ProcessGate;

    public LuaPatchRolloutResult Deploy(
        LuaPatchRolloutPlan plan,
        LuaPatchCoordinatorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        options ??= LuaPatchCoordinatorOptions.Default;
        ValidatePlan(plan, options);
        using var activity = LuaPatchTelemetry.Start(
            "lunil.patch.rollout",
            plan.Rings[0].Targets[0].PreparedPatch.Manifest.PatchId,
            plan.RolloutId);
        activity?.SetTag("lunil.rollout.ring_count", plan.Rings.Length);
        lock (_gate)
        {
            ThrowIfReentrant();
            _operationActive = true;
            try
            {
                var results = ImmutableArray.CreateBuilder<LuaPatchRingCommitResult>(
                    plan.Rings.Length);
                foreach (var ring in plan.Rings)
                {
                    var result = CommitRingCore(
                        plan.RolloutId,
                        ring,
                        options,
                        cancellationToken);
                    results.Add(result);
                    if (!result.Succeeded)
                    {
                        break;
                    }
                }

                var rollout = new LuaPatchRolloutResult(plan.RolloutId, results.ToImmutable());
                var status = rollout.Succeeded
                    ? LuaPatchRingCommitStatus.Committed.ToString()
                    : rollout.Rings[^1].Status.ToString();
                LuaPatchTelemetry.Complete(activity, status, rollout.Rings[^1].Message);
                return rollout;
            }
            finally
            {
                _operationActive = false;
            }
        }
    }

    public LuaPatchRingCommitResult CommitRing(
        string rolloutId,
        LuaPatchRolloutRing ring,
        LuaPatchCoordinatorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rolloutId);
        ArgumentNullException.ThrowIfNull(ring);
        options ??= LuaPatchCoordinatorOptions.Default;
        ValidateRing(ring, options);
        lock (_gate)
        {
            ThrowIfReentrant();
            _operationActive = true;
            try
            {
                return CommitRingCore(rolloutId, ring, options, cancellationToken);
            }
            finally
            {
                _operationActive = false;
            }
        }
    }

    private static LuaPatchRingCommitResult CommitRingCore(
        string rolloutId,
        LuaPatchRolloutRing ring,
        LuaPatchCoordinatorOptions options,
        CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var ordered = ring.Targets.OrderBy(static target => target.TargetId, StringComparer.Ordinal)
            .ToArray();
        var transactionId = Guid.NewGuid().ToString("N");
        var patch = ordered[0].PreparedPatch.Manifest;
        using var activity = LuaPatchTelemetry.Start(
            "lunil.patch.ring",
            patch.PatchId,
            rolloutId,
            ring.Name);
        activity?.SetTag("lunil.ring.target_count", ordered.Length);
        var targetIds = ordered.Select(static target => target.TargetId).ToImmutableArray();
        var windows = new List<(LuaPatchDeploymentTarget Target, LuaPatchUpdateWindow Window)>();
        var sessions = new List<(LuaPatchDeploymentTarget Target, LuaHost.PatchCommitSession Session)>();
        var targetResults = ordered.ToDictionary(
            static target => target.TargetId,
            static target => NotExecutedResult(
                target.PreparedPatch,
                LuaPatchCommitStatus.BarrierAborted,
                "The barrier did not reach this target."),
            StringComparer.Ordinal);
        try
        {
            if (!TryJournal(
                options,
                Entry(LuaPatchJournalPhase.Started),
                out var journalError))
            {
                return Result(LuaPatchRingCommitStatus.JournalFailed, journalError);
            }

            foreach (var target in ordered)
            {
                var opened = target.Host.TryOpenPatchUpdateWindow(
                    options.UpdateWindow,
                    cancellationToken);
                if (!opened.Succeeded)
                {
                    var status = opened.Status == LuaPatchUpdateWindowStatus.Cancelled
                        ? LuaPatchRingCommitStatus.Cancelled
                        : LuaPatchRingCommitStatus.Deferred;
                    targetResults[target.TargetId] = NotExecutedResult(
                        target.PreparedPatch,
                        opened.Status == LuaPatchUpdateWindowStatus.Cancelled
                            ? LuaPatchCommitStatus.Cancelled
                            : LuaPatchCommitStatus.Deferred,
                        opened.Message);
                    _ = TryJournal(options, Entry(LuaPatchJournalPhase.Failed, opened.Message), out _);
                    return Result(status, opened.Message);
                }

                windows.Add((target, opened.Window!));
            }

            foreach (var pair in windows)
            {
                var preparation = pair.Target.Host.PreparePatchCommitSession(
                    pair.Target.PreparedPatch,
                    pair.Window,
                    options.Commit,
                    cancellationToken);
                if (preparation.Failure is not null)
                {
                    targetResults[pair.Target.TargetId] = preparation.Failure;
                    RollbackSessions(
                        sessions,
                        targetResults,
                        "Another state failed barrier preparation.");
                    _ = TryJournal(
                        options,
                        Entry(LuaPatchJournalPhase.RolledBack, preparation.Failure.Message),
                        out _);
                    return Result(
                        preparation.Failure.Status switch
                        {
                            LuaPatchCommitStatus.Cancelled => LuaPatchRingCommitStatus.Cancelled,
                            LuaPatchCommitStatus.Deferred => LuaPatchRingCommitStatus.Deferred,
                            _ => LuaPatchRingCommitStatus.PrepareFailed,
                        },
                        preparation.Failure.Message);
                }

                sessions.Add((pair.Target, preparation.Session!));
            }

            if (!TryJournal(options, Entry(LuaPatchJournalPhase.Prepared), out journalError))
            {
                RollbackSessions(sessions, targetResults, journalError);
                return Result(LuaPatchRingCommitStatus.JournalFailed, journalError);
            }

            if (!TryJournal(options, Entry(LuaPatchJournalPhase.Publishing), out journalError))
            {
                RollbackSessions(sessions, targetResults, journalError);
                return Result(LuaPatchRingCommitStatus.JournalFailed, journalError);
            }

            foreach (var pair in sessions)
            {
                var failure = pair.Session.Publish(cancellationToken);
                if (failure is not null)
                {
                    targetResults[pair.Target.TargetId] = failure;
                    RollbackSessions(
                        sessions,
                        targetResults,
                        "Another state failed barrier publication.");
                    _ = TryJournal(
                        options,
                        Entry(LuaPatchJournalPhase.RolledBack, failure.Message),
                        out _);
                    return Result(
                        failure.Status switch
                        {
                            LuaPatchCommitStatus.Cancelled => LuaPatchRingCommitStatus.Cancelled,
                            LuaPatchCommitStatus.Deferred => LuaPatchRingCommitStatus.Deferred,
                            _ => LuaPatchRingCommitStatus.PublishFailed,
                        },
                        failure.Message);
                }
            }

            try
            {
                foreach (var pair in sessions)
                {
                    pair.Session.FinalizePublication();
                }
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                RollbackSessions(sessions, targetResults, exception.Message);
                _ = TryJournal(
                    options,
                    Entry(LuaPatchJournalPhase.RolledBack, exception.Message),
                    out _);
                return Result(LuaPatchRingCommitStatus.PublishFailed, exception.Message);
            }

            foreach (var pair in sessions)
            {
                targetResults[pair.Target.TargetId] = pair.Session.BuildCommittedResult();
            }

            if (options.HealthCheck is not null)
            {
                LuaPatchRingHealthDecision decision;
                try
                {
                    decision = options.HealthCheck(new LuaPatchRingHealthContext(
                        rolloutId,
                        ring.Name,
                        OrderedResults()));
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    decision = LuaPatchRingHealthDecision.Rollback;
                    journalError = exception.Message;
                }

                if (decision == LuaPatchRingHealthDecision.Rollback)
                {
                    var message = journalError ?? "The ring health check requested rollback.";
                    targetResults.Clear();
                    RollbackSessions(sessions, targetResults, message);
                    _ = TryJournal(
                        options,
                        Entry(LuaPatchJournalPhase.RolledBack, message),
                        out _);
                    return Result(LuaPatchRingCommitStatus.HealthRejected, message);
                }

                if (!Enum.IsDefined(decision))
                {
                    const string message = "The ring health check returned an invalid decision.";
                    targetResults.Clear();
                    RollbackSessions(sessions, targetResults, message);
                    _ = TryJournal(
                        options,
                        Entry(LuaPatchJournalPhase.RolledBack, message),
                        out _);
                    return Result(LuaPatchRingCommitStatus.HealthRejected, message);
                }
            }

            if (!TryJournal(options, Entry(LuaPatchJournalPhase.Committed), out journalError))
            {
                targetResults.Clear();
                RollbackSessions(sessions, targetResults, journalError);
                _ = TryJournal(
                    options,
                    Entry(LuaPatchJournalPhase.RolledBack, journalError),
                    out _);
                return Result(LuaPatchRingCommitStatus.JournalFailed, journalError);
            }

            return Result(LuaPatchRingCommitStatus.Committed, null);
        }
        finally
        {
            foreach (var pair in sessions)
            {
                pair.Session.Dispose();
            }

            for (var index = windows.Count - 1; index >= 0; index--)
            {
                windows[index].Window.Dispose();
            }
        }

        LuaPatchJournalEntry Entry(LuaPatchJournalPhase phase, string? message = null) => new()
        {
            Timestamp = options.TimeProvider.GetUtcNow(),
            TransactionId = transactionId,
            RolloutId = rolloutId,
            RingName = ring.Name,
            PatchId = patch.PatchId,
            TargetRevision = patch.TargetRevision,
            Phase = phase,
            TargetIds = targetIds,
            Message = message,
        };

        ImmutableArray<LuaPatchTargetCommitResult> OrderedResults() => ordered
            .Select(target => new LuaPatchTargetCommitResult(
                target.TargetId,
                targetResults[target.TargetId]))
            .ToImmutableArray();

        LuaPatchRingCommitResult Result(LuaPatchRingCommitStatus status, string? message)
        {
            var result = new LuaPatchRingCommitResult(
                rolloutId,
                ring.Name,
                transactionId,
                status,
                OrderedResults(),
                message);
            var duration = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            LuaPatchTelemetry.Complete(activity, status.ToString(), message);
            LuaPatchTelemetry.RecordRing(
                status.ToString(),
                duration,
                result.Targets.Any(static target =>
                    target.Commit.SideEffectsMayHaveOccurred && !target.Commit.Succeeded));
            return result;
        }
    }

    private static void RollbackSessions(
        IEnumerable<(LuaPatchDeploymentTarget Target, LuaHost.PatchCommitSession Session)> sessions,
        Dictionary<string, LuaPatchCommitResult> results,
        string? message)
    {
        foreach (var pair in sessions.Reverse())
        {
            results[pair.Target.TargetId] = pair.Session.Rollback(
                LuaPatchCommitStatus.BarrierAborted,
                message,
                sideEffectsMayHaveOccurred: true);
        }
    }

    private static bool TryJournal(
        LuaPatchCoordinatorOptions options,
        LuaPatchJournalEntry entry,
        out string? error)
    {
        if (options.Journal is null)
        {
            error = null;
            return true;
        }

        try
        {
            options.Journal.Append(entry);
            error = null;
            return true;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OperationCanceledException and
        not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;

    private static void ThrowIfReentrant()
    {
        if (_operationActive)
        {
            throw new InvalidOperationException(
                "A patch coordinator operation cannot be entered recursively.");
        }
    }

    private static LuaPatchCommitResult NotExecutedResult(
        LuaPreparedPatch patch,
        LuaPatchCommitStatus status,
        string? message) => new(
        patch.Manifest.PatchId,
        status,
        patch.Modules.Select(static module => new LuaPatchModuleCommitResult(
            module.ModuleName,
            LuaPatchModuleCommitStatus.NotExecuted,
            module.ExpectedRevision,
            null,
            null,
            null,
            null,
            null,
            0,
            0,
            0,
            0)).ToImmutableArray(),
        message,
        SideEffectsMayHaveOccurred: false,
        TimeSpan.Zero);

    private static void ValidatePlan(
        LuaPatchRolloutPlan plan,
        LuaPatchCoordinatorOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.RolloutId);
        ArgumentNullException.ThrowIfNull(options.ResourceLimits);
        options.ResourceLimits.Validate();
        if (plan.Rings.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A rollout requires at least one ring.", nameof(plan));
        }

        LuaPatchResourceLimits.EnsureWithin(
            nameof(options.ResourceLimits.MaximumRingsPerRollout),
            plan.Rings.Length,
            options.ResourceLimits.MaximumRingsPerRollout);
        LuaPatchResourceLimits.EnsureWithin(
            nameof(options.ResourceLimits.MaximumTargetsPerRollout),
            plan.Rings.Sum(static ring => (long)ring.Targets.Length),
            options.ResourceLimits.MaximumTargetsPerRollout);

        var names = new HashSet<string>(StringComparer.Ordinal);
        var targetIds = new HashSet<string>(StringComparer.Ordinal);
        var hosts = new HashSet<LuaHost>(ReferenceEqualityComparer.Instance);
        string? patchIdentity = null;
        foreach (var ring in plan.Rings)
        {
            ValidateRing(ring, options);
            if (!names.Add(ring.Name))
            {
                throw new ArgumentException("Rollout ring names must be unique.", nameof(plan));
            }

            foreach (var target in ring.Targets)
            {
                if (!targetIds.Add(target.TargetId) || !hosts.Add(target.Host))
                {
                    throw new ArgumentException(
                        "Target ids and LuaHost instances must be unique across a rollout.",
                        nameof(plan));
                }

                var identity = GetPatchIdentity(target.PreparedPatch.Manifest);
                patchIdentity ??= identity;
                if (!string.Equals(patchIdentity, identity, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "Every rollout ring must deploy the same canonical patch manifest.",
                        nameof(plan));
                }
            }
        }
    }

    private static void ValidateRing(
        LuaPatchRolloutRing ring,
        LuaPatchCoordinatorOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ring.Name);
        ArgumentNullException.ThrowIfNull(options.UpdateWindow);
        ArgumentNullException.ThrowIfNull(options.Commit);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        ArgumentNullException.ThrowIfNull(options.ResourceLimits);
        options.ResourceLimits.Validate();
        if (ring.Targets.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A rollout ring requires at least one target.", nameof(ring));
        }

        LuaPatchResourceLimits.EnsureWithin(
            nameof(options.ResourceLimits.MaximumTargetsPerRing),
            ring.Targets.Length,
            options.ResourceLimits.MaximumTargetsPerRing);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var hosts = new HashSet<LuaHost>(ReferenceEqualityComparer.Instance);
        string? patchIdentity = null;
        foreach (var target in ring.Targets)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(target.TargetId);
            ArgumentNullException.ThrowIfNull(target.Host);
            ArgumentNullException.ThrowIfNull(target.PreparedPatch);
            if (!ids.Add(target.TargetId) || !hosts.Add(target.Host))
            {
                throw new ArgumentException(
                    "Target ids and LuaHost instances must be unique within a ring.",
                    nameof(ring));
            }

            if (!ReferenceEquals(target.PreparedPatch.Owner, target.Host))
            {
                throw new ArgumentException(
                    $"Prepared patch for target '{target.TargetId}' belongs to another host.",
                    nameof(ring));
            }

            var identity = GetPatchIdentity(target.PreparedPatch.Manifest);
            patchIdentity ??= identity;
            if (!string.Equals(
                patchIdentity,
                identity,
                StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Every target in a barrier ring must use the same canonical patch manifest.",
                    nameof(ring));
            }
        }
    }

    private static string GetPatchIdentity(LuaPatchManifest manifest) => Convert.ToHexString(
        SHA256.HashData(LuaPatchManifestSerializer.Serialize(manifest)));
}
