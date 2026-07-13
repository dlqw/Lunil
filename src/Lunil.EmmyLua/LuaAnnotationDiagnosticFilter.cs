using System.Collections.Immutable;
using Lunil.Core.Diagnostics;

namespace Lunil.EmmyLua;

internal static class LuaAnnotationDiagnosticFilter
{
    public static LuaAnnotationDocument Apply(
        LuaAnnotationDocument document,
        LuaAnnotationOptions options)
    {
        if (document.Diagnostics.IsEmpty)
        {
            return document;
        }

        var directives = document.Annotations
            .OfType<LuaDiagnosticAnnotationSyntax>()
            .Select(annotation => new Directive(
                document.Source.GetLocation(annotation.Span.Start).Line,
                annotation))
            .OrderBy(static directive => directive.Line)
            .ToImmutableArray();
        var diagnostics = document.Diagnostics
            .OrderBy(static diagnostic => diagnostic.Span.Start)
            .ToImmutableArray();
        var filtered = ImmutableArray.CreateBuilder<Diagnostic>();
        var codeStates = new Dictionary<string, bool>(StringComparer.Ordinal);
        var nextLine = new Dictionary<int, List<LuaDiagnosticAnnotationSyntax>>();
        var wildcardDisabled = false;
        var directiveIndex = 0;
        foreach (var diagnostic in diagnostics)
        {
            var line = document.Source.GetLocation(diagnostic.Span.Start).Line;
            while (directiveIndex < directives.Length && directives[directiveIndex].Line < line)
            {
                var directive = directives[directiveIndex++];
                if (directive.Annotation.Action == LuaDiagnosticAction.DisableNextLine)
                {
                    if (!nextLine.TryGetValue(directive.Line + 1, out var list))
                    {
                        list = [];
                        nextLine.Add(directive.Line + 1, list);
                    }

                    list.Add(directive.Annotation);
                    continue;
                }

                ApplyPersistent(directive.Annotation, codeStates, ref wildcardDisabled);
            }

            if (options.SuppressedDiagnosticCodes.Contains(diagnostic.Code) ||
                IsSuppressed(diagnostic.Code, codeStates, wildcardDisabled) ||
                nextLine.TryGetValue(line, out var lineDirectives) &&
                lineDirectives.Any(directive => Matches(directive, diagnostic.Code)))
            {
                continue;
            }

            filtered.Add(diagnostic);
        }

        return document with { Diagnostics = filtered.ToImmutable() };
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
