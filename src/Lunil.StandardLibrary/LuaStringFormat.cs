using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.StandardLibrary;

internal static class LuaStringFormat
{
    public static LuaNativeStep Format(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        LuaValue[] arguments;
        List<byte> output;
        int index;
        int argument;
        if (continuationId == 0)
        {
            arguments = values.ToArray();
            var initialFormat = LuaLibraryHelpers.CheckStringBytes(arguments, 0, "format");
            output = new List<byte>(initialFormat.Length + 32);
            index = 0;
            argument = 1;
        }
        else
        {
            var state = context.InvocationState;
            var argumentCount = checked((int)state[6].AsInteger());
            arguments = state.Skip(7).Take(argumentCount).ToArray();
            output = new List<byte>(state[0].AsString().ToArray());
            index = checked((int)state[1].AsInteger());
            argument = checked((int)state[2].AsInteger());
            if (values.Length == 0 || values[0].Kind != LuaValueKind.String)
            {
                throw new LuaRuntimeException("'__tostring' must return a string");
            }

            var width = checked((int)state[3].AsInteger());
            var encodedPrecision = checked((int)state[4].AsInteger());
            int? precision = encodedPrecision < 0 ? null : encodedPrecision;
            var flags = state[5].AsString().ToString();
            AppendFormattedString(
                values[0].AsString().ToArray(),
                precision,
                width,
                flags,
                hasModifiers: flags.Length != 0 || width != 0 || precision.HasValue,
                argument,
                output);
        }

        var format = LuaLibraryHelpers.CheckStringBytes(arguments, 0, "format");
        while (index < format.Length)
        {
            if (format[index] != (byte)'%')
            {
                output.Add(format[index++]);
                continue;
            }

            if (++index < format.Length && format[index] == (byte)'%')
            {
                output.Add((byte)'%');
                index++;
                continue;
            }

            if ((uint)argument >= (uint)arguments.Length)
            {
                throw LuaLibraryHelpers.BadArgument("format", argument, "no value");
            }

            var value = arguments[argument];
            argument++;
            if (index >= format.Length)
            {
                throw new LuaRuntimeException("invalid conversion '%' to 'format'");
            }

            var start = index;
            while (index < format.Length && IsGeneralFormatCharacter(format[index]))
            {
                index++;
            }

            if (index - start >= 21)
            {
                throw new LuaRuntimeException("invalid format (too long)");
            }

            if (index >= format.Length)
            {
                throw new LuaRuntimeException("invalid conversion '%' to 'format'");
            }

            var conversion = (char)format[index];
            var modifiers = format.AsSpan(start, index - start);
            var specifier = Encoding.ASCII.GetString(format.AsSpan(start, index - start + 1));
            var parsed = ParseFormat(modifiers, conversion, specifier);
            var flags = parsed.Flags;
            var width = parsed.Width;
            var precision = parsed.Precision;
            index++;
            if (conversion == 'q' && !modifiers.IsEmpty)
            {
                throw new LuaRuntimeException("specifier '%q' cannot have modifiers");
            }

            if (conversion == 's')
            {
                var metamethod = LuaBasicLibrary.GetMetafield(
                    context.State,
                    value,
                    "__tostring");
                if (!metamethod.IsNil)
                {
                    return LuaNativeStep.CallLua(
                        metamethod,
                        [value],
                        continuationId: 1,
                        stateValues: EncodeState(
                            context.State,
                            arguments,
                            output,
                            index,
                            argument,
                            width,
                            precision,
                            flags),
                        callIsYieldable: false);
                }

                var renderedString = LuaBasicLibrary.DefaultToString(context.State, value)
                    .AsString()
                    .ToArray();
                AppendFormattedString(
                    renderedString,
                    precision,
                    width,
                    flags,
                    hasModifiers: flags.Length != 0 || width != 0 || precision.HasValue,
                    argument,
                    output);
                continue;
            }

            byte[] rendered = conversion switch
            {
                'c' => [(byte)CheckedInteger(value, argument - 1, "format")],
                'd' or 'i' or 'u' or 'o' or 'x' or 'X' => FormatInteger(
                    CheckedInteger(value, argument - 1, "format"),
                    conversion,
                    precision,
                    flags),
                'e' or 'E' or 'f' or 'g' or 'G' or 'a' or 'A' =>
                    FormatFloat(value, conversion, precision, flags),
                'q' => Quote(value),
                'p' => Encoding.ASCII.GetBytes(FormatPointer(value)),
                _ => throw new LuaRuntimeException($"invalid conversion '%{specifier}' to 'format'"),
            };

            rendered = ApplyNumericFlags(rendered, conversion, flags);
            var widthFlags = precision.HasValue && conversion is 'd' or 'i' or 'u' or 'o' or 'x' or 'X'
                ? flags.Replace("0", string.Empty, StringComparison.Ordinal)
                : flags;
            output.AddRange(ApplyWidth(rendered, width, widthFlags));
        }

        return LuaNativeStep.Completed(
            LuaValue.FromString(context.State.Strings.GetOrCreate(output.ToArray())));
    }

