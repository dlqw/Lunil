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

    [Theory]
    [InlineData(1.0, -1074)]
    [InlineData(1.0, -1022)]
    [InlineData(1.5, -1023)]
    [InlineData(1.0, 1023)]
    [InlineData(1.0, 1024)]
    [InlineData(double.MaxValue, 1)]
    [InlineData(double.Epsilon, -1)]
    [InlineData(-0.0, 2048)]
    public void PortableScaleBMatchesTheRuntimeAtNumericBoundaries(
        double value,
        int exponent)
    {
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(Math.ScaleB(value, exponent)),
            BitConverter.DoubleToInt64Bits(LunilMath.ScaleB(value, exponent)));
    }

    [Theory]
    [InlineData("0x1p-1074", double.Epsilon)]
    [InlineData("0x1p-1075", 0.0)]
    [InlineData("0x1.fffffffffffffp1023", double.MaxValue)]
    [InlineData("0x1p1024", double.PositiveInfinity)]
    public void ParsesHexadecimalFloatScaleBoundaries(string text, double expected)
    {
        Assert.True(LuaNumberParser.TryParseString(Encoding.ASCII.GetBytes(text), out var value));
        Assert.Equal(LuaNumberKind.Float, value.Kind);
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expected),
            BitConverter.DoubleToInt64Bits(value.Float));
    }
}
