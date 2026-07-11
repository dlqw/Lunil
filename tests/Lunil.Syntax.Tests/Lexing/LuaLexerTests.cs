using Lunil.Core.Text;
using Lunil.Syntax.Lexing;

namespace Lunil.Syntax.Tests.Lexing;

public sealed class LuaLexerTests
{
    [Fact]
    public void RecognizesEveryLua54Keyword()
    {
        const string text =
            "and break do else elseif end false for function goto if in local nil not or " +
            "repeat return then true until while";

        var result = LuaLexer.Lex(SourceText.FromUtf8(text));

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            [
                LuaTokenKind.AndKeyword,
                LuaTokenKind.BreakKeyword,
                LuaTokenKind.DoKeyword,
                LuaTokenKind.ElseKeyword,
                LuaTokenKind.ElseIfKeyword,
                LuaTokenKind.EndKeyword,
                LuaTokenKind.FalseKeyword,
                LuaTokenKind.ForKeyword,
                LuaTokenKind.FunctionKeyword,
                LuaTokenKind.GotoKeyword,
                LuaTokenKind.IfKeyword,
                LuaTokenKind.InKeyword,
                LuaTokenKind.LocalKeyword,
                LuaTokenKind.NilKeyword,
                LuaTokenKind.NotKeyword,
                LuaTokenKind.OrKeyword,
                LuaTokenKind.RepeatKeyword,
                LuaTokenKind.ReturnKeyword,
                LuaTokenKind.ThenKeyword,
                LuaTokenKind.TrueKeyword,
                LuaTokenKind.UntilKeyword,
                LuaTokenKind.WhileKeyword,
                LuaTokenKind.EndOfFile,
            ],
            result.Tokens.Select(static token => token.Kind));
    }

    [Fact]
    public void RecognizesLongestPunctuatorFirst()
    {
        const string text =
            "+ - * / // % ^ # & ~ | << >> .. ... < <= > >= == ~= = " +
            "( ) { } [ ] :: : ; , .";

        var result = LuaLexer.Lex(SourceText.FromUtf8(text));

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            [
                LuaTokenKind.Plus,
                LuaTokenKind.Minus,
                LuaTokenKind.Star,
                LuaTokenKind.Slash,
                LuaTokenKind.FloorDivide,
                LuaTokenKind.Percent,
                LuaTokenKind.Caret,
                LuaTokenKind.Length,
                LuaTokenKind.Ampersand,
                LuaTokenKind.Tilde,
                LuaTokenKind.Pipe,
                LuaTokenKind.ShiftLeft,
                LuaTokenKind.ShiftRight,
                LuaTokenKind.Concatenate,
                LuaTokenKind.VarArg,
                LuaTokenKind.LessThan,
                LuaTokenKind.LessThanOrEqual,
                LuaTokenKind.GreaterThan,
                LuaTokenKind.GreaterThanOrEqual,
                LuaTokenKind.Equal,
                LuaTokenKind.NotEqual,
                LuaTokenKind.Assign,
                LuaTokenKind.OpenParenthesis,
                LuaTokenKind.CloseParenthesis,
                LuaTokenKind.OpenBrace,
                LuaTokenKind.CloseBrace,
                LuaTokenKind.OpenBracket,
                LuaTokenKind.CloseBracket,
                LuaTokenKind.DoubleColon,
                LuaTokenKind.Colon,
                LuaTokenKind.Semicolon,
                LuaTokenKind.Comma,
                LuaTokenKind.Dot,
                LuaTokenKind.EndOfFile,
            ],
            result.Tokens.Select(static token => token.Kind));
    }

    [Fact]
    public void PreservesWhitespaceCommentsAndLineEndingsAsLeadingTrivia()
    {
        var source = SourceText.FromUtf8(" \t-- note\r\n  return");

        var result = LuaLexer.Lex(source);
        var token = result.Tokens[0];

        Assert.Equal(LuaTokenKind.ReturnKeyword, token.Kind);
        Assert.Equal(
            [
                LuaTriviaKind.Whitespace,
                LuaTriviaKind.Comment,
                LuaTriviaKind.EndOfLine,
                LuaTriviaKind.Whitespace,
            ],
            token.LeadingTrivia.Select(static trivia => trivia.Kind));
        Assert.Equal(new TextSpan(0, source.Length), token.FullSpan);
    }

    [Fact]
    public void RecognizesLongStringsWithExactDelimiterLevel()
    {
        var source = SourceText.FromUtf8("[==[abc]=]still]==]");

        var result = LuaLexer.Lex(source);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(LuaTokenKind.LongStringLiteral, result.Tokens[0].Kind);
        Assert.Equal(new TextSpan(0, source.Length), result.Tokens[0].Span);
    }

    [Fact]
    public void RecognizesLongCommentsAsTrivia()
    {
        var result = LuaLexer.Lex(SourceText.FromUtf8("--[=[comment ]=]\nreturn"));

        Assert.Empty(result.Diagnostics);
        Assert.Equal(LuaTokenKind.ReturnKeyword, result.Tokens[0].Kind);
        Assert.Equal(
            [LuaTriviaKind.LongComment, LuaTriviaKind.EndOfLine],
            result.Tokens[0].LeadingTrivia.Select(static trivia => trivia.Kind));
    }

    [Fact]
    public void QuotedStringsSupportEscapedQuotesLinesAndZWhitespace()
    {
        var result = LuaLexer.Lex(SourceText.FromUtf8("'a\\\'b' \"x\\\r\ny\" \"a\\z \r\n\t b\""));

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            [
                LuaTokenKind.StringLiteral,
                LuaTokenKind.StringLiteral,
                LuaTokenKind.StringLiteral,
                LuaTokenKind.EndOfFile,
            ],
            result.Tokens.Select(static token => token.Kind));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("123")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("1e2")]
    [InlineData("1E-2")]
    [InlineData("0x1")]
    [InlineData("0XAf")]
    [InlineData("0x1.8")]
    [InlineData("0x1p2")]
    [InlineData("0x.8p-1")]
    public void AcceptsLua54NumeralForms(string text)
    {
        var result = LuaLexer.Lex(SourceText.FromUtf8(text));

        Assert.Empty(result.Diagnostics);
        Assert.Equal(LuaTokenKind.NumericLiteral, result.Tokens[0].Kind);
        Assert.Equal(text.Length, result.Tokens[0].Span.Length);
    }

    [Theory]
    [InlineData("1..2")]
    [InlineData("0x")]
    [InlineData("1e+")]
    [InlineData("123abc")]
    [InlineData("0x1p+")]
    [InlineData("1_000")]
    public void ReportsGreedilyScannedMalformedNumerals(string text)
    {
        var result = LuaLexer.Lex(SourceText.FromUtf8(text));

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("LUA1006", diagnostic.Code);
        Assert.Equal(LuaTokenKind.NumericLiteral, result.Tokens[0].Kind);
        var expectedLength = text == "1_000" ? 2 : text.Length;
        Assert.Equal(expectedLength, result.Tokens[0].Span.Length);
    }

    [Fact]
    public void NumeralExponentSignDoesNotConsumeFollowingOperators()
    {
        var result = LuaLexer.Lex(SourceText.FromUtf8("1e+2+3"));

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            [
                LuaTokenKind.NumericLiteral,
                LuaTokenKind.Plus,
                LuaTokenKind.NumericLiteral,
                LuaTokenKind.EndOfFile,
            ],
            result.Tokens.Select(static token => token.Kind));
    }

    [Fact]
    public void MalformedNumeralConsumesOnlyOneNonHexadecimalNameByte()
    {
        var result = LuaLexer.Lex(SourceText.FromUtf8("123xyz"));

        Assert.Equal("LUA1006", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(new TextSpan(0, 4), result.Tokens[0].Span);
        Assert.Equal(LuaTokenKind.Identifier, result.Tokens[1].Kind);
        Assert.Equal(new TextSpan(4, 2), result.Tokens[1].Span);
    }

    [Fact]
    public void InvalidLongStringDelimiterIsDiagnosedAsOneBadToken()
    {
        var result = LuaLexer.Lex(SourceText.FromUtf8("[===value"));

        Assert.Equal("LUA1008", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(LuaTokenKind.BadToken, result.Tokens[0].Kind);
        Assert.Equal(new TextSpan(0, 4), result.Tokens[0].Span);
        Assert.Equal(LuaTokenKind.Identifier, result.Tokens[1].Kind);
    }

    [Fact]
    public void FileModeConsumesBomAndShebangOnlyAtTheStart()
    {
        byte[] bytes =
        [
            0xef, 0xbb, 0xbf,
            .. "#!/usr/bin/lua\nreturn"u8,
        ];

        var result = LuaLexer.Lex(new SourceText(bytes), LuaLexerOptions.File);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(LuaTokenKind.ReturnKeyword, result.Tokens[0].Kind);
        Assert.Equal(
            [
                LuaTriviaKind.Utf8ByteOrderMark,
                LuaTriviaKind.Shebang,
                LuaTriviaKind.EndOfLine,
            ],
            result.Tokens[0].LeadingTrivia.Select(static trivia => trivia.Kind));
    }

    [Fact]
    public void DefaultModeTreatsLeadingLengthOperatorAsLuaSource()
    {
        var result = LuaLexer.Lex(SourceText.FromUtf8("#value"));

        Assert.Empty(result.Diagnostics);
        Assert.Equal(LuaTokenKind.Length, result.Tokens[0].Kind);
        Assert.Equal(LuaTokenKind.Identifier, result.Tokens[1].Kind);
    }

    [Fact]
    public void UnterminatedConstructsProduceStableDiagnostics()
    {
        var quoted = LuaLexer.Lex(SourceText.FromUtf8("'value"));
        var longString = LuaLexer.Lex(SourceText.FromUtf8("[=[value"));
        var longComment = LuaLexer.Lex(SourceText.FromUtf8("--[=[value"));

        Assert.Equal("LUA1002", Assert.Single(quoted.Diagnostics).Code);
        Assert.Equal("LUA1004", Assert.Single(longString.Diagnostics).Code);
        Assert.Equal("LUA1005", Assert.Single(longComment.Diagnostics).Code);
    }

    [Fact]
    public void UnescapedNewlineTerminatesQuotedTokenWithoutConsumingLineEnding()
    {
        var result = LuaLexer.Lex(SourceText.FromUtf8("'a\nreturn"));

        Assert.Equal("LUA1003", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(LuaTokenKind.StringLiteral, result.Tokens[0].Kind);
        Assert.Equal(LuaTokenKind.ReturnKeyword, result.Tokens[1].Kind);
        Assert.Equal(LuaTriviaKind.EndOfLine, result.Tokens[1].LeadingTrivia[0].Kind);
    }

    [Fact]
    public void UnexpectedBytesAlwaysAdvance()
    {
        var result = LuaLexer.Lex(new SourceText([0xff, 0xfe]));

        Assert.Equal(2, result.Diagnostics.Length);
        Assert.Equal(
            [LuaTokenKind.BadToken, LuaTokenKind.BadToken, LuaTokenKind.EndOfFile],
            result.Tokens.Select(static token => token.Kind));
    }

    [Fact]
    public void TokenBudgetStopsPathologicalInput()
    {
        var options = LuaLexerOptions.Default with { MaximumTokenCount = 2 };

        var result = LuaLexer.Lex(SourceText.FromUtf8("a b c"), options);

        Assert.Equal("LUA1007", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(
            [LuaTokenKind.Identifier, LuaTokenKind.EndOfFile],
            result.Tokens.Select(static token => token.Kind));
    }

    [Fact]
    public void ArbitraryBytesAreCoveredExactlyOnceAndLexerAlwaysTerminates()
    {
        var random = new Random(0x54);
        var bytes = new byte[4096];
        random.NextBytes(bytes);
        var source = new SourceText(bytes);

        var result = LuaLexer.Lex(source);

        var expectedStart = 0;
        foreach (var token in result.Tokens)
        {
            foreach (var trivia in token.LeadingTrivia)
            {
                Assert.Equal(expectedStart, trivia.Span.Start);
                expectedStart = trivia.Span.End;
            }

            Assert.Equal(expectedStart, token.Span.Start);
            expectedStart = token.Span.End;
        }

        Assert.Equal(source.Length, expectedStart);
        Assert.Equal(LuaTokenKind.EndOfFile, result.Tokens[^1].Kind);
    }
}
