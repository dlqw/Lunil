using System.Collections.Immutable;
using Lunil.Core.Diagnostics;
using Lunil.EmmyLua;

namespace Lunil.Analysis;

internal static class AnalysisDiagnosticFilter
{
    public static ImmutableArray<Diagnostic> Apply(
        LuaAnnotationDocument document,
        ImmutableArray<Diagnostic> diagnostics,
        LuaAnalysisOptions options)
    {
        if (diagnostics.IsEmpty)
        {
            return diagnostics;
        }

        var directives = document.Annotations
            .OfType<LuaDiagnosticAnnotationSyntax>()
            .Select(item => new Directive(
                document.Source.GetLocation(item.Span.Start).Line,
                item))
            .OrderBy(static item => item.Line)
            .ToImmutableArray();
        var filtered = ImmutableArray.CreateBuilder<Diagnostic>();
        var codeStates = new Dictionary<string, bool>(StringComparer.Ordinal);
        var nextLine = new Dictionary<int, List<LuaDiagnosticAnnotationSyntax>>();
        var wildcardDisabled = false;
        var directiveIndex = 0;
        foreach (var diagnostic in diagnostics.OrderBy(static item => item.Span.Start))
        {
            var line = document.Source.GetLocation(diagnostic.Span.Start).Line;
            while (directiveIndex < directives.Length && directives[directiveIndex].Line < line)
            {
                var directive = directives[directiveIndex++];
                if (directive.Annotation.Action == LuaDiagnosticAction.DisableNextLine)
                {
                    if (!nextLine.TryGetValue(directive.Line + 1, out var items))
                    {
                        items = [];
                        nextLine.Add(directive.Line + 1, items);
                    }

                    items.Add(directive.Annotation);
                }
                else
                {
                    ApplyPersistent(directive.Annotation, codeStates, ref wildcardDisabled);
                }
            }

            if (options.SuppressedDiagnosticCodes.Contains(diagnostic.Code) ||
                IsSuppressed(diagnostic.Code, codeStates, wildcardDisabled) ||
                nextLine.TryGetValue(line, out var lineDirectives) &&
                lineDirectives.Any(item => Matches(item, diagnostic.Code)))
            {
                continue;
            }

            filtered.Add(diagnostic);
        }

        return filtered.ToImmutable();
    }

    private static void ApplyPersistent(
        LuaDiagnosticAnnotationSyntax directive,
        Dictionary<string, bool> codeStates,
        ref bool wildcardDisabled)
    {
        var disabled = directive.Action == LuaDiagnosticAction.Disable;
        if (directive.Codes.IsEmpty)
        {
            wildcardDisabled = disabled;
            codeStates.Clear();
            return;
        }

        foreach (var code in directive.Codes)
        {
            codeStates[code] = disabled;
        }
    }

    private static bool IsSuppressed(
        string code,
        Dictionary<string, bool> codeStates,
        bool wildcardDisabled) =>
        codeStates.TryGetValue(code, out var disabled) ? disabled : wildcardDisabled;

    private static bool Matches(LuaDiagnosticAnnotationSyntax directive, string code) =>
        directive.Codes.IsEmpty || directive.Codes.Contains(code);

    private readonly record struct Directive(
        int Line,
        LuaDiagnosticAnnotationSyntax Annotation);
}
