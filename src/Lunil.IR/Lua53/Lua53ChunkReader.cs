using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Lunil.IR.Lua53;

public sealed class Lua53ChunkFormatException : FormatException
{
    public Lua53ChunkFormatException(string reason, int offset = 0)
        : base($"Bad Lua 5.3 binary chunk at byte {offset}: {reason}")
    {
        Reason = reason;
        Offset = offset;
    }

    public string Reason { get; }
    public int Offset { get; }
}

public static class Lua53ChunkReader
{
    private static ReadOnlySpan<byte> Signature => [0x1b, (byte)'L', (byte)'u', (byte)'a'];
    private static ReadOnlySpan<byte> LuaData => [0x19, 0x93, (byte)'\r', (byte)'\n', 0x1a, (byte)'\n'];

    public static Lua53Chunk Read(
        ReadOnlySpan<byte> data,
        Lua53ChunkReaderOptions? options = null)
    {
        options ??= Lua53ChunkReaderOptions.Default;
        if (data.Length > options.MaximumChunkBytes)
        {
            throw new Lua53ChunkFormatException(
                $"chunk exceeds the configured {options.MaximumChunkBytes} byte limit");
        }

        return new Reader(data, options).ReadChunk();
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly Lua53ChunkReaderOptions _options;
        private int _offset;
        private int _prototypeCount;
        private int _instructionCount;
        private int _constantCount;
        private int _stringBytes;
        private int _debugEntryCount;
        private Lua53ChunkTarget _target;

        public Reader(ReadOnlySpan<byte> data, Lua53ChunkReaderOptions options)
        {
            _data = data;
            _options = options;
        }

        public Lua53Chunk ReadChunk()
        {
            Expect(Signature, "not a binary chunk");
            ExpectByte(0x53, "version mismatch; expected Lua 5.3");
            ExpectByte(0, "unsupported binary chunk format");
            Expect(LuaData, "corrupted LUAC_DATA marker");

            var sizeOfInt = ReadByte();
            var sizeOfSizeT = ReadByte();
            var instructionSize = ReadByte();
            var integerSize = ReadByte();
            var numberSize = ReadByte();
            if (sizeOfInt != 4 || instructionSize != 4 ||
                sizeOfSizeT is not (4 or 8) || integerSize is not (4 or 8) ||
                numberSize is not (4 or 8))
            {
                Fail("unsupported Lua 5.3 scalar layout");
            }

            var integerSentinel = ReadBytes(integerSize);
            var littleInteger = ReadInteger(integerSentinel, Lua53ByteOrder.LittleEndian);
            var bigInteger = ReadInteger(integerSentinel, Lua53ByteOrder.BigEndian);
            var byteOrder = (littleInteger, bigInteger) switch
            {
                (0x5678, not 0x5678) => Lua53ByteOrder.LittleEndian,
                (not 0x5678, 0x5678) => Lua53ByteOrder.BigEndian,
                _ => Fail<Lua53ByteOrder>("integer format mismatch"),
            };

            var numberSentinel = ReadBytes(numberSize);
            if (ReadNumber(numberSentinel, byteOrder) != 370.5)
            {
                Fail("floating-point format mismatch");
            }

            _target = new Lua53ChunkTarget(
                byteOrder,
                sizeOfInt,
                sizeOfSizeT,
                instructionSize,
                integerSize,
                numberSize);
            var mainUpvalueCount = ReadByte();
            var main = ReadPrototype(parentSource: null, depth: 1);
            if (!_options.AllowTrailingData && _offset != _data.Length)
            {
                Fail("trailing data after main prototype");
            }

            return new Lua53Chunk(_target, mainUpvalueCount, main);
        }

        private Lua53Prototype ReadPrototype(Lua53String? parentSource, int depth)
        {
            if (depth > _options.MaximumPrototypeDepth)
            {
                Fail("prototype nesting exceeds the configured limit");
            }

            AddToBudget(ref _prototypeCount, 1, _options.MaximumPrototypeCount, "prototype count");
            var source = ReadNullableString() ?? parentSource;
            var lineDefined = ReadInt("line number");
            var lastLineDefined = ReadInt("line number");
            var parameterCount = ReadByte();
            var varArgFlags = ReadByte();
            var maximumStackSize = ReadByte();

            var codeCount = ReadInt("instruction count");
            AddToBudget(ref _instructionCount, codeCount, _options.MaximumInstructionCount,
                "instruction count");
            EnsureCountFitsRemaining(codeCount, 4, "instruction count");
            var code = ImmutableArray.CreateBuilder<Lua53Instruction>(codeCount);
            for (var index = 0; index < codeCount; index++)
            {
                var bytes = ReadBytes(4);
                var raw = _target.ByteOrder == Lua53ByteOrder.LittleEndian
                    ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
                    : BinaryPrimitives.ReadUInt32BigEndian(bytes);
                code.Add(new Lua53Instruction(raw));
            }

            var constantCount = ReadInt("constant count");
            AddToBudget(ref _constantCount, constantCount, _options.MaximumConstantCount,
                "constant count");
            EnsureCountFitsRemaining(constantCount, 1, "constant count");
            var constants = ImmutableArray.CreateBuilder<Lua53Constant>(constantCount);
            for (var index = 0; index < constantCount; index++)
            {
                constants.Add(ReadConstant());
            }

            var upvalueCount = ReadInt("upvalue count");
            AddToBudget(ref _debugEntryCount, upvalueCount, _options.MaximumUpvalueCount,
                "upvalue count");
            EnsureCountFitsRemaining(upvalueCount, 2, "upvalue count");
            var upvalues = ImmutableArray.CreateBuilder<Lua53UpvalueDescriptor>(upvalueCount);
            for (var index = 0; index < upvalueCount; index++)
            {
                upvalues.Add(new Lua53UpvalueDescriptor(ReadByte(), ReadByte()));
            }

            var nestedCount = ReadInt("nested prototype count");
            EnsureCountFitsRemaining(nestedCount, 2, "nested prototype count");
            var nested = ImmutableArray.CreateBuilder<Lua53Prototype>(nestedCount);
            for (var index = 0; index < nestedCount; index++)
            {
                nested.Add(ReadPrototype(source, depth + 1));
            }

            var lineInfoCount = ReadInt("line info count");
            AddToBudget(ref _debugEntryCount, lineInfoCount, _options.MaximumDebugEntryCount,
                "debug entry count");
            EnsureCountFitsRemaining(lineInfoCount, 4, "line info count");
            var lineInfo = ImmutableArray.CreateBuilder<int>(lineInfoCount);
            for (var index = 0; index < lineInfoCount; index++)
            {
                lineInfo.Add(ReadInt("line info"));
            }

            var localCount = ReadInt("local variable count");
            AddToBudget(ref _debugEntryCount, localCount, _options.MaximumDebugEntryCount,
                "debug entry count");
            var locals = ImmutableArray.CreateBuilder<Lua53LocalVariable>(localCount);
            for (var index = 0; index < localCount; index++)
            {
                locals.Add(new Lua53LocalVariable(
                    ReadNullableString(),
                    ReadInt("local start program counter"),
                    ReadInt("local end program counter")));
            }

            var upvalueNameCount = ReadInt("upvalue name count");
            AddToBudget(ref _debugEntryCount, upvalueNameCount, _options.MaximumDebugEntryCount,
                "debug entry count");
            var upvalueNames = ImmutableArray.CreateBuilder<Lua53String?>(upvalueNameCount);
            for (var index = 0; index < upvalueNameCount; index++)
            {
                upvalueNames.Add(ReadNullableString());
            }

            if (upvalueNameCount != 0 && upvalueNameCount != upvalueCount)
            {
                Fail("upvalue name count must be zero or match the upvalue count");
            }

            return new Lua53Prototype
            {
                Source = source,
                LineDefined = lineDefined,
                LastLineDefined = lastLineDefined,
                ParameterCount = parameterCount,
                VarArgFlags = varArgFlags,
                MaximumStackSize = maximumStackSize,
                Code = code.MoveToImmutable(),
                Constants = constants.MoveToImmutable(),
                Upvalues = upvalues.MoveToImmutable(),
                NestedPrototypes = nested.MoveToImmutable(),
                LineInfo = lineInfo.MoveToImmutable(),
                LocalVariables = locals.MoveToImmutable(),
                UpvalueNames = upvalueNames.MoveToImmutable(),
            };
        }

        private Lua53Constant ReadConstant()
        {
            var tagOffset = _offset;
            return ReadByte() switch
            {
                0 => Lua53Constant.Nil,
                1 => Lua53Constant.FromBoolean(false),
                17 => Lua53Constant.FromBoolean(true),
                3 => Lua53Constant.FromFloat(ReadNumber(ReadBytes(_target.NumberSize), _target.ByteOrder)),
                19 => Lua53Constant.FromInteger(ReadInteger(ReadBytes(_target.IntegerSize), _target.ByteOrder)),
                4 => Lua53Constant.FromString(ReadRequiredString(), isShort: true),
                20 => Lua53Constant.FromString(ReadRequiredString(), isShort: false),
                var unknown => throw new Lua53ChunkFormatException(
                    $"unknown constant tag {unknown}", tagOffset),
            };
        }

        private Lua53String ReadRequiredString() =>
            ReadNullableString() ?? Fail<Lua53String>("constant string cannot be null");

        private Lua53String? ReadNullableString()
        {
            var firstSizeByte = ReadByte();
            if (firstSizeByte == 0)
            {
                return null;
            }

            var encodedSize = firstSizeByte == byte.MaxValue
                ? ReadSizeT()
                : firstSizeByte;
            if (encodedSize == 0)
            {
                Fail("extended string size cannot be zero");
            }

            var byteCount = checked(encodedSize - 1);
            if (byteCount > int.MaxValue)
            {
                Fail("string is too large for this runtime");
            }

            AddToBudget(ref _stringBytes, (int)byteCount, _options.MaximumStringBytes, "string bytes");
            return new Lua53String(ReadBytes((int)byteCount).ToArray());
        }

        private int ReadInt(string description)
        {
            var value = ReadSignedInt32();
            if (value < 0)
            {
                Fail($"{description} cannot be negative");
            }

            return value;
        }

        private ulong ReadSizeT() => _target.SizeOfSizeT switch
        {
            4 => ReadUnsignedInt32(),
            8 => ReadUnsignedInt64(),
            _ => throw new UnreachableException(),
        };

        private int ReadSignedInt32() => _target.ByteOrder == Lua53ByteOrder.LittleEndian
            ? BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(4))
            : BinaryPrimitives.ReadInt32BigEndian(ReadBytes(4));

