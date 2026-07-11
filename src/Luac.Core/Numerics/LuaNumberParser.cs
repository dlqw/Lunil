using System.Buffers.Text;

namespace Luac.Core.Numerics;

/// <summary>Parses source numerals and the stricter string-to-number grammar used by Lua 5.4.</summary>
public static class LuaNumberParser
{
    public static bool TryParseLiteral(ReadOnlySpan<byte> text, out LuaNumber value) =>
        TryParse(text, allowSignAndWhitespace: false, out value);

    public static bool TryParseString(ReadOnlySpan<byte> text, out LuaNumber value) =>
        TryParse(text, allowSignAndWhitespace: true, out value);

    private static bool TryParse(
        ReadOnlySpan<byte> text,
        bool allowSignAndWhitespace,
        out LuaNumber value)
    {
        value = default;
        if (allowSignAndWhitespace)
        {
            text = TrimAsciiWhitespace(text);
        }

        if (text.IsEmpty)
        {
            return false;
        }

        var negative = false;
        var body = text;
        if (body[0] is (byte)'+' or (byte)'-')
        {
            if (!allowSignAndWhitespace)
            {
                return false;
            }

            negative = body[0] == (byte)'-';
            body = body[1..];
            if (body.IsEmpty)
            {
                return false;
            }
        }

        var hexadecimal = body.Length >= 2 && body[0] == (byte)'0' &&
            body[1] is (byte)'x' or (byte)'X';
        if (!ValidateBody(body, hexadecimal))
        {
            return false;
        }

        if (hexadecimal && body.IndexOfAny((byte)'.', (byte)'p', (byte)'P') < 0)
        {
            ulong integer = 0;
            foreach (var current in body[2..])
            {
                integer = unchecked(integer * 16 + (uint)HexadecimalValue(current));
            }

            var signed = unchecked((long)integer);
            value = LuaNumber.FromInteger(negative ? unchecked(-signed) : signed);
            return true;
        }

        if (!hexadecimal && IsDecimalInteger(body, negative, out var decimalInteger))
        {
            value = LuaNumber.FromInteger(decimalInteger);
            return true;
        }

        double number;
        if (hexadecimal)
        {
            number = ParseHexadecimalFloat(body);
            if (negative)
            {
                number = -number;
            }
        }
        else if (!Utf8Parser.TryParse(text, out number, out var consumed) || consumed != text.Length)
        {
            return false;
        }

        value = LuaNumber.FromFloat(number);
        return true;
    }

    private static bool ValidateBody(ReadOnlySpan<byte> text, bool hexadecimal)
    {
        var position = hexadecimal ? 2 : 0;
        var digitCount = hexadecimal
            ? ConsumeHexadecimalDigits(text, ref position)
            : ConsumeDecimalDigits(text, ref position);

        if (position < text.Length && text[position] == (byte)'.')
        {
            position++;
            digitCount += hexadecimal
                ? ConsumeHexadecimalDigits(text, ref position)
                : ConsumeDecimalDigits(text, ref position);
        }

        if (digitCount == 0)
        {
            return false;
        }

        var exponentMarker = hexadecimal ? (byte)'p' : (byte)'e';
        if (position < text.Length &&
            (text[position] | 0x20) == exponentMarker)
        {
            position++;
            if (position < text.Length && text[position] is (byte)'+' or (byte)'-')
            {
                position++;
            }

            if (ConsumeDecimalDigits(text, ref position) == 0)
            {
                return false;
            }
        }

        return position == text.Length;
    }

    private static bool IsDecimalInteger(
        ReadOnlySpan<byte> text,
        bool negative,
        out long result)
    {
        result = 0;
        ulong magnitude = 0;
        var maximum = negative ? 0x8000_0000_0000_0000UL : long.MaxValue;
        foreach (var current in text)
        {
            if (current is < (byte)'0' or > (byte)'9')
            {
                return false;
            }

            var digit = (uint)(current - (byte)'0');
            if (magnitude > (maximum - digit) / 10)
            {
                return false;
            }

            magnitude = magnitude * 10 + digit;
        }

        var signed = unchecked((long)magnitude);
        result = negative ? unchecked(-signed) : signed;
        return true;
    }

    private static double ParseHexadecimalFloat(ReadOnlySpan<byte> text)
    {
        const int maximumSignificantDigits = 30;
        var position = 2;
        var result = 0.0;
        var significantDigits = 0;
        var exponentCorrection = 0;
        var hasDot = false;

        while (position < text.Length)
        {
            var current = text[position];
            if (current == (byte)'.')
            {
                hasDot = true;
                position++;
                continue;
            }

            if (!IsHexadecimalDigit(current))
            {
                break;
            }

            if (significantDigits != 0 || current != (byte)'0')
            {
                significantDigits++;
                if (significantDigits <= maximumSignificantDigits)
                {
                    result = result * 16.0 + HexadecimalValue(current);
                }
                else
                {
                    exponentCorrection = unchecked(exponentCorrection + 1);
                }
            }

            if (hasDot)
            {
                exponentCorrection = unchecked(exponentCorrection - 1);
            }

            position++;
        }

        exponentCorrection = unchecked(exponentCorrection * 4);

        if (position < text.Length)
        {
            position++;
            var negativeExponent = false;
            if (position < text.Length && text[position] is (byte)'+' or (byte)'-')
            {
                negativeExponent = text[position] == (byte)'-';
                position++;
            }

            var explicitExponent = 0;
            while (position < text.Length)
            {
                explicitExponent = unchecked(
                    explicitExponent * 10 + text[position++] - (byte)'0');
            }

            exponentCorrection = unchecked(
                exponentCorrection +
                    (negativeExponent ? -explicitExponent : explicitExponent));
        }

        return Math.ScaleB(result, exponentCorrection);
    }

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> text)
    {
        var start = 0;
        while (start < text.Length && IsAsciiWhitespace(text[start]))
        {
            start++;
        }

        var end = text.Length;
        while (end > start && IsAsciiWhitespace(text[end - 1]))
        {
            end--;
        }

        return text[start..end];
    }

    private static int ConsumeDecimalDigits(ReadOnlySpan<byte> text, ref int position)
    {
        var start = position;
        while (position < text.Length && text[position] is >= (byte)'0' and <= (byte)'9')
        {
            position++;
        }

        return position - start;
    }

    private static int ConsumeHexadecimalDigits(ReadOnlySpan<byte> text, ref int position)
    {
        var start = position;
        while (position < text.Length && IsHexadecimalDigit(text[position]))
        {
            position++;
        }

        return position - start;
    }

    private static bool IsAsciiWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\n' or 0x0B or 0x0C or (byte)'\r';

    private static bool IsHexadecimalDigit(byte value) =>
        value is >= (byte)'0' and <= (byte)'9' or
            >= (byte)'a' and <= (byte)'f' or
            >= (byte)'A' and <= (byte)'F';

    private static int HexadecimalValue(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
        _ => value - (byte)'A' + 10,
    };
}
