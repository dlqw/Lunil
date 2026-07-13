using System.Collections.Immutable;
using Lunil.Core.Diagnostics;

namespace Lunil.Analysis;

/// <summary>Deterministic budgets and diagnostic policy for static analysis.</summary>
public sealed record LuaAnalysisOptions
{
    public static LuaAnalysisOptions Default { get; } = new();

    public bool Enabled { get; init; } = true;

    public DiagnosticSeverity DiagnosticSeverity { get; init; } = DiagnosticSeverity.Warning;

    public bool ReportUnknownGlobals { get; init; }

    public bool ReportImplicitAny { get; init; }

    public bool ReportUnreachableCode { get; init; } = true;

    public bool ReportRedundantConditions { get; init; }

    public int MaximumTypeCount { get; init; } = 100_000;

    public int MaximumConstraintCount { get; init; } = 200_000;

    public int MaximumControlFlowBlockCount { get; init; } = 100_000;

    public int MaximumFlowIterations { get; init; } = 32;

    public int MaximumUnionMemberCount { get; init; } = 32;

    public int MaximumTypeDepth { get; init; } = 64;

    public int MaximumGenericInstantiationCount { get; init; } = 4_096;

    public int MaximumReturnPackLength { get; init; } = 64;

    public int MaximumDiagnosticCount { get; init; } = 1_000;

    public ImmutableHashSet<string> SuppressedDiagnosticCodes { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
}
