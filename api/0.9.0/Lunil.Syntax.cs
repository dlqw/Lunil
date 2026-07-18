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
        GotoKeyword = 15,
        IfKeyword = 16,
        InKeyword = 17,
        LocalKeyword = 18,
        NilKeyword = 19,
        NotKeyword = 20,
        OrKeyword = 21,
        RepeatKeyword = 22,
        ReturnKeyword = 23,
        ThenKeyword = 24,
        TrueKeyword = 25,
        UntilKeyword = 26,
        WhileKeyword = 27,
        Plus = 28,
        Minus = 29,
        Star = 30,
        Slash = 31,
        FloorDivide = 32,
        Percent = 33,
        Caret = 34,
        Length = 35,
        Ampersand = 36,
        Tilde = 37,
        Pipe = 38,
        ShiftLeft = 39,
        ShiftRight = 40,
        Concatenate = 41,
        VarArg = 42,
        LessThan = 43,
        LessThanOrEqual = 44,
        GreaterThan = 45,
        GreaterThanOrEqual = 46,
        Equal = 47,
        NotEqual = 48,
        Assign = 49,
        OpenParenthesis = 50,
        CloseParenthesis = 51,
        OpenBrace = 52,
        CloseBrace = 53,
        OpenBracket = 54,
        CloseBracket = 55,
        DoubleColon = 56,
        Colon = 57,
        Semicolon = 58,
        Comma = 59,
        Dot = 60
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
    public sealed class LuaParseResult : System.IEquatable<Lunil.Syntax.Parsing.LuaParseResult>
    {
        public Lunil.Core.Text.SourceText Source { get => throw null; init { } }
        public Lunil.Syntax.Parsing.LuaSyntaxNode Root { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics { get => throw null; init { } }
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
        LocalFunctionDeclarationStatement = 17,
        LocalDeclarationStatement = 18,
        AttributedName = 19,
        ReturnStatement = 20,
        NilLiteralExpression = 21,
        FalseLiteralExpression = 22,
        TrueLiteralExpression = 23,
        NumericLiteralExpression = 24,
        StringLiteralExpression = 25,
        VarArgExpression = 26,
        IdentifierExpression = 27,
        ParenthesizedExpression = 28,
        UnaryExpression = 29,
        BinaryExpression = 30,
        FunctionExpression = 31,
        TableConstructorExpression = 32,
        TableField = 33,
        IndexExpression = 34,
        MemberAccessExpression = 35,
        CallExpression = 36,
        MethodCallExpression = 37,
        ArgumentList = 38,
        FunctionBody = 39,
        ParameterList = 40,
        NameList = 41,
        ExpressionList = 42,
        VariableList = 43,
        FunctionName = 44,
        Error = 45
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
}
