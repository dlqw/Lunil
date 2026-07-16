using System.Collections.Immutable;
using Lunil.Compiler;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.Hosting;

/// <summary>Selects how a successful reload candidate updates <c>package.loaded</c>.</summary>
public enum LuaModuleReloadCachePolicy : byte
{
    /// <summary>Replace the cached value with the new loader result.</summary>
    ReplaceCache,

    /// <summary>
    /// Preserve the existing table identity while replacing its raw entries and metatable with
    /// those from the new loader result.
    /// </summary>
    PatchExistingTable,

    /// <summary>Use <see cref="LuaModuleReloadOptions.CustomCachePolicy"/>.</summary>
    Custom,
}

/// <summary>Input passed to a host-defined module cache merge policy.</summary>
public sealed record LuaModuleReloadContext(
    string ModuleName,
    LuaModuleRecord PreviousRecord,
    LuaValue CandidateValue,
    LuaValue CandidateLoader,
    LuaIrModule? CandidateModule);

/// <summary>Computes the value committed to <c>package.loaded</c> for a reload candidate.</summary>
public delegate LuaValue LuaModuleReloadCacheCallback(LuaModuleReloadContext context);

/// <summary>Controls one idle-only manual module reload.</summary>
public sealed record LuaModuleReloadOptions
{
    public static LuaModuleReloadOptions Default { get; } = new();

    /// <summary>
    /// Overrides the recorded file path for a module loaded by the built-in Lua file searcher.
    /// </summary>
    public string? SourcePath { get; init; }

    public LuaModuleReloadCachePolicy CachePolicy { get; init; } =
        LuaModuleReloadCachePolicy.ReplaceCache;

    public LuaModuleReloadCacheCallback? CustomCachePolicy { get; init; }
}

public enum LuaModuleReloadStatus : byte
{
    Reloaded,
    NotLoaded,
    StateBusy,
    UnsupportedLoader,
    SourceReadFailed,
    CompilationFailed,
    ExecutionFailed,
    CachePolicyFailed,
}

/// <summary>Outcome for one old closure slot discovered below the previous module cache.</summary>
public enum LuaFunctionMigrationStatus : byte
{
    /// <summary>The old closure now publishes the compatible replacement version.</summary>
    Updated,

    /// <summary>No replacement with the same stable lexical key was produced.</summary>
    ReplacementMissing,

    /// <summary>The lexical key matched but the captured-upvalue layout did not.</summary>
    UpvalueLayoutMismatch,

    /// <summary>The slot changed concurrently before the prepared replacement was published.</summary>
    ConcurrentUpdate,
}

/// <summary>Structured closure-version migration evidence from one reload.</summary>
public sealed record LuaFunctionMigrationResult(
    string LogicalKey,
    LuaFunctionMigrationStatus Status,
    long PreviousGeneration,
    long CurrentGeneration,
    string PreviousUpvalueLayoutFingerprint,
    string? CandidateUpvalueLayoutFingerprint);

/// <summary>Structured outcome of one manual module reload attempt.</summary>
/// <param name="SideEffectsMayHaveOccurred">
/// Whether a failed reload executed candidate or policy code whose arbitrary side effects could
/// not be rolled back. Successful reloads report <see langword="false"/>.
/// </param>
/// <param name="FunctionMigrations">
/// Compatible and rejected old-closure slot migrations discovered below the module cache.
/// </param>
public sealed record LuaModuleReloadResult(
    string ModuleName,
    LuaModuleReloadStatus Status,
    LuaModuleRecord? PreviousRecord,
    LuaModuleRecord? CurrentRecord,
    LuaCompilationResult? Compilation,
    LuaExecutionResult? Execution,
    string? Message,
    bool SideEffectsMayHaveOccurred,
    int ReusedUpvalueCount,
    int UpvalueMismatchCount,
    int PatchedExportCount,
    int RemovedExportCount,
    ImmutableArray<LuaFunctionMigrationResult> FunctionMigrations = default)
{
    public bool Succeeded => Status == LuaModuleReloadStatus.Reloaded;

    public int UpdatedFunctionCount => FunctionMigrations.IsDefaultOrEmpty
        ? 0
        : FunctionMigrations.Count(
            static migration => migration.Status == LuaFunctionMigrationStatus.Updated);

    public int IncompatibleFunctionCount => FunctionMigrations.IsDefaultOrEmpty
        ? 0
        : FunctionMigrations.Length - UpdatedFunctionCount;
}
