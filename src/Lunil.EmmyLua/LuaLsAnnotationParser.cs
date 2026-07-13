using Lunil.Syntax.Lexing;

namespace Lunil.EmmyLua;

/// <summary>Parser for the default Lua Language Server annotation dialect.</summary>
public static class LuaLsAnnotationParser
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
            LuaAnnotationDialect.LuaLs,
            ParseDirective,
            applySuppression);

    private static LuaAnnotationSyntax ParseDirective(
        AnnotationParseContext context,
        string tag) => tag.ToLowerInvariant() switch
        {
            "alias" => AnnotationDirectiveParser.ParseAlias(context),
            "cast" => AnnotationDirectiveParser.ParseCast(context),
            "class" => AnnotationDirectiveParser.ParseClass(context),
            "diagnostic" => AnnotationDirectiveParser.ParseDiagnostic(context),
            "enum" => AnnotationDirectiveParser.ParseEnum(context),
            "field" => AnnotationDirectiveParser.ParseField(context),
            "generic" => AnnotationDirectiveParser.ParseLuaLsGeneric(context),
            "operator" => AnnotationDirectiveParser.ParseOperator(context),
            "overload" => AnnotationDirectiveParser.ParseOverload(context),
            "param" => AnnotationDirectiveParser.ParseParam(context),
            "return" => AnnotationDirectiveParser.ParseReturn(context),
            "type" => AnnotationDirectiveParser.ParseType(context),
            "vararg" => AnnotationDirectiveParser.ParseVararg(context),
            _ when AnnotationDirectiveParser.IsMarker(tag) =>
                AnnotationDirectiveParser.ParseMarker(context, tag),
            _ => AnnotationDirectiveParser.ParseUnknown(context, tag),
        };
}
