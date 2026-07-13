using System.Collections.Immutable;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;

namespace Lunil.EmmyLua;

public enum LuaAnnotationTokenKind : byte
{
    EndOfFile,
    Identifier,
    StringLiteral,
    NumericLiteral,
    At,
    Colon,
    Comma,
    Dot,
    Ellipsis,
    Pipe,
    Ampersand,
    Question,
    OpenParenthesis,
    CloseParenthesis,
    OpenBrace,
    CloseBrace,
    OpenBracket,
    CloseBracket,
    LessThan,
    GreaterThan,
    Assign,
    Plus,
    Minus,
    Star,
    Hash,
    BadToken,
}

public readonly record struct LuaAnnotationToken(
    LuaAnnotationTokenKind Kind,
    TextSpan Span,
    string Text);

public sealed record LuaAnnotationLexResult(
    TextSpan Span,
    ImmutableArray<LuaAnnotationToken> Tokens,
    ImmutableArray<Diagnostic> Diagnostics,
    int ErrorCount);
