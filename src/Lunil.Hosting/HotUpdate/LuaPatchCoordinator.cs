using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Lunil.Hosting;

public sealed record LuaPatchDeploymentTarget(
    string TargetId,
    LuaHost Host,
    LuaPreparedPatch PreparedPatch)
{
    /// <summary>
    /// Optional traffic-isolation and quiescence adapter for this deployment target.
    /// </summary>
    public ILuaPatchTargetLifecycle? Lifecycle { get; init; }
}

/// <summary>Timeouts used by a target lifecycle adapter.</summary>
public sealed record LuaPatchTargetLifecycleOptions
{
    public static LuaPatchTargetLifecycleOptions Default { get; } = new();

    public TimeSpan IsolationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan QuiescenceTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan RestoreTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Stable transaction identity and deadline budget supplied to lifecycle adapters.</summary>
public sealed record LuaPatchTargetLifecycleContext(
    string TransactionId,
    string RolloutId,
    string RingName,
    string TargetId,
    string PatchId,
    string TargetRevision,
    TimeSpan Timeout);

public enum LuaPatchTargetIsolationStatus : byte
{
    Isolated,
    Deferred,
    Cancelled,
    Failed,
}

public sealed record LuaPatchTargetIsolationResult(
    LuaPatchTargetIsolationStatus Status,
    ILuaPatchTargetIsolation? Isolation,
    string? Message)
{
    public bool Succeeded => Status == LuaPatchTargetIsolationStatus.Isolated &&
        Isolation is not null;
}

public enum LuaPatchTargetQuiescenceStatus : byte
{
    Quiescent,
    Deferred,
    Cancelled,
    Failed,
}

public sealed record LuaPatchTargetQuiescenceResult(
    LuaPatchTargetQuiescenceStatus Status,
    string? Message)
{
    public bool Succeeded => Status == LuaPatchTargetQuiescenceStatus.Quiescent;
}

public enum LuaPatchTargetRestoreOutcome : byte
{
    Committed,
    RolledBack,
}

public sealed record LuaPatchTargetRestoreContext(
    LuaPatchTargetLifecycleContext Target,
    LuaPatchTargetRestoreOutcome Outcome,
    string? Message);

public enum LuaPatchTargetRestoreStatus : byte
{
    Restored,
    Failed,
}

public sealed record LuaPatchTargetRestoreResult(
    LuaPatchTargetRestoreStatus Status,
    string? Message)
{
    public bool Succeeded => Status == LuaPatchTargetRestoreStatus.Restored;
}

/// <summary>
/// Stops new target traffic before returning an isolation session. The coordinator then requests
/// quiescence before entering the host update window.
/// </summary>
public interface ILuaPatchTargetLifecycle
{
    LuaPatchTargetIsolationResult TryIsolate(
        LuaPatchTargetLifecycleContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// One isolated target. Restore must be idempotent for the supplied transaction identity.
/// Dispose releases adapter resources only and must not change traffic routing.
/// </summary>
public interface ILuaPatchTargetIsolation : IDisposable
{
    LuaPatchTargetQuiescenceResult WaitForQuiescence(
        LuaPatchTargetLifecycleContext context,
        CancellationToken cancellationToken);

    LuaPatchTargetRestoreResult Restore(
        LuaPatchTargetRestoreContext context,
        CancellationToken cancellationToken);
}

public enum LuaPatchTargetLifecycleStatus : byte
{
    NotConfigured,
    Isolated,
    Quiescent,
    Restored,
    IsolationDeferred,
    IsolationCancelled,
    IsolationFailed,
    QuiescenceDeferred,
    QuiescenceCancelled,
    QuiescenceFailed,
    RestoreFailed,
}

public sealed record LuaPatchTargetLifecycleResult(
    LuaPatchTargetLifecycleStatus Status,
    string? Message)
{
    /// <summary>
    /// Earlier failed stage when cleanup subsequently restored the target successfully.
    /// </summary>
    public LuaPatchTargetLifecycleStatus? Failure { get; init; }
}

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

    public LuaPatchTargetLifecycleOptions TargetLifecycle { get; init; } =
        LuaPatchTargetLifecycleOptions.Default;

    /// <summary>Rejects rollout plans containing a target without a lifecycle adapter.</summary>
    public bool RequireTargetIsolation { get; init; }

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
    ReplayFailed,
    IsolationFailed,
    QuiescenceFailed,
    RestoreFailed,
}

public sealed record LuaPatchTargetCommitResult(
    string TargetId,
    LuaPatchCommitResult Commit)
{
    public LuaPatchTargetLifecycleResult Lifecycle { get; init; } = new(
        LuaPatchTargetLifecycleStatus.NotConfigured,
        null);
}

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
    Restoring,
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
        var isolations = new List<(
            LuaPatchDeploymentTarget Target,
            ILuaPatchTargetIsolation Isolation)>();
        var windows = new List<(LuaPatchDeploymentTarget Target, LuaPatchUpdateWindow Window)>();
        var sessions = new List<(LuaPatchDeploymentTarget Target, LuaHost.PatchCommitSession Session)>();
        var targetResults = ordered.ToDictionary(
            static target => target.TargetId,
            static target => NotExecutedResult(
                target.PreparedPatch,
                LuaPatchCommitStatus.BarrierAborted,
                "The barrier did not reach this target."),
            StringComparer.Ordinal);
        var lifecycleResults = ordered.ToDictionary(
            static target => target.TargetId,
            static _ => new LuaPatchTargetLifecycleResult(
                LuaPatchTargetLifecycleStatus.NotConfigured,
                null),
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
                if (target.Lifecycle is null)
                {
                    continue;
                }

                var context = LifecycleContext(target, options.TargetLifecycle.IsolationTimeout);
                LuaPatchTargetIsolationResult isolation;
                try
                {
                    isolation = target.Lifecycle.TryIsolate(context, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    isolation = new LuaPatchTargetIsolationResult(
                        LuaPatchTargetIsolationStatus.Cancelled,
                        null,
                        "Target isolation was cancelled.");
                }
                catch (OperationCanceledException exception)
                {
                    isolation = new LuaPatchTargetIsolationResult(
                        LuaPatchTargetIsolationStatus.Failed,
                        null,
                        exception.Message);
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    isolation = new LuaPatchTargetIsolationResult(
                        LuaPatchTargetIsolationStatus.Failed,
                        null,
                        exception.Message);
                }

                if (!isolation.Succeeded)
                {
                    var malformed = isolation.Status == LuaPatchTargetIsolationStatus.Isolated ||
                        isolation.Isolation is not null;
                    if (isolation.Isolation is not null)
                    {
                        isolations.Add((target, isolation.Isolation));
                    }

                    var message = malformed
                        ? "The target lifecycle returned an invalid isolation result."
                        : isolation.Message;
                    var (ringStatus, lifecycleStatus, commitStatus) = malformed
                        ? (
                            LuaPatchRingCommitStatus.IsolationFailed,
                            LuaPatchTargetLifecycleStatus.IsolationFailed,
                            LuaPatchCommitStatus.BarrierAborted)
                        : isolation.Status switch
                        {
                            LuaPatchTargetIsolationStatus.Cancelled => (
                                LuaPatchRingCommitStatus.Cancelled,
                                LuaPatchTargetLifecycleStatus.IsolationCancelled,
                                LuaPatchCommitStatus.Cancelled),
                            LuaPatchTargetIsolationStatus.Deferred => (
                                LuaPatchRingCommitStatus.Deferred,
                                LuaPatchTargetLifecycleStatus.IsolationDeferred,
                                LuaPatchCommitStatus.Deferred),
                            _ => (
                                LuaPatchRingCommitStatus.IsolationFailed,
                                LuaPatchTargetLifecycleStatus.IsolationFailed,
                                LuaPatchCommitStatus.BarrierAborted),
                        };
                    lifecycleResults[target.TargetId] = new(lifecycleStatus, message);
                    targetResults[target.TargetId] = NotExecutedResult(
                        target.PreparedPatch,
                        commitStatus,
                        message);
                    return FinishBeforeCommit(
                        ringStatus,
                        message,
                        LuaPatchJournalPhase.Failed);
                }

                isolations.Add((target, isolation.Isolation!));
                lifecycleResults[target.TargetId] = new(
                    LuaPatchTargetLifecycleStatus.Isolated,
                    isolation.Message);
            }

            foreach (var pair in isolations)
            {
                var target = pair.Target;
                var quiescenceContext = LifecycleContext(
                    target,
                    options.TargetLifecycle.QuiescenceTimeout);
                LuaPatchTargetQuiescenceResult quiescence;
                try
                {
                    quiescence = pair.Isolation.WaitForQuiescence(
                        quiescenceContext,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    quiescence = new LuaPatchTargetQuiescenceResult(
                        LuaPatchTargetQuiescenceStatus.Cancelled,
                        "Target quiescence was cancelled.");
                }
                catch (OperationCanceledException exception)
                {
                    quiescence = new LuaPatchTargetQuiescenceResult(
                        LuaPatchTargetQuiescenceStatus.Failed,
                        exception.Message);
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    quiescence = new LuaPatchTargetQuiescenceResult(
                        LuaPatchTargetQuiescenceStatus.Failed,
                        exception.Message);
                }

                if (!quiescence.Succeeded)
                {
                    var (ringStatus, lifecycleStatus, commitStatus) = quiescence.Status switch
                    {
                        LuaPatchTargetQuiescenceStatus.Cancelled => (
                            LuaPatchRingCommitStatus.Cancelled,
                            LuaPatchTargetLifecycleStatus.QuiescenceCancelled,
                            LuaPatchCommitStatus.Cancelled),
                        LuaPatchTargetQuiescenceStatus.Deferred => (
                            LuaPatchRingCommitStatus.Deferred,
                            LuaPatchTargetLifecycleStatus.QuiescenceDeferred,
                            LuaPatchCommitStatus.Deferred),
                        _ => (
                            LuaPatchRingCommitStatus.QuiescenceFailed,
                            LuaPatchTargetLifecycleStatus.QuiescenceFailed,
                            LuaPatchCommitStatus.BarrierAborted),
                    };
                    lifecycleResults[target.TargetId] = new(lifecycleStatus, quiescence.Message);
                    targetResults[target.TargetId] = NotExecutedResult(
                        target.PreparedPatch,
                        commitStatus,
                        quiescence.Message);
                    return FinishBeforeCommit(
                        ringStatus,
                        quiescence.Message,
                        LuaPatchJournalPhase.Failed);
                }

                lifecycleResults[target.TargetId] = new(
                    LuaPatchTargetLifecycleStatus.Quiescent,
                    quiescence.Message);
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
                    return FinishBeforeCommit(
                        status,
                        opened.Message,
                        LuaPatchJournalPhase.Failed);
                }

                windows.Add((target, opened.Window!));
            }

            foreach (var pair in windows)
            {
                var preparation = pair.Target.Host.PreparePatchCommitSession(
                    pair.Target.PreparedPatch,
                    pair.Window,
                    options.Commit,
                    cancellationToken,
                    options.TimeProvider);
                if (preparation.Failure is not null)
                {
                    targetResults[pair.Target.TargetId] = preparation.Failure;
                    RollbackSessions(
                        sessions,
                        targetResults,
                        "Another state failed barrier preparation.");
                    return FinishBeforeCommit(
                        preparation.Failure.Status switch
                        {
                            LuaPatchCommitStatus.Cancelled => LuaPatchRingCommitStatus.Cancelled,
                            LuaPatchCommitStatus.Deferred => LuaPatchRingCommitStatus.Deferred,
                            _ => LuaPatchRingCommitStatus.PrepareFailed,
                        },
                        preparation.Failure.Message,
                        LuaPatchJournalPhase.RolledBack);
                }

                sessions.Add((pair.Target, preparation.Session!));
            }

            if (!TryJournal(options, Entry(LuaPatchJournalPhase.Prepared), out journalError))
            {
                RollbackSessions(sessions, targetResults, journalError);
                return FinishBeforeCommit(
                    LuaPatchRingCommitStatus.JournalFailed,
                    journalError,
                    terminalPhase: null);
            }

            if (!TryJournal(options, Entry(LuaPatchJournalPhase.Publishing), out journalError))
            {
                RollbackSessions(sessions, targetResults, journalError);
                return FinishBeforeCommit(
                    LuaPatchRingCommitStatus.JournalFailed,
                    journalError,
                    terminalPhase: null);
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
                    return FinishBeforeCommit(
                        failure.Status switch
                        {
                            LuaPatchCommitStatus.Cancelled => LuaPatchRingCommitStatus.Cancelled,
                            LuaPatchCommitStatus.Deferred => LuaPatchRingCommitStatus.Deferred,
                            _ => LuaPatchRingCommitStatus.PublishFailed,
                        },
                        failure.Message,
                        LuaPatchJournalPhase.RolledBack);
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
                return FinishBeforeCommit(
                    LuaPatchRingCommitStatus.PublishFailed,
                    exception.Message,
                    LuaPatchJournalPhase.RolledBack);
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
                    return FinishBeforeCommit(
                        LuaPatchRingCommitStatus.HealthRejected,
                        message,
                        LuaPatchJournalPhase.RolledBack);
                }

                if (!Enum.IsDefined(decision))
                {
                    const string message = "The ring health check returned an invalid decision.";
                    targetResults.Clear();
                    RollbackSessions(sessions, targetResults, message);
                    return FinishBeforeCommit(
                        LuaPatchRingCommitStatus.HealthRejected,
                        message,
                        LuaPatchJournalPhase.RolledBack);
                }
            }

