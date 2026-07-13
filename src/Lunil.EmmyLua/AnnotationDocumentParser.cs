using System.Collections.Immutable;
using Lunil.Core.Diagnostics;
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
        ArgumentNullException.ThrowIfNull(lexing);
        ArgumentNullException.ThrowIfNull(options);
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
                var tag = context.Advance().Text;
                annotation = parseDirective(context, tag);
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
}
