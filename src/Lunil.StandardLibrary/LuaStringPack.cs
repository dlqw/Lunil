using System.Buffers.Binary;
using System.Numerics;
using Lunil.Runtime;
using Lunil.Runtime.Values;

namespace Lunil.StandardLibrary;

internal static class LuaStringPack
{
    private const int MaximumIntegerSize = 16;
    private const int NativeAlignment = 8;
    private static readonly int NativeLongSize = OperatingSystem.IsWindows() ? 4 : 8;

    public static LuaValue Pack(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var format = LuaLibraryHelpers.CheckStringBytes(arguments, 0, "pack");
        var parser = new FormatParser(format);
        var output = new List<byte>();
        var argument = 1;
        while (parser.TryRead(out var option))
        {
            AddPadding(output, option.Alignment);
            switch (option.Kind)
            {
                case PackKind.Padding:
                    output.Add(0);
                    break;
                case PackKind.AlignOnly:
                    break;
                case PackKind.SignedInteger:
                    WriteInteger(output, CheckInteger(arguments, argument++, "pack"), option.Size, option.LittleEndian, signed: true);
                    break;
                case PackKind.UnsignedInteger:
                    WriteInteger(output, CheckInteger(arguments, argument++, "pack"), option.Size, option.LittleEndian, signed: false);
                    break;
                case PackKind.Float:
                    {
                        var value = LuaLibraryHelpers.CheckNumber(arguments, argument++, "pack");
                        Span<byte> bytes = stackalloc byte[4];
                        var bits = BitConverter.SingleToUInt32Bits((float)value);
                        if (option.LittleEndian) BinaryPrimitives.WriteUInt32LittleEndian(bytes, bits);
                        else BinaryPrimitives.WriteUInt32BigEndian(bytes, bits);
                        output.AddRange(bytes.ToArray());
                        break;
                    }
                case PackKind.Double:
                    {
                        var value = LuaLibraryHelpers.CheckNumber(arguments, argument++, "pack");
                        Span<byte> bytes = stackalloc byte[8];
                        var bits = BitConverter.DoubleToUInt64Bits(value);
                        if (option.LittleEndian) BinaryPrimitives.WriteUInt64LittleEndian(bytes, bits);
                        else BinaryPrimitives.WriteUInt64BigEndian(bytes, bits);
                        output.AddRange(bytes.ToArray());
                        break;
                    }
                case PackKind.FixedString:
                    {
                        var bytes = LuaLibraryHelpers.CheckStringBytes(arguments, argument++, "pack");
                        if (bytes.Length > option.Size)
                        {
                            throw LuaLibraryHelpers.BadArgument("pack", argument - 1, "string longer than given size");
                        }

                        output.AddRange(bytes);
                        output.AddRange(new byte[option.Size - bytes.Length]);
                        break;
                    }
                case PackKind.ZeroString:
                    {
                        var bytes = LuaLibraryHelpers.CheckStringBytes(arguments, argument++, "pack");
                        if (bytes.AsSpan().Contains((byte)0))
                        {
                            throw LuaLibraryHelpers.BadArgument("pack", argument - 1, "string contains zeros");
                        }

                        output.AddRange(bytes);
                        output.Add(0);
                        break;
                    }
                case PackKind.SizedString:
                    {
                        var bytes = LuaLibraryHelpers.CheckStringBytes(arguments, argument++, "pack");
                        if (option.Size < 8 && (ulong)bytes.Length >= 1UL << (option.Size * 8))
                        {
                            throw LuaLibraryHelpers.BadArgument("pack", argument - 1, "string length does not fit in given size");
                        }

                        WriteInteger(output, bytes.Length, option.Size, option.LittleEndian, signed: false);
                        output.AddRange(bytes);
                        break;
                    }
            }
        }

        return LuaValue.FromString(state.Strings.GetOrCreate(output.ToArray()));
    }

