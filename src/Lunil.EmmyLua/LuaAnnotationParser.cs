using Lunil.Syntax.Lexing;

namespace Lunil.EmmyLua;

/// <summary>Dialect-selecting public entry point for annotation parsing.</summary>
public static class LuaAnnotationParser
{
    public static LuaAnnotationDocument Parse(
        LuaLexResult lexing,
        LuaAnnotationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(lexing);
        options ??= LuaAnnotationOptions.Default;
        LuaAnnotationLexer.ValidateOptions(options);
        if (!options.Enabled)
        {
            return LuaAnnotationDocument.Empty(lexing.Source, options.Dialect);
        }

        return options.Dialect switch
        {
            LuaAnnotationDialect.LuaLs when options.EnableLegacyFallback =>
                AnnotationCompatibilityResolver.Parse(lexing, options),
            LuaAnnotationDialect.LuaLs => LuaLsAnnotationParser.Parse(lexing, options),
            LuaAnnotationDialect.LegacyEmmyLua =>
                LegacyEmmyAnnotationParser.Parse(lexing, options),
            LuaAnnotationDialect.Compatible =>
                AnnotationCompatibilityResolver.Parse(lexing, options),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }
}
