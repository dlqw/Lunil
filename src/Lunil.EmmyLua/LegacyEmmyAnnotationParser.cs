using Lunil.Syntax.Lexing;

namespace Lunil.EmmyLua;

/// <summary>
/// Separately dispatched parser for legacy EmmyLua IDE inputs. It intentionally does not alias
/// the LuaLS parser so legacy-only syntax can be retired or diagnosed independently.
/// </summary>
public static class LegacyEmmyAnnotationParser
{
    public static LuaAnnotationDocument Parse(
        LuaLexResult lexing,
        LuaAnnotationOptions? options = null) =>
        ParseCore(lexing, options ?? LuaAnnotationOptions.Default, applySuppression: true);

    internal static LuaAnnotationDocument ParseCore(
        LuaLexResult lexing,
        LuaAnnotationOptions options,
        bool applySuppression) =>
        AnnotationDocumentParser.Parse(
            lexing,
            options,
            LuaAnnotationDialect.LegacyEmmyLua,
            ParseDirective,
            applySuppression);

    private static LuaAnnotationSyntax ParseDirective(
        AnnotationParseContext context,
        string tag) => tag.ToLowerInvariant() switch
        {
            "alias" => AnnotationDirectiveParser.ParseAlias(context),
            "class" => AnnotationDirectiveParser.ParseClass(context),
            "field" => AnnotationDirectiveParser.ParseField(context),
            "generic" => AnnotationDirectiveParser.ParseLegacyGeneric(context),
            "language" or "namespace" => AnnotationDirectiveParser.ParseMarker(context, tag),
            "overload" => AnnotationDirectiveParser.ParseOverload(context),
            "param" => AnnotationDirectiveParser.ParseParam(context),
            "return" => AnnotationDirectiveParser.ParseReturn(context),
            "type" => AnnotationDirectiveParser.ParseType(context),
            "vararg" => AnnotationDirectiveParser.ParseVararg(context),
            "deprecated" or "private" or "protected" or "public" =>
                AnnotationDirectiveParser.ParseMarker(context, tag),
            _ => AnnotationDirectiveParser.ParseUnknown(context, tag),
        };
}