    public static long PackSize(ReadOnlySpan<LuaValue> arguments)
    {
        var format = LuaLibraryHelpers.CheckStringBytes(arguments, 0, "packsize");
        var parser = new FormatParser(format);
        long size = 0;
        while (parser.TryRead(out var option))
        {
            if (option.Kind is PackKind.ZeroString or PackKind.SizedString)
            {
                throw new LuaRuntimeException("variable-length format");
            }

            var aligned = Align(size, option.Alignment);
            if (aligned > int.MaxValue - option.DataSize)
            {
                throw new LuaRuntimeException("format result too large");
            }

            size = aligned + option.DataSize;
        }

        return size;
    }

    public static LuaValue[] Unpack(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var format = LuaLibraryHelpers.CheckStringBytes(arguments, 0, "unpack");
        var source = LuaLibraryHelpers.CheckStringBytes(arguments, 1, "unpack");
        var position = RelativePosition(
            LuaLibraryHelpers.OptionalInteger(arguments, 2, 1, "unpack"),
            source.Length);
        if (position < 1 || position > source.Length + 1L)
        {
            throw LuaLibraryHelpers.BadArgument("unpack", 2, "initial position out of string");
        }

        var offset = position - 1;
        var parser = new FormatParser(format);
        var result = new List<LuaValue>();
        while (parser.TryRead(out var option))
        {
            offset = Align(offset, option.Alignment);
            EnsureAvailable(source, offset, option.DataSize);
            switch (option.Kind)
            {
                case PackKind.Padding:
                    offset++;
                    break;
                case PackKind.AlignOnly:
                    break;
                case PackKind.SignedInteger:
                    result.Add(LuaValue.FromInteger(ReadInteger(source, ref offset, option.Size, option.LittleEndian, signed: true)));
                    break;
                case PackKind.UnsignedInteger:
                    result.Add(LuaValue.FromInteger(ReadInteger(source, ref offset, option.Size, option.LittleEndian, signed: false)));
                    break;
                case PackKind.Float:
                    {
                        var span = source.AsSpan((int)offset, 4);
                        var bits = option.LittleEndian
                            ? BinaryPrimitives.ReadUInt32LittleEndian(span)
                            : BinaryPrimitives.ReadUInt32BigEndian(span);
                        result.Add(LuaValue.FromFloat(BitConverter.UInt32BitsToSingle(bits)));
                        offset += 4;
                        break;
                    }
                case PackKind.Double:
                    {
                        var span = source.AsSpan((int)offset, 8);
                        var bits = option.LittleEndian
                            ? BinaryPrimitives.ReadUInt64LittleEndian(span)
                            : BinaryPrimitives.ReadUInt64BigEndian(span);
                        result.Add(LuaValue.FromFloat(BitConverter.UInt64BitsToDouble(bits)));
                        offset += 8;
                        break;
                    }
                case PackKind.FixedString:
                    result.Add(LuaValue.FromString(state.Strings.GetOrCreate(source.AsSpan((int)offset, option.Size))));
                    offset += option.Size;
                    break;
                case PackKind.ZeroString:
                    {
                        var terminator = source.AsSpan((int)offset).IndexOf((byte)0);
                        if (terminator < 0)
                        {
                            throw new LuaRuntimeException("unfinished string for format 'z'");
                        }

                        result.Add(LuaValue.FromString(state.Strings.GetOrCreate(source.AsSpan((int)offset, terminator))));
                        offset += terminator + 1;
                        break;
                    }
                case PackKind.SizedString:
                    {
                        var length = unchecked((ulong)ReadInteger(source, ref offset, option.Size, option.LittleEndian, signed: false));
                        if (length > int.MaxValue)
                        {
                            throw new LuaRuntimeException("data string too short");
                        }

                        EnsureAvailable(source, offset, (int)length);
                        result.Add(LuaValue.FromString(state.Strings.GetOrCreate(source.AsSpan((int)offset, (int)length))));
                        offset += (long)length;
                        break;
                    }
            }
        }

        result.Add(LuaValue.FromInteger(offset + 1));
        return result.ToArray();
    }

    private static long CheckInteger(ReadOnlySpan<LuaValue> arguments, int index, string function) =>
        LuaLibraryHelpers.CheckInteger(arguments, index, function);

