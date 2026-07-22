using System.Collections.Immutable;
using Lunil.Compiler;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.Execution;

namespace Lunil.Hosting;

/// <summary>Outcome of binding an isolated patch preflight to one live host revision.</summary>
public enum LuaPatchPrepareStatus : byte
{
    Ready,
    PreflightFailed,
    LanguageVersionMismatch,
    ModuleNotLoaded,
    UnsupportedCachePolicy,
    MigrationAdapterMissing,
    StateSchemaVersionMismatch,
    AcceptanceRejected,
}

/// <summary>Per-module evidence captured while a patch is prepared.</summary>
public sealed record LuaPatchModulePrepareResult(
    string ModuleName,
    LuaPatchPrepareStatus Status,
    long? ExpectedRevision,
    LuaPatchModulePreflightResult Preflight,
    string? Message)
{
    public bool Succeeded => Status == LuaPatchPrepareStatus.Ready;
}

/// <summary>Options used to prepare a verified bundle without executing code on the live state.</summary>
public sealed record LuaPatchPrepareOptions
{
    public static LuaPatchPrepareOptions Default { get; } = new();

    public ILuaPatchCanonicalIrDecoder? CanonicalIrDecoder { get; init; }

    /// <summary>
    /// Optional per-module cache policies. Modules not present use
    /// <see cref="LuaModuleReloadOptions.Default"/>. Source-path overrides are not accepted because
    /// patch payloads are supplied by the verified bundle.
    /// </summary>
    public IReadOnlyDictionary<string, LuaModuleReloadOptions>? ModuleOptions { get; init; }

    public IReadOnlyDictionary<string, ILuaPatchStateMigrationAdapter>?
    StateMigrationAdapters
    { get; init; }

    public IReadOnlyDictionary<string, ILuaPatchResourceMigrationAdapter>?
        ResourceMigrationAdapters
    { get; init; }

    public LuaPatchResourceLimits ResourceLimits { get; init; } = LuaPatchResourceLimits.Default;

    /// <summary>
    /// Optional deployment acceptance policy. It must be configured together with
    /// <see cref="ReplayStore"/> so preparation cannot rely on a non-atomic replay lookup.
    /// </summary>
    public LuaPatchAcceptancePolicy? AcceptancePolicy { get; init; }

    /// <summary>Atomic, durable replay store paired with <see cref="AcceptancePolicy"/>.</summary>
    public ILuaPatchReplayStore? ReplayStore { get; init; }

    /// <summary>Clock used for acceptance timestamps and time-based policy checks.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// An immutable, host-bound patch whose source or chunk payloads were decoded in an isolated host.
/// Expected revisions are checked again immediately before any candidate code is executed.
/// </summary>
public sealed class LuaPreparedPatch
{
    internal LuaPreparedPatch(
        LuaHost owner,
        LuaPatchManifest manifest,
        LuaPatchDependencyPlan dependencyPlan,
        ImmutableArray<LuaPreparedPatchModule> modules,
        LuaPatchMigrationSchema? migrationSchema,
        string? expectedStateSchemaVersion,
        IReadOnlyDictionary<string, ILuaPatchStateMigrationAdapter> stateMigrationAdapters,
        IReadOnlyDictionary<string, ILuaPatchResourceMigrationAdapter> resourceMigrationAdapters)
    {
        Owner = owner;
        Manifest = manifest;
        DependencyPlan = dependencyPlan;
        Modules = modules;
        MigrationSchema = migrationSchema;
        ExpectedStateSchemaVersion = expectedStateSchemaVersion;
        StateMigrationAdapters = stateMigrationAdapters;
        ResourceMigrationAdapters = resourceMigrationAdapters;
    }

    public LuaPatchManifest Manifest { get; }

    public LuaPatchDependencyPlan DependencyPlan { get; }

    public ImmutableArray<LuaPreparedPatchModule> Modules { get; }

    public LuaPatchMigrationSchema? MigrationSchema { get; }

    public string? ExpectedStateSchemaVersion { get; }

    internal LuaHost Owner { get; }

    internal IReadOnlyDictionary<string, ILuaPatchStateMigrationAdapter>
    StateMigrationAdapters
    { get; }

