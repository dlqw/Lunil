using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using Lunil.IR.Lua54;

namespace Lunil.IR.Lua55;

/// <summary>Reads the official Lua 5.5 varint binary chunk format.</summary>
public static class Lua55ChunkReader
{
    private static ReadOnlySpan<byte> Signature => [0x1b, (byte)'L', (byte)'u', (byte)'a'];
    private static ReadOnlySpan<byte> LuaData => [0x19, 0x93, (byte)'\r', (byte)'\n', 0x1a, (byte)'\n'];

    public static Lua54Chunk Read(
        ReadOnlySpan<byte> data,
        Lua54ChunkReaderOptions? options = null)
    {
        options ??= Lua54ChunkReaderOptions.Default;
        ValidateOptions(options);
        if (data.Length > options.MaximumChunkBytes)
        {
            throw new Lua55ChunkFormatException(
                $"chunk exceeds the configured {options.MaximumChunkBytes}-byte limit");
        }

        return new Reader(data, options).ReadChunk();
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
        private readonly List<Lua54String> _strings;
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
            _strings = [];
            _target = default;
        }

        public Lua54Chunk ReadChunk()
        {
            Expect(Signature, "not a binary chunk");
            ExpectByte(0x55, "version mismatch; expected Lua 5.5");
            ExpectByte(0, "unsupported binary chunk format");
            Expect(LuaData, "corrupted LUAC_DATA marker");

            var intSize = ReadByte();
            var intSentinel = ReadBytes(intSize);
            var byteOrder = DetectByteOrder(intSentinel, -0x5678, "int format mismatch");

            var instructionSize = ReadByte();
            var instructionSentinel = ReadBytes(instructionSize);
            if (instructionSize != 4 || ReadUnsigned(instructionSentinel, byteOrder) != 0x12345678)
            {
                Fail("instruction format mismatch");
            }

            var integerSize = ReadByte();
            var integerSentinel = ReadBytes(integerSize);
            if (integerSize != 4 && integerSize != 8 ||
                ReadSigned(integerSentinel, byteOrder) != -0x5678)
            {
                Fail("Lua integer format mismatch");
            }

            var numberSize = ReadByte();
            var numberSentinel = ReadBytes(numberSize);
            if (numberSize is not (4 or 8))
            {
                Fail("Lua number size mismatch");
            }

            if (ReadNumber(numberSentinel, byteOrder) != -370.5)
            {
                Fail("Lua number format mismatch");
            }

            _target = new Lua54ChunkTarget(byteOrder, instructionSize, integerSize, numberSize);
            var mainUpvalueCount = ReadByte();
            var main = ReadPrototype(parentSource: null, depth: 1);
            if (!_options.AllowTrailingData && _offset != _data.Length)
            {
                Fail("trailing data after main prototype");
            }

            return Lua55RegisterLayoutAdapter.NormalizeToCanonical(
                new Lua54Chunk(_target, mainUpvalueCount, main));
        }

        private Lua54Prototype ReadPrototype(Lua54String? parentSource, int depth)
        {
            if (depth > _options.MaximumPrototypeDepth)
            {
                Fail("prototype nesting exceeds the configured limit");
            }

            AddToBudget(ref _prototypeCount, 1, _options.MaximumPrototypeCount, "prototype count");
            var lineDefined = ReadInt("line number");
            var lastLineDefined = ReadInt("line number");
            var parameterCount = ReadByte();
            var varArgFlags = ReadByte();
            var maximumStackSize = ReadByte();

            var codeCount = ReadInt("instruction count");
            AddToBudget(ref _instructionCount, codeCount, _options.MaximumInstructionCount, "instruction count");
            Align(4);
            EnsureCountFitsRemaining(codeCount, 4, "instruction count");
            var encodedCode = new Lua55Instruction[codeCount];
            for (var index = 0; index < codeCount; index++)
            {
                var raw = ReadUnsigned(ReadBytes(4), _target.ByteOrder);
                encodedCode[index] = new Lua55Instruction(raw);
            }

            var code = ImmutableArray.CreateBuilder<Lua54Instruction>(codeCount);
            for (var index = 0; index < codeCount; index++)
            {
                code.Add(TranslateInstruction(encodedCode, index));
            }

            var constantCount = ReadInt("constant count");
            AddToBudget(ref _constantCount, constantCount, _options.MaximumConstantCount, "constant count");
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
                nested.Add(ReadPrototype(parentSource, depth + 1));
            }

