using Lunil.Core.Text;
using Lunil.Syntax.Lexing;

namespace Lunil.Syntax.Tests.Lexing;

public sealed class LuaNumericLiteralDecoderTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("0x7FFFFFFFFFFFFFFF", long.MaxValue)]
    [InlineData("0xFFFFFFFFFFFFFFFF", -1)]
    [InlineData("0x10000000000000000", 0)]
    public void ClassifiesRepresentableAndWrappingIntegers(string text, long expected)
    {
        var value = LexValue<LuaIntegerTokenValue>(text);

        Assert.Equal(expected, value.Integer);
    }

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData(".5", 0.5)]
    [InlineData("1e2", 100.0)]
    [InlineData("0x1.8", 1.5)]
    [InlineData("0x1p4", 16.0)]
    [InlineData("0x.8p-1", 0.25)]
    [InlineData("9223372036854775808", 9223372036854775808.0)]
    public void ClassifiesFloatingPointNumerals(string text, double expected)
    {
        var value = LexValue<LuaFloatTokenValue>(text);

        Assert.Equal(expected, value.Float);
    }

    [Fact]
    public void HugeHexExponentMatchesPucLua32BitExponentWrapping()
    {
        var value = LexValue<LuaFloatTokenValue>("0x1p999999999999999999999999");

        Assert.Equal(0.0, value.Float);
    }

    [Fact]
    public void InvalidNumeralHasNoDecodedValue()
    {
        var result = LuaLexer.Lex(SourceText.FromUtf8("1_000"));

        Assert.Null(result.Tokens[0].Value);
        Assert.Equal("LUA1006", Assert.Single(result.Diagnostics).Code);
    }

    private static TValue LexValue<TValue>(string text)
        where TValue : LuaTokenValue
    {
        var result = LuaLexer.Lex(SourceText.FromUtf8(text));
        Assert.Empty(result.Diagnostics);
        return Assert.IsType<TValue>(result.Tokens[0].Value);
    }
}
