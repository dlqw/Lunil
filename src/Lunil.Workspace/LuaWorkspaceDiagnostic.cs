using Lunil.Compiler;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;

namespace Lunil.Workspace;

public enum LuaWorkspaceDiagnosticPhase : byte
{
    Discovery,
    Resolution,
    Graph,
    Compilation,
    FixedPoint,
    Budget,
}

public sealed record LuaWorkspaceDiagnostic(
    LuaWorkspaceDiagnosticPhase Phase,
    LuaModuleIdentity? Module,
    string Code,
    DiagnosticSeverity Severity,
    TextSpan Span,
    string Message,
    LuaCompilationPhase? CompilationPhase = null);
