using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Lunil.Hosting;

/// <summary>Hard limits applied after bundle verification and before live patch execution.</summary>
public sealed record LuaPatchResourceLimits
{
    public static LuaPatchResourceLimits Default { get; } = new();

    public int MaximumPatchModules { get; init; } = 512;

    public int MaximumMigrationSchemaBytes { get; init; } = 1024 * 1024;

    public int MaximumMigrationModules { get; init; } = 512;

    public int MaximumStateMigrationRules { get; init; } = 8192;

    public int MaximumResourceMigrationRules { get; init; } = 8192;

    /// <summary>Gets the maximum entries retained by table patch journals.</summary>
    public int MaximumTablePatchEntryCount { get; init; } = 65_536;

    public int MaximumRingsPerRollout { get; init; } = 16;

    public int MaximumTargetsPerRing { get; init; } = 256;

    public int MaximumTargetsPerRollout { get; init; } = 1024;

    internal void Validate()
    {
        if (MaximumPatchModules <= 0 || MaximumMigrationSchemaBytes <= 0 ||
            MaximumMigrationModules <= 0 || MaximumStateMigrationRules <= 0 ||
            MaximumResourceMigrationRules <= 0 || MaximumTablePatchEntryCount <= 0 ||
            MaximumRingsPerRollout <= 0 ||
            MaximumTargetsPerRing <= 0 || MaximumTargetsPerRollout <= 0 ||
            MaximumTargetsPerRing > MaximumTargetsPerRollout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LuaPatchResourceLimits),
                "Patch resource limits must be positive and internally consistent.");
        }
    }

    internal static void EnsureWithin(string limitName, long observed, long maximum)
    {
        if (observed > maximum)
        {
            throw new LuaPatchResourceLimitException(limitName, observed, maximum);
        }
    }
}

public sealed class LuaPatchResourceLimitException : Exception
{
    public LuaPatchResourceLimitException(string limitName, long observed, long maximum)
        : base($"Patch resource limit '{limitName}' was exceeded: {observed} > {maximum}.")
    {
        LimitName = limitName;
        Observed = observed;
        Maximum = maximum;
    }

    public string LimitName { get; }

    public long Observed { get; }

    public long Maximum { get; }
}

/// <summary>Stable diagnostics source names for hot-update traces and metrics.</summary>
public static class LuaPatchTelemetry
{
    public const string ActivitySourceName = "Lunil.Hosting.HotUpdate";

    public const string MeterName = "Lunil.Hosting.HotUpdate";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    internal static readonly Meter Meter = new(MeterName);
    internal static readonly Counter<long> Preparations = Meter.CreateCounter<long>(
        "lunil.patch.preparations",
        description: "Patch preparation attempts.");
    internal static readonly Counter<long> Commits = Meter.CreateCounter<long>(
        "lunil.patch.commits",
        description: "Single-State patch commit outcomes.");
    internal static readonly Counter<long> Rings = Meter.CreateCounter<long>(
        "lunil.patch.rings",
        description: "Multi-State barrier ring outcomes.");
    internal static readonly Counter<long> Rollbacks = Meter.CreateCounter<long>(
        "lunil.patch.rollbacks",
        description: "Patch transactions that requested atomic rollback.");
    internal static readonly Counter<long> Recoveries = Meter.CreateCounter<long>(
        "lunil.patch.recoveries",
        description: "Crash recovery resolutions.");
    internal static readonly Histogram<double> PrepareDuration = Meter.CreateHistogram<double>(
        "lunil.patch.prepare.duration",
        unit: "ms",
        description: "Patch preparation duration.");
    internal static readonly Histogram<double> CommitPauseDuration = Meter.CreateHistogram<double>(
        "lunil.patch.commit.pause.duration",
        unit: "ms",
        description: "Time spent inside a single-State commit safe point.");
    internal static readonly Histogram<double> RingDuration = Meter.CreateHistogram<double>(
        "lunil.patch.ring.duration",
        unit: "ms",
        description: "Barrier ring duration including health and durable journal gates.");

    internal static Activity? Start(
        string operation,
        string? patchId = null,
        string? rolloutId = null,
        string? ringName = null)
    {
        var activity = ActivitySource.StartActivity(operation, ActivityKind.Internal);
        activity?.SetTag("lunil.patch.id", patchId);
        activity?.SetTag("lunil.rollout.id", rolloutId);
        activity?.SetTag("lunil.ring.name", ringName);
        return activity;
    }

    internal static void Complete(Activity? activity, string status, string? message = null)
    {
        activity?.SetTag("lunil.patch.status", status);
        if (message is not null)
        {
            activity?.SetTag("error.message", message);
        }

        activity?.SetStatus(IsSuccessfulStatus(status)
            ? ActivityStatusCode.Ok
            : ActivityStatusCode.Error,
            message);
    }

    internal static void Failed(Activity? activity, Exception exception)
    {
        activity?.SetTag("error.type", exception.GetType().FullName);
        activity?.SetTag("error.message", exception.Message);
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
    }

    internal static void RecordPreparation(string status, TimeSpan duration)
    {
        Preparations.Add(1, new KeyValuePair<string, object?>("lunil.patch.status", status));
        PrepareDuration.Record(
            duration.TotalMilliseconds,
            new KeyValuePair<string, object?>("lunil.patch.status", status));
    }

    internal static void RecordCommit(string status, TimeSpan duration, bool rollbackAttempted)
    {
        Commits.Add(1, new KeyValuePair<string, object?>("lunil.patch.status", status));
        CommitPauseDuration.Record(
            duration.TotalMilliseconds,
            new KeyValuePair<string, object?>("lunil.patch.status", status));
        if (rollbackAttempted)
        {
            Rollbacks.Add(1, new KeyValuePair<string, object?>("lunil.patch.status", status));
        }
    }

    internal static void RecordRing(string status, TimeSpan duration, bool rollbackAttempted)
    {
        Rings.Add(1, new KeyValuePair<string, object?>("lunil.patch.status", status));
        RingDuration.Record(
            duration.TotalMilliseconds,
            new KeyValuePair<string, object?>("lunil.patch.status", status));
        if (rollbackAttempted)
        {
            Rollbacks.Add(1, new KeyValuePair<string, object?>("lunil.patch.status", status));
        }
    }

    internal static void RecordRecovery(string status) => Recoveries.Add(
        1,
        new KeyValuePair<string, object?>("lunil.patch.status", status));

    private static bool IsSuccessfulStatus(string status) =>
        string.Equals(status, "Ready", StringComparison.Ordinal) ||
        string.Equals(status, "Committed", StringComparison.Ordinal) ||
        string.Equals(status, "Accept", StringComparison.Ordinal) ||
        string.Equals(status, "RecoveredCommitted", StringComparison.Ordinal) ||
        string.Equals(status, "RecoveredRolledBack", StringComparison.Ordinal);
}
