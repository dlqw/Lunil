using System.Collections.Immutable;
using System.Text;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;
using Lunil.Syntax.Lexing;

namespace Lunil.EmmyLua;

internal sealed class AnnotationParseContext
{
    private readonly ImmutableArray<LuaAnnotationToken> _tokens;
    private readonly ImmutableArray<Diagnostic>.Builder _diagnostics;
    private int _position;
    private int _typeDepth;

    public AnnotationParseContext(
        SourceText source,
        AnnotationLine line,
        LuaAnnotationLexResult lexing,
        LuaAnnotationOptions options,
        LuaAnnotationDialect dialect,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        Source = source;
        Line = line;
        Options = options;
        Dialect = dialect;
        _tokens = lexing.Tokens;
        _diagnostics = diagnostics;
    }

    public SourceText Source { get; }

    public AnnotationLine Line { get; }

    public LuaAnnotationOptions Options { get; }

    public LuaAnnotationDialect Dialect { get; }

    public int ParseErrorCount { get; private set; }

    public LuaAnnotationToken Current => TokenAt(_position);

    public LuaAnnotationToken Previous => TokenAt(Math.Max(_position - 1, 0));

    public bool IsAtEnd => Current.Kind == LuaAnnotationTokenKind.EndOfFile;

    public LuaAnnotationTokenKind PeekKind(int offset) => TokenAt(_position + offset).Kind;

    public string PeekText(int offset) => TokenAt(_position + offset).Text;

    public LuaAnnotationToken Advance()
    {
        var current = Current;
        if (!IsAtEnd)
        {
            _position++;
        }

        return current;
    }

    public bool Match(LuaAnnotationTokenKind kind)
    {
        if (Current.Kind != kind)
        {
            return false;
        }

        Advance();
        return true;
    }