            try
            {
                var committedAt = options.TimeProvider.GetUtcNow();
                foreach (var pair in sessions)
                {
                    pair.Session.CompleteReplayAcceptance(committedAt);
                }
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                targetResults.Clear();
                RollbackSessions(sessions, targetResults, exception.Message);
                return FinishBeforeCommit(
                    LuaPatchRingCommitStatus.ReplayFailed,
                    exception.Message,
                    LuaPatchJournalPhase.RolledBack);
            }

            if (isolations.Count != 0)
            {
                if (!TryJournal(
                    options,
                    Entry(LuaPatchJournalPhase.Restoring),
                    out journalError))
                {
                    targetResults.Clear();
                    RollbackSessions(sessions, targetResults, journalError);
                    return FinishBeforeCommit(
                        LuaPatchRingCommitStatus.JournalFailed,
                        journalError,
                        LuaPatchJournalPhase.RolledBack);
                }

                var restoreError = RestoreTargets(
                    LuaPatchTargetRestoreOutcome.Committed,
                    null);
                if (restoreError is not null)
                {
                    return Result(LuaPatchRingCommitStatus.RestoreFailed, restoreError);
                }

                if (!TryJournal(options, Entry(LuaPatchJournalPhase.Committed), out journalError))
                {
                    return Result(LuaPatchRingCommitStatus.JournalFailed, journalError);
                }
            }
            else if (!TryJournal(
                options,
                Entry(LuaPatchJournalPhase.Committed),
                out journalError))
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

