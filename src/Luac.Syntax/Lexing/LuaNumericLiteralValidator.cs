namespace Luac.Syntax.Lexing;

internal static class LuaNumericLiteralValidator
{
    public static bool IsValid(ReadOnlySpan<byte> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        return text.Length >= 2 && text[0] == (byte)'0' && text[1] is (byte)'x' or (byte)'X'
            ? IsValidHexadecimal(text)
            : IsValidDecimal(text);
    }

    private static bool IsValidDecimal(ReadOnlySpan<byte> text)
    {
        var position = 0;
        var digitCount = ConsumeDecimalDigits(text, ref position);

        if (position < text.Length && text[position] == (byte)'.')
        {
            position++;
            digitCount += ConsumeDecimalDigits(text, ref position);
        }

        if (digitCount == 0)
        {
            return false;
        }

        if (position < text.Length && text[position] is (byte)'e' or (byte)'E')
        {
            position++;
            ConsumeOptionalSign(text, ref position);
            if (ConsumeDecimalDigits(text, ref position) == 0)
            {
                return false;
            }
        }

        return position == text.Length;
    }

    private static bool IsValidHexadecimal(ReadOnlySpan<byte> text)
    {
        var position = 2;
        var digitCount = ConsumeHexadecimalDigits(text, ref position);

        if (position < text.Length && text[position] == (byte)'.')
        {
            position++;
            digitCount += ConsumeHexadecimalDigits(text, ref position);
        }

        if (digitCount == 0)
        {
            return false;
        }

        if (position < text.Length && text[position] is (byte)'p' or (byte)'P')
        {
            position++;
            ConsumeOptionalSign(text, ref position);
            if (ConsumeDecimalDigits(text, ref position) == 0)
            {
                return false;
            }
        }

        return position == text.Length;
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

    private static void ConsumeOptionalSign(ReadOnlySpan<byte> text, ref int position)
    {
        if (position < text.Length && text[position] is (byte)'+' or (byte)'-')
        {
            position++;
        }
    }

    private static bool IsHexadecimalDigit(byte value) =>
        value is >= (byte)'0' and <= (byte)'9' or
            >= (byte)'a' and <= (byte)'f' or
            >= (byte)'A' and <= (byte)'F';
}
