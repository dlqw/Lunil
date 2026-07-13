using System.Collections.Immutable;
using Lunil.Core.Diagnostics;
using Lunil.Syntax.Lexing;

namespace Lunil.EmmyLua;

/// <summary>Resolves LuaLS-first parsing with bounded legacy EmmyLua fallback.</summary>
public static class AnnotationCompatibilityResolver
{
    public static LuaAnnotationDocument Parse(
        LuaLexResult lexing,
        LuaAnnotationOptions? options = null)
    {
        options ??= LuaAnnotationOptions.Default with
        {
            Dialect = LuaAnnotationDialect.Compatible,
            EnableLegacyFallback = true,
        };
        var luaLs = LuaLsAnnotationParser.ParseCore(lexing, options, applySuppression: false);
        var legacy = LegacyEmmyAnnotationParser.ParseCore(
            lexing,
            options,
            applySuppression: false);
        var luaLsUnknown = CountUnknown(luaLs);
        var legacyUnknown = CountUnknown(legacy);

        LuaAnnotationDocument selected;
        var compatibilityDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (legacy.ParseErrorCount < luaLs.ParseErrorCount ||
            legacy.ParseErrorCount == luaLs.ParseErrorCount && legacyUnknown < luaLsUnknown)
        {
            selected = legacy;
            AddCompatibilityDiagnostic(
                compatibilityDiagnostics,
                selected,
                options,
                "LUA5007",
                "The annotation document was parsed using legacy EmmyLua compatibility.");
        }
        else
        {
            selected = luaLs;
            if (options.ReportDialectAmbiguity &&
                luaLs.ParseErrorCount == legacy.ParseErrorCount &&
                luaLsUnknown == legacyUnknown &&
                !AreEquivalent(luaLs, legacy))
            {
                AddCompatibilityDiagnostic(
                    compatibilityDiagnostics,
                    selected,
                    options,
                    "LUA5001",
                    "The annotation document is dialect-ambiguous; LuaLS semantics were selected.");
            }
        }

        var combined = selected with
        {
            Diagnostics = selected.Diagnostics.AddRange(compatibilityDiagnostics),
        };
        return LuaAnnotationDiagnosticFilter.Apply(combined, options);
    }

    private static int CountUnknown(LuaAnnotationDocument document) =>
        document.Annotations.Count(static annotation =>
            annotation is LuaUnknownAnnotationSyntax);

    private static bool AreEquivalent(
        LuaAnnotationDocument left,
        LuaAnnotationDocument right)
    {
        if (left.Annotations.Length != right.Annotations.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Annotations.Length; index++)
        {
            if (left.Annotations[index].GetType() != right.Annotations[index].GetType() ||
                !string.Equals(
                    left.Annotations[index].Tag,
                    right.Annotations[index].Tag,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddCompatibilityDiagnostic(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        LuaAnnotationDocument document,
        LuaAnnotationOptions options,
        string code,
        string message)
    {
        if (document.Diagnostics.Length + diagnostics.Count >= options.MaximumDiagnosticCount)
        {
            return;
        }

        var span = document.Annotations.IsEmpty
            ? default
            : document.Annotations[0].Span;
        diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Information, span, message));
    }
}
