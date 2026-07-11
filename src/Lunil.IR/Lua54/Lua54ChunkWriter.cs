using System.Buffers;
using System.Buffers.Binary;

namespace Lunil.IR.Lua54;

/// <summary>Writes chunks accepted by a matching PUC Lua 5.4 build.</summary>
public static class Lua54ChunkWriter
{
    private static ReadOnlySpan<byte> Signature => [0x1b, (byte)'L', (byte)'u', (byte)'a'];

    private static ReadOnlySpan<byte> LunilData => [0x19, 0x93, (byte)'\r', (byte)'\n', 0x1a, (byte)'\n'];

    public static byte[] Write(Lua54Chunk chunk, bool stripDebugInformation = false)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        Lua54ChunkVerifier.ThrowIfInvalid(chunk);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new Writer(buffer, chunk.Target, stripDebugInformation);
        writer.WriteChunk(chunk);
        return buffer.WrittenSpan.ToArray();
    }

    private sealed class Writer
    {
        private readonly ArrayBufferWriter<byte> _output;
        private readonly Lua54ChunkTarget _target;
        private readonly bool _strip;

        public Writer(
            ArrayBufferWriter<byte> output,
            Lua54ChunkTarget target,
            bool stripDebugInformation)
        {
            _output = output;
            _target = target;
            _strip = stripDebugInformation;
        }

        public void WriteChunk(Lua54Chunk chunk)
        {
            WriteBytes(Signature);
            WriteByte(0x54);
            WriteByte(0);
            WriteBytes(LunilData);
            WriteByte(_target.InstructionSize);
            WriteByte(_target.IntegerSize);
            WriteByte(_target.NumberSize);
            WriteInteger(0x5678);
            WriteNumber(370.5);
            WriteByte(chunk.MainUpvalueCount);
            WritePrototype(chunk.MainPrototype, parentSource: null);
        }

        private void WritePrototype(Lua54Prototype prototype, Lua54String? parentSource)
        {
            if (_strip || Equals(prototype.Source, parentSource))
            {
                WriteString(null);
            }
            else
            {
                WriteString(prototype.Source);
            }

            WriteInt(prototype.LineDefined);
            WriteInt(prototype.LastLineDefined);
            WriteByte(prototype.ParameterCount);
            WriteByte(prototype.VarArgFlags);
            WriteByte(prototype.MaximumStackSize);

            WriteInt(prototype.Code.Length);
            Span<byte> instructionBytes = stackalloc byte[4];
            foreach (var instruction in prototype.Code)
            {
                if (_target.ByteOrder == Lua54ByteOrder.LittleEndian)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(instructionBytes, instruction.RawValue);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32BigEndian(instructionBytes, instruction.RawValue);
                }

                WriteBytes(instructionBytes);
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
            foreach (var line in prototype.AbsoluteLineInfo)
            {
                WriteInt(line.ProgramCounter);
                WriteInt(line.Line);
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
                return;
            }

            WriteSize(checked((ulong)value.Length + 1));
            WriteBytes(value.AsSpan());
        }

        private void WriteInt(int value)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            WriteSize((uint)value);
        }

        private void WriteSize(ulong value)
        {
            Span<byte> bytes = stackalloc byte[10];
            var index = bytes.Length;
            do
            {
                bytes[--index] = (byte)(value & 0x7f);
                value >>= 7;
            }
            while (value != 0);

            bytes[^1] |= 0x80;
            WriteBytes(bytes[index..]);
        }

        private void WriteInteger(long value)
        {
            Span<byte> bytes = stackalloc byte[8];
            if (_target.IntegerSize == 4)
            {
                if (value is < int.MinValue or > int.MaxValue)
                {
                    throw new InvalidOperationException(
                        $"Integer {value} cannot be represented by the selected 32-bit chunk target.");
                }

                if (_target.ByteOrder == Lua54ByteOrder.LittleEndian)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)value);
                }
                else
                {
                    BinaryPrimitives.WriteInt32BigEndian(bytes, (int)value);
                }
            }
            else if (_target.ByteOrder == Lua54ByteOrder.LittleEndian)
            {
                BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            }
            else
            {
                BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            }

            WriteBytes(bytes[.._target.IntegerSize]);
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
            else
            {
                var bits = BitConverter.DoubleToInt64Bits(value);
                if (_target.ByteOrder == Lua54ByteOrder.LittleEndian)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(bytes, bits);
                }
                else
                {
                    BinaryPrimitives.WriteInt64BigEndian(bytes, bits);
                }
            }

            WriteBytes(bytes[.._target.NumberSize]);
        }

        private void WriteByte(byte value)
        {
            var destination = _output.GetSpan(1);
            destination[0] = value;
            _output.Advance(1);
        }

        private void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(_output.GetSpan(bytes.Length));
            _output.Advance(bytes.Length);
        }
    }
}
