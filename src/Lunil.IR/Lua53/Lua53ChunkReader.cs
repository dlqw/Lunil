using System.Buffers.Binary;
using System.Collections.Immutable;

namespace Lunil.IR.Lua53;

public sealed class Lua53ChunkFormatException : LuaChunkFormatException
{
    public Lua53ChunkFormatException(string reason, int offset = 0)
        : base("Lua 5.3", reason, offset)
    {
    }
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
        private readonly Lua53ChunkReaderOptions _options;
        private ChunkByteReader _bytes;
        private int _prototypeCount;
        private int _instructionCount;
        private int _constantCount;
        private int _stringBytes;
        private int _debugEntryCount;
        private Lua53ChunkTarget _target;

        public Reader(ReadOnlySpan<byte> data, Lua53ChunkReaderOptions options)
        {
            _options = options;
            _bytes = new ChunkByteReader(
                data,
                static (reason, offset) => new Lua53ChunkFormatException(reason, offset));
        }

        public Lua53Chunk ReadChunk()
        {
            _bytes.Expect(Signature, "not a binary chunk");
            _bytes.ExpectByte(0x53, "version mismatch; expected Lua 5.3");
            _bytes.ExpectByte(0, "unsupported binary chunk format");
            _bytes.Expect(LuaData, "corrupted LUAC_DATA marker");

            var sizeOfInt = _bytes.ReadByte();
            var sizeOfSizeT = _bytes.ReadByte();
            var instructionSize = _bytes.ReadByte();
            var integerSize = _bytes.ReadByte();
            var numberSize = _bytes.ReadByte();
            if (sizeOfInt != 4 || instructionSize != 4 ||
                sizeOfSizeT is not (4 or 8) || integerSize is not (4 or 8) ||
                numberSize is not (4 or 8))
            {
                _bytes.Fail("unsupported Lua 5.3 scalar layout");
            }

            var integerSentinel = _bytes.ReadBytes(integerSize);
            var littleInteger = ReadInteger(integerSentinel, Lua53ByteOrder.LittleEndian);
            var bigInteger = ReadInteger(integerSentinel, Lua53ByteOrder.BigEndian);
            var byteOrder = (littleInteger, bigInteger) switch
            {
                (0x5678, not 0x5678) => Lua53ByteOrder.LittleEndian,
                (not 0x5678, 0x5678) => Lua53ByteOrder.BigEndian,
                _ => _bytes.Fail<Lua53ByteOrder>("integer format mismatch"),
            };
            _bytes.SetLittleEndian(byteOrder == Lua53ByteOrder.LittleEndian);

            var numberSentinel = _bytes.ReadBytes(numberSize);
            if (ReadNumber(numberSentinel, byteOrder) != 370.5)
            {
                _bytes.Fail("floating-point format mismatch");
            }

            _target = new Lua53ChunkTarget(
                byteOrder,
                sizeOfInt,
                sizeOfSizeT,
                instructionSize,
                integerSize,
                numberSize);
            var mainUpvalueCount = _bytes.ReadByte();
            var main = ReadPrototype(parentSource: null, depth: 1);
            if (!_options.AllowTrailingData && !_bytes.AtEnd)
            {
                _bytes.Fail("trailing data after main prototype");
            }

            return new Lua53Chunk(_target, mainUpvalueCount, main);
        }

