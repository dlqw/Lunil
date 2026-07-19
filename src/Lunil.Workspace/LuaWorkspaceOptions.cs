using System.Collections.Immutable;
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

    public int MaximumModuleCount { get; init; } = 4_096;

    public int MaximumDependencyCount { get; init; } = 65_536;

    public long MaximumSourceBytes { get; init; } = 256L * 1024 * 1024;

    public int MaximumParallelism { get; init; } = Math.Max(1, Environment.ProcessorCount);

    public int MaximumFixedPointIterations { get; init; } = 16;

    public int MaximumCacheEntryCount { get; init; } = 16_384;

    public int MaximumDiagnosticCount { get; init; } = 10_000;

    public DiagnosticSeverity UnresolvedModuleSeverity { get; init; } = DiagnosticSeverity.Warning;

    public DiagnosticSeverity DynamicRequireSeverity { get; init; } = DiagnosticSeverity.Warning;

    public DiagnosticSeverity FixedPointSeverity { get; init; } = DiagnosticSeverity.Warning;

    public ImmutableHashSet<string> SuppressedDiagnosticCodes { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
}
