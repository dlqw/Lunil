using System.Text.Json;
using Lunil.Cli.CommandLine;
using Lunil.Cli.IO;
using Lunil.Compiler;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;
using Lunil.Workspace;

namespace Lunil.Cli.Diagnostics;

internal sealed record CliDiagnosticRecord(
    string Source,
    int Line,
    int Column,
    string Code,
    DiagnosticSeverity Severity,
    string Phase,
    string Message);

internal static class CliDiagnosticWriter
{
    public static IReadOnlyList<CliDiagnosticRecord> FromCompilation(
        LuaCompilationResult compilation,
        bool warningsAsErrors) =>
        compilation.Diagnostics.Select(diagnostic => Create(
            compilation.Source.SourceName ?? "<source>",
            compilation.Source.Text,
            diagnostic.Code,
            Promote(diagnostic.Severity, warningsAsErrors),
            diagnostic.Phase.ToString().ToLowerInvariant(),
            diagnostic.Span,
            diagnostic.Message)).ToArray();

    public static IReadOnlyList<CliDiagnosticRecord> FromWorkspace(
        LuaWorkspaceResult result,
        bool warningsAsErrors)
    {
        var sources = result.Modules.ToDictionary(
            static module => module.Identity.Name,
            static module => module.Compilation.Source,
            StringComparer.Ordinal);
        return result.Diagnostics.Select(diagnostic =>
        {
            var source = diagnostic.Module is not null &&
                sources.TryGetValue(diagnostic.Module.Name, out var resolved)
                    ? resolved
                    : null;
            return Create(
                source?.SourceName ?? diagnostic.Module?.Name ?? "<workspace>",
                source?.Text,
                diagnostic.Code,
                Promote(diagnostic.Severity, warningsAsErrors),
                diagnostic.CompilationPhase?.ToString().ToLowerInvariant() ??
                    diagnostic.Phase.ToString().ToLowerInvariant(),
                diagnostic.Span,
                diagnostic.Message);
        }).ToArray();
    }

    public static CliDiagnosticRecord CreateProblem(
        string source,
        string code,
        DiagnosticSeverity severity,
        string phase,
        string message) =>
        new(source, 0, 0, code, severity, phase, message);

    public static bool HasErrors(IEnumerable<CliDiagnosticRecord> diagnostics) =>
        diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    public static async Task WriteAsync(
        Stream stream,
        IEnumerable<CliDiagnosticRecord> diagnostics,
        CliDiagnosticFormat format,
        CancellationToken cancellationToken)
    {
        var ordered = diagnostics
            .OrderBy(static diagnostic => diagnostic.Source, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Line)
            .ThenBy(static diagnostic => diagnostic.Column)
            .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0)
        {
            return;
        }

        if (format == CliDiagnosticFormat.Text)
        {
            foreach (var diagnostic in ordered)
            {
                var location = diagnostic.Line > 0
                    ? $"({diagnostic.Line},{diagnostic.Column})"
                    : string.Empty;
                await CliStreams.WriteTextAsync(
                    stream,
                    $"{diagnostic.Source}{location}: " +
                    $"{diagnostic.Severity.ToString().ToLowerInvariant()} " +
                    $"{diagnostic.Code}: {diagnostic.Message} [{diagnostic.Phase}]\n",
                    cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "lunil.diagnostics.v1");
            writer.WriteStartArray("diagnostics");
            foreach (var diagnostic in ordered)
            {
                writer.WriteStartObject();
                writer.WriteString("source", diagnostic.Source);
                writer.WriteNumber("line", diagnostic.Line);
                writer.WriteNumber("column", diagnostic.Column);
                writer.WriteString("code", diagnostic.Code);
                writer.WriteString(
                    "severity",
                    diagnostic.Severity.ToString().ToLowerInvariant());
                writer.WriteString("phase", diagnostic.Phase);
                writer.WriteString("message", diagnostic.Message);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        buffer.Position = 0;
        await buffer.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static CliDiagnosticRecord Create(
        string source,
        SourceText? text,
        string code,
        DiagnosticSeverity severity,
        string phase,
        TextSpan span,
        string message)
    {
        var line = 0;
        var column = 0;
        if (text is not null && span.Start <= text.Length)
        {
            var location = text.GetLocation(span.Start);
            line = location.Line + 1;
            column = location.Utf16Column + 1;
        }

        return new CliDiagnosticRecord(source, line, column, code, severity, phase, message);
    }

    private static DiagnosticSeverity Promote(DiagnosticSeverity severity, bool warningsAsErrors) =>
        warningsAsErrors && severity == DiagnosticSeverity.Warning
            ? DiagnosticSeverity.Error
            : severity;
}
