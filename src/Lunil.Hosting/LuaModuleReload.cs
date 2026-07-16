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

/// <summary>Structured outcome of one manual module reload attempt.</summary>
/// <param name="SideEffectsMayHaveOccurred">
/// Whether a failed reload executed candidate or policy code whose arbitrary side effects could
/// not be rolled back. Successful reloads report <see langword="false"/>.
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
    int RemovedExportCount)
{
    public bool Succeeded => Status == LuaModuleReloadStatus.Reloaded;
}
