using System.Security.Cryptography;

namespace Lunil.Core.Tests.Compatibility;

public sealed class LunilRuntimeCompatibilityTests
{
    [Fact]
    public void ILogBMatchesRuntimeForFiniteAndSpecialValues()
    {
        double[] values =
        [
            double.NegativeInfinity,
            -double.MaxValue,
            -1,
            -double.Epsilon,
            -0.0,
            0.0,
            double.Epsilon,
            BitConverter.Int64BitsToDouble(0x000f_ffff_ffff_ffff),
            1,
            2,
            double.MaxValue,
            double.PositiveInfinity,
            double.NaN,
        ];

        foreach (var value in values)
        {
            Assert.Equal(Math.ILogB(value), LunilMath.ILogB(value));
        }
    }

    [Fact]
    public void CompatibilityPrimitivesMatchRuntimeContracts()
    {
        var bytes = "portable-compatibility"u8.ToArray();

        Assert.Equal(Array.MaxLength, LunilArray.MaximumLength);
        Assert.Equal(Convert.ToHexString(bytes), LunilConvert.ToHexString(bytes));
        Assert.Equal(bytes, LunilConvert.FromHexString(Convert.ToHexString(bytes)));
        Assert.Equal(SHA256.HashData(bytes), LunilCryptography.Sha256(bytes));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), LunilCryptography.Sha256Hex(bytes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("GG")]
    [InlineData("0g")]
    public void HexDecoderMatchesRuntimeFailureContract(string value)
    {
        if (value.Length == 0)
        {
            Assert.Empty(LunilConvert.FromHexString(value));
            return;
        }

        Assert.Throws<FormatException>(() => LunilConvert.FromHexString(value));
    }

    [Fact]
    public void StopwatchElapsedTimeMatchesRuntimeConversion()
    {
        const long start = 1234567;
        var end = start + System.Diagnostics.Stopwatch.Frequency +
            (System.Diagnostics.Stopwatch.Frequency / 2);

        Assert.Equal(
            System.Diagnostics.Stopwatch.GetElapsedTime(start, end),
            LunilStopwatch.GetElapsedTime(start, end));
    }
}
