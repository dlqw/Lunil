// Target Frameworks: net10.0
#nullable enable

namespace Lunil.Syntax.Lexing
{
    public sealed class LuaFloatTokenValue : Lunil.Syntax.Lexing.LuaTokenValue, System.IEquatable<Lunil.Syntax.Lexing.LuaFloatTokenValue>
    {
        protected System.Type EqualityContract { get => throw null; }
        public double Float { get => throw null; init { } }
        public LuaFloatTokenValue(double Float) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Syntax.Lexing.LuaFloatTokenValue? left, Lunil.Syntax.Lexing.LuaFloatTokenValue? right) => throw null;
        public static bool operator ==(Lunil.Syntax.Lexing.LuaFloatTokenValue? left, Lunil.Syntax.Lexing.LuaFloatTokenValue? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Syntax.Lexing.LuaTokenValue? other) => throw null;
        public bool Equals(Lunil.Syntax.Lexing.LuaFloatTokenValue? other) => throw null;
        public void Deconstruct(out double Float) => throw null;
    }

    public sealed class LuaIntegerTokenValue : Lunil.Syntax.Lexing.LuaTokenValue, System.IEquatable<Lunil.Syntax.Lexing.LuaIntegerTokenValue>
    {
        protected System.Type EqualityContract { get => throw null; }
        public long Integer { get => throw null; init { } }
        public LuaIntegerTokenValue(long Integer) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Syntax.Lexing.LuaIntegerTokenValue? left, Lunil.Syntax.Lexing.LuaIntegerTokenValue? right) => throw null;
        public static bool operator ==(Lunil.Syntax.Lexing.LuaIntegerTokenValue? left, Lunil.Syntax.Lexing.LuaIntegerTokenValue? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Syntax.Lexing.LuaTokenValue? other) => throw null;
        public bool Equals(Lunil.Syntax.Lexing.LuaIntegerTokenValue? other) => throw null;
        public void Deconstruct(out long Integer) => throw null;
    }

    public sealed class LuaLexResult : System.IEquatable<Lunil.Syntax.Lexing.LuaLexResult>
    {
        public Lunil.Core.Text.SourceText Source { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Syntax.Lexing.LuaSyntaxToken> Tokens { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics { get => throw null; init { } }
        public Lunil.Core.LuaLanguageVersion LanguageVersion { get => throw null; init { } }
        public LuaLexResult(Lunil.Core.Text.SourceText Source, System.Collections.Immutable.ImmutableArray<Lunil.Syntax.Lexing.LuaSyntaxToken> Tokens, System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Syntax.Lexing.LuaLexResult? left, Lunil.Syntax.Lexing.LuaLexResult? right) => throw null;
        public static bool operator ==(Lunil.Syntax.Lexing.LuaLexResult? left, Lunil.Syntax.Lexing.LuaLexResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Syntax.Lexing.LuaLexResult? other) => throw null;
        public void Deconstruct(out Lunil.Core.Text.SourceText Source, out System.Collections.Immutable.ImmutableArray<Lunil.Syntax.Lexing.LuaSyntaxToken> Tokens, out System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics) => throw null;
    }

    public static class LuaLexer
    {
        public static Lunil.Syntax.Lexing.LuaLexResult Lex(Lunil.Core.Text.SourceText source, Lunil.Syntax.Lexing.LuaLexerOptions? options = null) => throw null;
    }

    public sealed class LuaLexerOptions : System.IEquatable<Lunil.Syntax.Lexing.LuaLexerOptions>
    {
        public static Lunil.Syntax.Lexing.LuaLexerOptions Default { get => throw null; }
        public static Lunil.Syntax.Lexing.LuaLexerOptions File { get => throw null; }
        public Lunil.Core.LuaLanguageVersion LanguageVersion { get => throw null; init { } }
        public bool AcceptUtf8ByteOrderMark { get => throw null; init { } }
        public bool AcceptShebang { get => throw null; init { } }
        public int MaximumTokenCount { get => throw null; init { } }
        public int MaximumDiagnosticCount { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Syntax.Lexing.LuaLexerOptions? left, Lunil.Syntax.Lexing.LuaLexerOptions? right) => throw null;
        public static bool operator ==(Lunil.Syntax.Lexing.LuaLexerOptions? left, Lunil.Syntax.Lexing.LuaLexerOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Syntax.Lexing.LuaLexerOptions? other) => throw null;
    }

    public static class LuaNumericLiteralDecoder
    {
        public static Lunil.Syntax.Lexing.LuaTokenValue Decode(Lunil.Core.Text.SourceText source, Lunil.Syntax.Lexing.LuaSyntaxToken token) => throw null;
    }

    public sealed class LuaStringLiteralDecodeResult : System.IEquatable<Lunil.Syntax.Lexing.LuaStringLiteralDecodeResult>
    {
        public Lunil.Syntax.Lexing.LuaStringTokenValue Value { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics { get => throw null; init { } }
        public LuaStringLiteralDecodeResult(Lunil.Syntax.Lexing.LuaStringTokenValue Value, System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Syntax.Lexing.LuaStringLiteralDecodeResult? left, Lunil.Syntax.Lexing.LuaStringLiteralDecodeResult? right) => throw null;
        public static bool operator ==(Lunil.Syntax.Lexing.LuaStringLiteralDecodeResult? left, Lunil.Syntax.Lexing.LuaStringLiteralDecodeResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Syntax.Lexing.LuaStringLiteralDecodeResult? other) => throw null;
        public void Deconstruct(out Lunil.Syntax.Lexing.LuaStringTokenValue Value, out System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics) => throw null;
    }

    public static class LuaStringLiteralDecoder
    {
        public static Lunil.Syntax.Lexing.LuaStringLiteralDecodeResult Decode(Lunil.Core.Text.SourceText source, Lunil.Syntax.Lexing.LuaSyntaxToken token) => throw null;
    }

    public sealed class LuaStringTokenValue : Lunil.Syntax.Lexing.LuaTokenValue, System.IEquatable<Lunil.Syntax.Lexing.LuaStringTokenValue>
    {
        protected System.Type EqualityContract { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<byte> Bytes { get => throw null; init { } }
        public LuaStringTokenValue(System.Collections.Immutable.ImmutableArray<byte> Bytes) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Syntax.Lexing.LuaStringTokenValue? left, Lunil.Syntax.Lexing.LuaStringTokenValue? right) => throw null;
        public static bool operator ==(Lunil.Syntax.Lexing.LuaStringTokenValue? left, Lunil.Syntax.Lexing.LuaStringTokenValue? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Syntax.Lexing.LuaTokenValue? other) => throw null;
        public bool Equals(Lunil.Syntax.Lexing.LuaStringTokenValue? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<byte> Bytes) => throw null;
    }

    public static class LuaSyntaxFacts
    {
        public static Lunil.Syntax.Lexing.LuaTokenKind GetIdentifierOrKeywordKind(System.ReadOnlySpan<byte> text) => throw null;
        public static bool IsKeyword(Lunil.Syntax.Lexing.LuaTokenKind kind) => throw null;
    }

    public sealed class LuaSyntaxToken : System.IEquatable<Lunil.Syntax.Lexing.LuaSyntaxToken>
    {
        public Lunil.Syntax.Lexing.LuaTokenKind Kind { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Syntax.Lexing.LuaSyntaxTrivia> LeadingTrivia { get => throw null; init { } }
        public Lunil.Syntax.Lexing.LuaTokenValue? Value { get => throw null; init { } }
        public bool IsMissing { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan FullSpan { get => throw null; }
        public LuaSyntaxToken(Lunil.Syntax.Lexing.LuaTokenKind Kind, Lunil.Core.Text.TextSpan Span, System.Collections.Immutable.ImmutableArray<Lunil.Syntax.Lexing.LuaSyntaxTrivia> LeadingTrivia) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Syntax.Lexing.LuaSyntaxToken? left, Lunil.Syntax.Lexing.LuaSyntaxToken? right) => throw null;
        public static bool operator ==(Lunil.Syntax.Lexing.LuaSyntaxToken? left, Lunil.Syntax.Lexing.LuaSyntaxToken? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Syntax.Lexing.LuaSyntaxToken? other) => throw null;
        public void Deconstruct(out Lunil.Syntax.Lexing.LuaTokenKind Kind, out Lunil.Core.Text.TextSpan Span, out System.Collections.Immutable.ImmutableArray<Lunil.Syntax.Lexing.LuaSyntaxTrivia> LeadingTrivia) => throw null;
    }

    public readonly struct LuaSyntaxTrivia : System.IEquatable<Lunil.Syntax.Lexing.LuaSyntaxTrivia>
    {
        public Lunil.Syntax.Lexing.LuaTriviaKind Kind { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public LuaSyntaxTrivia(Lunil.Syntax.Lexing.LuaTriviaKind Kind, Lunil.Core.Text.TextSpan Span) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.Syntax.Lexing.LuaSyntaxTrivia left, Lunil.Syntax.Lexing.LuaSyntaxTrivia right) => throw null;
        public static bool operator ==(Lunil.Syntax.Lexing.LuaSyntaxTrivia left, Lunil.Syntax.Lexing.LuaSyntaxTrivia right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.Syntax.Lexing.LuaSyntaxTrivia other) => throw null;
        public void Deconstruct(out Lunil.Syntax.Lexing.LuaTriviaKind Kind, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public enum LuaTokenKind
    {
        BadToken = 0,
        EndOfFile = 1,
        Identifier = 2,
        NumericLiteral = 3,
        StringLiteral = 4,
        LongStringLiteral = 5,
        AndKeyword = 6,
        BreakKeyword = 7,
        DoKeyword = 8,
        ElseKeyword = 9,
        ElseIfKeyword = 10,
        EndKeyword = 11,
        FalseKeyword = 12,
        ForKeyword = 13,
        FunctionKeyword = 14,
        GlobalKeyword = 15,
        GotoKeyword = 16,
        IfKeyword = 17,
        InKeyword = 18,
        LocalKeyword = 19,
        NilKeyword = 20,
        NotKeyword = 21,
        OrKeyword = 22,
        RepeatKeyword = 23,
        ReturnKeyword = 24,
        ThenKeyword = 25,
        TrueKeyword = 26,
        UntilKeyword = 27,
        WhileKeyword = 28,
        Plus = 29,
        Minus = 30,
        Star = 31,
        Slash = 32,
        FloorDivide = 33,
        Percent = 34,
        Caret = 35,
        Length = 36,
        Ampersand = 37,
        Tilde = 38,
        Pipe = 39,
        ShiftLeft = 40,
        ShiftRight = 41,
        Concatenate = 42,
        VarArg = 43,
        LessThan = 44,
        LessThanOrEqual = 45,
        GreaterThan = 46,
        GreaterThanOrEqual = 47,
        Equal = 48,
        NotEqual = 49,
        Assign = 50,
        OpenParenthesis = 51,
        CloseParenthesis = 52,
        OpenBrace = 53,
        CloseBrace = 54,
        OpenBracket = 55,
        CloseBracket = 56,
        DoubleColon = 57,
        Colon = 58,
        Semicolon = 59,
        Comma = 60,
        Dot = 61
    }

    public abstract class LuaTokenValue : System.IEquatable<Lunil.Syntax.Lexing.LuaTokenValue>
    {
        protected System.Type EqualityContract { get => throw null; }
        public override string ToString() => throw null;
        protected virtual bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Syntax.Lexing.LuaTokenValue? left, Lunil.Syntax.Lexing.LuaTokenValue? right) => throw null;
        public static bool operator ==(Lunil.Syntax.Lexing.LuaTokenValue? left, Lunil.Syntax.Lexing.LuaTokenValue? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public virtual bool Equals(Lunil.Syntax.Lexing.LuaTokenValue? other) => throw null;
        protected LuaTokenValue(Lunil.Syntax.Lexing.LuaTokenValue original) { }
    }

    public enum LuaTriviaKind
    {
        Whitespace = 0,
        EndOfLine = 1,
        Comment = 2,
        LongComment = 3,
        Utf8ByteOrderMark = 4,
        Shebang = 5
    }
}
namespace Lunil.Syntax.Parsing
{
    public sealed class LuaBlockSyntax
    {
        public Lunil.Syntax.Parsing.LuaSyntaxNode Node { get => throw null; }
        public bool IsComplete { get => throw null; }
    }

    public sealed class LuaCallExpressionSyntax : Lunil.Syntax.Parsing.LuaExpressionSyntax
    {
        public Lunil.Syntax.Parsing.LuaExpressionSyntax? Callee { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.Syntax.Parsing.LuaExpressionSyntax> Arguments { get => throw null; }
        public bool IsMethodCall { get => throw null; }
        public bool IsComplete { get => throw null; }
    }

    public class LuaExpressionSyntax
    {
        public Lunil.Syntax.Parsing.LuaSyntaxNode Node { get => throw null; }
        public Lunil.Syntax.Parsing.LuaSyntaxKind Kind { get => throw null; }
        public Lunil.Core.Text.TextSpan Span { get => throw null; }
        public bool IsComplete { get => throw null; }
        public bool TryGetIdentifierToken([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Lunil.Syntax.Lexing.LuaSyntaxToken? token) => throw null;
        public bool TryGetConstantString(out string value) => throw null;
    }

    public sealed class LuaFunctionDeclarationSyntax
    {
        public Lunil.Syntax.Parsing.LuaSyntaxNode Node { get => throw null; }
        public Lunil.Syntax.Parsing.LuaFunctionNameSyntax? Name { get => throw null; }
        public Lunil.Syntax.Parsing.LuaParameterListSyntax? Parameters { get => throw null; }
        public Lunil.Syntax.Parsing.LuaBlockSyntax? Body { get => throw null; }
        public bool IsLocal { get => throw null; }
        public bool IsGlobal { get => throw null; }
        public bool IsExpression { get => throw null; }
        public bool HasImplicitSelf { get => throw null; }
        public bool IsComplete { get => throw null; }
    }

    public sealed class LuaFunctionNameSyntax
    {
        public Lunil.Syntax.Parsing.LuaSyntaxNode Node { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.Syntax.Lexing.LuaSyntaxToken> Segments { get => throw null; }
        public bool HasImplicitSelf { get => throw null; }
        public bool IsComplete { get => throw null; }
    }

    public sealed class LuaMemberAccessExpressionSyntax : Lunil.Syntax.Parsing.LuaExpressionSyntax
    {
        public Lunil.Syntax.Parsing.LuaExpressionSyntax? Receiver { get => throw null; }
        public Lunil.Syntax.Lexing.LuaSyntaxToken? MemberName { get => throw null; }
        public bool IsColonAccess { get => throw null; }
        public bool IsComplete { get => throw null; }
    }

    public sealed class LuaParameterListSyntax
    {
        public Lunil.Syntax.Parsing.LuaSyntaxNode Node { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.Syntax.Lexing.LuaSyntaxToken> Parameters { get => throw null; }
        public bool HasVarArg { get => throw null; }
        public Lunil.Syntax.Lexing.LuaSyntaxToken? VarArgName { get => throw null; }
        public bool IsComplete { get => throw null; }
    }

    public sealed class LuaParseResult : System.IEquatable<Lunil.Syntax.Parsing.LuaParseResult>
    {
        public Lunil.Core.Text.SourceText Source { get => throw null; init { } }
        public Lunil.Syntax.Parsing.LuaSyntaxNode Root { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics { get => throw null; init { } }
        public Lunil.Core.LuaLanguageVersion LanguageVersion { get => throw null; init { } }
        public LuaParseResult(Lunil.Core.Text.SourceText Source, Lunil.Syntax.Parsing.LuaSyntaxNode Root, System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Syntax.Parsing.LuaParseResult? left, Lunil.Syntax.Parsing.LuaParseResult? right) => throw null;
        public static bool operator ==(Lunil.Syntax.Parsing.LuaParseResult? left, Lunil.Syntax.Parsing.LuaParseResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Syntax.Parsing.LuaParseResult? other) => throw null;
        public void Deconstruct(out Lunil.Core.Text.SourceText Source, out Lunil.Syntax.Parsing.LuaSyntaxNode Root, out System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics) => throw null;
    }

    public static class LuaParser
    {
        public static Lunil.Syntax.Parsing.LuaParseResult Parse(Lunil.Core.Text.SourceText source, Lunil.Syntax.Lexing.LuaLexerOptions? lexerOptions = null, Lunil.Syntax.Parsing.LuaParserOptions? parserOptions = null) => throw null;
        public static Lunil.Syntax.Parsing.LuaParseResult Parse(Lunil.Syntax.Lexing.LuaLexResult lexResult, Lunil.Syntax.Parsing.LuaParserOptions? options = null) => throw null;
    }

    public sealed class LuaParserOptions : System.IEquatable<Lunil.Syntax.Parsing.LuaParserOptions>
    {
        public static Lunil.Syntax.Parsing.LuaParserOptions Default { get => throw null; }
        public Lunil.Core.LuaLanguageVersion LanguageVersion { get => throw null; init { } }
        public int MaximumRecursionDepth { get => throw null; init { } }
        public int MaximumNodeCount { get => throw null; init { } }
        public int MaximumDiagnosticCount { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Syntax.Parsing.LuaParserOptions? left, Lunil.Syntax.Parsing.LuaParserOptions? right) => throw null;
        public static bool operator ==(Lunil.Syntax.Parsing.LuaParserOptions? left, Lunil.Syntax.Parsing.LuaParserOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Syntax.Parsing.LuaParserOptions? other) => throw null;
    }

    public readonly struct LuaSyntaxElement : System.IEquatable<Lunil.Syntax.Parsing.LuaSyntaxElement>
    {
        public Lunil.Syntax.Parsing.LuaSyntaxNode? Node { get => throw null; }
        public Lunil.Syntax.Lexing.LuaSyntaxToken? Token { get => throw null; }
        public bool IsNode { get => throw null; }
        public bool IsToken { get => throw null; }
        public static implicit operator Lunil.Syntax.Parsing.LuaSyntaxElement(Lunil.Syntax.Parsing.LuaSyntaxNode node) => throw null;
        public static implicit operator Lunil.Syntax.Parsing.LuaSyntaxElement(Lunil.Syntax.Lexing.LuaSyntaxToken token) => throw null;
        public override string? ToString() => throw null;
        public static bool operator !=(Lunil.Syntax.Parsing.LuaSyntaxElement left, Lunil.Syntax.Parsing.LuaSyntaxElement right) => throw null;
        public static bool operator ==(Lunil.Syntax.Parsing.LuaSyntaxElement left, Lunil.Syntax.Parsing.LuaSyntaxElement right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Syntax.Parsing.LuaSyntaxElement other) => throw null;
    }

    public enum LuaSyntaxKind
    {
        CompilationUnit = 0,
        Block = 1,
        EmptyStatement = 2,
        AssignmentStatement = 3,
        CallStatement = 4,
        LabelStatement = 5,
        BreakStatement = 6,
        GotoStatement = 7,
        DoStatement = 8,
        WhileStatement = 9,
        RepeatStatement = 10,
        IfStatement = 11,
        ElseIfClause = 12,
        ElseClause = 13,
        NumericForStatement = 14,
        GenericForStatement = 15,
        FunctionDeclarationStatement = 16,
        GlobalDeclarationStatement = 17,
        LocalFunctionDeclarationStatement = 18,
        LocalDeclarationStatement = 19,
        AttributedName = 20,
        ReturnStatement = 21,
        NilLiteralExpression = 22,
        FalseLiteralExpression = 23,
        TrueLiteralExpression = 24,
        NumericLiteralExpression = 25,
        StringLiteralExpression = 26,
        VarArgExpression = 27,
        IdentifierExpression = 28,
        ParenthesizedExpression = 29,
        UnaryExpression = 30,
        BinaryExpression = 31,
        FunctionExpression = 32,
        TableConstructorExpression = 33,
        TableField = 34,
        IndexExpression = 35,
        MemberAccessExpression = 36,
        CallExpression = 37,
        MethodCallExpression = 38,
        ArgumentList = 39,
        FunctionBody = 40,
        ParameterList = 41,
        NameList = 42,
        ExpressionList = 43,
        VariableList = 44,
        FunctionName = 45,
        Error = 46
    }

    public sealed class LuaSyntaxNode
    {
        public Lunil.Syntax.Parsing.LuaSyntaxKind Kind { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.Syntax.Parsing.LuaSyntaxElement> Children { get => throw null; }
        public Lunil.Core.Text.TextSpan Span { get => throw null; }
        public Lunil.Core.Text.TextSpan FullSpan { get => throw null; }
        public LuaSyntaxNode(Lunil.Syntax.Parsing.LuaSyntaxKind kind, System.Collections.Generic.IEnumerable<Lunil.Syntax.Parsing.LuaSyntaxElement> children, int emptyPosition = 0) { }
        public System.Collections.Generic.IEnumerable<Lunil.Syntax.Parsing.LuaSyntaxNode> ChildNodes() => throw null;
        public System.Collections.Generic.IEnumerable<Lunil.Syntax.Lexing.LuaSyntaxToken> ChildTokens() => throw null;
        public System.Collections.Generic.IEnumerable<Lunil.Syntax.Parsing.LuaSyntaxNode> DescendantNodes() => throw null;
        public System.Collections.Generic.IEnumerable<Lunil.Syntax.Lexing.LuaSyntaxToken> DescendantTokens() => throw null;
    }

    public abstract class LuaSyntaxVisitor<TResult>
    {
        public virtual TResult Visit(Lunil.Syntax.Parsing.LuaSyntaxNode? node) => throw null;
        public virtual TResult VisitCallExpression(Lunil.Syntax.Parsing.LuaCallExpressionSyntax node) => throw null;
        public virtual TResult VisitFunctionDeclaration(Lunil.Syntax.Parsing.LuaFunctionDeclarationSyntax node) => throw null;
        public virtual TResult VisitMemberAccessExpression(Lunil.Syntax.Parsing.LuaMemberAccessExpressionSyntax node) => throw null;
        public virtual TResult VisitExpression(Lunil.Syntax.Parsing.LuaExpressionSyntax node) => throw null;
        public virtual TResult DefaultVisit(Lunil.Syntax.Parsing.LuaSyntaxNode node) => throw null;
    }

    public abstract class LuaSyntaxWalker
    {
        public virtual void Visit(Lunil.Syntax.Parsing.LuaSyntaxNode? node) { }
        public virtual void VisitCallExpression(Lunil.Syntax.Parsing.LuaCallExpressionSyntax node) { }
        public virtual void VisitFunctionDeclaration(Lunil.Syntax.Parsing.LuaFunctionDeclarationSyntax node) { }
        public virtual void VisitMemberAccessExpression(Lunil.Syntax.Parsing.LuaMemberAccessExpressionSyntax node) { }
        public virtual void VisitExpression(Lunil.Syntax.Parsing.LuaExpressionSyntax node) { }
        public virtual void DefaultVisit(Lunil.Syntax.Parsing.LuaSyntaxNode node) { }
    }

    public static class LuaTypedSyntaxExtensions
    {
        public static string GetText(this Lunil.Syntax.Lexing.LuaSyntaxToken token, Lunil.Core.Text.SourceText source) => throw null;
        public static bool TryGetCallExpression(this Lunil.Syntax.Parsing.LuaSyntaxNode node, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Lunil.Syntax.Parsing.LuaCallExpressionSyntax? call) => throw null;
        public static bool TryGetMemberAccessExpression(this Lunil.Syntax.Parsing.LuaSyntaxNode node, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Lunil.Syntax.Parsing.LuaMemberAccessExpressionSyntax? member) => throw null;
        public static bool TryGetFunctionDeclaration(this Lunil.Syntax.Parsing.LuaSyntaxNode node, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Lunil.Syntax.Parsing.LuaFunctionDeclarationSyntax? function) => throw null;
        public static bool TryGetExpression(this Lunil.Syntax.Parsing.LuaSyntaxNode node, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Lunil.Syntax.Parsing.LuaExpressionSyntax? expression) => throw null;
        public static bool TryGetConstantString(this Lunil.Syntax.Parsing.LuaSyntaxNode node, out string value) => throw null;
    }
}
