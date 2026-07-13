using System.Collections.Immutable;
using Lunil.Core.Diagnostics;

namespace Lunil.EmmyLua;

public enum LuaAnnotationDialect : byte
{
    LuaLs,
    LegacyEmmyLua,
    Compatible,
}

/// <summary>Bounded dialect and diagnostic policy for annotation parsing.</summary>
public sealed record LuaAnnotationOptions
{
    public static LuaAnnotationOptions Default { get; } = new();

    public bool Enabled { get; init; } = true;

    public LuaAnnotationDialect Dialect { get; init; } = LuaAnnotationDialect.LuaLs;

    /// <summary>
    /// Gets whether a source configured for LuaLS may fall back to the separately implemented
    /// legacy EmmyLua parser when the LuaLS parser rejects a known directive.
    /// </summary>
    public bool EnableLegacyFallback { get; init; }

    public bool ReportDialectAmbiguity { get; init; } = true;

    public bool ReportUnknownTags { get; init; }

    public DiagnosticSeverity SyntaxDiagnosticSeverity { get; init; } =
        DiagnosticSeverity.Warning;

    public int MaximumAnnotationCount { get; init; } = 100_000;

    public int MaximumTokensPerAnnotation { get; init; } = 4_096;

    public int MaximumTypeDepth { get; init; } = 64;

    public int MaximumDiagnosticCount { get; init; } = 1_000;

    public ImmutableHashSet<string> SuppressedDiagnosticCodes { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
}
