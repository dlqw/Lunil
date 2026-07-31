using System.Collections.Immutable;
using Lunil.Analysis;
using Lunil.Compiler;
using Lunil.Core;
using Lunil.Core.Diagnostics;

namespace Lunil.Workspace;

/// <summary>Global workspace budgets, resolver policy, and compiler configuration.</summary>
public sealed record LuaWorkspaceOptions
{
    public static LuaWorkspaceOptions Default { get; } = new();

    /// <summary>Gets the authoritative language contract for every module in this workspace.</summary>
    public LuaLanguageVersion LanguageVersion { get; init; } = LuaLanguageVersions.Default;

    public LuaCompilerOptions Compiler { get; init; } = LuaCompilerOptions.Default;

    /// <summary>Optional C++, C#, or generated host values visible to every module.</summary>
    public LuaHostAnalysisContract? HostContract { get; init; }

    public int MaximumModuleCount { get; init; } = 65_536;

    public int MaximumDependencyCount { get; init; } = 1_048_576;

    public long MaximumSourceBytes { get; init; } = 1024L * 1024 * 1024;

    public int MaximumParallelism { get; init; } = Math.Max(1, Environment.ProcessorCount);

    public int MaximumFixedPointIterations { get; init; } = 16;

    public int MaximumCacheEntryCount { get; init; } = 16_384;

    public long MaximumCacheBytes { get; init; } = 512L * 1024 * 1024;

    public int MaximumPendingWorkItems { get; init; } = 4_096;

    public int IndexShardCount { get; init; } = 64;

    /// <summary>Optional directory for versioned, content-addressed compact summary cache files.</summary>
    public string? DiskCacheDirectory { get; init; }

    public long MaximumDiskCacheBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Keeps analysis cache values strongly rooted; false allows full models to be reclaimed.</summary>
    public bool RetainFullAnalysisCacheResults { get; init; }

    public IProgress<LuaWorkspaceProgress>? Progress { get; init; }

    public int MaximumDiagnosticCount { get; init; } = 10_000;

    public DiagnosticSeverity UnresolvedModuleSeverity { get; init; } = DiagnosticSeverity.Warning;

    public DiagnosticSeverity DynamicRequireSeverity { get; init; } = DiagnosticSeverity.Warning;

    public DiagnosticSeverity FixedPointSeverity { get; init; } = DiagnosticSeverity.Warning;

    public ImmutableHashSet<string> SuppressedDiagnosticCodes { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
}