            CloseUpdateWindows();

            foreach (var pair in isolations)
            {
                pair.Isolation.Dispose();
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
                targetResults[target.TargetId])
            {
                Lifecycle = lifecycleResults[target.TargetId],
            })
            .ToImmutableArray();

        LuaPatchTargetLifecycleContext LifecycleContext(
            LuaPatchDeploymentTarget target,
            TimeSpan timeout) => new(
                transactionId,
                rolloutId,
                ring.Name,
                target.TargetId,
                patch.PatchId,
                patch.TargetRevision,
                timeout);

        LuaPatchRingCommitResult FinishBeforeCommit(
            LuaPatchRingCommitStatus status,
            string? message,
            LuaPatchJournalPhase? terminalPhase)
        {
            var restoreError = RestoreTargets(LuaPatchTargetRestoreOutcome.RolledBack, message);
            if (restoreError is not null)
            {
                return Result(LuaPatchRingCommitStatus.RestoreFailed, restoreError);
            }

            if (terminalPhase is { } phase &&
                !TryJournal(options, Entry(phase, message), out var terminalError))
            {
                return Result(LuaPatchRingCommitStatus.JournalFailed, terminalError);
            }

            return Result(status, message);
        }

        string? RestoreTargets(LuaPatchTargetRestoreOutcome outcome, string? message)
        {
            CloseUpdateWindows();
            List<string>? errors = null;
            for (var index = isolations.Count - 1; index >= 0; index--)
            {
                var pair = isolations[index];
                LuaPatchTargetRestoreResult restoration;
                try
                {
                    restoration = pair.Isolation.Restore(
                        new LuaPatchTargetRestoreContext(
                            LifecycleContext(
                                pair.Target,
                                options.TargetLifecycle.RestoreTimeout),
                            outcome,
                            message),
                        CancellationToken.None);
                }
                catch (OperationCanceledException exception)
                {
                    restoration = new LuaPatchTargetRestoreResult(
                        LuaPatchTargetRestoreStatus.Failed,
                        exception.Message);
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    restoration = new LuaPatchTargetRestoreResult(
                        LuaPatchTargetRestoreStatus.Failed,
                        exception.Message);
                }

                if (restoration.Succeeded)
                {
                    var previous = lifecycleResults[pair.Target.TargetId];
                    var failure = previous.Failure ??
                        (previous.Status is LuaPatchTargetLifecycleStatus.IsolationDeferred or
                            LuaPatchTargetLifecycleStatus.IsolationCancelled or
                            LuaPatchTargetLifecycleStatus.IsolationFailed or
                            LuaPatchTargetLifecycleStatus.QuiescenceDeferred or
                            LuaPatchTargetLifecycleStatus.QuiescenceCancelled or
                            LuaPatchTargetLifecycleStatus.QuiescenceFailed or
                            LuaPatchTargetLifecycleStatus.RestoreFailed
                                ? previous.Status
                                : null);
                    lifecycleResults[pair.Target.TargetId] = new(
                        LuaPatchTargetLifecycleStatus.Restored,
                        restoration.Message ?? previous.Message)
                    {
                        Failure = failure,
                    };
                    continue;
                }

                var error = restoration.Message ?? "The target lifecycle failed to restore traffic.";
                lifecycleResults[pair.Target.TargetId] = new(
                    LuaPatchTargetLifecycleStatus.RestoreFailed,
                    error)
                {
                    Failure = LuaPatchTargetLifecycleStatus.RestoreFailed,
                };
                (errors ??= []).Add($"{pair.Target.TargetId}: {error}");
            }

            return errors is null
                ? null
                : "One or more deployment targets could not restore traffic: " +
                    string.Join("; ", errors);
        }