    private static bool IsGeneralFormatCharacter(byte value) =>
        value is (byte)'-' or (byte)'+' or (byte)' ' or (byte)'#' or (byte)'0' or
            (byte)'.' or >= (byte)'1' and <= (byte)'9';

    private static ParsedFormat ParseFormat(
        ReadOnlySpan<byte> modifiers,
        char conversion,
        string specifier)
    {
        if (conversion == 'q')
        {
            return new ParsedFormat(string.Empty, 0, null);
        }

        if (conversion is not ('a' or 'A' or 'e' or 'E' or 'f' or 'g' or 'G' or
            'o' or 'x' or 'X' or 'd' or 'i' or 'u' or 'c' or 'p' or 's'))
        {
            return new ParsedFormat(string.Empty, 0, null);
        }

        var acceptedFlags = conversion switch
        {
            'a' or 'A' or 'e' or 'E' or 'f' or 'g' or 'G' => "-+#0 ",
            'o' or 'x' or 'X' => "-#0",
            'd' or 'i' => "-+0 ",
            'u' => "-0",
            'c' or 'p' or 's' => "-",
            _ => string.Empty,
        };
        var acceptsPrecision = conversion is not ('c' or 'p');
        var index = 0;
        while (index < modifiers.Length && acceptedFlags.Contains((char)modifiers[index]))
        {
            index++;
        }

        var flags = Encoding.ASCII.GetString(modifiers[..index]);
        var width = 0;
        if (index >= modifiers.Length || modifiers[index] != (byte)'0')
        {
            var widthStart = index;
            while (index < modifiers.Length && IsDigit(modifiers[index]) && index - widthStart < 2)
            {
                width = (width * 10) + modifiers[index++] - (byte)'0';
            }
        }

        int? precision = null;
        if (acceptsPrecision && index < modifiers.Length && modifiers[index] == (byte)'.')
        {
            index++;
            precision = 0;
            var precisionStart = index;
            while (index < modifiers.Length && IsDigit(modifiers[index]) && index - precisionStart < 2)
            {
                precision = (precision * 10) + modifiers[index++] - (byte)'0';
            }
        }

        if (index != modifiers.Length || !char.IsAsciiLetter(conversion))
        {
            throw new LuaRuntimeException(
                $"invalid conversion specification: '%{specifier}'");
        }

        return new ParsedFormat(flags, width, precision);
    }

    private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

    private static LuaValue[] EncodeState(
        LuaState state,
        LuaValue[] arguments,
        List<byte> output,
        int index,
        int argument,
        int width,
        int? precision,
        string flags) =>
        [
            LuaValue.FromString(state.Strings.GetOrCreate(output.ToArray())),
            LuaValue.FromInteger(index),
            LuaValue.FromInteger(argument),
            LuaValue.FromInteger(width),
            LuaValue.FromInteger(precision ?? -1),
            LuaLibraryHelpers.String(state, flags),
            LuaValue.FromInteger(arguments.Length),
            .. arguments,
        ];