            var source = ReadNullableString() ?? parentSource;

            var lineInfoCount = ReadDebugCount("line info count");
            EnsureCountFitsRemaining(lineInfoCount, 1, "line info count");
            var lineInfo = ImmutableArray.CreateBuilder<sbyte>(lineInfoCount);
            for (var index = 0; index < lineInfoCount; index++)
            {
                lineInfo.Add(unchecked((sbyte)ReadByte()));
            }

            var absoluteLineCount = ReadDebugCount("absolute line info count");
            if (absoluteLineCount > 0)
            {
                Align(4);
            }

            EnsureCountFitsRemaining(absoluteLineCount, 8, "absolute line info count");
            var absoluteLines = ImmutableArray.CreateBuilder<Lua54AbsoluteLineInfo>(absoluteLineCount);
            for (var index = 0; index < absoluteLineCount; index++)
            {
                absoluteLines.Add(new Lua54AbsoluteLineInfo(
                    ReadRawInt("absolute line program counter"),
                    ReadRawInt("absolute line number")));
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
            var tagOffset = _offset;
            return ReadByte() switch
            {
                0 => Lua54Constant.Nil,
                1 => Lua54Constant.False,
                17 => Lua54Constant.True,
                19 => Lua54Constant.FromFloat(ReadNumber(ReadBytes(_target.NumberSize), _target.ByteOrder)),
                3 => Lua54Constant.FromInteger(ReadInteger()),
                4 => Lua54Constant.FromString(ReadRequiredString(), isShort: true),
                20 => Lua54Constant.FromString(ReadRequiredString(), isShort: false),
                var unknown => throw new Lua55ChunkFormatException(
                    $"unknown constant tag {unknown}", tagOffset),
            };
        }

        private Lua54String ReadRequiredString() =>
            ReadNullableString() ?? Fail<Lua54String>("constant string cannot be null");

        private Lua54String? ReadNullableString()
        {
            var encodedSize = ReadSize();
            if (encodedSize == 0)
            {
                var index = ReadSize();
                if (index == 0)
                {
                    return null;
                }

                if (index > (ulong)_strings.Count)
                {
                    Fail("invalid saved string index");
                }

                return _strings[(int)index - 1];
            }

            var byteCount = encodedSize - 1;
            if (byteCount > int.MaxValue)
            {
                Fail("string is too large for this runtime");
            }

            AddToBudget(ref _stringBytes, checked((int)byteCount), _options.MaximumStringBytes, "string bytes");
            var bytes = ReadBytes(checked((int)byteCount + 1));
            if (bytes[^1] != 0)
            {
                Fail("string is missing its terminating NUL");
            }

            var value = new Lua54String(bytes[..checked((int)byteCount)]);
            _strings.Add(value);
            return value;
        }

