using System.Buffers;
using System.Collections.Immutable;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;

namespace Lunil.Syntax.Lexing;

/// <summary>Decodes Lua 5.4 quoted and long string literals into their byte value.</summary>
public static class LuaStringLiteralDecoder
{
    public static LuaStringLiteralDecodeResult Decode(SourceText source, LuaSyntaxToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(token);

        if (token.Kind is not (LuaTokenKind.StringLiteral or LuaTokenKind.LongStringLiteral))
        {
            throw new ArgumentException("Token is not a Lua string literal.", nameof(token));
        }

        if (token.Span.End > source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(token));
        }

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var output = new ArrayBufferWriter<byte>();
        var raw = source.GetSpan(token.Span);

        if (token.Kind == LuaTokenKind.LongStringLiteral)
        {
            DecodeLongString(raw, output);
        }
        else
        {
            DecodeQuotedString(raw, token.Span.Start, output, diagnostics);
        }

        return new LuaStringLiteralDecodeResult(
            new LuaStringTokenValue(ImmutableArray.Create(output.WrittenSpan.ToArray())),
            diagnostics.ToImmutable());
    }

    private static void DecodeLongString(
        ReadOnlySpan<byte> raw,
        ArrayBufferWriter<byte> output)
    {
        if (raw.Length < 2 || raw[0] != (byte)'[')
        {
            return;
        }

        var equalsCount = 0;
        var position = 1;
        while (position < raw.Length && raw[position] == (byte)'=')
        {
            equalsCount++;
            position++;
        }

        if (position >= raw.Length || raw[position] != (byte)'[')
        {
            return;
        }

        position++;
        var end = HasLongStringCloser(raw, equalsCount)
            ? raw.Length - equalsCount - 2
            : raw.Length;

        if (position < end && IsNewLine(raw[position]))
        {
            ConsumeNewLine(raw, ref position, end);
        }

        while (position < end)
        {
            if (IsNewLine(raw[position]))
            {
                ConsumeNewLine(raw, ref position, end);
                WriteByte(output, (byte)'\n');
            }
            else
            {
                WriteByte(output, raw[position++]);
            }
        }
    }

    private static void DecodeQuotedString(
        ReadOnlySpan<byte> raw,
        int absoluteStart,
        ArrayBufferWriter<byte> output,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (raw.IsEmpty || raw[0] is not ((byte)'\'' or (byte)'"'))
        {
            return;
        }

        var quote = raw[0];
        var end = raw.Length > 1 && raw[^1] == quote ? raw.Length - 1 : raw.Length;
        var position = 1;

        while (position < end)
        {
            if (raw[position] != (byte)'\\')
            {
                WriteByte(output, raw[position++]);
                continue;
            }

            var escapeStart = position++;
            if (position >= end)
            {
                break;
            }

            var escape = raw[position++];
            switch (escape)
            {
                case (byte)'a': WriteByte(output, 0x07); break;
                case (byte)'b': WriteByte(output, 0x08); break;
                case (byte)'f': WriteByte(output, 0x0c); break;
                case (byte)'n': WriteByte(output, (byte)'\n'); break;
                case (byte)'r': WriteByte(output, (byte)'\r'); break;
                case (byte)'t': WriteByte(output, (byte)'\t'); break;
                case (byte)'v': WriteByte(output, 0x0b); break;
                case (byte)'\\': WriteByte(output, (byte)'\\'); break;
                case (byte)'"': WriteByte(output, (byte)'"'); break;
                case (byte)'\'': WriteByte(output, (byte)'\''); break;

                case (byte)'\r':
                case (byte)'\n':
                    position--;
                    ConsumeNewLine(raw, ref position, end);
                    WriteByte(output, (byte)'\n');
                    break;

                case (byte)'z':
                    while (position < end && IsLuaWhitespace(raw[position]))
                    {
                        if (IsNewLine(raw[position]))
                        {
                            ConsumeNewLine(raw, ref position, end);
                        }
                        else
                        {
                            position++;
                        }
                    }

                    break;

                case (byte)'x':
                    DecodeHexadecimalEscape(
                        raw,
                        ref position,
                        end,
                        absoluteStart,
                        escapeStart,
                        output,
                        diagnostics);
                    break;

                case (byte)'u':
                    DecodeUtf8Escape(
                        raw,
                        ref position,
                        end,
                        absoluteStart,
                        escapeStart,
                        output,
                        diagnostics);
                    break;

                default:
                    if (IsDecimalDigit(escape))
                    {
                        DecodeDecimalEscape(
                            raw,
                            escape,
                            ref position,
                            end,
                            absoluteStart,
                            escapeStart,
                            output,
                            diagnostics);
                    }
                    else
                    {
                        diagnostics.Add(CreateDiagnostic(
                            "LUA1009",
                            absoluteStart,
                            escapeStart,
                            position,
                            "Invalid escape sequence."));
                    }

                    break;
            }
        }
    }

    private static void DecodeHexadecimalEscape(
        ReadOnlySpan<byte> raw,
        ref int position,
        int end,
        int absoluteStart,
        int escapeStart,
        ArrayBufferWriter<byte> output,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var value = 0;
        for (var index = 0; index < 2; index++)
        {
            if (position >= end || !IsHexadecimalDigit(raw[position]))
            {
                diagnostics.Add(CreateDiagnostic(
                    "LUA1010",
                    absoluteStart,
                    escapeStart,
                    position,
                    "Two hexadecimal digits are required after \\x."));
                return;
            }

            value = (value << 4) | HexadecimalValue(raw[position++]);
        }

        WriteByte(output, (byte)value);
    }

    private static void DecodeDecimalEscape(
        ReadOnlySpan<byte> raw,
        byte firstDigit,
        ref int position,
        int end,
        int absoluteStart,
        int escapeStart,
        ArrayBufferWriter<byte> output,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var value = firstDigit - (byte)'0';
        var count = 1;
        while (count < 3 && position < end && IsDecimalDigit(raw[position]))
        {
            value = (value * 10) + raw[position++] - (byte)'0';
            count++;
        }

        if (value > byte.MaxValue)
        {
            diagnostics.Add(CreateDiagnostic(
                "LUA1011",
                absoluteStart,
                escapeStart,
                position,
                "Decimal escape exceeds 255."));
            return;
        }

        WriteByte(output, (byte)value);
    }

    private static void DecodeUtf8Escape(
        ReadOnlySpan<byte> raw,
        ref int position,
        int end,
        int absoluteStart,
        int escapeStart,
        ArrayBufferWriter<byte> output,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (position >= end || raw[position] != (byte)'{')
        {
            diagnostics.Add(CreateDiagnostic(
                "LUA1012",
                absoluteStart,
                escapeStart,
                position,
                "Missing '{' after \\u."));
            return;
        }

        position++;
        var digitStart = position;
        uint value = 0;
        var valueTooLarge = false;
        while (position < end && IsHexadecimalDigit(raw[position]))
        {
            if (value > (0x7fff_ffffu >> 4))
            {
                valueTooLarge = true;
            }
            else if (!valueTooLarge)
            {
                value = (value << 4) | (uint)HexadecimalValue(raw[position]);
            }

            position++;
        }

        var hasDigits = position != digitStart;
        if (!hasDigits)
        {
            diagnostics.Add(CreateDiagnostic(
                "LUA1010",
                absoluteStart,
                escapeStart,
                position,
                "At least one hexadecimal digit is required in a UTF-8 escape."));
        }

        if (position >= end || raw[position] != (byte)'}')
        {
            diagnostics.Add(CreateDiagnostic(
                "LUA1013",
                absoluteStart,
                escapeStart,
                position,
                "Missing '}' after UTF-8 escape."));
            return;
        }

        position++;
        if (valueTooLarge)
        {
            diagnostics.Add(CreateDiagnostic(
                "LUA1014",
                absoluteStart,
                escapeStart,
                position,
                "UTF-8 escape value exceeds 0x7FFFFFFF."));
            return;
        }

        if (hasDigits)
        {
            WriteExtendedUtf8(output, value);
        }
    }

    private static void WriteExtendedUtf8(ArrayBufferWriter<byte> output, uint value)
    {
        Span<byte> bytes = stackalloc byte[6];
        var count = 1;

        if (value < 0x80)
        {
            bytes[^1] = (byte)value;
        }
        else
        {
            uint maximumFirstByteValue = 0x3f;
            do
            {
                bytes[^(count++)] = (byte)(0x80 | (value & 0x3f));
                value >>= 6;
                maximumFirstByteValue >>= 1;
            }
            while (value > maximumFirstByteValue);

            bytes[^count] = (byte)((~maximumFirstByteValue << 1) | value);
        }

        WriteBytes(output, bytes[^count..]);
    }

    private static bool HasLongStringCloser(ReadOnlySpan<byte> raw, int equalsCount)
    {
        var closerLength = equalsCount + 2;
        if (raw.Length < closerLength || raw[^1] != (byte)']')
        {
            return false;
        }

        var start = raw.Length - closerLength;
        if (raw[start] != (byte)']')
        {
            return false;
        }

        for (var index = 1; index <= equalsCount; index++)
        {
            if (raw[start + index] != (byte)'=')
            {
                return false;
            }
        }

        return true;
    }

    private static void ConsumeNewLine(ReadOnlySpan<byte> raw, ref int position, int end)
    {
        var first = raw[position++];
        if (position < end && raw[position] is (byte)'\r' or (byte)'\n' && raw[position] != first)
        {
            position++;
        }
    }

    private static Diagnostic CreateDiagnostic(
        string code,
        int absoluteStart,
        int relativeStart,
        int relativeEnd,
        string message) =>
        new(
            code,
            DiagnosticSeverity.Error,
            TextSpan.FromBounds(
                absoluteStart + relativeStart,
                absoluteStart + Math.Max(relativeStart + 1, relativeEnd)),
            message);

    private static void WriteByte(ArrayBufferWriter<byte> output, byte value)
    {
        output.GetSpan(1)[0] = value;
        output.Advance(1);
    }

    private static void WriteBytes(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(output.GetSpan(bytes.Length));
        output.Advance(bytes.Length);
    }

    private static bool IsDecimalDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

    private static bool IsHexadecimalDigit(byte value) =>
        IsDecimalDigit(value) ||
        value is >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    private static int HexadecimalValue(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
        _ => value - (byte)'A' + 10,
    };

    private static bool IsNewLine(byte value) => value is (byte)'\r' or (byte)'\n';

    private static bool IsLuaWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\v' or (byte)'\f' or (byte)'\r' or (byte)'\n';
}