    private static void AppendFormattedString(
        byte[] bytes,
        int? precision,
        int width,
        string flags,
        bool hasModifiers,
        int argument,
        List<byte> output)
    {
        if (hasModifiers && bytes.AsSpan().Contains((byte)0))
        {
            throw LuaLibraryHelpers.BadArgument("format", argument - 1, "string contains zeros");
        }

        var rendered = precision is { } limit && bytes.Length > limit ? bytes[..limit] : bytes;
        output.AddRange(ApplyWidth(rendered, width, flags));
    }

    private static long CheckedInteger(LuaValue value, int index, string function)
    {
        if (LuaValueOperations.TryToNumber(value, out var number) && number.TryGetInteger(out var integer))
        {
            return integer;
        }

        throw LuaLibraryHelpers.BadArgument(function, index, "number has no integer representation");
    }

    private static byte[] FormatInteger(
        long value,
        char conversion,
        int? precision,
        string flags)
    {
        var unsigned = unchecked((ulong)value);
        var isSigned = conversion is 'd' or 'i';
        var negative = isSigned && value < 0;
        string magnitude = conversion switch
        {
            'd' or 'i' => value == long.MinValue
                ? "9223372036854775808"
                : Math.Abs(value).ToString(CultureInfo.InvariantCulture),
            'u' => unsigned.ToString(CultureInfo.InvariantCulture),
            'o' => Convert.ToString(unchecked((long)unsigned), 8),
            'x' => unsigned.ToString("x", CultureInfo.InvariantCulture),
            'X' => unsigned.ToString("X", CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException(),
        };
        if (precision == 0 && value == 0)
        {
            magnitude = string.Empty;
        }

        if (precision is { } digits)
        {
            magnitude = magnitude.PadLeft(digits, '0');
        }

        var prefix = conversion switch
        {
            'o' when flags.Contains('#') && magnitude.Length > 0 && magnitude[0] != '0' => "0",
            'x' when flags.Contains('#') && value != 0 => "0x",
            'X' when flags.Contains('#') && value != 0 => "0X",
            _ => string.Empty,
        };
        var sign = negative
            ? "-"
            : isSigned && flags.Contains('+')
                ? "+"
                : isSigned && flags.Contains(' ')
                    ? " "
                    : string.Empty;
        return Encoding.ASCII.GetBytes(sign + prefix + magnitude);
    }

    private static byte[] FormatFloat(
        LuaValue value,
        char conversion,
        int? precision,
        string flags)
    {
        if (!LuaValueOperations.TryToNumber(value, out var numeric))
        {
            throw new LuaRuntimeException("number expected");
        }

        var number = numeric.AsFloat();
        string text;
        if (conversion is 'a' or 'A')
        {
            text = FormatHexFloat(number, precision, conversion == 'A');
        }
        else
        {
            var effectivePrecision = conversion is 'g' or 'G' && precision == 0
                ? 1
                : precision ?? 6;
            var specifier = conversion switch
            {
                'e' or 'E' => $"{conversion}{effectivePrecision}",
                'f' or 'F' => $"F{effectivePrecision}",
                _ => $"{conversion}{effectivePrecision}",
            };
            text = number.ToString(specifier, CultureInfo.InvariantCulture);
        }

        if (flags.Contains('#') && double.IsFinite(number))
        {
            text = ApplyAlternateFloatForm(text, conversion, precision);
        }

        return Encoding.ASCII.GetBytes(text);
    }

    private static string FormatPointer(LuaValue value)
    {
        object? identity = value.Kind switch
        {
            LuaValueKind.String => value.AsString(),
            LuaValueKind.Table => value.AsTable(),
            LuaValueKind.Function => value.TryGetClosure() ??
                (object?)value.TryGetNativeClosure() ?? value.TryGetNativeFunction(),
            LuaValueKind.Thread => value.AsThread(),
            LuaValueKind.Userdata => value.AsUserdata(),
            LuaValueKind.LightUserdata => value.AsLightUserdata().Identity,
            _ => null,
        };
        return identity is null ? "(null)" : $"0x{RuntimeHelpers.GetHashCode(identity):x}";
    }

    private static string ApplyAlternateFloatForm(string text, char conversion, int? precision)
    {
        var exponentIndex = text.IndexOfAny(['e', 'E', 'p', 'P']);
        var mantissa = exponentIndex < 0 ? text : text[..exponentIndex];
        var exponent = exponentIndex < 0 ? string.Empty : text[exponentIndex..];
        if (!mantissa.Contains('.'))
        {
            mantissa += ".";
        }

        if (conversion is 'g' or 'G')
        {
            var wanted = precision is null or 0 ? (precision == 0 ? 1 : 6) : precision.Value;
            var digits = mantissa.Count(char.IsAsciiDigit);
            var leading = mantissa.TrimStart('+', '-').TrimStart('0').StartsWith('.')
                ? mantissa.SkipWhile(character => character is '+' or '-' or '0' or '.').TakeWhile(character => character == '0').Count()
                : 0;
            digits -= leading;
            if (digits < wanted)
            {
                mantissa += new string('0', wanted - digits);
            }
        }

        return mantissa + exponent;
    }

    private static string FormatHexFloat(double value, int? precision, bool upper)
    {
        if (double.IsNaN(value))
        {
            return upper ? "NAN" : "nan";
        }

        if (double.IsPositiveInfinity(value))
        {
            return upper ? "INF" : "inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return upper ? "-INF" : "-inf";
        }

        var bits = BitConverter.DoubleToUInt64Bits(value);
        var negative = (bits >> 63) != 0;
        var exponentBits = (int)((bits >> 52) & 0x7ff);
        var mantissa = bits & 0x000f_ffff_ffff_ffffUL;
        if (exponentBits == 0 && mantissa == 0)
        {
            return $"{(negative ? "-" : string.Empty)}{(upper ? "0X0" : "0x0")}" +
                $"{(precision > 0 ? "." + new string('0', precision.Value) : string.Empty)}" +
                $"{(upper ? "P" : "p")}+0";
        }

        int exponent;
        if (exponentBits == 0)
        {
            exponent = -1022;
            while ((mantissa & (1UL << 52)) == 0)
            {
                mantissa <<= 1;
                exponent--;
            }
        }
        else
        {
            mantissa |= 1UL << 52;
            exponent = exponentBits - 1023;
        }

        var wanted = precision ?? 13;
        string digits;
        if (wanted < 13)
        {
            var discardedBits = 52 - (wanted * 4);
            var retained = mantissa >> discardedBits;
            var discardedMask = (1UL << discardedBits) - 1;
            var discarded = mantissa & discardedMask;
            var halfway = 1UL << (discardedBits - 1);
            if (discarded > halfway || (discarded == halfway && (retained & 1) != 0))
            {
                retained++;
            }

            if (retained == (1UL << ((wanted * 4) + 1)))
            {
                retained >>= 1;
                exponent++;
            }

            var fractionMask = wanted == 0 ? 0 : (1UL << (wanted * 4)) - 1;
            digits = wanted == 0
                ? string.Empty
                : (retained & fractionMask).ToString(
                    upper ? $"X{wanted}" : $"x{wanted}",
                    CultureInfo.InvariantCulture);
        }
        else
        {
            digits = mantissa.ToString(upper ? "X13" : "x13", CultureInfo.InvariantCulture)[1..]
                .PadRight(wanted, '0');
        }

        if (precision is null)
        {
            digits = digits.TrimEnd('0');
        }

        var prefix = upper ? "0X1" : "0x1";
        var exponentMarker = upper ? 'P' : 'p';
        return $"{(negative ? "-" : string.Empty)}{prefix}{(digits.Length == 0 ? string.Empty : "." + digits)}{exponentMarker}{(exponent >= 0 ? "+" : string.Empty)}{exponent}";
    }

    private static byte[] Quote(LuaValue value) => value.Kind switch
    {
        LuaValueKind.String => QuoteString(value.AsString().AsSpan()),
        LuaValueKind.Integer => Encoding.ASCII.GetBytes(value.AsInteger() == long.MinValue
            ? "0x8000000000000000"
            : value.AsInteger().ToString(CultureInfo.InvariantCulture)),
        LuaValueKind.Float => Encoding.ASCII.GetBytes(QuoteFloat(value.AsFloat())),
        LuaValueKind.Boolean => value.AsBoolean() ? "true"u8.ToArray() : "false"u8.ToArray(),
        LuaValueKind.Nil => "nil"u8.ToArray(),
        _ => throw new LuaRuntimeException("value has no literal form"),
    };

    private static byte[] QuoteString(ReadOnlySpan<byte> value)
    {
        var output = new List<byte>(value.Length + 2) { (byte)'"' };
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case (byte)'"': output.AddRange("\\\""u8.ToArray()); break;
                case (byte)'\\': output.AddRange("\\\\"u8.ToArray()); break;
                case (byte)'\n':
                    output.Add((byte)'\\');
                    output.Add((byte)'\n');
                    break;
                default:
                    if (character < 32 || character == 127)
                    {
                        output.Add((byte)'\\');
                        var nextIsDigit = index + 1 < value.Length && IsDigit(value[index + 1]);
                        output.AddRange(Encoding.ASCII.GetBytes(character.ToString(
                            nextIsDigit ? "D3" : "D",
                            CultureInfo.InvariantCulture)));
                    }
                    else
                    {
                        output.Add(character);
                    }

                    break;
            }
        }