        private uint ReadUnsignedInt32() => _target.ByteOrder == Lua53ByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4))
            : BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4));

        private ulong ReadUnsignedInt64() => _target.ByteOrder == Lua53ByteOrder.LittleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(8))
            : BinaryPrimitives.ReadUInt64BigEndian(ReadBytes(8));

        private static long ReadInteger(ReadOnlySpan<byte> bytes, Lua53ByteOrder byteOrder) =>
            (bytes.Length, byteOrder) switch
            {
                (4, Lua53ByteOrder.LittleEndian) => BinaryPrimitives.ReadInt32LittleEndian(bytes),
                (4, Lua53ByteOrder.BigEndian) => BinaryPrimitives.ReadInt32BigEndian(bytes),
                (8, Lua53ByteOrder.LittleEndian) => BinaryPrimitives.ReadInt64LittleEndian(bytes),
                (8, Lua53ByteOrder.BigEndian) => BinaryPrimitives.ReadInt64BigEndian(bytes),
                _ => throw new UnreachableException(),
            };

        private static double ReadNumber(ReadOnlySpan<byte> bytes, Lua53ByteOrder byteOrder) =>
            (bytes.Length, byteOrder) switch
            {
                (4, Lua53ByteOrder.LittleEndian) => BitConverter.Int32BitsToSingle(
                    byteOrder == Lua53ByteOrder.LittleEndian
                        ? BinaryPrimitives.ReadInt32LittleEndian(bytes)
                        : BinaryPrimitives.ReadInt32BigEndian(bytes)),
                (8, Lua53ByteOrder.LittleEndian) => BitConverter.Int64BitsToDouble(
                    BinaryPrimitives.ReadInt64LittleEndian(bytes)),
                (4, Lua53ByteOrder.BigEndian) => BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32BigEndian(bytes)),
                (8, Lua53ByteOrder.BigEndian) => BitConverter.Int64BitsToDouble(
                    BinaryPrimitives.ReadInt64BigEndian(bytes)),
                _ => throw new UnreachableException(),
            };

        private void Expect(ReadOnlySpan<byte> expected, string reason)
        {
            var offset = _offset;
            if (!ReadBytes(expected.Length).SequenceEqual(expected))
            {
                throw new Lua53ChunkFormatException(reason, offset);
            }
        }

        private void ExpectByte(byte expected, string reason)
        {
            var offset = _offset;
            if (ReadByte() != expected)
            {
                throw new Lua53ChunkFormatException(reason, offset);
            }
        }

        private byte ReadByte()
        {
            if ((uint)_offset >= (uint)_data.Length)
            {
                Fail("truncated chunk");
            }

            return _data[_offset++];
        }

        private ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count < 0 || count > _data.Length - _offset)
            {
                Fail("truncated chunk");
            }

            var result = _data.Slice(_offset, count);
            _offset += count;
            return result;
        }

        private void EnsureCountFitsRemaining(int count, int minimumBytesPerEntry, string description)
        {
            if (count < 0 || count > (_data.Length - _offset) / minimumBytesPerEntry)
            {
                Fail($"truncated chunk: {description} cannot fit in the remaining chunk data");
            }
        }

        private static void AddToBudget(ref int current, int added, int maximum, string description)
        {
            if (added < 0 || current > maximum - added)
            {
                throw new Lua53ChunkFormatException(
                    $"{description} exceeds the configured {maximum} limit");
            }

            current += added;
        }

        private void Fail(string reason) => throw new Lua53ChunkFormatException(reason, _offset);
        private T Fail<T>(string reason) => throw new Lua53ChunkFormatException(reason, _offset);
    }
}
