using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Luac.IR.Lua54;

/// <summary>Reads the platform-dependent binary format emitted by PUC Lua 5.4.</summary>
public static class Lua54ChunkReader
{
    private static ReadOnlySpan<byte> Signature => [0x1b, (byte)'L', (byte)'u', (byte)'a'];

    private static ReadOnlySpan<byte> LuacData => [0x19, 0x93, (byte)'\r', (byte)'\n', 0x1a, (byte)'\n'];

    public static Lua54Chunk Read(
        ReadOnlySpan<byte> data,
        Lua54ChunkReaderOptions? options = null)
    {
        options ??= Lua54ChunkReaderOptions.Default;
        ValidateOptions(options);

        if (data.Length > options.MaximumChunkBytes)
        {
            throw new Lua54ChunkFormatException(
                $"chunk exceeds the configured {options.MaximumChunkBytes}-byte limit",
                0);
        }

        var reader = new Reader(data, options);
        return reader.ReadChunk();
    }

    private static void ValidateOptions(Lua54ChunkReaderOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumChunkBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumPrototypeDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumPrototypeCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumInstructionCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumConstantCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumUpvalueCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumStringBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumDebugEntryCount);
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly Lua54ChunkReaderOptions _options;
        private int _offset;
        private int _prototypeCount;
        private int _instructionCount;
        private int _constantCount;
        private int _stringBytes;
        private int _debugEntryCount;
        private Lua54ChunkTarget _target;

        public Reader(ReadOnlySpan<byte> data, Lua54ChunkReaderOptions options)
        {
            _data = data;
            _options = options;
        }

        public Lua54Chunk ReadChunk()
        {
            Expect(Signature, "not a binary chunk");
            ExpectByte(0x54, "version mismatch; expected Lua 5.4");
            ExpectByte(0, "unsupported binary chunk format");
            Expect(LuacData, "corrupted LUAC_DATA marker");

            var instructionSize = ReadByte();
            var integerSize = ReadByte();
            var numberSize = ReadByte();

            if (instructionSize != 4)
            {
                Fail($"unsupported Instruction size {instructionSize}");
            }

            if (integerSize is not (4 or 8))
            {
                Fail($"unsupported lua_Integer size {integerSize}");
            }

            if (numberSize is not (4 or 8))
            {
                Fail($"unsupported lua_Number size {numberSize}");
            }

            var integerSentinel = ReadBytes(integerSize);
            var littleInteger = ReadInteger(integerSentinel, Lua54ByteOrder.LittleEndian);
            var bigInteger = ReadInteger(integerSentinel, Lua54ByteOrder.BigEndian);
            var byteOrder = (littleInteger, bigInteger) switch
            {
                (0x5678, not 0x5678) => Lua54ByteOrder.LittleEndian,
                (not 0x5678, 0x5678) => Lua54ByteOrder.BigEndian,
                _ => Fail<Lua54ByteOrder>("integer format mismatch"),
            };

            var numberSentinel = ReadBytes(numberSize);
            var luacNumber = ReadNumber(numberSentinel, byteOrder);
            if (luacNumber != 370.5)
            {
                Fail("floating-point format mismatch");
            }

            _target = new Lua54ChunkTarget(byteOrder, instructionSize, integerSize, numberSize);
            var mainUpvalueCount = ReadByte();
            var main = ReadPrototype(parentSource: null, depth: 1);

            if (!_options.AllowTrailingData && _offset != _data.Length)
            {
                Fail("trailing data after main prototype");
            }

            var chunk = new Lua54Chunk(_target, mainUpvalueCount, main);
            var verificationErrors = Lua54ChunkVerifier.Verify(chunk);
            if (!verificationErrors.IsEmpty)
            {
                var first = verificationErrors[0];
                var pc = first.ProgramCounter is int programCounter
                    ? $" at instruction {programCounter}"
                    : string.Empty;
                Fail($"invalid prototype {first.PrototypePath}{pc}: {first.Message}");
            }

            return chunk;
        }

        private Lua54Prototype ReadPrototype(Lua54String? parentSource, int depth)
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
            AddToBudget(
                ref _instructionCount,
                codeCount,
                _options.MaximumInstructionCount,
                "instruction count");
            EnsureCountFitsRemaining(codeCount, 4, "instruction count");
            var code = ImmutableArray.CreateBuilder<Lua54Instruction>(codeCount);
            for (var index = 0; index < codeCount; index++)
            {
                var bytes = ReadBytes(4);
                var raw = _target.ByteOrder == Lua54ByteOrder.LittleEndian
                    ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
                    : BinaryPrimitives.ReadUInt32BigEndian(bytes);
                code.Add(new Lua54Instruction(raw));
            }

