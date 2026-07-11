using Lunil.Core.Text;

namespace Lunil.Core.Diagnostics;

/// <summary>A stable compiler diagnostic over a byte-oriented source range.</summary>
public sealed record Diagnostic
{
    public Diagnostic(
        string code,
        DiagnosticSeverity severity,
        TextSpan span,
        string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Code = code;
        Severity = severity;
        Span = span;
        Message = message;
    }

    public string Code { get; }

    public DiagnosticSeverity Severity { get; }

    public TextSpan Span { get; }

    public string Message { get; }
}
