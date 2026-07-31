using System.Collections.Immutable;
using Lunil.Analysis;
using Lunil.Core;
using Lunil.Core.Diagnostics;
using Lunil.EmmyLua;
using Lunil.IR.Canonical;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Parsing;

namespace Lunil.Compiler;

/// <summary>Immutable output of the complete source-to-canonical compilation pipeline.</summary>
public sealed record LuaCompilationResult(
    LuaSourceDocument Source,
    LuaParseResult Syntax,
    LuaAnnotationDocument Annotations,
    LuaSemanticModel SemanticModel,
    LuaAnalysisResult Analysis,
    LuaIrModule? Module,
    ImmutableArray<LuaCompilationDiagnostic> Diagnostics)
{
    /// <summary>Gets the reusable verified front-end snapshot that produced this result.</summary>
    public LuaFrontEndSnapshot? FrontEndSnapshot { get; init; }

    /// <summary>True when the result intentionally stops after semantic analysis.</summary>
    public bool IsAnalysisOnly { get; init; }

    public LuaLanguageVersion LanguageVersion => Syntax.LanguageVersion;

    public bool Succeeded => (Module is not null || IsAnalysisOnly) &&
        Diagnostics.All(static diagnostic =>
            diagnostic.Severity != DiagnosticSeverity.Error);
}