    private static void WriteInteger(
        List<byte> output,
        long value,
        int size,
        bool littleEndian,
        bool signed)
    {
        var number = new BigInteger(value);
        var minimum = signed ? -(BigInteger.One << (size * 8 - 1)) : BigInteger.Zero;
        var maximum = signed
            ? (BigInteger.One << (size * 8 - 1)) - 1
            : (BigInteger.One << (size * 8)) - 1;
        if (size < 8 && (number < minimum || number > maximum) || !signed && value < 0 && size < 8)
        {
            throw new LuaRuntimeException(signed ? "integer overflow" : "unsigned overflow");
        }

        if (!signed && value < 0)
        {
            number += BigInteger.One << 64;
        }

        if (number < 0)
        {
            number += BigInteger.One << (size * 8);
        }

        var bytes = new byte[size];
        for (var index = 0; index < size; index++)
        {
            bytes[index] = (byte)(number & 0xff);
            number >>= 8;
        }

        if (!littleEndian)
        {
            Array.Reverse(bytes);
        }

        output.AddRange(bytes);
    }

    private static long ReadInteger(byte[] source, ref long offset, int size, bool littleEndian, bool signed)
    {
        EnsureAvailable(source, offset, size);
        BigInteger number = BigInteger.Zero;
        for (var index = 0; index < size; index++)
        {
            var sourceIndex = littleEndian ? index : size - 1 - index;
            number |= (BigInteger)source[(int)offset + sourceIndex] << (index * 8);
        }

        if (signed && (source[(int)offset + (littleEndian ? size - 1 : 0)] & 0x80) != 0)
        {
            number -= BigInteger.One << (size * 8);
        }

        offset += size;
        if (number < long.MinValue || number > ulong.MaxValue)
        {
            throw new LuaRuntimeException($"{size}-byte integer does not fit into Lua Integer");
        }

        return number > long.MaxValue ? unchecked((long)(ulong)number) : (long)number;
    }

    private static void AddPadding(List<byte> output, int alignment)
    {
        var aligned = Align(output.Count, alignment);
        while (output.Count < aligned)
        {
            output.Add(0);
        }
    }

    private static long Align(long position, int alignment) => alignment <= 1
        ? position
        : checked((position + alignment - 1) & ~(alignment - 1L));

    private static void EnsureAvailable(byte[] source, long offset, int count)
    {
        if (offset < 0 || offset > source.Length || count > source.Length - offset)
        {
            throw new LuaRuntimeException("data string too short");
        }
    }

    private static long RelativePosition(long position, int length) => position >= 0
        ? position
        : position < -length ? 0 : length + position + 1L;

    private enum PackKind : byte
    {
        SignedInteger,
        UnsignedInteger,
        Float,
        Double,
        FixedString,
        ZeroString,
        SizedString,
        Padding,
        AlignOnly,
    }

    private readonly record struct PackOption(
        PackKind Kind,
        int Size,
        int Alignment,
        bool LittleEndian)
    {
        public int DataSize => Kind is PackKind.AlignOnly ? 0 : Size;
    }

    private sealed class FormatParser(byte[] format)
    {
        private int _index;
        private bool _littleEndian = BitConverter.IsLittleEndian;
        private int _maximumAlignment = 1;

