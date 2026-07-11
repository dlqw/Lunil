using System.Globalization;
using System.Text;
using Luac.Core.Text;

namespace Luac.Syntax.Lexing;

/// <summary>Classifies and decodes a valid Lua 5.4 numeral as integer or float.</summary>
public static class LuaNumericLiteralDecoder
{
    public static LuaTokenValue Decode(SourceText source, LuaSyntaxToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(token);

        if (token.Kind != LuaTokenKind.NumericLiteral)
        {
            throw new ArgumentException("Token is not a Lua numeric literal.", nameof(token));
        }

        if (!TryDecode(source.GetSpan(token.Span), out var value))
        {
            throw new FormatException("The token does not contain a valid Lua 5.4 numeral.");
        }

        return value;
    }

    internal static bool TryDecode(ReadOnlySpan<byte> text, out LuaTokenValue value)
    {
        value = null!;
        if (!LuaNumericLiteralValidator.IsValid(text))
        {
            return false;
        }

        var hexadecimal = text.Length >= 2 &&
            text[0] == (byte)'0' &&
            text[1] is (byte)'x' or (byte)'X';

        if (hexadecimal && !Contains(text, (byte)'.') &&
            !Contains(text, (byte)'p') && !Contains(text, (byte)'P'))
        {
            ulong integer = 0;
            foreach (var current in text[2..])
            {
                integer = unchecked((integer * 16) + (uint)HexadecimalValue(current));
            }

            value = new LuaIntegerTokenValue(unchecked((long)integer));
            return true;
        }

        if (!hexadecimal && IsDecimalInteger(text, out var decimalInteger))
        {
            value = new LuaIntegerTokenValue(decimalInteger);
            return true;
        }

        var number = hexadecimal
            ? ParseHexadecimalFloat(text)
            : ParseDecimalFloat(text);
        if (number is null)
        {
            return false;
        }

        value = new LuaFloatTokenValue(number.Value);
        return true;
    }

    private static bool IsDecimalInteger(ReadOnlySpan<byte> text, out long result)
    {
        result = 0;
        foreach (var current in text)
        {
            if (current is < (byte)'0' or > (byte)'9')
            {
                return false;
            }

            var digit = current - (byte)'0';
            if (result > (long.MaxValue - digit) / 10)
            {
                return false;
            }

            result = (result * 10) + digit;
        }

        return true;
    }

    private static double? ParseDecimalFloat(ReadOnlySpan<byte> text)
    {
        var stringValue = Encoding.ASCII.GetString(text);
        return double.TryParse(
            stringValue,
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture,
            out var result)
            ? result
            : null;
    }

    private static double ParseHexadecimalFloat(ReadOnlySpan<byte> text)
    {
        const int maximumSignificantDigits = 30;

        var position = 2;
        var result = 0.0;
        var significantDigits = 0;
        var nonSignificantZeros = 0;
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

            if (significantDigits == 0 && current == (byte)'0')
            {
                nonSignificantZeros++;
            }
            else if (++significantDigits <= maximumSignificantDigits)
            {
                result = (result * 16.0) + HexadecimalValue(current);
            }
            else
            {
                exponentCorrection++;
            }

            if (hasDot)
            {
                exponentCorrection--;
            }

            position++;
        }

        _ = nonSignificantZeros;
        exponentCorrection = unchecked(exponentCorrection * 4);

        if (position < text.Length && text[position] is (byte)'p' or (byte)'P')
        {
            position++;
            var negative = false;
            if (position < text.Length && text[position] is (byte)'+' or (byte)'-')
            {
                negative = text[position] == (byte)'-';
                position++;
            }

            var explicitExponent = 0;
            while (position < text.Length)
            {
                explicitExponent = unchecked(
                    (explicitExponent * 10) + text[position++] - (byte)'0');
            }

            exponentCorrection = unchecked(
                exponentCorrection + (negative ? -explicitExponent : explicitExponent));
        }

        return Math.ScaleB(result, exponentCorrection);
    }

    private static bool Contains(ReadOnlySpan<byte> text, byte value) => text.IndexOf(value) >= 0;

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