    public bool MatchIdentifier(string text)
    {
        if (Current.Kind != LuaAnnotationTokenKind.Identifier ||
            !string.Equals(Current.Text, text, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Advance();
        return true;
    }

    public string? ReadIdentifier(string message)
    {
        if (Current.Kind is LuaAnnotationTokenKind.Identifier or
            LuaAnnotationTokenKind.StringLiteral)
        {
            return Unquote(Advance().Text);
        }

        AddError(Current.Span, message);
        return null;
    }

    public LuaTypeSyntax ParseType()
    {
        if (++_typeDepth > Options.MaximumTypeDepth)
        {
            _typeDepth--;
            AddError(Current.Span, "Annotation type nesting exceeds the configured limit.", "LUA5005");
            return new LuaNamedTypeSyntax("unknown", [], Current.Span);
        }

        try
        {
            return ParseUnionType();
        }
        finally
        {
            _typeDepth--;
        }
    }

    public ImmutableArray<LuaTypeSyntax> ParseTypeList()
    {
        var types = ImmutableArray.CreateBuilder<LuaTypeSyntax>();
        if (IsAtEnd)
        {
            AddError(Current.Span, "Expected an annotation type.");
            return types.ToImmutable();
        }

        do
        {
            types.Add(ParseType());
        }
        while (Match(LuaAnnotationTokenKind.Comma));

        return types.ToImmutable();
    }

    public string RawTextFromCurrent()
    {
        var start = IsAtEnd ? Line.PayloadSpan.End : Current.Span.Start;
        return Decode(Source.GetSpan(TextSpan.FromBounds(start, Line.PayloadSpan.End))).Trim();
    }

    public void AddError(TextSpan span, string message, string code = "LUA5003")
    {
        ParseErrorCount++;
        if (_diagnostics.Count < Options.MaximumDiagnosticCount)
        {
            _diagnostics.Add(new Diagnostic(
                code,
                Options.SyntaxDiagnosticSeverity,
                span,
                message));
        }
    }

    public static string Unquote(string text)
    {
        if (text.Length >= 2 && text[0] == text[^1] && text[0] is '\'' or '"')
        {
            return text[1..^1];
        }

        return text;
    }

    private LuaTypeSyntax ParseUnionType()
    {
        var first = ParseIntersectionType();
        if (!Match(LuaAnnotationTokenKind.Pipe))
        {
            return first;
        }

        var types = ImmutableArray.CreateBuilder<LuaTypeSyntax>();
        types.Add(first);
        do
        {
            types.Add(ParseIntersectionType());
        }
        while (Match(LuaAnnotationTokenKind.Pipe));

        return new LuaUnionTypeSyntax(
            types.ToImmutable(),
            TextSpan.FromBounds(first.Span.Start, types[^1].Span.End));
    }

    private LuaTypeSyntax ParseIntersectionType()
    {
        var first = ParsePostfixType();
        if (!Match(LuaAnnotationTokenKind.Ampersand))
        {
            return first;
        }

        var types = ImmutableArray.CreateBuilder<LuaTypeSyntax>();
        types.Add(first);
        do
        {
            types.Add(ParsePostfixType());
        }
        while (Match(LuaAnnotationTokenKind.Ampersand));

        return new LuaIntersectionTypeSyntax(
            types.ToImmutable(),
            TextSpan.FromBounds(first.Span.Start, types[^1].Span.End));
    }

    private LuaTypeSyntax ParsePostfixType()
    {
        var type = ParsePrimaryType();
        while (true)
        {
            if (Match(LuaAnnotationTokenKind.Question))
            {
                type = new LuaNullableTypeSyntax(
                    type,
                    TextSpan.FromBounds(type.Span.Start, Previous.Span.End));
                continue;
            }

            if (Current.Kind == LuaAnnotationTokenKind.OpenBracket &&
                PeekKind(1) == LuaAnnotationTokenKind.CloseBracket)
            {
                Advance();
                var close = Advance();
                type = new LuaArrayTypeSyntax(
                    type,
                    TextSpan.FromBounds(type.Span.Start, close.Span.End));
                continue;
            }

            return type;
        }
    }

    private LuaTypeSyntax ParsePrimaryType()
    {
        var token = Current;
        if (Match(LuaAnnotationTokenKind.Ellipsis))
        {
            LuaTypeSyntax? element = null;
            if (!IsAtEnd && Current.Kind is not LuaAnnotationTokenKind.Comma and
                not LuaAnnotationTokenKind.CloseParenthesis)
            {
                element = ParseType();
            }

            return new LuaVarargTypeSyntax(
                element,
                TextSpan.FromBounds(token.Span.Start, element?.Span.End ?? token.Span.End));
        }

        if (token.Kind == LuaAnnotationTokenKind.StringLiteral)
        {
            Advance();
            return new LuaLiteralTypeSyntax(
                LuaTypeLiteralKind.Text,
                Unquote(token.Text),
                token.Span);
        }

        if (token.Kind == LuaAnnotationTokenKind.NumericLiteral)
        {
            Advance();
            return new LuaLiteralTypeSyntax(LuaTypeLiteralKind.Number, token.Text, token.Span);
        }

        if (Match(LuaAnnotationTokenKind.OpenParenthesis))
        {
            var elements = ImmutableArray.CreateBuilder<LuaTypeSyntax>();
            if (!Match(LuaAnnotationTokenKind.CloseParenthesis))
            {
                do
                {
                    elements.Add(ParseType());
                }
                while (Match(LuaAnnotationTokenKind.Comma));

                Expect(LuaAnnotationTokenKind.CloseParenthesis, "Expected ')' after annotation type.");
            }

            if (elements.Count == 1)
            {
                return elements[0];
            }

            return new LuaTupleTypeSyntax(
                elements.ToImmutable(),
                TextSpan.FromBounds(token.Span.Start, Previous.Span.End));
        }

        if (Match(LuaAnnotationTokenKind.OpenBrace))
        {
            return ParseTableType(token.Span.Start);
        }

        if (token.Kind == LuaAnnotationTokenKind.Identifier)
        {
            Advance();
            if (string.Equals(token.Text, "fun", StringComparison.OrdinalIgnoreCase) &&
                Current.Kind == LuaAnnotationTokenKind.OpenParenthesis)
            {
                return ParseFunctionType(token.Span.Start);
            }

            if (string.Equals(token.Text, "nil", StringComparison.OrdinalIgnoreCase))
            {
                return new LuaLiteralTypeSyntax(LuaTypeLiteralKind.Nil, token.Text, token.Span);
            }

            if (string.Equals(token.Text, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token.Text, "false", StringComparison.OrdinalIgnoreCase))
            {
                return new LuaLiteralTypeSyntax(
                    LuaTypeLiteralKind.Boolean,
                    token.Text,
                    token.Span);
            }

            var name = new StringBuilder(token.Text);
            var end = token.Span.End;
            while (Match(LuaAnnotationTokenKind.Dot))
            {
                if (Current.Kind != LuaAnnotationTokenKind.Identifier)
                {
                    AddError(Current.Span, "Expected a type name after '.'.");
                    break;
                }

                name.Append('.').Append(Current.Text);
                end = Advance().Span.End;
            }

            var arguments = ImmutableArray.CreateBuilder<LuaTypeSyntax>();
            if (Match(LuaAnnotationTokenKind.LessThan))
            {
                if (!Match(LuaAnnotationTokenKind.GreaterThan))
                {
                    do
                    {
                        arguments.Add(ParseType());
                    }
                    while (Match(LuaAnnotationTokenKind.Comma));

                    Expect(LuaAnnotationTokenKind.GreaterThan, "Expected '>' after generic arguments.");
                }

                end = Previous.Span.End;
            }

            return new LuaNamedTypeSyntax(
                name.ToString(),
                arguments.ToImmutable(),
                TextSpan.FromBounds(token.Span.Start, end));
        }

        AddError(token.Span, "Expected an annotation type.");
        if (!IsAtEnd)
        {
            Advance();
        }

        return new LuaNamedTypeSyntax("unknown", [], token.Span);
    }

    private LuaFunctionTypeSyntax ParseFunctionType(int start)
    {
        Expect(LuaAnnotationTokenKind.OpenParenthesis, "Expected '(' after 'fun'.");
        var parameters = ImmutableArray.CreateBuilder<LuaFunctionParameterTypeSyntax>();
        if (!Match(LuaAnnotationTokenKind.CloseParenthesis))
        {
            do
            {
                var parameterStart = Current.Span.Start;
                if (Match(LuaAnnotationTokenKind.Ellipsis))
                {
                    LuaTypeSyntax element = new LuaNamedTypeSyntax("any", [], Previous.Span);
                    if (Match(LuaAnnotationTokenKind.Colon))
                    {
                        element = ParseType();
                    }

                    parameters.Add(new LuaFunctionParameterTypeSyntax(
                        null,
                        element,
                        false,
                        true,
                        TextSpan.FromBounds(parameterStart, element.Span.End)));
                    continue;
                }

                string? name = null;
                var optional = false;
                if (Current.Kind == LuaAnnotationTokenKind.Identifier &&
                    (PeekKind(1) == LuaAnnotationTokenKind.Colon ||
                     PeekKind(1) == LuaAnnotationTokenKind.Question &&
                     PeekKind(2) == LuaAnnotationTokenKind.Colon))
                {
                    name = Advance().Text;
                    optional = Match(LuaAnnotationTokenKind.Question);
                    Advance();
                }

                var type = ParseType();
                parameters.Add(new LuaFunctionParameterTypeSyntax(
                    name,
                    type,
                    optional,
                    false,
                    TextSpan.FromBounds(parameterStart, type.Span.End)));
            }
            while (Match(LuaAnnotationTokenKind.Comma));

            Expect(LuaAnnotationTokenKind.CloseParenthesis, "Expected ')' after function parameters.");
        }

        var returns = ImmutableArray.CreateBuilder<LuaTypeSyntax>();
        if (Match(LuaAnnotationTokenKind.Colon))
        {
            do
            {
                returns.Add(ParseType());
            }
            while (Match(LuaAnnotationTokenKind.Comma));
        }

        return new LuaFunctionTypeSyntax(
            parameters.ToImmutable(),
            returns.ToImmutable(),
            TextSpan.FromBounds(start, Previous.Span.End));
    }

    private LuaTableTypeSyntax ParseTableType(int start)
    {
        var fields = ImmutableArray.CreateBuilder<LuaTableFieldTypeSyntax>();
        if (!Match(LuaAnnotationTokenKind.CloseBrace))
        {
            do
            {
                var fieldStart = Current.Span.Start;
                if (Match(LuaAnnotationTokenKind.OpenBracket))
                {
                    var key = ParseType();
                    Expect(LuaAnnotationTokenKind.CloseBracket, "Expected ']' after table key type.");
                    Expect(LuaAnnotationTokenKind.Colon, "Expected ':' after table key type.");
                    var value = ParseType();
                    fields.Add(new LuaTableFieldTypeSyntax(
                        null,
                        key,
                        value,
                        false,
                        TextSpan.FromBounds(fieldStart, value.Span.End)));
                    continue;
                }

                if (Current.Kind is LuaAnnotationTokenKind.Identifier or
                    LuaAnnotationTokenKind.StringLiteral &&
                    (PeekKind(1) == LuaAnnotationTokenKind.Colon ||
                     PeekKind(1) == LuaAnnotationTokenKind.Question &&
                     PeekKind(2) == LuaAnnotationTokenKind.Colon))
                {
                    var name = Unquote(Advance().Text);
                    var optional = Match(LuaAnnotationTokenKind.Question);
                    Expect(LuaAnnotationTokenKind.Colon, "Expected ':' after table field name.");
                    var value = ParseType();
                    fields.Add(new LuaTableFieldTypeSyntax(
                        name,
                        null,
                        value,
                        optional,
                        TextSpan.FromBounds(fieldStart, value.Span.End)));
                    continue;
                }

                var element = ParseType();
                fields.Add(new LuaTableFieldTypeSyntax(
                    null,
                    null,
                    element,
                    false,
                    element.Span));
            }
            while (Match(LuaAnnotationTokenKind.Comma));

            Expect(LuaAnnotationTokenKind.CloseBrace, "Expected '}' after table type.");
        }

        return new LuaTableTypeSyntax(
            fields.ToImmutable(),
            TextSpan.FromBounds(start, Previous.Span.End));
    }

    private void Expect(LuaAnnotationTokenKind kind, string message)
    {
        if (!Match(kind))
        {
            AddError(Current.Span, message);
        }
    }

    private LuaAnnotationToken TokenAt(int index) =>
        (uint)index < (uint)_tokens.Length
            ? _tokens[index]
            : _tokens[^1];

    private static string Decode(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
}

internal static class AnnotationDirectiveParser
{
    private static readonly ImmutableHashSet<string> MarkerTags =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "async",
            "deprecated",
            "meta",
            "module",
            "nodiscard",
            "package",
            "private",
            "protected",
            "public",
            "see",
            "source",
            "version");

    public static bool IsMarker(string tag) => MarkerTags.Contains(tag);

    public static LuaAnnotationSyntax ParseClass(AnnotationParseContext context)
    {
        var name = context.ReadIdentifier("Expected a class name.") ?? "<missing>";
        var typeParameters = ImmutableArray.CreateBuilder<string>();
        if (context.Match(LuaAnnotationTokenKind.LessThan))
        {
            if (!context.Match(LuaAnnotationTokenKind.GreaterThan))
            {
                do
                {
                    typeParameters.Add(
                        context.ReadIdentifier("Expected a class type parameter.") ?? "<missing>");
                }
                while (context.Match(LuaAnnotationTokenKind.Comma));

                if (!context.Match(LuaAnnotationTokenKind.GreaterThan))
                {
                    context.AddError(
                        context.Current.Span,
                        "Expected '>' after class type parameters.");
                }
            }
        }

        var bases = context.Match(LuaAnnotationTokenKind.Colon)
            ? context.ParseTypeList()
            : ImmutableArray<LuaTypeSyntax>.Empty;
        return new LuaClassAnnotationSyntax(
            name,
            typeParameters.ToImmutable(),
            bases,
            context.Dialect,
            context.Line.FullSpan);
    }

    public static LuaAnnotationSyntax ParseField(AnnotationParseContext context)
    {
        var visibility = ParseVisibility(context);
        var name = context.ReadIdentifier("Expected a field name.") ?? "<missing>";
        var optional = context.Match(LuaAnnotationTokenKind.Question);
        var type = context.ParseType();
        return new LuaFieldAnnotationSyntax(
            name,
            type,
            visibility,
            optional,
            context.Dialect,
            context.Line.FullSpan);
    }

    public static LuaAnnotationSyntax ParseAlias(AnnotationParseContext context)
    {
        var name = context.ReadIdentifier("Expected an alias name.") ?? "<missing>";
        var type = context.IsAtEnd ? null : context.ParseType();
        return new LuaAliasAnnotationSyntax(name, type, context.Dialect, context.Line.FullSpan);
    }

    public static LuaAnnotationSyntax ParseContinuation(AnnotationParseContext context) =>
        new LuaAliasContinuationAnnotationSyntax(
            context.ParseType(),
            context.Dialect,
            context.Line.FullSpan);

    public static LuaAnnotationSyntax ParseEnum(AnnotationParseContext context)
    {
        var name = context.ReadIdentifier("Expected an enum name.") ?? "<missing>";
        var keyType = context.Match(LuaAnnotationTokenKind.Colon) ? context.ParseType() : null;
        return new LuaEnumAnnotationSyntax(
            name,
            keyType,
            context.Dialect,
            context.Line.FullSpan);
    }

    public static LuaAnnotationSyntax ParseType(AnnotationParseContext context) =>
        new LuaTypeAnnotationSyntax(
            context.ParseTypeList(),
            context.Dialect,
            context.Line.FullSpan);

    public static LuaAnnotationSyntax ParseParam(AnnotationParseContext context)
    {
        var name = context.ReadIdentifier("Expected a parameter name.") ?? "<missing>";
        var optional = context.Match(LuaAnnotationTokenKind.Question);
        return new LuaParamAnnotationSyntax(
            name,
            context.ParseType(),
            optional,
            context.Dialect,
            context.Line.FullSpan);
    }

    public static LuaAnnotationSyntax ParseReturn(AnnotationParseContext context)
    {
        var returns = ImmutableArray.CreateBuilder<LuaReturnTypeSyntax>();
        do
        {
            var type = context.ParseType();
            string? name = null;
            if (context.Current.Kind == LuaAnnotationTokenKind.Identifier &&
                context.PeekKind(1) is LuaAnnotationTokenKind.Comma or
                    LuaAnnotationTokenKind.EndOfFile)
            {
                name = context.Advance().Text;
            }

            returns.Add(new LuaReturnTypeSyntax(
                type,
                name,
                TextSpan.FromBounds(type.Span.Start, name is null
                    ? type.Span.End
                    : context.Previous.Span.End)));
        }
        while (context.Match(LuaAnnotationTokenKind.Comma));

        return new LuaReturnAnnotationSyntax(
            returns.ToImmutable(),
            context.Dialect,
            context.Line.FullSpan);
    }

    public static LuaAnnotationSyntax ParseLuaLsGeneric(AnnotationParseContext context) =>
        ParseGeneric(context, legacy: false);

    public static LuaAnnotationSyntax ParseLegacyGeneric(AnnotationParseContext context) =>
        ParseGeneric(context, legacy: true);

    public static LuaAnnotationSyntax ParseOverload(AnnotationParseContext context)
    {
        var type = context.ParseType();
        if (type is not LuaFunctionTypeSyntax function)
        {
            context.AddError(type.Span, "An overload annotation requires a function type.");
            function = new LuaFunctionTypeSyntax([], [], type.Span);
        }

        return new LuaOverloadAnnotationSyntax(
            function,
            context.Dialect,
            context.Line.FullSpan);
    }

    public static LuaAnnotationSyntax ParseVararg(AnnotationParseContext context) =>
        new LuaVarargAnnotationSyntax(
            context.ParseType(),
            context.Dialect,
            context.Line.FullSpan);

    public static LuaAnnotationSyntax ParseCast(AnnotationParseContext context)
    {
        var name = context.ReadIdentifier("Expected a cast target name.") ?? "<missing>";
        var operation = context.Match(LuaAnnotationTokenKind.Plus)
            ? LuaCastOperation.Add
            : context.Match(LuaAnnotationTokenKind.Minus)
                ? LuaCastOperation.Remove
                : LuaCastOperation.Replace;
        return new LuaCastAnnotationSyntax(
            name,
            context.ParseType(),
            operation,
            context.Dialect,
            context.Line.FullSpan);
    }

    public static LuaAnnotationSyntax ParseDiagnostic(AnnotationParseContext context)
    {
        var actionText = context.ReadIdentifier("Expected a diagnostic action.") ?? string.Empty;
        var normalizedAction = actionText.ToLowerInvariant();
        var action = normalizedAction switch
        {
            "disable" => LuaDiagnosticAction.Disable,
            "disable-next-line" => LuaDiagnosticAction.DisableNextLine,
            "enable" => LuaDiagnosticAction.Enable,
            _ => LuaDiagnosticAction.Disable,
        };
        if (normalizedAction is not ("disable" or "disable-next-line" or "enable"))
        {
            context.AddError(
                context.Line.FullSpan,
                $"Unknown diagnostic action '{actionText}'.",
                "LUA5006");
        }

        context.Match(LuaAnnotationTokenKind.Colon);
        var codes = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        while (!context.IsAtEnd)
        {
            var code = new StringBuilder();
            while (!context.IsAtEnd && context.Current.Kind != LuaAnnotationTokenKind.Comma)
            {
                code.Append(context.Advance().Text);
            }

            if (code.Length > 0)
            {
                codes.Add(code.ToString());
            }

            if (!context.Match(LuaAnnotationTokenKind.Comma))
            {
                break;
            }
        }

        return new LuaDiagnosticAnnotationSyntax(
            action,
            codes.ToImmutable(),
            context.Dialect,
            context.Line.FullSpan);
    }

    public static LuaAnnotationSyntax ParseOperator(AnnotationParseContext context)
    {
        var name = context.ReadIdentifier("Expected an operator name.") ?? "<missing>";
        LuaTypeSyntax? operand = null;
        if (context.Match(LuaAnnotationTokenKind.OpenParenthesis))
        {
            operand = context.ParseType();
            if (!context.Match(LuaAnnotationTokenKind.CloseParenthesis))
            {
                context.AddError(context.Current.Span, "Expected ')' after operator operand type.");
            }
        }

        if (!context.Match(LuaAnnotationTokenKind.Colon))
        {
            context.AddError(context.Current.Span, "Expected ':' before operator result type.");
        }

        return new LuaOperatorAnnotationSyntax(
            name,
            operand,
            context.ParseType(),
            context.Dialect,
            context.Line.FullSpan);
    }

    public static LuaAnnotationSyntax ParseMarker(
        AnnotationParseContext context,
        string tag) =>
        new LuaMarkerAnnotationSyntax(
            tag,
            context.RawTextFromCurrent(),
            context.Dialect,
            context.Line.FullSpan);

    public static LuaAnnotationSyntax ParseUnknown(
        AnnotationParseContext context,
        string tag)
    {
        if (context.Options.ReportUnknownTags)
        {
            context.AddError(
                context.Line.FullSpan,
                $"Unknown annotation tag '{tag}'.",
                "LUA5002");
        }

        return new LuaUnknownAnnotationSyntax(
            tag,
            context.RawTextFromCurrent(),
            context.Dialect,
            context.Line.FullSpan);
    }

    private static LuaGenericAnnotationSyntax ParseGeneric(
        AnnotationParseContext context,
        bool legacy)
    {
        var parameters = ImmutableArray.CreateBuilder<LuaGenericParameterSyntax>();
        do
        {
            var start = context.Current.Span.Start;
            var name = context.ReadIdentifier("Expected a generic parameter name.") ?? "<missing>";
            LuaTypeSyntax? constraint = null;
            if (legacy ? context.MatchIdentifier("extends") : context.Match(LuaAnnotationTokenKind.Colon))
            {
                constraint = context.ParseType();
            }
            else if (legacy && context.Current.Kind == LuaAnnotationTokenKind.Colon)
            {
                context.AddError(
                    context.Current.Span,
                    "Legacy EmmyLua generic constraints use 'extends'.");
                context.Advance();
                constraint = context.ParseType();
            }
            else if (!legacy && context.MatchIdentifier("extends"))
            {
                context.AddError(
                    context.Previous.Span,
                    "LuaLS generic constraints use ':'.");
                constraint = context.ParseType();
            }

            parameters.Add(new LuaGenericParameterSyntax(
                name,
                constraint,
                TextSpan.FromBounds(start, constraint?.Span.End ?? context.Previous.Span.End)));
        }
        while (context.Match(LuaAnnotationTokenKind.Comma));

        return new LuaGenericAnnotationSyntax(
            parameters.ToImmutable(),
            context.Dialect,
            context.Line.FullSpan);
    }

    private static LuaAnnotationVisibility ParseVisibility(AnnotationParseContext context)
    {
        if (context.MatchIdentifier("public"))
        {
            return LuaAnnotationVisibility.Public;
        }

        if (context.MatchIdentifier("protected"))
        {
            return LuaAnnotationVisibility.Protected;
        }

        if (context.MatchIdentifier("private"))
        {
            return LuaAnnotationVisibility.Private;
        }

        if (context.MatchIdentifier("package"))
        {
            return LuaAnnotationVisibility.Package;
        }

        return LuaAnnotationVisibility.Unspecified;
    }
}