            var constantCount = ReadInt("constant count");
            AddToBudget(
                ref _constantCount,
                constantCount,
                _options.MaximumConstantCount,
                "constant count");
            EnsureCountFitsRemaining(constantCount, 1, "constant count");
            var constants = ImmutableArray.CreateBuilder<Lua54Constant>(constantCount);
            for (var index = 0; index < constantCount; index++)
            {
                constants.Add(ReadConstant());
            }

            var upvalueCount = ReadInt("upvalue count");
            if (upvalueCount > _options.MaximumUpvalueCount)
            {
                Fail($"upvalue count exceeds the configured {_options.MaximumUpvalueCount} limit");
            }

            EnsureCountFitsRemaining(upvalueCount, 3, "upvalue count");
            var upvalues = ImmutableArray.CreateBuilder<Lua54UpvalueDescriptor>(upvalueCount);
            for (var index = 0; index < upvalueCount; index++)
            {
                upvalues.Add(new Lua54UpvalueDescriptor(ReadByte(), ReadByte(), ReadByte()));
            }

            var nestedCount = ReadInt("nested prototype count");
            if (nestedCount > _options.MaximumPrototypeCount - _prototypeCount)
            {
                Fail("nested prototype count exceeds the configured prototype budget");
            }

            EnsureCountFitsRemaining(nestedCount, 14, "nested prototype count");
            var nested = ImmutableArray.CreateBuilder<Lua54Prototype>(nestedCount);
            for (var index = 0; index < nestedCount; index++)
            {
                nested.Add(ReadPrototype(source, depth + 1));
            }

            var lineInfoCount = ReadDebugCount("line info count");
            EnsureCountFitsRemaining(lineInfoCount, 1, "line info count");
            var lineInfo = ImmutableArray.CreateBuilder<sbyte>(lineInfoCount);
            for (var index = 0; index < lineInfoCount; index++)
            {
                lineInfo.Add(unchecked((sbyte)ReadByte()));
            }

            var absoluteLineCount = ReadDebugCount("absolute line info count");
            EnsureCountFitsRemaining(absoluteLineCount, 2, "absolute line info count");
            var absoluteLines = ImmutableArray.CreateBuilder<Lua54AbsoluteLineInfo>(absoluteLineCount);
            for (var index = 0; index < absoluteLineCount; index++)
            {
                absoluteLines.Add(new Lua54AbsoluteLineInfo(
                    ReadInt("absolute line program counter"),
                    ReadInt("absolute line number")));
            }

            var localCount = ReadDebugCount("local variable count");
            EnsureCountFitsRemaining(localCount, 3, "local variable count");
            var locals = ImmutableArray.CreateBuilder<Lua54LocalVariable>(localCount);
            for (var index = 0; index < localCount; index++)
            {
                locals.Add(new Lua54LocalVariable(
                    ReadNullableString(),
                    ReadInt("local start program counter"),
                    ReadInt("local end program counter")));
            }

            var encodedUpvalueNameCount = ReadDebugCount("upvalue name count");
            if (encodedUpvalueNameCount != 0 && encodedUpvalueNameCount != upvalueCount)
            {
                Fail("upvalue name count must be zero or match the upvalue count");
            }

            EnsureCountFitsRemaining(encodedUpvalueNameCount, 1, "upvalue name count");
            var upvalueNames = ImmutableArray.CreateBuilder<Lua54String?>(encodedUpvalueNameCount);
            for (var index = 0; index < encodedUpvalueNameCount; index++)
            {
                upvalueNames.Add(ReadNullableString());
            }

