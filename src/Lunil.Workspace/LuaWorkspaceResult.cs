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
    bool WasWidened);

public sealed record LuaWorkspaceMetrics(
    int DiscoveredModuleCount,
    int AnalyzedModuleCount,
    int CacheHitCount,
    int CacheMissCount,
    int InvalidatedModuleCount,
    int FixedPointIterationCount,
    int PeakParallelism);

public sealed record LuaWorkspaceResult(
    LuaModuleGraph Graph,
    ImmutableArray<LuaWorkspaceModuleResult> Modules,
    ImmutableArray<LuaWorkspaceDiagnostic> Diagnostics,
    LuaWorkspaceMetrics Metrics)
{
    public bool Succeeded =>
        Diagnostics.All(static diagnostic => diagnostic.Severity != Lunil.Core.Diagnostics.DiagnosticSeverity.Error) &&
        Modules.All(static module => module.Compilation.Succeeded);

    public LuaWorkspaceModuleResult? GetModule(string name) => Modules.FirstOrDefault(module =>
        string.Equals(module.Identity.Name, name, StringComparison.Ordinal));
}
