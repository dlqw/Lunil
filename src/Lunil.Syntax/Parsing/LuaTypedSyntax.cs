using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Lunil.Core.Text;
using Lunil.Syntax.Lexing;

namespace Lunil.Syntax.Parsing;

/// <summary>Provides a typed view over an expression in the lossless syntax tree.</summary>
public class LuaExpressionSyntax
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal LuaExpressionSyntax(LuaSyntaxNode node)
    {
        Node = node;
    }

    /// <summary>Gets the underlying lossless node.</summary>
    public LuaSyntaxNode Node { get; }

    /// <summary>Gets the underlying node kind.</summary>
    public LuaSyntaxKind Kind => Node.Kind;

    /// <summary>Gets the source span excluding trivia.</summary>
    public TextSpan Span => Node.Span;

    /// <summary>
    /// Gets whether this view contains all required syntax. Recovery nodes and missing tokens make
    /// this property <see langword="false"/>; the underlying node remains available for inspection.
    /// </summary>
    public virtual bool IsComplete =>
        Node.Kind != LuaSyntaxKind.Error &&
        !Node.DescendantTokens().Any(static token => token.IsMissing);

    /// <summary>Tries to get the token of a complete identifier expression.</summary>
    public bool TryGetIdentifierToken(
        [NotNullWhen(true)] out LuaSyntaxToken? token)
    {
        token = Node.Kind == LuaSyntaxKind.IdentifierExpression
            ? Node.ChildTokens().SingleOrDefault(static candidate =>
                candidate.Kind == LuaTokenKind.Identifier && !candidate.IsMissing)
            : null;
        return token is not null;
    }

    /// <summary>Tries to decode this expression as a valid UTF-8 constant Lua string.</summary>
    public bool TryGetConstantString(out string value)
    {
        value = string.Empty;
        if (Node.Kind != LuaSyntaxKind.StringLiteralExpression)
        {
            return false;
        }

        var token = Node.ChildTokens().SingleOrDefault(static candidate =>
            candidate.Kind is LuaTokenKind.StringLiteral or LuaTokenKind.LongStringLiteral &&
            !candidate.IsMissing);
        if (token?.Value is not LuaStringTokenValue stringValue)
        {
            return false;
        }

        try
        {
            value = StrictUtf8.GetString(stringValue.Bytes.AsSpan());
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    internal static LuaExpressionSyntax Create(LuaSyntaxNode node) =>
        node.Kind is LuaSyntaxKind.MemberAccessExpression or LuaSyntaxKind.MethodCallExpression
            ? new LuaMemberAccessExpressionSyntax(node)
            : new LuaExpressionSyntax(node);
}

/// <summary>Provides a typed view over a function call or method call expression.</summary>
public sealed class LuaCallExpressionSyntax : LuaExpressionSyntax
{
    private readonly bool _hasArgumentList;

    internal LuaCallExpressionSyntax(LuaSyntaxNode node)
        : base(node)
    {
        var childNodes = node.ChildNodes().ToImmutableArray();
        IsMethodCall = node.Kind == LuaSyntaxKind.MethodCallExpression;
        Callee = IsMethodCall
            ? new LuaMemberAccessExpressionSyntax(node)
            : childNodes.FirstOrDefault(static child => child.Kind != LuaSyntaxKind.ArgumentList)
                is { } callee
                ? Create(callee)
                : null;

        var argumentList = childNodes.FirstOrDefault(static child =>
            child.Kind == LuaSyntaxKind.ArgumentList);
        _hasArgumentList = argumentList is not null;
        Arguments = GetArguments(argumentList);
    }

    /// <summary>
    /// Gets the callee, or <see langword="null"/> when recovery did not produce one. For a method
    /// call this is a <see cref="LuaMemberAccessExpressionSyntax"/> projected from the method-call
    /// node, with its receiver and colon member name available without exposing child order.
    /// </summary>
    public LuaExpressionSyntax? Callee { get; }

    /// <summary>Gets the arguments in source order, including shorthand string and table arguments.</summary>
    public ImmutableArray<LuaExpressionSyntax> Arguments { get; }

    /// <summary>Gets whether this is a colon method call with an implicit <c>self</c> argument.</summary>
    public bool IsMethodCall { get; }

    /// <inheritdoc />
    public override bool IsComplete =>
        base.IsComplete &&
        _hasArgumentList &&
        Callee is { IsComplete: true } &&
        Arguments.All(static argument => argument.IsComplete);

    private static ImmutableArray<LuaExpressionSyntax> GetArguments(LuaSyntaxNode? argumentList)
    {
        if (argumentList is null)
        {
            return [];
        }

        var expressionList = argumentList.ChildNodes().FirstOrDefault(static child =>
            child.Kind == LuaSyntaxKind.ExpressionList);
        var argumentNodes = expressionList?.ChildNodes() ?? argumentList.ChildNodes();
        return argumentNodes.Select(Create).ToImmutableArray();
    }
}

/// <summary>Provides a typed view over dot member access or a colon method target.</summary>
public sealed class LuaMemberAccessExpressionSyntax : LuaExpressionSyntax
{
    private readonly bool _hasAccessOperator;

    internal LuaMemberAccessExpressionSyntax(LuaSyntaxNode node)
        : base(node)
    {
        Receiver = node.ChildNodes().FirstOrDefault() is { } receiver
            ? Create(receiver)
            : null;
        MemberName = node.ChildTokens().LastOrDefault(static token =>
            token.Kind == LuaTokenKind.Identifier);
        IsColonAccess = node.Kind == LuaSyntaxKind.MethodCallExpression;
        _hasAccessOperator = node.ChildTokens().Any(token =>
            token.Kind == (IsColonAccess ? LuaTokenKind.Colon : LuaTokenKind.Dot) &&
            !token.IsMissing);
    }

    /// <summary>Gets the receiver, or <see langword="null"/> for an incomplete recovery shape.</summary>
    public LuaExpressionSyntax? Receiver { get; }

    /// <summary>
    /// Gets the member-name token. A synthesized missing identifier is returned when the parser
    /// recovered one; <see langword="null"/> means the node has no member-name slot at all.
    /// </summary>
    public LuaSyntaxToken? MemberName { get; }

    /// <summary>Gets whether this view represents colon access rather than dot access.</summary>
    public bool IsColonAccess { get; }

    /// <inheritdoc />
    public override bool IsComplete =>
        Receiver is { IsComplete: true } &&
        _hasAccessOperator &&
        MemberName is { IsMissing: false };
}

/// <summary>Provides a typed view over a named, local, global, or anonymous function.</summary>
public sealed class LuaFunctionDeclarationSyntax
{
    private readonly bool _hasRequiredTokens;

    internal LuaFunctionDeclarationSyntax(LuaSyntaxNode node)
    {
        Node = node;
        IsLocal = node.Kind == LuaSyntaxKind.LocalFunctionDeclarationStatement;
        IsGlobal = node.Kind == LuaSyntaxKind.GlobalDeclarationStatement;
        IsExpression = node.Kind == LuaSyntaxKind.FunctionExpression;

        var functionBody = node.ChildNodes().FirstOrDefault(static child =>
            child.Kind == LuaSyntaxKind.FunctionBody);
        if (functionBody is not null)
        {
            var parameterList = functionBody.ChildNodes().FirstOrDefault(static child =>
                child.Kind == LuaSyntaxKind.ParameterList);
            Parameters = parameterList is not null
                ? new LuaParameterListSyntax(parameterList)
                : null;
            var block = functionBody.ChildNodes().FirstOrDefault(static child =>
                child.Kind == LuaSyntaxKind.Block);
            Body = block is not null ? new LuaBlockSyntax(block) : null;
        }

        Name = CreateName(node);
        _hasRequiredTokens = HasRequiredTokens(node, functionBody);
    }

    /// <summary>Gets the underlying lossless function node.</summary>
    public LuaSyntaxNode Node { get; }

    /// <summary>Gets the function name, or <see langword="null"/> for anonymous functions or absent recovery slots.</summary>
    public LuaFunctionNameSyntax? Name { get; }

    /// <summary>Gets the parameter list, or <see langword="null"/> when its recovery slot is absent.</summary>
    public LuaParameterListSyntax? Parameters { get; }

    /// <summary>Gets the function body block, or <see langword="null"/> when its recovery slot is absent.</summary>
    public LuaBlockSyntax? Body { get; }

    /// <summary>Gets whether this is a <c>local function</c> declaration.</summary>
    public bool IsLocal { get; }

    /// <summary>Gets whether this is a Lua 5.5 <c>global function</c> declaration.</summary>
    public bool IsGlobal { get; }

    /// <summary>Gets whether this is an anonymous function expression.</summary>
    public bool IsExpression { get; }

    /// <summary>Gets whether a colon-qualified name supplies an implicit <c>self</c> parameter.</summary>
    public bool HasImplicitSelf => Name?.HasImplicitSelf == true;

    /// <summary>Gets whether every required name, parameter, body, and delimiter was parsed.</summary>
    public bool IsComplete =>
        (IsExpression || Name is { IsComplete: true }) &&
        _hasRequiredTokens &&
        Parameters is { IsComplete: true } &&
        Body is { IsComplete: true } &&
        !Node.DescendantTokens().Any(static token => token.IsMissing);

    private static LuaFunctionNameSyntax? CreateName(LuaSyntaxNode node)
    {
        if (node.Kind == LuaSyntaxKind.FunctionExpression)
        {
            return null;
        }

        var nameNode = node.ChildNodes().FirstOrDefault(static child =>
            child.Kind == LuaSyntaxKind.FunctionName);
        if (nameNode is not null)
        {
            return LuaFunctionNameSyntax.Create(nameNode);
        }

        var identifier = node.ChildTokens().FirstOrDefault(static token =>
            token.Kind == LuaTokenKind.Identifier);
        return identifier is not null
            ? new LuaFunctionNameSyntax(node, [identifier], hasImplicitSelf: false)
            : null;
    }

    private static bool HasRequiredTokens(LuaSyntaxNode node, LuaSyntaxNode? functionBody)
    {
        if (functionBody is null)
        {
            return false;
        }

        var declarationTokens = node.ChildTokens().ToImmutableArray();
        var hasFunctionKeyword = declarationTokens.Any(static token =>
            token.Kind == LuaTokenKind.FunctionKeyword && !token.IsMissing);
        var hasDeclarationPrefix = node.Kind switch
        {
            LuaSyntaxKind.LocalFunctionDeclarationStatement => declarationTokens.Any(static token =>
                token.Kind == LuaTokenKind.LocalKeyword && !token.IsMissing),
            LuaSyntaxKind.GlobalDeclarationStatement => declarationTokens.Any(static token =>
                token.Kind == LuaTokenKind.GlobalKeyword && !token.IsMissing),
            _ => true,
        };
        var bodyTokens = functionBody.ChildTokens().ToImmutableArray();
        return hasFunctionKeyword &&
            hasDeclarationPrefix &&
            bodyTokens.Any(static token =>
                token.Kind == LuaTokenKind.OpenParenthesis && !token.IsMissing) &&
            bodyTokens.Any(static token =>
                token.Kind == LuaTokenKind.CloseParenthesis && !token.IsMissing) &&
            bodyTokens.Any(static token =>
                token.Kind == LuaTokenKind.EndKeyword && !token.IsMissing);
    }
}

/// <summary>Provides a typed function-name projection.</summary>
public sealed class LuaFunctionNameSyntax
{
    internal LuaFunctionNameSyntax(
        LuaSyntaxNode node,
        ImmutableArray<LuaSyntaxToken> segments,
        bool hasImplicitSelf)
    {
        Node = node;
        Segments = segments;
        HasImplicitSelf = hasImplicitSelf;
    }

    /// <summary>Gets the lossless node that owns the projected name.</summary>
    public LuaSyntaxNode Node { get; }

    /// <summary>Gets each identifier segment in source order, including a synthesized missing segment.</summary>
    public ImmutableArray<LuaSyntaxToken> Segments { get; }

    /// <summary>Gets whether the final segment follows a colon and supplies implicit <c>self</c>.</summary>
    public bool HasImplicitSelf { get; }

    /// <summary>Gets whether all required name segments are present.</summary>
    public bool IsComplete =>
        !Segments.IsEmpty &&
        Segments.All(static segment => !segment.IsMissing);

    internal static LuaFunctionNameSyntax Create(LuaSyntaxNode node) =>
        new(
            node,
            node.ChildTokens()
                .Where(static token => token.Kind == LuaTokenKind.Identifier)
                .ToImmutableArray(),
            node.ChildTokens().Any(static token => token.Kind == LuaTokenKind.Colon));
}

/// <summary>Provides a typed function-parameter projection.</summary>
public sealed class LuaParameterListSyntax
{
    internal LuaParameterListSyntax(LuaSyntaxNode node)
    {
        Node = node;
        var tokens = node.ChildTokens().ToImmutableArray();
        var varArgIndex = -1;
        for (var index = 0; index < tokens.Length; index++)
        {
            if (tokens[index].Kind == LuaTokenKind.VarArg)
            {
                varArgIndex = index;
                break;
            }
        }
        HasVarArg = varArgIndex >= 0;
        Parameters = tokens
            .Take(varArgIndex >= 0 ? varArgIndex : tokens.Length)
            .Where(static token => token.Kind == LuaTokenKind.Identifier)
            .ToImmutableArray();
        VarArgName = varArgIndex >= 0
            ? tokens.Skip(varArgIndex + 1).FirstOrDefault(static token =>
                token.Kind == LuaTokenKind.Identifier)
            : null;
    }

    /// <summary>Gets the underlying lossless parameter-list node.</summary>
    public LuaSyntaxNode Node { get; }

    /// <summary>Gets fixed parameter identifiers in source order.</summary>
    public ImmutableArray<LuaSyntaxToken> Parameters { get; }

    /// <summary>Gets whether the list contains <c>...</c>.</summary>
    public bool HasVarArg { get; }

    /// <summary>Gets the optional Lua 5.5 named-vararg identifier.</summary>
    public LuaSyntaxToken? VarArgName { get; }

    /// <summary>Gets whether all parameter tokens are present.</summary>
    public bool IsComplete =>
        !Node.DescendantTokens().Any(static token => token.IsMissing);
}

/// <summary>Provides a typed view over a function block.</summary>
public sealed class LuaBlockSyntax
{
    internal LuaBlockSyntax(LuaSyntaxNode node)
    {
        Node = node;
    }

    /// <summary>Gets the underlying lossless block node.</summary>
    public LuaSyntaxNode Node { get; }

    /// <summary>Gets whether the block contains no recovery nodes or missing tokens.</summary>
    public bool IsComplete =>
        !Node.DescendantNodes().Any(static node => node.Kind == LuaSyntaxKind.Error) &&
        !Node.DescendantTokens().Any(static token => token.IsMissing);
}

/// <summary>Creates typed, non-owning projections over lossless Lua syntax nodes.</summary>
public static class LuaTypedSyntaxExtensions
{
    /// <summary>Gets the UTF-8 text covered by a syntax token's byte span.</summary>
    public static string GetText(this LuaSyntaxToken token, SourceText source)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(source);
        return Encoding.UTF8.GetString(source.GetSpan(token.Span));
    }

    /// <summary>Tries to view a normal call or colon method call.</summary>
    public static bool TryGetCallExpression(
        this LuaSyntaxNode node,
        [NotNullWhen(true)] out LuaCallExpressionSyntax? call)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Kind is LuaSyntaxKind.CallExpression or LuaSyntaxKind.MethodCallExpression)
        {
            call = new LuaCallExpressionSyntax(node);
            return true;
        }

        call = null;
        return false;
    }

    /// <summary>Tries to view dot member access or the target of a colon method call.</summary>
    public static bool TryGetMemberAccessExpression(
        this LuaSyntaxNode node,
        [NotNullWhen(true)] out LuaMemberAccessExpressionSyntax? member)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Kind is LuaSyntaxKind.MemberAccessExpression or LuaSyntaxKind.MethodCallExpression)
        {
            member = new LuaMemberAccessExpressionSyntax(node);
            return true;
        }

        member = null;
        return false;
    }

    /// <summary>Tries to view a named, local, global, or anonymous function node.</summary>
    public static bool TryGetFunctionDeclaration(
        this LuaSyntaxNode node,
        [NotNullWhen(true)] out LuaFunctionDeclarationSyntax? function)
    {
        ArgumentNullException.ThrowIfNull(node);
        var isFunction = node.Kind is
            LuaSyntaxKind.FunctionDeclarationStatement or
            LuaSyntaxKind.LocalFunctionDeclarationStatement or
            LuaSyntaxKind.FunctionExpression;
        if (!isFunction && node.Kind == LuaSyntaxKind.GlobalDeclarationStatement)
        {
            isFunction = node.ChildNodes().Any(static child =>
                child.Kind == LuaSyntaxKind.FunctionBody);
        }

        if (isFunction)
        {
            function = new LuaFunctionDeclarationSyntax(node);
            return true;
        }

        function = null;
        return false;
    }

    /// <summary>Tries to view any expression node, including an error-recovery expression.</summary>
    public static bool TryGetExpression(
        this LuaSyntaxNode node,
        [NotNullWhen(true)] out LuaExpressionSyntax? expression)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (IsExpressionKind(node.Kind))
        {
            expression = LuaExpressionSyntax.Create(node);
            return true;
        }

        expression = null;
        return false;
    }

    /// <summary>Tries to decode a syntax node as a valid UTF-8 constant Lua string.</summary>
    public static bool TryGetConstantString(this LuaSyntaxNode node, out string value)
    {
        ArgumentNullException.ThrowIfNull(node);
        return LuaExpressionSyntax.Create(node).TryGetConstantString(out value);
    }

    internal static bool IsExpressionKind(LuaSyntaxKind kind) => kind is
        LuaSyntaxKind.NilLiteralExpression or
        LuaSyntaxKind.FalseLiteralExpression or
        LuaSyntaxKind.TrueLiteralExpression or
        LuaSyntaxKind.NumericLiteralExpression or
        LuaSyntaxKind.StringLiteralExpression or
        LuaSyntaxKind.VarArgExpression or
        LuaSyntaxKind.IdentifierExpression or
        LuaSyntaxKind.ParenthesizedExpression or
        LuaSyntaxKind.UnaryExpression or
        LuaSyntaxKind.BinaryExpression or
        LuaSyntaxKind.FunctionExpression or
        LuaSyntaxKind.TableConstructorExpression or
        LuaSyntaxKind.IndexExpression or
        LuaSyntaxKind.MemberAccessExpression or
        LuaSyntaxKind.CallExpression or
        LuaSyntaxKind.MethodCallExpression;
}
