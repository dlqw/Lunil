using System.Collections.Immutable;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;
using Lunil.Syntax.Lexing;

namespace Lunil.EmmyLua;

internal static class AnnotationDocumentParser
{
    public static LuaAnnotationDocument Parse(
        LuaLexResult lexing,
        LuaAnnotationOptions options,
        LuaAnnotationDialect dialect,
        Func<AnnotationParseContext, string, LuaAnnotationSyntax> parseDirective,
        bool applySuppression)
    {
        LunilGuard.NotNull(lexing);
        LunilGuard.NotNull(options);
        LuaAnnotationLexer.ValidateOptions(options);
        if (!options.Enabled)
        {
            return LuaAnnotationDocument.Empty(lexing.Source, dialect);
        }

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var lines = AnnotationLineExtractor.Extract(lexing, options, diagnostics);
        var annotations = ImmutableArray.CreateBuilder<LuaAnnotationSyntax>();
        var parseErrorCount = diagnostics.Count;
        foreach (var line in lines)
        {
            var remainingDiagnostics = Math.Max(
                0,
                options.MaximumDiagnosticCount - diagnostics.Count);
            var annotationLexing = LuaAnnotationLexer.Lex(
                lexing.Source,
                line.PayloadSpan,
                options,
                remainingDiagnostics);
            diagnostics.AddRange(annotationLexing.Diagnostics);
            parseErrorCount += annotationLexing.ErrorCount;
            var context = new AnnotationParseContext(
                lexing.Source,
                line,
                annotationLexing,
                options,
                dialect,
                diagnostics);
            LuaAnnotationSyntax annotation;
            if (line.IsContinuation)
            {
                annotation = AnnotationDirectiveParser.ParseContinuation(context);
            }
            else if (context.Current.Kind == LuaAnnotationTokenKind.Identifier)
            {
                var tagToken = context.Advance();
                annotation = parseDirective(context, tagToken.Text) with
                {
                    TagSpan = GetTagSpan(lexing.Source, line, tagToken.Span),
                };
            }
            else
            {
                context.AddError(context.Current.Span, "Expected an annotation tag after '@'.");
                annotation = new LuaUnknownAnnotationSyntax(
                    string.Empty,
                    context.RawTextFromCurrent(),
                    dialect,
                    line.FullSpan);
            }

            parseErrorCount += context.ParseErrorCount;
            annotations.Add(annotation);
        }

        var result = new LuaAnnotationDocument(
            lexing.Source,
            dialect,
            annotations.ToImmutable(),
            diagnostics.ToImmutable(),
            parseErrorCount);
        return applySuppression ? LuaAnnotationDiagnosticFilter.Apply(result, options) : result;
    }

    /// <summary>
    /// Computes the span of the <c>@tag</c> keyword. The line extractor skips the <c>@</c> and
    /// any following whitespace before the payload, so the <c>@</c> is found by scanning back
    /// from the payload start.
    /// </summary>
    private static TextSpan GetTagSpan(
        SourceText source,
        AnnotationLine line,
        TextSpan tagTokenSpan)
    {
        var at = line.PayloadSpan.Start;
        var bytes = source.AsSpan();
        while (at > line.FullSpan.Start && bytes[at - 1] is (byte)' ' or (byte)'\t')
        {
            at--;
        }

        if (at > line.FullSpan.Start && bytes[at - 1] == (byte)'@')
        {
            at--;
        }

        return TextSpan.FromBounds(at, tagTokenSpan.End);
    }
}