        private int ReadDebugCount(string description)
        {
            var count = ReadInt(description);
            AddToBudget(ref _debugEntryCount, count, _options.MaximumDebugEntryCount, "debug entry count");
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

        private int ReadRawInt(string description)
        {
            if (_target.InstructionSize != 4)
            {
                Fail($"{description} uses an unsupported native integer width");
            }

            return checked((int)ReadUnsigned(ReadBytes(4), _target.ByteOrder));
        }

        private long ReadInteger() => DecodeSigned(ReadSize());

        private ulong ReadSize()
        {
            ulong value = 0;
            while (true)
            {
                var current = ReadByte();
                if (value > (ulong.MaxValue >> 7))
                {
                    Fail("variable-length integer overflow");
                }

                value = (value << 7) | (uint)(current & 0x7f);
                if ((current & 0x80) == 0)
                {
                    return value;
                }
            }
        }

        private void Align(int alignment)
        {
            var padding = alignment - (_offset % alignment);
            if (padding < alignment)
            {
                _ = ReadBytes(padding);
            }
        }

        private static long DecodeSigned(ulong value) =>
            (value & 1) == 0 ? checked((long)(value >> 1)) : checked(-1 - (long)(value >> 1));

        private static Lua54ByteOrder DetectByteOrder(
            ReadOnlySpan<byte> bytes,
            long expected,
            string reason)
        {
            if (bytes.Length is not (4 or 8))
            {
                throw new Lua55ChunkFormatException(reason);
            }

            var little = ReadSigned(bytes, Lua54ByteOrder.LittleEndian);
            var big = ReadSigned(bytes, Lua54ByteOrder.BigEndian);
            return little == expected && big != expected
                ? Lua54ByteOrder.LittleEndian
                : big == expected && little != expected
                    ? Lua54ByteOrder.BigEndian
                    : throw new Lua55ChunkFormatException(reason);
        }

        private static uint ReadUnsigned(ReadOnlySpan<byte> bytes, Lua54ByteOrder order) =>
            bytes.Length switch
            {
                4 when order == Lua54ByteOrder.LittleEndian => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
                4 => BinaryPrimitives.ReadUInt32BigEndian(bytes),
                _ => throw new UnreachableException(),
            };

        private static long ReadSigned(ReadOnlySpan<byte> bytes, Lua54ByteOrder order) =>
            bytes.Length switch
            {
                4 when order == Lua54ByteOrder.LittleEndian => BinaryPrimitives.ReadInt32LittleEndian(bytes),
                4 => BinaryPrimitives.ReadInt32BigEndian(bytes),
                8 when order == Lua54ByteOrder.LittleEndian => BinaryPrimitives.ReadInt64LittleEndian(bytes),
                8 => BinaryPrimitives.ReadInt64BigEndian(bytes),
                _ => throw new UnreachableException(),
            };

        private static double ReadNumber(ReadOnlySpan<byte> bytes, Lua54ByteOrder order) =>
            bytes.Length switch
            {
                4 when order == Lua54ByteOrder.LittleEndian => BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(bytes)),
                4 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes)),
                8 when order == Lua54ByteOrder.LittleEndian => BitConverter.Int64BitsToDouble(
                    BinaryPrimitives.ReadInt64LittleEndian(bytes)),
                8 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(bytes)),
                _ => throw new UnreachableException(),
            };

        private static Lua54Instruction TranslateInstruction(
            Lua55Instruction[] code,
            int programCounter)
        {
            var instruction = code[programCounter];
            var mapped = instruction.Opcode switch
            {
                Lua55Opcode.Move => Lua54Opcode.Move,
                Lua55Opcode.LoadInteger => Lua54Opcode.LoadInteger,
                Lua55Opcode.LoadFloat => Lua54Opcode.LoadFloat,
                Lua55Opcode.LoadConstant => Lua54Opcode.LoadConstant,
                Lua55Opcode.LoadConstantExtra => Lua54Opcode.LoadConstantExtra,
                Lua55Opcode.LoadFalse => Lua54Opcode.LoadFalse,
                Lua55Opcode.LoadFalseAndSkip => Lua54Opcode.LoadFalseAndSkip,
                Lua55Opcode.LoadTrue => Lua54Opcode.LoadTrue,
                Lua55Opcode.LoadNil => Lua54Opcode.LoadNil,
                Lua55Opcode.GetUpvalue => Lua54Opcode.GetUpvalue,
                Lua55Opcode.SetUpvalue => Lua54Opcode.SetUpvalue,
                Lua55Opcode.GetTableUpvalue => Lua54Opcode.GetTableUpvalue,
                Lua55Opcode.GetTable => Lua54Opcode.GetTable,
                Lua55Opcode.GetInteger => Lua54Opcode.GetInteger,
                Lua55Opcode.GetField => Lua54Opcode.GetField,
                Lua55Opcode.SetTableUpvalue => Lua54Opcode.SetTableUpvalue,
                Lua55Opcode.SetTable => Lua54Opcode.SetTable,
                Lua55Opcode.SetInteger => Lua54Opcode.SetInteger,
                Lua55Opcode.SetField => Lua54Opcode.SetField,
                Lua55Opcode.NewTable => Lua54Opcode.NewTable,
                Lua55Opcode.Self => Lua54Opcode.Self,
                Lua55Opcode.AddImmediate => Lua54Opcode.AddImmediate,
                Lua55Opcode.AddConstant => Lua54Opcode.AddConstant,
                Lua55Opcode.SubtractConstant => Lua54Opcode.SubtractConstant,
                Lua55Opcode.MultiplyConstant => Lua54Opcode.MultiplyConstant,
                Lua55Opcode.ModuloConstant => Lua54Opcode.ModuloConstant,
                Lua55Opcode.PowerConstant => Lua54Opcode.PowerConstant,
                Lua55Opcode.DivideConstant => Lua54Opcode.DivideConstant,
                Lua55Opcode.FloorDivideConstant => Lua54Opcode.FloorDivideConstant,
                Lua55Opcode.BitwiseAndConstant => Lua54Opcode.BitwiseAndConstant,
                Lua55Opcode.BitwiseOrConstant => Lua54Opcode.BitwiseOrConstant,
                Lua55Opcode.BitwiseXorConstant => Lua54Opcode.BitwiseXorConstant,
                Lua55Opcode.ShiftLeftImmediate => Lua54Opcode.ShiftLeftImmediate,
                Lua55Opcode.ShiftRightImmediate => Lua54Opcode.ShiftRightImmediate,
                Lua55Opcode.Add => Lua54Opcode.Add,
                Lua55Opcode.Subtract => Lua54Opcode.Subtract,
                Lua55Opcode.Multiply => Lua54Opcode.Multiply,
                Lua55Opcode.Modulo => Lua54Opcode.Modulo,
                Lua55Opcode.Power => Lua54Opcode.Power,
                Lua55Opcode.Divide => Lua54Opcode.Divide,
                Lua55Opcode.FloorDivide => Lua54Opcode.FloorDivide,
                Lua55Opcode.BitwiseAnd => Lua54Opcode.BitwiseAnd,
                Lua55Opcode.BitwiseOr => Lua54Opcode.BitwiseOr,
                Lua55Opcode.BitwiseXor => Lua54Opcode.BitwiseXor,
                Lua55Opcode.ShiftLeft => Lua54Opcode.ShiftLeft,
                Lua55Opcode.ShiftRight => Lua54Opcode.ShiftRight,
                Lua55Opcode.MetamethodBinary => Lua54Opcode.MetamethodBinary,
                Lua55Opcode.MetamethodBinaryImmediate => Lua54Opcode.MetamethodBinaryImmediate,
                Lua55Opcode.MetamethodBinaryConstant => Lua54Opcode.MetamethodBinaryConstant,
                Lua55Opcode.UnaryMinus => Lua54Opcode.UnaryMinus,
                Lua55Opcode.BitwiseNot => Lua54Opcode.BitwiseNot,
                Lua55Opcode.LogicalNot => Lua54Opcode.LogicalNot,
                Lua55Opcode.Length => Lua54Opcode.Length,
                Lua55Opcode.Concatenate => Lua54Opcode.Concatenate,
                Lua55Opcode.Close => Lua54Opcode.Close,
                Lua55Opcode.ToBeClosed => Lua54Opcode.ToBeClosed,
                Lua55Opcode.Jump => Lua54Opcode.Jump,
                Lua55Opcode.Equal => Lua54Opcode.Equal,
                Lua55Opcode.LessThan => Lua54Opcode.LessThan,
                Lua55Opcode.LessOrEqual => Lua54Opcode.LessOrEqual,
                Lua55Opcode.EqualConstant => Lua54Opcode.EqualConstant,
                Lua55Opcode.EqualImmediate => Lua54Opcode.EqualImmediate,
                Lua55Opcode.LessThanImmediate => Lua54Opcode.LessThanImmediate,
                Lua55Opcode.LessOrEqualImmediate => Lua54Opcode.LessOrEqualImmediate,
                Lua55Opcode.GreaterThanImmediate => Lua54Opcode.GreaterThanImmediate,
                Lua55Opcode.GreaterOrEqualImmediate => Lua54Opcode.GreaterOrEqualImmediate,
                Lua55Opcode.Test => Lua54Opcode.Test,
                Lua55Opcode.TestSet => Lua54Opcode.TestSet,
                Lua55Opcode.Call => Lua54Opcode.Call,
                Lua55Opcode.TailCall => Lua54Opcode.TailCall,
                Lua55Opcode.Return => Lua54Opcode.Return,
                Lua55Opcode.ReturnZero => Lua54Opcode.ReturnZero,
                Lua55Opcode.ReturnOne => Lua54Opcode.ReturnOne,
                Lua55Opcode.NumericForLoop => Lua54Opcode.NumericForLoop,
                Lua55Opcode.NumericForPrepare => Lua54Opcode.NumericForPrepare,
                Lua55Opcode.GenericForPrepare => Lua54Opcode.GenericForPrepare,
                Lua55Opcode.GenericForCall => Lua54Opcode.GenericForCall,
                Lua55Opcode.GenericForLoop => Lua54Opcode.GenericForLoop,
                Lua55Opcode.SetList => Lua54Opcode.SetList,
                Lua55Opcode.Closure => Lua54Opcode.Closure,
                Lua55Opcode.VarArg => Lua54Opcode.VarArg,
                Lua55Opcode.VarArgPrepare => Lua54Opcode.VarArgPrepare,
                Lua55Opcode.ExtraArgument => Lua54Opcode.ExtraArgument,
                Lua55Opcode.GetVarArg => Lua54Opcode.GetTable,
                Lua55Opcode.ErrorIfNotNil => throw new Lua55ChunkFormatException(
                    "OP_ERRNNIL is not representable by the canonical runtime"),
                _ => throw new Lua55ChunkFormatException(
                    $"unknown opcode {(int)instruction.Opcode}"),
            };

            var raw = (instruction.RawValue & ~0x7fU) | (uint)mapped;
            if (instruction.Opcode is Lua55Opcode.NewTable or Lua55Opcode.SetList)
            {
                var extended = instruction.VC;
                if (instruction.K)
                {
                    if (programCounter + 1 >= code.Length ||
                        code[programCounter + 1].Opcode != Lua55Opcode.ExtraArgument)
                    {
                        throw new Lua55ChunkFormatException(
                            $"{instruction.Opcode} with k set must be followed by EXTRAARG");
                    }

                    extended = checked(extended +
                        code[programCounter + 1].Ax * 1024);
                }

                if (instruction.Opcode == Lua55Opcode.SetList)
                {
                    if (extended % 50 != 0)
                    {
                        throw new Lua55ChunkFormatException(
                            "SETLIST block operand is not aligned to the canonical flush size");
                    }

                    extended /= 50;
                }

                var canonicalExtra = extended / 256;
                var canonicalLow = extended % 256;
                raw = (raw & ~((0xffU << 16) | (0xffU << 24))) |
                    ((uint)instruction.VB << 16) |
                    ((uint)canonicalLow << 24);
                raw = canonicalExtra == 0
                    ? raw & ~(1U << 15)
                    : raw | (1U << 15);
            }

            if (instruction.Opcode == Lua55Opcode.ExtraArgument &&
                programCounter > 0 &&
                code[programCounter - 1].Opcode is Lua55Opcode.NewTable or Lua55Opcode.SetList &&
                code[programCounter - 1].K)
            {
                var previous = code[programCounter - 1];
                var extended = previous.VC + checked(previous.Ax * 1024);
                if (previous.Opcode == Lua55Opcode.SetList)
                {
                    if (extended % 50 != 0)
                    {
                        throw new Lua55ChunkFormatException(
                            "SETLIST block operand is not aligned to the canonical flush size");
                    }

                    extended /= 50;
                }

                return new Lua54Instruction(
                    Lua54Instruction.CreateAx(Lua54Opcode.ExtraArgument, extended / 256).RawValue |
                    ((uint)mapped & 0x7f));
            }

            return new Lua54Instruction(raw);
        }

        private void Expect(ReadOnlySpan<byte> expected, string reason)
        {
            var offset = _offset;
            if (!ReadBytes(expected.Length).SequenceEqual(expected))
            {
                throw new Lua55ChunkFormatException(reason, offset);
            }
        }

        private void ExpectByte(byte expected, string reason)
        {
            var offset = _offset;
            if (ReadByte() != expected)
            {
                throw new Lua55ChunkFormatException(reason, offset);
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
            if (count > (_data.Length - _offset) / minimumBytesPerEntry)
            {
                Fail($"truncated chunk: {description} cannot fit in the remaining chunk data");
            }
        }

        private static void AddToBudget(ref int current, int added, int maximum, string description)
        {
            if (added < 0 || current > maximum - added)
            {
                throw new Lua55ChunkFormatException($"{description} exceeds the configured {maximum} limit");
            }

            current += added;
        }

        private void Fail(string reason) => throw new Lua55ChunkFormatException(reason, _offset);
        private T Fail<T>(string reason) => throw new Lua55ChunkFormatException(reason, _offset);
    }
}