        output.Add((byte)'"');
        return output.ToArray();
    }

    private static string QuoteFloat(double value)
    {
        if (double.IsNaN(value))
        {
            return "(0/0)";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "1e9999";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-1e9999";
        }

        return FormatHexFloat(value, precision: null, upper: false);
    }

    private static byte[] FormatString(LuaValue value, int? precision)
    {
        var bytes = value.Kind switch
        {
            LuaValueKind.String => value.AsString().ToArray(),
            LuaValueKind.Integer => Encoding.ASCII.GetBytes(value.AsInteger().ToString(CultureInfo.InvariantCulture)),
            LuaValueKind.Float => Encoding.ASCII.GetBytes(LuaValueOperations.FormatFloat(value.AsFloat())),
            _ => throw new LuaRuntimeException("string expected"),
        };
        return precision is { } limit && bytes.Length > limit ? bytes[..limit] : bytes;
    }

    private static byte[] ApplyNumericFlags(byte[] bytes, char conversion, string flags)
    {
        if (conversion is not ('e' or 'E' or 'f' or 'g' or 'G' or 'a' or 'A'))
        {
            return bytes;
        }

        var text = Encoding.ASCII.GetString(bytes);
        if (!text.StartsWith('-') && flags.Contains('+'))
        {
            text = "+" + text;
        }
        else if (!text.StartsWith('-') && flags.Contains(' '))
        {
            text = " " + text;
        }

        return Encoding.ASCII.GetBytes(text);
    }

    private static byte[] ApplyWidth(byte[] bytes, int width, string flags)
    {
        if (bytes.Length >= width)
        {
            return bytes;
        }

        var padding = width - bytes.Length;
        var pad = flags.Contains('0') && !flags.Contains('-') ? (byte)'0' : (byte)' ';
        var result = new byte[width];
        if (flags.Contains('-'))
        {
            bytes.CopyTo(result, 0);
            result.AsSpan(bytes.Length).Fill((byte)' ');
        }
        else if (pad == (byte)'0')
        {
            var prefixLength = bytes.Length > 0 && bytes[0] is (byte)'+' or (byte)'-' or (byte)' '
                ? 1
                : 0;
            if (bytes.Length >= prefixLength + 2 && bytes[prefixLength] == (byte)'0' &&
                bytes[prefixLength + 1] is (byte)'x' or (byte)'X')
            {
                prefixLength += 2;
            }

            bytes.AsSpan(0, prefixLength).CopyTo(result);
            result.AsSpan(prefixLength, padding).Fill(pad);
            bytes.AsSpan(prefixLength).CopyTo(result.AsSpan(prefixLength + padding));
        }
        else
        {
            result.AsSpan(0, padding).Fill(pad);
            bytes.CopyTo(result, padding);
        }

        return result;
    }

    private readonly record struct ParsedFormat(string Flags, int Width, int? Precision);
}