        public bool TryRead(out PackOption option)
        {
            while (_index < format.Length)
            {
                var code = (char)format[_index++];
                if (code == ' ')
                {
                    continue;
                }

                switch (code)
                {
                    case '<': _littleEndian = true; continue;
                    case '>': _littleEndian = false; continue;
                    case '=': _littleEndian = BitConverter.IsLittleEndian; continue;
                    case '!':
                        _maximumAlignment = ReadSize(NativeAlignment, MaximumIntegerSize);
                        if ((_maximumAlignment & (_maximumAlignment - 1)) != 0)
                        {
                            throw new LuaRuntimeException("format asks for alignment not power of 2");
                        }

                        continue;
                }

                var kind = code switch
                {
                    'b' or 'h' or 'l' or 'j' or 'i' => PackKind.SignedInteger,
                    'B' or 'H' or 'L' or 'J' or 'T' or 'I' => PackKind.UnsignedInteger,
                    'f' => PackKind.Float,
                    'd' or 'n' => PackKind.Double,
                    'c' => PackKind.FixedString,
                    'z' => PackKind.ZeroString,
                    's' => PackKind.SizedString,
                    'x' => PackKind.Padding,
                    'X' => PackKind.AlignOnly,
                    _ => throw new LuaRuntimeException($"invalid format option '{code}'"),
                };
                var size = code switch
                {
                    'b' or 'B' or 'x' => 1,
                    'h' or 'H' => 2,
                    'l' or 'L' => NativeLongSize,
                    'j' or 'J' or 'T' => 8,
                    'f' => 4,
                    'd' or 'n' => 8,
                    'i' or 'I' => ReadSize(4, MaximumIntegerSize),
                    'c' => ReadSize(-1, int.MaxValue),
                    's' => ReadSize(8, MaximumIntegerSize),
                    'z' or 'X' => 0,
                    _ => 0,
                };
                var alignmentSize = code == 'X' ? ReadFollowingAlignmentSize() : size;
                var alignment = kind is PackKind.ZeroString or PackKind.FixedString or PackKind.Padding
                    ? 1
                    : Math.Min(alignmentSize, _maximumAlignment);
                if (alignment > 1 && (alignment & (alignment - 1)) != 0)
                {
                    throw new LuaRuntimeException("format asks for alignment not power of 2");
                }

                option = new PackOption(kind, size, alignment, _littleEndian);
                return true;
            }

            option = default;
            return false;
        }

        private int ReadFollowingAlignmentSize()
        {
            if (_index >= format.Length)
            {
                throw new LuaRuntimeException("invalid next option for option 'X'");
            }

            var code = (char)format[_index++];
            var kind = code switch
            {
                'b' or 'h' or 'l' or 'j' or 'i' => PackKind.SignedInteger,
                'B' or 'H' or 'L' or 'J' or 'T' or 'I' => PackKind.UnsignedInteger,
                'f' => PackKind.Float,
                'd' or 'n' => PackKind.Double,
                'c' => PackKind.FixedString,
                's' => PackKind.SizedString,
                'x' => PackKind.Padding,
                'z' => PackKind.ZeroString,
                'X' or ' ' or '<' or '>' or '=' or '!' => PackKind.AlignOnly,
                _ => throw new LuaRuntimeException($"invalid format option '{code}'"),
            };
            var size = code switch
            {
                'b' or 'B' or 'x' => 1,
                'h' or 'H' => 2,
                'l' or 'L' => NativeLongSize,
                'j' or 'J' or 'T' => 8,
                'f' => 4,
                'd' or 'n' => 8,
                'i' or 'I' => ReadSize(4, MaximumIntegerSize),
                'c' => ReadSize(-1, int.MaxValue),
                's' => ReadSize(8, MaximumIntegerSize),
                _ => 0,
            };
            if (kind == PackKind.FixedString || size == 0)
            {
                throw new LuaRuntimeException("invalid next option for option 'X'");
            }

            return size;
        }

        private int ReadSize(int defaultValue, int limit)
        {
            if (_index >= format.Length || format[_index] is < (byte)'0' or > (byte)'9')
            {
                if (defaultValue < 0)
                {
                    throw new LuaRuntimeException("missing size for format option 'c'");
                }

                return defaultValue;
            }

            var value = 0;
            do
            {
                value = value * 10 + format[_index++] - (byte)'0';
            }
            while (_index < format.Length &&
                   format[_index] is >= (byte)'0' and <= (byte)'9' &&
                   value <= (int.MaxValue - 9) / 10);

            if (value == 0 && defaultValue >= 0)
            {
                throw new LuaRuntimeException("integral size (0) out of limits [1,16]");
            }

            if (value > limit)
            {
                throw new LuaRuntimeException(
                    $"integral size ({value}) out of limits [1,{limit}]");
            }

            return value;
        }
    }
}
