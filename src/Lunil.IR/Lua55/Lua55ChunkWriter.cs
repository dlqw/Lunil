using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using Lunil.IR.Lua54;

namespace Lunil.IR.Lua55;

/// <summary>Writes the official Lua 5.5 varint binary chunk format.</summary>
public static class Lua55ChunkWriter
{
    private static ReadOnlySpan<byte> Signature => [0x1b, (byte)'L', (byte)'u', (byte)'a'];
    private static ReadOnlySpan<byte> LuaData => [0x19, 0x93, (byte)'\r', (byte)'\n', 0x1a, (byte)'\n'];

    public static byte[] Write(Lua54Chunk chunk, bool stripDebugInformation = false)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        Lua54ChunkVerifier.ThrowIfInvalid(chunk);
        var output = new ArrayBufferWriter<byte>();
        new Writer(output, chunk.Target, stripDebugInformation).WriteChunk(
            Lua55RegisterLayoutAdapter.DenormalizeForLua55(chunk));
        return output.WrittenSpan.ToArray();
    }

    private sealed class Writer
    {
        private readonly ArrayBufferWriter<byte> _output;
        private readonly Lua54ChunkTarget _target;
        private readonly bool _strip;
        private readonly Dictionary<string, ulong> _strings = new(StringComparer.Ordinal);
        private ulong _nextStringIndex = 1;

        public Writer(ArrayBufferWriter<byte> output, Lua54ChunkTarget target, bool strip)
        {
            _output = output;
            _target = target;
            _strip = strip;
        }

        public void WriteChunk(Lua54Chunk chunk)
        {
            WriteBytes(Signature);
            WriteByte(0x55);
            WriteByte(0);
            WriteBytes(LuaData);
            WriteNumInfo(4, -0x5678);
            WriteNumInfo(4, 0x12345678u);
            WriteNumInfo(_target.IntegerSize, -0x5678);
            WriteNumInfo(_target.NumberSize, -370.5);
            WriteByte(chunk.MainUpvalueCount);
            WritePrototype(chunk.MainPrototype, parentSource: null);
        }

        private void WritePrototype(Lua54Prototype prototype, Lua54String? parentSource)
        {
            WriteInt(prototype.LineDefined);
            WriteInt(prototype.LastLineDefined);
            WriteByte(prototype.ParameterCount);
            WriteByte(prototype.VarArgFlags);
            WriteByte(prototype.MaximumStackSize);

            WriteInt(prototype.Code.Length);
            Align(4);
            for (var index = 0; index < prototype.Code.Length; index++)
            {
                var translated = TranslateInstruction(prototype.Code, index);
                WriteUnsigned(translated, _target.ByteOrder);
            }

            WriteInt(prototype.Constants.Length);
            foreach (var constant in prototype.Constants)
            {
                WriteConstant(constant);
            }

            WriteInt(prototype.Upvalues.Length);
            foreach (var upvalue in prototype.Upvalues)
            {
                WriteByte(upvalue.InStack);
                WriteByte(upvalue.Index);
                WriteByte(upvalue.Kind);
            }

            WriteInt(prototype.NestedPrototypes.Length);
            foreach (var nested in prototype.NestedPrototypes)
            {
                WritePrototype(nested, prototype.Source);
            }

            WriteString(_strip ? null : prototype.Source ?? parentSource);
            if (_strip)
            {
                WriteInt(0);
                WriteInt(0);
                WriteInt(0);
                WriteInt(0);
                return;
            }

            WriteInt(prototype.LineInfo.Length);
            foreach (var lineDelta in prototype.LineInfo)
            {
                WriteByte(unchecked((byte)lineDelta));
            }

            WriteInt(prototype.AbsoluteLineInfo.Length);
            if (!prototype.AbsoluteLineInfo.IsEmpty)
            {
                Align(4);
            }

            foreach (var line in prototype.AbsoluteLineInfo)
            {
                WriteRawInt(line.ProgramCounter);
                WriteRawInt(line.Line);
            }

            WriteInt(prototype.LocalVariables.Length);
            foreach (var local in prototype.LocalVariables)
            {
                WriteString(local.Name);
                WriteInt(local.StartProgramCounter);
                WriteInt(local.EndProgramCounter);
            }

            WriteInt(prototype.Upvalues.Length);
            for (var index = 0; index < prototype.Upvalues.Length; index++)
            {
                var name = prototype.UpvalueNames.IsEmpty ? null : prototype.UpvalueNames[index];
                WriteString(name);
            }
        }

        private void WriteConstant(Lua54Constant constant)
        {
            switch (constant.Kind)
            {
                case Lua54ConstantKind.Nil:
                    WriteByte(0);
                    break;
                case Lua54ConstantKind.False:
                    WriteByte(1);
                    break;
                case Lua54ConstantKind.True:
                    WriteByte(17);
                    break;
                case Lua54ConstantKind.Float:
                    WriteByte(19);
                    WriteNumber(constant.FloatValue);
                    break;
                case Lua54ConstantKind.Integer:
                    WriteByte(3);
                    WriteInteger(constant.IntegerValue);
                    break;
                case Lua54ConstantKind.ShortString:
                    WriteByte(4);
                    WriteString(constant.StringValue);
                    break;
                case Lua54ConstantKind.LongString:
                    WriteByte(20);
                    WriteString(constant.StringValue);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown constant kind {constant.Kind}.");
            }
        }

        private void WriteString(Lua54String? value)
        {
            if (value is null)
            {
                WriteSize(0);
                WriteSize(0);
                return;
            }

            var key = Convert.ToBase64String(value.AsSpan());
            if (_strings.TryGetValue(key, out var index))
            {
                WriteSize(0);
                WriteSize(index);
                return;
            }

            WriteSize(checked((ulong)value.Length + 1));
            WriteBytes(value.AsSpan());
            WriteByte(0);
            _strings.Add(key, _nextStringIndex++);
        }

        private void WriteInt(int value)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            WriteSize((uint)value);
        }

        private void WriteRawInt(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            if (_target.ByteOrder == Lua54ByteOrder.LittleEndian)
            {
                BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            }
            else
            {
                BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            }

            WriteBytes(bytes);
        }

        private void WriteInteger(long value)
        {
            var encoded = value >= 0
                ? checked((ulong)value * 2)
                : checked((ulong)(-(value + 1)) * 2 + 1);
            WriteSize(encoded);
        }

        private void WriteSize(ulong value)
        {
            Span<byte> buffer = stackalloc byte[10];
            var index = buffer.Length;
            do
            {
                buffer[--index] = (byte)(value & 0x7f);
                value >>= 7;
            }
            while (value != 0);

            for (var continuation = index; continuation < buffer.Length - 1; continuation++)
            {
                buffer[continuation] |= 0x80;
            }
            WriteBytes(buffer[index..]);
        }

        private void WriteNumber(double value)
        {
            Span<byte> bytes = stackalloc byte[8];
            if (_target.NumberSize == 4)
            {
                var bits = BitConverter.SingleToInt32Bits((float)value);
                if (_target.ByteOrder == Lua54ByteOrder.LittleEndian)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(bytes, bits);
                }
                else
                {
                    BinaryPrimitives.WriteInt32BigEndian(bytes, bits);
                }
            }
            else if (_target.ByteOrder == Lua54ByteOrder.LittleEndian)
            {
                BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(value));
            }
            else
            {
                BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(value));
            }

            WriteBytes(bytes[.._target.NumberSize]);
        }

        private void WriteNumInfo(int size, int value)
        {
            WriteByte(checked((byte)size));
            Span<byte> bytes = stackalloc byte[8];
            if (_target.ByteOrder == Lua54ByteOrder.LittleEndian)
            {
                if (size == 4)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
                }
                else
                {
                    BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
                }
            }
            else if (size == 4)
            {
                BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            }
            else
            {
                BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            }

            WriteBytes(bytes[..size]);
        }

        private void WriteNumInfo(int size, uint value)
        {
            WriteByte(checked((byte)size));
            Span<byte> bytes = stackalloc byte[4];
            if (_target.ByteOrder == Lua54ByteOrder.LittleEndian)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            }

            WriteBytes(bytes);
        }

        private void WriteNumInfo(int size, double value)
        {
            WriteByte(checked((byte)size));
            WriteNumber(value);
        }

        private void Align(int alignment)
        {
            var padding = alignment - (_output.WrittenCount % alignment);
            if (padding < alignment)
            {
                for (var index = 0; index < padding; index++)
                {
                    WriteByte(0);
                }
            }
        }

        private static uint TranslateInstruction(
            ImmutableArray<Lua54Instruction> code,
            int programCounter)
        {
            var instruction = code[programCounter];
            var mapped = instruction.Opcode switch
            {
                Lua54Opcode.Move => Lua55Opcode.Move,
                Lua54Opcode.LoadInteger => Lua55Opcode.LoadInteger,
                Lua54Opcode.LoadFloat => Lua55Opcode.LoadFloat,
                Lua54Opcode.LoadConstant => Lua55Opcode.LoadConstant,
                Lua54Opcode.LoadConstantExtra => Lua55Opcode.LoadConstantExtra,
                Lua54Opcode.LoadFalse => Lua55Opcode.LoadFalse,
                Lua54Opcode.LoadFalseAndSkip => Lua55Opcode.LoadFalseAndSkip,
                Lua54Opcode.LoadTrue => Lua55Opcode.LoadTrue,
                Lua54Opcode.LoadNil => Lua55Opcode.LoadNil,
                Lua54Opcode.GetUpvalue => Lua55Opcode.GetUpvalue,
                Lua54Opcode.SetUpvalue => Lua55Opcode.SetUpvalue,
                Lua54Opcode.GetTableUpvalue => Lua55Opcode.GetTableUpvalue,
                Lua54Opcode.GetTable => Lua55Opcode.GetTable,
                Lua54Opcode.GetInteger => Lua55Opcode.GetInteger,
                Lua54Opcode.GetField => Lua55Opcode.GetField,
                Lua54Opcode.SetTableUpvalue => Lua55Opcode.SetTableUpvalue,
                Lua54Opcode.SetTable => Lua55Opcode.SetTable,
                Lua54Opcode.SetInteger => Lua55Opcode.SetInteger,
                Lua54Opcode.SetField => Lua55Opcode.SetField,
                Lua54Opcode.NewTable => Lua55Opcode.NewTable,
                Lua54Opcode.Self => Lua55Opcode.Self,
                Lua54Opcode.AddImmediate => Lua55Opcode.AddImmediate,
                Lua54Opcode.AddConstant => Lua55Opcode.AddConstant,
                Lua54Opcode.SubtractConstant => Lua55Opcode.SubtractConstant,
                Lua54Opcode.MultiplyConstant => Lua55Opcode.MultiplyConstant,
                Lua54Opcode.ModuloConstant => Lua55Opcode.ModuloConstant,
                Lua54Opcode.PowerConstant => Lua55Opcode.PowerConstant,
                Lua54Opcode.DivideConstant => Lua55Opcode.DivideConstant,
                Lua54Opcode.FloorDivideConstant => Lua55Opcode.FloorDivideConstant,
                Lua54Opcode.BitwiseAndConstant => Lua55Opcode.BitwiseAndConstant,
                Lua54Opcode.BitwiseOrConstant => Lua55Opcode.BitwiseOrConstant,
                Lua54Opcode.BitwiseXorConstant => Lua55Opcode.BitwiseXorConstant,
                Lua54Opcode.ShiftLeftImmediate => Lua55Opcode.ShiftLeftImmediate,
                Lua54Opcode.ShiftRightImmediate => Lua55Opcode.ShiftRightImmediate,
                Lua54Opcode.Add => Lua55Opcode.Add,
                Lua54Opcode.Subtract => Lua55Opcode.Subtract,
                Lua54Opcode.Multiply => Lua55Opcode.Multiply,
                Lua54Opcode.Modulo => Lua55Opcode.Modulo,
                Lua54Opcode.Power => Lua55Opcode.Power,
                Lua54Opcode.Divide => Lua55Opcode.Divide,
                Lua54Opcode.FloorDivide => Lua55Opcode.FloorDivide,
                Lua54Opcode.BitwiseAnd => Lua55Opcode.BitwiseAnd,
                Lua54Opcode.BitwiseOr => Lua55Opcode.BitwiseOr,
                Lua54Opcode.BitwiseXor => Lua55Opcode.BitwiseXor,
                Lua54Opcode.ShiftLeft => Lua55Opcode.ShiftLeft,
                Lua54Opcode.ShiftRight => Lua55Opcode.ShiftRight,
                Lua54Opcode.MetamethodBinary => Lua55Opcode.MetamethodBinary,
                Lua54Opcode.MetamethodBinaryImmediate => Lua55Opcode.MetamethodBinaryImmediate,
                Lua54Opcode.MetamethodBinaryConstant => Lua55Opcode.MetamethodBinaryConstant,
                Lua54Opcode.UnaryMinus => Lua55Opcode.UnaryMinus,
                Lua54Opcode.BitwiseNot => Lua55Opcode.BitwiseNot,
                Lua54Opcode.LogicalNot => Lua55Opcode.LogicalNot,
                Lua54Opcode.Length => Lua55Opcode.Length,
                Lua54Opcode.Concatenate => Lua55Opcode.Concatenate,
                Lua54Opcode.Close => Lua55Opcode.Close,
                Lua54Opcode.ToBeClosed => Lua55Opcode.ToBeClosed,
                Lua54Opcode.Jump => Lua55Opcode.Jump,
                Lua54Opcode.Equal => Lua55Opcode.Equal,
                Lua54Opcode.LessThan => Lua55Opcode.LessThan,
                Lua54Opcode.LessOrEqual => Lua55Opcode.LessOrEqual,
                Lua54Opcode.EqualConstant => Lua55Opcode.EqualConstant,
                Lua54Opcode.EqualImmediate => Lua55Opcode.EqualImmediate,
                Lua54Opcode.LessThanImmediate => Lua55Opcode.LessThanImmediate,
                Lua54Opcode.LessOrEqualImmediate => Lua55Opcode.LessOrEqualImmediate,
                Lua54Opcode.GreaterThanImmediate => Lua55Opcode.GreaterThanImmediate,
                Lua54Opcode.GreaterOrEqualImmediate => Lua55Opcode.GreaterOrEqualImmediate,
                Lua54Opcode.Test => Lua55Opcode.Test,
                Lua54Opcode.TestSet => Lua55Opcode.TestSet,
                Lua54Opcode.Call => Lua55Opcode.Call,
                Lua54Opcode.TailCall => Lua55Opcode.TailCall,
                Lua54Opcode.Return => Lua55Opcode.Return,
                Lua54Opcode.ReturnZero => Lua55Opcode.ReturnZero,
                Lua54Opcode.ReturnOne => Lua55Opcode.ReturnOne,
                Lua54Opcode.NumericForLoop => Lua55Opcode.NumericForLoop,
                Lua54Opcode.NumericForPrepare => Lua55Opcode.NumericForPrepare,
                Lua54Opcode.GenericForPrepare => Lua55Opcode.GenericForPrepare,
                Lua54Opcode.GenericForCall => Lua55Opcode.GenericForCall,
                Lua54Opcode.GenericForLoop => Lua55Opcode.GenericForLoop,
                Lua54Opcode.SetList => Lua55Opcode.SetList,
                Lua54Opcode.Closure => Lua55Opcode.Closure,
                Lua54Opcode.VarArg => Lua55Opcode.VarArg,
                Lua54Opcode.VarArgPrepare => Lua55Opcode.VarArgPrepare,
                Lua54Opcode.ExtraArgument => Lua55Opcode.ExtraArgument,
                Lua54Opcode.Lua55GetVarArg => Lua55Opcode.GetVarArg,
                Lua54Opcode.Lua55ErrorIfNotNil => Lua55Opcode.ErrorIfNotNil,
                _ => throw new InvalidDataException(
                    $"Opcode {instruction.Opcode} is not representable in Lua 5.5"),
            };

            if (instruction.Opcode is Lua54Opcode.NewTable or Lua54Opcode.SetList)
            {
                var canonical = instruction.C +
                    (instruction.K && programCounter + 1 < code.Length
                        ? checked(code[programCounter + 1].Ax * 256)
                        : 0);
                var full = instruction.Opcode == Lua54Opcode.SetList
                    ? checked(canonical * 50)
                    : canonical;
                var variantC = full % 1024;
                return Lua55GeneratedInstructionCodec.EncodeVAbc(
                    mapped,
                    instruction.A,
                    instruction.B,
                    variantC,
                    full >= 1024);
            }

            if (instruction.Opcode == Lua54Opcode.ExtraArgument &&
                programCounter > 0 &&
                code[programCounter - 1].Opcode is Lua54Opcode.NewTable or Lua54Opcode.SetList &&
                code[programCounter - 1].K)
            {
                var previous = code[programCounter - 1];
                var canonical = previous.C + checked(instruction.Ax * 256);
                var full = previous.Opcode == Lua54Opcode.SetList
                    ? checked(canonical * 50)
                    : canonical;
                return Lua55GeneratedInstructionCodec.EncodeAx(
                    mapped,
                    full / 1024);
            }

            return (instruction.RawValue & ~0x7fU) | (uint)mapped;
        }

        private void WriteByte(byte value)
        {
            _output.GetSpan(1)[0] = value;
            _output.Advance(1);
        }

        private void WriteUnsigned(uint value, Lua54ByteOrder order)
        {
            Span<byte> bytes = stackalloc byte[4];
            if (order == Lua54ByteOrder.LittleEndian)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            }

            WriteBytes(bytes);
        }

        private void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(_output.GetSpan(bytes.Length));
            _output.Advance(bytes.Length);
        }
    }
}
