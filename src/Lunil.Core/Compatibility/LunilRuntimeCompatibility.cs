using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal sealed class LunilReferenceEqualityComparer : IEqualityComparer<object>
{
    public static LunilReferenceEqualityComparer Instance { get; } = new();

    private LunilReferenceEqualityComparer()
    {
    }

    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

    public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
}

internal static class LunilEnum
{
    public static bool IsDefined<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
#if NETSTANDARD2_1
        return Enum.IsDefined(typeof(TEnum), value);
#else
        return Enum.IsDefined(value);
#endif
    }
}

internal static class LunilByteHash
{
    private const uint OffsetBasis = 2166136261;
    private const uint Prime = 16777619;

    public static int Compute(ReadOnlySpan<byte> bytes)
    {
        var hash = OffsetBasis;
        foreach (var value in bytes)
        {
            hash = unchecked((hash ^ value) * Prime);
        }

        return unchecked((int)hash);
    }
}

internal static class LunilCryptography
{
    public const int Sha256HashSize = 32;

    private const string HexDigits = "0123456789ABCDEF";

    public static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        var hash = Sha256(bytes);
        var characters = new char[hash.Length * 2];
        for (var index = 0; index < hash.Length; index++)
        {
            characters[index * 2] = HexDigits[hash[index] >> 4];
            characters[(index * 2) + 1] = HexDigits[hash[index] & 0x0f];
        }

        return new string(characters);
    }

    public static byte[] Sha256(ReadOnlySpan<byte> bytes)
    {
#if NETSTANDARD2_1
        using var algorithm = SHA256.Create();
        return algorithm.ComputeHash(bytes.ToArray());
#else
        return SHA256.HashData(bytes);
#endif
    }
}

internal static class LunilInterlocked
{
    public static long Or(ref long location, long value)
    {
        var current = Volatile.Read(ref location);
        while (true)
        {
            var updated = current | value;
            var observed = Interlocked.CompareExchange(ref location, updated, current);
            if (observed == current)
            {
                return current;
            }

            current = observed;
        }
    }
}

internal static class LunilArray
{
    public static int MaximumLength
    {
        get
        {
#if NETSTANDARD2_1
            return 0X7FFFFFC7;
#else
            return Array.MaxLength;
#endif
        }
    }
}

internal static class LunilOperatingSystem
{
    public static bool IsWindows() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}

internal static class LunilRuntimeFeature
{
    public static bool IsDynamicCodeAvailable
    {
        get
        {
#if NETSTANDARD2_1
            return false;
#else
            return RuntimeFeature.IsDynamicCodeSupported && RuntimeFeature.IsDynamicCodeCompiled;
#endif
        }
    }
}

internal static class LunilBitConverter
{
    public static uint SingleToUInt32Bits(float value) =>
        unchecked((uint)BitConverter.SingleToInt32Bits(value));

    public static ulong DoubleToUInt64Bits(double value) =>
        unchecked((ulong)BitConverter.DoubleToInt64Bits(value));

    public static float UInt32BitsToSingle(uint value) =>
        BitConverter.Int32BitsToSingle(unchecked((int)value));

    public static double UInt64BitsToDouble(ulong value) =>
        BitConverter.Int64BitsToDouble(unchecked((long)value));
}

internal static class LunilBitOperations
{
    public static ulong RotateLeft(ulong value, int offset)
    {
        var count = offset & 63;
        return (value << count) | (value >> ((64 - count) & 63));
    }
}

internal static class LunilChar
{
    public static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    public static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';
}

internal static class LunilStopwatch
{
    public static TimeSpan GetElapsedTime(long startingTimestamp) =>
        GetElapsedTime(startingTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());

    public static TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp)
    {
        var elapsed = endingTimestamp - startingTimestamp;
        return TimeSpan.FromTicks((long)(elapsed *
            (TimeSpan.TicksPerSecond / (double)System.Diagnostics.Stopwatch.Frequency)));
    }
}

internal static class LunilConvert
{
    private const string HexDigits = "0123456789ABCDEF";

    public static string ToHexString(ReadOnlySpan<byte> bytes)
    {
        var characters = new char[bytes.Length * 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            characters[index * 2] = HexDigits[bytes[index] >> 4];
            characters[(index * 2) + 1] = HexDigits[bytes[index] & 0x0f];
        }

        return new string(characters);
    }

    public static byte[] FromHexString(string value)
    {
        LunilGuard.NotNull(value);
        if ((value.Length & 1) != 0)
        {
            throw new FormatException("The hexadecimal value must contain an even number of digits.");
        }

        var result = new byte[value.Length / 2];
        for (var index = 0; index < result.Length; index++)
        {
            var high = HexValue(value[index * 2]);
            var low = HexValue(value[(index * 2) + 1]);
            if ((high | low) < 0)
            {
                throw new FormatException("The value contains a non-hexadecimal character.");
            }

            result[index] = (byte)((high << 4) | low);
        }

        return result;
    }

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'F' => value - 'A' + 10,
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => -1,
    };
}

internal static class LunilMarshal
{
    public static int GetLastPInvokeError()
    {
#if NETSTANDARD2_1
        return Marshal.GetLastWin32Error();
#else
        return Marshal.GetLastPInvokeError();
#endif
    }
}

internal static class LunilStream
{
    public static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer[read..]);
            if (count == 0)
            {
                throw new EndOfStreamException();
            }

            read += count;
        }
    }
}

internal sealed class LunilUnreachableException : InvalidOperationException
{
    public LunilUnreachableException()
        : base("The program executed a path that was expected to be unreachable.")
    {
    }
}
