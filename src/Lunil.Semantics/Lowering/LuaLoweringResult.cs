using System.Collections.Immutable;
using Lunil.Core.Diagnostics;
using Lunil.IR.Canonical;

namespace Lunil.Semantics.Lowering;

public sealed record LuaLoweringResult(
    LuaIrModule? Module,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public bool Succeeded => Module is not null &&
        Diagnostics.All(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Error);
}