            return new Lua54Prototype
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
                AbsoluteLineInfo = absoluteLines.MoveToImmutable(),
                LocalVariables = locals.MoveToImmutable(),
                UpvalueNames = upvalueNames.MoveToImmutable(),
            };
        }

        private Lua54Constant ReadConstant()
        {
            const byte nilTag = 0;
            const byte falseTag = 1;
            const byte trueTag = 17;
            const byte integerTag = 3;
            const byte floatTag = 19;
            const byte shortStringTag = 4;
            const byte longStringTag = 20;

            var tagOffset = _offset;
            return ReadByte() switch
            {
                nilTag => Lua54Constant.Nil,
                falseTag => Lua54Constant.False,
                trueTag => Lua54Constant.True,
                floatTag => Lua54Constant.FromFloat(ReadNumber(
                    ReadBytes(_target.NumberSize),
                    _target.ByteOrder)),
                integerTag => Lua54Constant.FromInteger(ReadInteger(
                    ReadBytes(_target.IntegerSize),
                    _target.ByteOrder)),
                shortStringTag => Lua54Constant.FromString(ReadRequiredString(), isShort: true),
                longStringTag => Lua54Constant.FromString(ReadRequiredString(), isShort: false),
                var unknown => throw new Lua54ChunkFormatException(
                    $"unknown constant tag {unknown}",
                    tagOffset),
            };
        }

        private Lua54String ReadRequiredString() =>
            ReadNullableString() ?? Fail<Lua54String>("constant string cannot be null");

        private Lua54String? ReadNullableString()
        {
            var encodedSize = ReadSize();
            if (encodedSize == 0)
            {
                return null;
            }

            var byteCount = encodedSize - 1;
            if (byteCount > int.MaxValue)
            {
                Fail("string is too large for this runtime");
            }

            AddToBudget(
                ref _stringBytes,
                (int)byteCount,
                _options.MaximumStringBytes,
                "string bytes");
            return new Lua54String(ReadBytes((int)byteCount));
        }

        private int ReadDebugCount(string description)
        {
            var count = ReadInt(description);
            AddToBudget(
                ref _debugEntryCount,
                count,
                _options.MaximumDebugEntryCount,
                "debug entry count");
            return count;
        }

        private int ReadInt(string description)
        {
            var value = ReadSize();
            if (value > int.MaxValue)
            {
                Fail($"{description} exceeds Int32 range");
            }

            return (int)value;
        }

        private ulong ReadSize()
        {
            ulong value = 0;
            while (true)
            {
                var current = ReadByte();
                if (value >= (ulong.MaxValue >> 7))
                {
                    Fail("variable-length integer overflow");
                }

                value = (value << 7) | (uint)(current & 0x7f);
                if ((current & 0x80) != 0)
                {
                    return value;
                }
            }
        }

        private void Expect(ReadOnlySpan<byte> expected, string reason)
        {
            var offset = _offset;
            if (!ReadBytes(expected.Length).SequenceEqual(expected))
            {
                throw new Lua54ChunkFormatException(reason, offset);
            }
        }

        private void ExpectByte(byte expected, string reason)
        {
            var offset = _offset;
            if (ReadByte() != expected)
            {
                throw new Lua54ChunkFormatException(reason, offset);
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

        private void EnsureCountFitsRemaining(
            int count,
            int minimumBytesPerEntry,
            string description)
        {
            if (count > (_data.Length - _offset) / minimumBytesPerEntry)
            {
                Fail($"{description} cannot fit in the remaining chunk data");
            }
        }

        private static long ReadInteger(ReadOnlySpan<byte> bytes, Lua54ByteOrder byteOrder) =>
            (bytes.Length, byteOrder) switch
            {
                (4, Lua54ByteOrder.LittleEndian) => BinaryPrimitives.ReadInt32LittleEndian(bytes),
                (4, Lua54ByteOrder.BigEndian) => BinaryPrimitives.ReadInt32BigEndian(bytes),
                (8, Lua54ByteOrder.LittleEndian) => BinaryPrimitives.ReadInt64LittleEndian(bytes),
                (8, Lua54ByteOrder.BigEndian) => BinaryPrimitives.ReadInt64BigEndian(bytes),
                _ => throw new UnreachableException(),
            };

        private static double ReadNumber(ReadOnlySpan<byte> bytes, Lua54ByteOrder byteOrder) =>
            (bytes.Length, byteOrder) switch
            {
                (4, Lua54ByteOrder.LittleEndian) => BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(bytes)),
                (4, Lua54ByteOrder.BigEndian) => BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32BigEndian(bytes)),
                (8, Lua54ByteOrder.LittleEndian) => BitConverter.Int64BitsToDouble(
                    BinaryPrimitives.ReadInt64LittleEndian(bytes)),
                (8, Lua54ByteOrder.BigEndian) => BitConverter.Int64BitsToDouble(
                    BinaryPrimitives.ReadInt64BigEndian(bytes)),
                _ => throw new UnreachableException(),
            };

        private static void AddToBudget(
            ref int current,
            int added,
            int maximum,
            string description)
        {
            if (added < 0 || current > maximum - added)
            {
                throw new Lua54ChunkFormatException(
                    $"{description} exceeds the configured {maximum} limit",
                    0);
            }

            current += added;
        }

        private void Fail(string reason) => throw new Lua54ChunkFormatException(reason, _offset);

        private T Fail<T>(string reason) => throw new Lua54ChunkFormatException(reason, _offset);
    }
}
