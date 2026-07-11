using System.Text;
using Lunil.Core.Numerics;

namespace Lunil.Core.Tests.Numerics;

public sealed class LuaNumberParserTests
{
    [Theory]
    [InlineData("  +12\t", LuaNumberKind.Integer, 12.0)]
    [InlineData("-9223372036854775808", LuaNumberKind.Integer, -9223372036854775808.0)]
    [InlineData("9223372036854775808", LuaNumberKind.Float, 9223372036854775808.0)]
    [InlineData("-0xffffffffffffffff", LuaNumberKind.Integer, 1.0)]
    [InlineData("0x1.8p1", LuaNumberKind.Float, 3.0)]
    [InlineData(".5", LuaNumberKind.Float, 0.5)]
    [InlineData("+1.5", LuaNumberKind.Float, 1.5)]
    [InlineData("1e309", LuaNumberKind.Float, double.PositiveInfinity)]
    public void ParsesLuaStringNumbers(
        string text,
        LuaNumberKind expectedKind,
        double expected)
    {
        Assert.True(LuaNumberParser.TryParseString(Encoding.ASCII.GetBytes(text), out var value));
        Assert.Equal(expectedKind, value.Kind);
        Assert.Equal(
            expected,
            value.Kind == LuaNumberKind.Integer ? value.Integer : value.Float);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("1 2")]
    [InlineData("nan")]
    [InlineData("inf")]
    [InlineData("0x")]
    public void RejectsNonNumeralStrings(string text)
    {
        Assert.False(LuaNumberParser.TryParseString(Encoding.ASCII.GetBytes(text), out _));
    }
}
