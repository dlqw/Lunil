using System.Collections.Immutable;
using Lunil.Core.Diagnostics;
using Lunil.IR.Canonical;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Parsing;

namespace Lunil.Compiler;

/// <summary>Immutable output of the complete source-to-canonical compilation pipeline.</summary>
public sealed record LuaCompilationResult(
    LuaSourceDocument Source,
    LuaParseResult Syntax,
    LuaSemanticModel SemanticModel,
    LuaIrModule? Module,
    ImmutableArray<LuaCompilationDiagnostic> Diagnostics)
{
    public bool Succeeded => Module is not null &&
        Diagnostics.All(static diagnostic =>
            diagnostic.Severity != DiagnosticSeverity.Error);
}