        private Lua53Prototype ReadPrototype(Lua53String? parentSource, int depth)
        {
            _bytes.FailIfDepthExceeds(depth, _options.MaximumPrototypeDepth);
            AddToBudget(ref _prototypeCount, 1, _options.MaximumPrototypeCount, "prototype count");
            var source = ReadNullableString() ?? parentSource;
            var lineDefined = _bytes.ReadNonNegativeInt32("line number");
            var lastLineDefined = _bytes.ReadNonNegativeInt32("line number");
            var parameterCount = _bytes.ReadByte();
            var varArgFlags = _bytes.ReadByte();
            var maximumStackSize = _bytes.ReadByte();

            var codeCount = _bytes.ReadNonNegativeInt32("instruction count");
            AddToBudget(
                ref _instructionCount,
                codeCount,
                _options.MaximumInstructionCount,
                "instruction count");
            _bytes.EnsureCountFitsRemaining(codeCount, 4, "instruction count");
            var code = ImmutableArray.CreateBuilder<Lua53Instruction>(codeCount);
            for (var index = 0; index < codeCount; index++)
            {
                var bytes = _bytes.ReadBytes(4);
                var raw = _target.ByteOrder == Lua53ByteOrder.LittleEndian
                    ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
                    : BinaryPrimitives.ReadUInt32BigEndian(bytes);
                code.Add(new Lua53Instruction(raw));
            }

            var constantCount = _bytes.ReadNonNegativeInt32("constant count");
            AddToBudget(
                ref _constantCount,
                constantCount,
                _options.MaximumConstantCount,
                "constant count");
            _bytes.EnsureCountFitsRemaining(constantCount, 1, "constant count");
            var constants = ImmutableArray.CreateBuilder<Lua53Constant>(constantCount);
            for (var index = 0; index < constantCount; index++)
            {
                constants.Add(ReadConstant());
            }

            var upvalueCount = _bytes.ReadNonNegativeInt32("upvalue count");
            AddToBudget(
                ref _debugEntryCount,
                upvalueCount,
                _options.MaximumUpvalueCount,
                "upvalue count");
            _bytes.EnsureCountFitsRemaining(upvalueCount, 2, "upvalue count");
            var upvalues = ImmutableArray.CreateBuilder<Lua53UpvalueDescriptor>(upvalueCount);
            for (var index = 0; index < upvalueCount; index++)
            {
                upvalues.Add(new Lua53UpvalueDescriptor(_bytes.ReadByte(), _bytes.ReadByte()));
            }

            var nestedCount = _bytes.ReadNonNegativeInt32("nested prototype count");
            _bytes.EnsureCountFitsRemaining(nestedCount, 2, "nested prototype count");
            var nested = ImmutableArray.CreateBuilder<Lua53Prototype>(nestedCount);
            for (var index = 0; index < nestedCount; index++)
            {
                nested.Add(ReadPrototype(source, depth + 1));
            }

            var lineInfoCount = _bytes.ReadNonNegativeInt32("line info count");
            AddToBudget(
                ref _debugEntryCount,
                lineInfoCount,
                _options.MaximumDebugEntryCount,
                "debug entry count");
            _bytes.EnsureCountFitsRemaining(lineInfoCount, 4, "line info count");
            var lineInfo = ImmutableArray.CreateBuilder<int>(lineInfoCount);
            for (var index = 0; index < lineInfoCount; index++)
            {
                lineInfo.Add(_bytes.ReadNonNegativeInt32("line info"));
            }

            var localCount = _bytes.ReadNonNegativeInt32("local variable count");
            AddToBudget(
                ref _debugEntryCount,
                localCount,
                _options.MaximumDebugEntryCount,
                "debug entry count");
            var locals = ImmutableArray.CreateBuilder<Lua53LocalVariable>(localCount);
            for (var index = 0; index < localCount; index++)
            {
                locals.Add(new Lua53LocalVariable(
                    ReadNullableString(),
                    _bytes.ReadNonNegativeInt32("local start program counter"),
                    _bytes.ReadNonNegativeInt32("local end program counter")));
            }

            var upvalueNameCount = _bytes.ReadNonNegativeInt32("upvalue name count");
            AddToBudget(
                ref _debugEntryCount,
                upvalueNameCount,
                _options.MaximumDebugEntryCount,
                "debug entry count");
            var upvalueNames = ImmutableArray.CreateBuilder<Lua53String?>(upvalueNameCount);
            for (var index = 0; index < upvalueNameCount; index++)
            {
                upvalueNames.Add(ReadNullableString());
            }

            if (upvalueNameCount != 0 && upvalueNameCount != upvalueCount)
            {
                _bytes.Fail("upvalue name count must be zero or match the upvalue count");
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
            var tagOffset = _bytes.Offset;
            return _bytes.ReadByte() switch
            {
                0 => Lua53Constant.Nil,
                1 => Lua53Constant.FromBoolean(false),
                17 => Lua53Constant.FromBoolean(true),
                3 => Lua53Constant.FromFloat(ReadNumber(_bytes.ReadBytes(_target.NumberSize), _target.ByteOrder)),
                19 => Lua53Constant.FromInteger(ReadInteger(_bytes.ReadBytes(_target.IntegerSize), _target.ByteOrder)),
                4 => Lua53Constant.FromString(ReadRequiredString(), isShort: true),
                20 => Lua53Constant.FromString(ReadRequiredString(), isShort: false),
                var unknown => throw new Lua53ChunkFormatException(
                    $"unknown constant tag {unknown}", tagOffset),
            };
        }

        private Lua53String ReadRequiredString() =>
            ReadNullableString() ?? _bytes.Fail<Lua53String>("constant string cannot be null");

        private Lua53String? ReadNullableString()
        {
            var firstSizeByte = _bytes.ReadByte();
            if (firstSizeByte == 0)
            {
                return null;
            }

            var encodedSize = firstSizeByte == byte.MaxValue
                ? ReadSizeT()
                : firstSizeByte;
            if (encodedSize == 0)
            {
                _bytes.Fail("extended string size cannot be zero");
            }

            var byteCount = checked(encodedSize - 1);
            if (byteCount > int.MaxValue)
            {
                _bytes.Fail("string is too large for this runtime");
            }

            AddToBudget(ref _stringBytes, (int)byteCount, _options.MaximumStringBytes, "string bytes");
            return new Lua53String(_bytes.ReadBytes((int)byteCount).ToArray());
        }

        private ulong ReadSizeT() => _target.SizeOfSizeT switch
        {
            4 => _bytes.ReadUnsignedInt32(),
            8 => _bytes.ReadUnsignedInt64(),
            _ => throw new LunilUnreachableException(),
        };

        private static long ReadInteger(ReadOnlySpan<byte> bytes, Lua53ByteOrder byteOrder) =>
            (bytes.Length, byteOrder) switch
            {
                (4, Lua53ByteOrder.LittleEndian) => BinaryPrimitives.ReadInt32LittleEndian(bytes),
                (4, Lua53ByteOrder.BigEndian) => BinaryPrimitives.ReadInt32BigEndian(bytes),
                (8, Lua53ByteOrder.LittleEndian) => BinaryPrimitives.ReadInt64LittleEndian(bytes),
                (8, Lua53ByteOrder.BigEndian) => BinaryPrimitives.ReadInt64BigEndian(bytes),
                _ => throw new LunilUnreachableException(),
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
                _ => throw new LunilUnreachableException(),
            };

        private static void AddToBudget(
            ref int current,
            int added,
            int maximum,
            string description)
        {
            if (added < 0 || current > maximum - added)
            {
                throw new Lua53ChunkFormatException(
                    $"{description} exceeds the configured {maximum} limit");
            }

            current += added;
        }
    }
}