    internal IReadOnlyDictionary<string, ILuaPatchResourceMigrationAdapter>
    ResourceMigrationAdapters
    { get; }
}

/// <summary>One precompiled module and the live revision against which it was prepared.</summary>
public sealed record LuaPreparedPatchModule(
    string ModuleName,
    LuaPatchEntryKind Kind,
    long ExpectedRevision,
    LuaIrModule Module,
    LuaCompilationResult? Compilation,
    LuaModuleReloadOptions ReloadOptions,
    LuaPatchModuleMigrationSchema? MigrationSchema);

/// <summary>Structured result of isolated compilation followed by live revision binding.</summary>
public sealed record LuaPatchPrepareResult(
    LuaPatchPrepareStatus Status,
    LuaPreparedPatch? PreparedPatch,
    LuaPatchPreflightResult Preflight,
    ImmutableArray<LuaPatchModulePrepareResult> Modules,
    string? Message)
{
    /// <summary>Acceptance evidence when preparation was configured with a policy.</summary>
    public LuaPatchAcceptanceResult? Acceptance { get; init; }

    public bool Succeeded => Status == LuaPatchPrepareStatus.Ready && PreparedPatch is not null;
}

/// <summary>Controls acquisition of a game-loop update window.</summary>
public sealed record LuaPatchUpdateWindowOptions
{
    public static LuaPatchUpdateWindowOptions Default { get; } = new();

    /// <summary>How long to wait for an execution already using the host.</summary>
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Maximum wall-clock duration reserved by this window. Use
    /// <see cref="Timeout.InfiniteTimeSpan"/> for no window-level deadline.
    /// </summary>
    public TimeSpan MaximumDuration { get; init; } = Timeout.InfiniteTimeSpan;
}

public enum LuaPatchUpdateWindowStatus : byte
{
    Opened,
    Deferred,
    Cancelled,
}

/// <summary>Result of a non-throwing update-window acquisition attempt.</summary>
public sealed record LuaPatchUpdateWindowResult(
    LuaPatchUpdateWindowStatus Status,
    LuaPatchUpdateWindow? Window,
    string? Message)
{
    public bool Succeeded => Status == LuaPatchUpdateWindowStatus.Opened && Window is not null;
}

/// <summary>
/// Same-thread game-loop safe point. The window excludes host execution until disposed.
/// </summary>
public sealed class LuaPatchUpdateWindow : IDisposable
{
    private LuaHost? _owner;

    internal LuaPatchUpdateWindow(LuaHost owner, TimeSpan maximumDuration)
    {
        _owner = owner;
        MaximumDuration = maximumDuration;
        StartedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        OwnerThreadId = Environment.CurrentManagedThreadId;
    }

    public TimeSpan MaximumDuration { get; }

    public TimeSpan Elapsed => System.Diagnostics.Stopwatch.GetElapsedTime(StartedTimestamp);

    public bool IsActive => Volatile.Read(ref _owner) is not null;

    internal long StartedTimestamp { get; }

    internal int OwnerThreadId { get; }

    internal LuaHost? Owner => Volatile.Read(ref _owner);

    public void Dispose()
    {
        if (Environment.CurrentManagedThreadId != OwnerThreadId && Owner is not null)
        {
            throw new InvalidOperationException(
                "A patch update window must be disposed by the thread that opened it.");
        }

        var owner = Interlocked.Exchange(ref _owner, null);
        owner?.ClosePatchUpdateWindow(this);
    }
}

/// <summary>Controls one atomic patch commit inside an update window.</summary>
public sealed record LuaPatchCommitOptions
{
    public static LuaPatchCommitOptions Default { get; } = new();

    /// <summary>
    /// Commit-specific pause budget. The effective deadline is the earlier of this value and the
    /// update-window deadline. Exceeding it rolls back all target-module publications.
    /// </summary>
    public TimeSpan MaximumPauseDuration { get; init; } = Timeout.InfiniteTimeSpan;
}

public enum LuaPatchCommitStatus : byte
{
    Committed,
    Deferred,
    Cancelled,
    RevisionConflict,
    ExecutionFailed,
    MigrationFailed,
    CachePolicyFailed,
    PublicationFailed,
    BarrierAborted,
    Expired,
}

public enum LuaPatchModuleCommitStatus : byte
{
    NotExecuted,
    RevisionConflict,
    Executed,
    Committed,
    ExecutionFailed,
    MigrationFailed,
    CachePolicyFailed,
    RolledBack,
}

/// <summary>Per-module result from one transaction.</summary>
public sealed record LuaPatchModuleCommitResult(
    string ModuleName,
    LuaPatchModuleCommitStatus Status,
    long ExpectedRevision,
    long? ObservedRevision,
    LuaModuleRecord? PreviousRecord,
    LuaModuleRecord? CurrentRecord,
    LuaExecutionResult? Execution,
    string? Message,
    int ReusedUpvalueCount,
    int UpvalueMismatchCount,
    int PatchedExportCount,
    int RemovedExportCount,
    ImmutableArray<LuaFunctionMigrationResult> FunctionMigrations = default);

/// <summary>Atomic publication outcome for every module in a prepared patch.</summary>
public sealed record LuaPatchCommitResult(
    string PatchId,
    LuaPatchCommitStatus Status,
    ImmutableArray<LuaPatchModuleCommitResult> Modules,
    string? Message,
    bool SideEffectsMayHaveOccurred,
    TimeSpan PauseDuration)
{
    public bool Succeeded => Status == LuaPatchCommitStatus.Committed;
}
