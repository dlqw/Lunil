using System.Collections.Immutable;
using Lunil.Analysis;
using Lunil.Compiler;

namespace Lunil.Workspace;

public sealed record LuaWorkspaceModuleResult(
    LuaModuleIdentity Identity,
    string SourceIdentity,
    string ContentHash,
    LuaCompilationResult Compilation,
    ImmutableArray<LuaModuleDependency> Dependencies,
    LuaType ExportedType,
    string ExportHash,
    int FixedPointIterationCount,
    bool WasCacheHit,
    bool WasWidened)
{
    public ImmutableArray<LuaWorkspaceExportSymbol> ExportedSymbols { get; init; } = [];

    public string ExportSymbolHash { get; init; } = string.Empty;

    public string FunctionSummaryHash { get; init; } = string.Empty;

    public string AnalysisSummaryHash { get; init; } = string.Empty;

    public string DependencySummaryHash { get; init; } = string.Empty;

    public ImmutableDictionary<string, string> ExportSummaryHashes { get; init; } =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    public ImmutableDictionary<string, string> FunctionSummaryHashes { get; init; } =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    public string HostSummaryHash { get; init; } = string.Empty;
}

public sealed record LuaWorkspaceMetrics(
    int DiscoveredModuleCount,
    int AnalyzedModuleCount,
    int CacheHitCount,
    int CacheMissCount,
    int InvalidatedModuleCount,
    int FixedPointIterationCount,
    int PeakParallelism)
{
    public int DirtyFunctionCount { get; init; }

    public int DirtyExportCount { get; init; }

    public int DirtyHostSummaryCount { get; init; }

    public int IndexedReferenceCount { get; init; }

    public int IndexedCallCount { get; init; }

    public int PendingWorkItemHighWatermark { get; init; }

    public int CacheEvictionCount { get; init; }

    public int ReclaimedAnalysisCount { get; init; }

    public int DiskCacheHitCount { get; init; }

    public long CacheResidentBytes { get; init; }

    public long CompactResidentBytes { get; init; }
}

public sealed record LuaWorkspaceResult(
    LuaModuleGraph Graph,
    ImmutableArray<LuaWorkspaceModuleResult> Modules,
    ImmutableArray<LuaWorkspaceDiagnostic> Diagnostics,
    LuaWorkspaceMetrics Metrics)
{
    public LuaWorkspaceExportGraph ExportGraph { get; init; } = LuaWorkspaceExportGraph.Empty;

    public LuaWorkspaceModuleCallBindings CallBindings { get; init; } = LuaWorkspaceModuleCallBindings.Empty;

    public bool Succeeded =>
        Diagnostics.All(static diagnostic => diagnostic.Severity != Lunil.Core.Diagnostics.DiagnosticSeverity.Error) &&
        Modules.All(static module => module.Compilation.Succeeded);

    public LuaWorkspaceModuleResult? GetModule(string name) => Modules.FirstOrDefault(module =>
        string.Equals(module.Identity.Name, name, StringComparison.Ordinal));
}
