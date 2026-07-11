using System.Collections.Immutable;
using Luac.Core.Diagnostics;
using Luac.IR.Canonical;

namespace Luac.Semantics.Lowering;

public sealed record LuaLoweringResult(
    LuaIrModule? Module,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Succeeded => Module is not null &&
        Diagnostics.All(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
}
