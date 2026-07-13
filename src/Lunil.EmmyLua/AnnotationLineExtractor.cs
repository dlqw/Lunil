using System.Collections.Immutable;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;
using Lunil.Syntax.Lexing;

namespace Lunil.EmmyLua;

internal readonly record struct AnnotationLine(
    TextSpan FullSpan,
    TextSpan PayloadSpan,
    bool IsContinuation);

internal static class AnnotationLineExtractor
{
    public static ImmutableArray<AnnotationLine> Extract(
        LuaLexResult lexing,
        LuaAnnotationOptions options,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var lines = ImmutableArray.CreateBuilder<AnnotationLine>();
        foreach (var token in lexing.Tokens)
        {
            foreach (var trivia in token.LeadingTrivia)
            {
                if (trivia.Kind != LuaTriviaKind.Comment || trivia.Span.Length < 4)
                {
                    continue;
                }

                if (!TryGetPayload(lexing.Source, trivia.Span, out var payload, out var continuation))
                {
                    continue;
                }

                if (lines.Count >= options.MaximumAnnotationCount)
                {
                    if (diagnostics.Count < options.MaximumDiagnosticCount)
                    {
                        diagnostics.Add(new Diagnostic(
                            "LUA5004",
                            options.SyntaxDiagnosticSeverity,
                            trivia.Span,
                            $"Annotation count exceeds the configured " +
                            $"{options.MaximumAnnotationCount} limit."));
                    }

                    return lines.ToImmutable();
                }

                lines.Add(new AnnotationLine(trivia.Span, payload, continuation));
            }
        }

        return lines.ToImmutable();
    }

    private static bool TryGetPayload(
        SourceText source,
        TextSpan span,
        out TextSpan payload,
        out bool continuation)
    {
        payload = default;
        continuation = false;
        var bytes = source.AsSpan();
        var cursor = span.Start + 2;
        if (cursor >= span.End || bytes[cursor] != (byte)'-')
        {
            return false;
        }

        cursor++;
        while (cursor < span.End && bytes[cursor] is (byte)' ' or (byte)'\t')
        {
            cursor++;
        }

        if (cursor < span.End && bytes[cursor] == (byte)'@')
        {
            cursor++;
        }
        else if (cursor < span.End && bytes[cursor] == (byte)'|')
        {
            continuation = true;
            cursor++;
        }
        else
        {
            return false;
        }

        while (cursor < span.End && bytes[cursor] is (byte)' ' or (byte)'\t')
        {
            cursor++;
        }

        payload = TextSpan.FromBounds(cursor, span.End);
        return true;
    }
}
