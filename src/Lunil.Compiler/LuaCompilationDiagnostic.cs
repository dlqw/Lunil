using Lunil.Core.Diagnostics;
using Lunil.Core.Text;

namespace Lunil.Compiler;

public enum LuaCompilationPhase : byte
{
    Lexing,
    Parsing,
    Binding,
    Lowering,
    Verification,
}

/// <summary>A compiler diagnostic attributed to the public pipeline phase that produced it.</summary>
public sealed record LuaCompilationDiagnostic(
    LuaCompilationPhase Phase,
    Diagnostic Diagnostic)
{
    public string Code => Diagnostic.Code;

    public DiagnosticSeverity Severity => Diagnostic.Severity;

    public TextSpan Span => Diagnostic.Span;

    public string Message => Diagnostic.Message;
}