        void CloseUpdateWindows()
        {
            for (var index = windows.Count - 1; index >= 0; index--)
            {
                windows[index].Window.Dispose();
            }

            windows.Clear();
        }

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
                status == LuaPatchRingCommitStatus.RestoreFailed ||
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
        ArgumentNullException.ThrowIfNull(options.TargetLifecycle);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        ArgumentNullException.ThrowIfNull(options.ResourceLimits);
        options.ResourceLimits.Validate();
        ValidateLifecycleTimeout(
            options.TargetLifecycle.IsolationTimeout,
            nameof(options.TargetLifecycle.IsolationTimeout));
        ValidateLifecycleTimeout(
            options.TargetLifecycle.QuiescenceTimeout,
            nameof(options.TargetLifecycle.QuiescenceTimeout));
        ValidateLifecycleTimeout(
            options.TargetLifecycle.RestoreTimeout,
            nameof(options.TargetLifecycle.RestoreTimeout));
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
            if (options.RequireTargetIsolation && target.Lifecycle is null)
            {
                throw new ArgumentException(
                    $"Target '{target.TargetId}' requires a lifecycle adapter.",
                    nameof(ring));
            }
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

            if (target.PreparedPatch.ReplayScope is { } replayScope &&
                !string.Equals(replayScope, target.TargetId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Prepared patch replay scope '{replayScope}' does not match target " +
                    $"'{target.TargetId}'.",
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

    private static void ValidateLifecycleTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                timeout,
                "A target lifecycle timeout must be non-negative or infinite.");
        }
    }

    private static string GetPatchIdentity(LuaPatchManifest manifest) => Convert.ToHexString(
        SHA256.HashData(LuaPatchManifestSerializer.Serialize(manifest)));
}
