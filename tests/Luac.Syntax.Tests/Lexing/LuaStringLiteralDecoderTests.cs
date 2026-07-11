using Luac.Core.Text;
using Luac.Syntax.Lexing;

namespace Luac.Syntax.Tests.Lexing;

public sealed class LuaStringLiteralDecoderTests
{
    [Fact]
    public void DecodesAllSimpleEscapeSequences()
    {
        var result = LexSingle("'\\a\\b\\f\\n\\r\\t\\v\\\\\\\"\\\''");

        Assert.Empty(result.Lex.Diagnostics);
        byte[] expected = [0x07, 0x08, 0x0c, 0x0a, 0x0d, 0x09, 0x0b, 0x5c, 0x22, 0x27];
        Assert.Equal(expected, result.Value.Bytes.ToArray());
    }

    [Fact]
    public void DecodesDecimalHexadecimalAndUtf8Escapes()
    {
        var result = LexSingle("'\\65\\x42\\u{1F600}'");

        Assert.Empty(result.Lex.Diagnostics);
        Assert.Equal("AB😀"u8.ToArray(), result.Value.Bytes);
    }

    [Fact]
    public void Utf8EscapeUsesLuaExtendedRange()
    {
        var result = LexSingle("'\\u{7FFFFFFF}'");

        Assert.Empty(result.Lex.Diagnostics);
        byte[] expected = [0xfd, 0xbf, 0xbf, 0xbf, 0xbf, 0xbf];
        Assert.Equal(expected, result.Value.Bytes.ToArray());
    }

    [Fact]
    public void ZEscapeRemovesAllFollowingLuaWhitespace()
    {
        var result = LexSingle("'a\\z \t\r\n\v\f b'");

        Assert.Empty(result.Lex.Diagnostics);
        Assert.Equal("ab"u8.ToArray(), result.Value.Bytes);
    }

    [Fact]
    public void EscapedMixedNewlineBecomesOneLfByte()
    {
        var result = LexSingle("'a\\\n\rb'");

        Assert.Empty(result.Lex.Diagnostics);
        Assert.Equal("a\nb"u8.ToArray(), result.Value.Bytes);
    }

    [Fact]
    public void LongStringDropsInitialNewlineAndNormalizesAllLineEndings()
    {
        var result = LexSingle("[=[\r\na\rb\n\rc\nd]=]");

        Assert.Empty(result.Lex.Diagnostics);
        Assert.Equal("a\nb\nc\nd"u8.ToArray(), result.Value.Bytes);
    }

    [Theory]
    [InlineData("'\\q'", "LUA1009")]
    [InlineData("'\\x1'", "LUA1010")]
    [InlineData("'\\256'", "LUA1011")]
    [InlineData("'\\u1'", "LUA1012")]
    [InlineData("'\\u{1'", "LUA1013")]
    [InlineData("'\\u{80000000}'", "LUA1014")]
    [InlineData("'\\u{FFFFFFFFFFFFFFFFFFFFFFFF}'", "LUA1014")]
    public void ReportsMalformedEscapes(string text, string expectedCode)
    {
        var source = SourceText.FromUtf8(text);

        var result = LuaLexer.Lex(source);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    private static (LuaLexResult Lex, LuaStringTokenValue Value) LexSingle(string text)
    {
        var result = LuaLexer.Lex(SourceText.FromUtf8(text));
        var token = result.Tokens[0];
        var value = Assert.IsType<LuaStringTokenValue>(token.Value);
        return (result, value);
    }
}
