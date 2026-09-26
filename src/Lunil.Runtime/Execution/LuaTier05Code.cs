using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Lunil.IR.Canonical;

namespace Lunil.Runtime.Execution;

internal enum LuaTier05Opcode : byte
{
    Fallback = 0,
    LoadConstInt = 1,
    LoadConstIntWide = 2,
    LoadConstFloat = 3,
    LoadConst = 4,
    LoadNilOne = 5,
    LoadNil = 6,
    Move = 7,
    SetTop = 8,
    GetUpvalue = 9,
    SetUpvalue = 10,
    Jump = 11,
    JumpIfFalse = 12,
    JumpIfTrue = 13,
    NumericForLoop = 14,
    GetTable = 15,
    SetTable = 16,
    Negate = 17,
    BitwiseNot = 18,
    LogicalNot = 19,
    Length = 20,
    BinaryAdd = 21,
    BinarySubtract = 22,
    BinaryMultiply = 23,
    BinaryDivide = 24,
    BinaryFloorDivide = 25,
    BinaryModulo = 26,
    BinaryPower = 27,
    BinaryConcatenate = 28,
    BinaryEqual = 29,
    BinaryNotEqual = 30,
    BinaryLessThan = 31,
    BinaryLessThanOrEqual = 32,
    BinaryGreaterThan = 33,
    BinaryGreaterThanOrEqual = 34,
    BinaryBitwiseAnd = 35,
    BinaryBitwiseOr = 36,
    BinaryBitwiseXor = 37,
    BinaryShiftLeft = 38,
    BinaryShiftRight = 39,
}

internal sealed class LuaTier05Code
{
    private const byte BinaryOpcodeBase = (byte)LuaTier05Opcode.BinaryAdd;
    private const byte UnaryOpcodeBase = (byte)LuaTier05Opcode.Negate;

    private LuaTier05Code(byte[] stream, int[] canonicalToOffset, bool hasFastInstructions)
    {
        Stream = stream;
        CanonicalToOffset = canonicalToOffset;
        HasFastInstructions = hasFastInstructions;
    }

    public byte[] Stream { get; }

    public int[] CanonicalToOffset { get; }

    public bool HasFastInstructions { get; }

    public static LuaTier05Code Encode(LuaIrFunction function)
    {
        var instructions = ImmutableCollectionsMarshal.AsArray(function.Instructions)!;
        var offsets = new int[instructions.Length];
        var patches = new List<int>();
        var patchTargets = new List<int>();
        var stream = new MemoryStream(instructions.Length * 4 + 16);
        var compactRegisters = function.RegisterCount <= byte.MaxValue;
        var compactConstants = function.Constants.Length <= byte.MaxValue;
        var compactUpvalues = function.Upvalues.Length <= byte.MaxValue;
        var fastInstructions = 0;
        for (var index = 0; index < instructions.Length; index++)
        {
            offsets[index] = (int)stream.Position;
            if (EncodeInstruction(
                    function,
                    instructions[index],
                    compactRegisters,
                    compactConstants,
                    compactUpvalues,
                    stream,
                    patches,
                    patchTargets))
            {
                fastInstructions++;
            }
            else
            {
                stream.WriteByte((byte)LuaTier05Opcode.Fallback);
            }
        }

        var streamBytes = stream.ToArray();
        for (var index = 0; index < patches.Count; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                streamBytes.AsSpan(patches[index], 4),
                patchTargets[index]);
        }

        return new LuaTier05Code(streamBytes, offsets, fastInstructions != 0);
    }

    private static bool EncodeInstruction(
        LuaIrFunction function,
        LuaIrInstruction instruction,
        bool compactRegisters,
        bool compactConstants,
        bool compactUpvalues,
        MemoryStream stream,
        List<int> patches,
        List<int> patchTargets)
    {
        switch (instruction.Opcode)
        {
            case LuaIrOpcode.LoadConstant:
                if (!compactRegisters || instruction.A > byte.MaxValue ||
                    !compactConstants || instruction.B > byte.MaxValue)
                {
                    return false;
                }

                var constant = function.Constants[instruction.B];
                if (constant.Kind == LuaIrConstantKind.Integer)
                {
                    if (constant.Integer is >= int.MinValue and <= int.MaxValue)
                    {
                        stream.WriteByte((byte)LuaTier05Opcode.LoadConstInt);
                        stream.WriteByte((byte)instruction.A);
                        WriteInt32(stream, (int)constant.Integer);
                    }
                    else
                    {
                        stream.WriteByte((byte)LuaTier05Opcode.LoadConstIntWide);
                        stream.WriteByte((byte)instruction.A);
                        WriteInt64(stream, constant.Integer);
                    }
                }
                else if (constant.Kind == LuaIrConstantKind.Float)
                {
                    stream.WriteByte((byte)LuaTier05Opcode.LoadConstFloat);
                    stream.WriteByte((byte)instruction.A);
                    WriteInt64(stream, BitConverter.DoubleToInt64Bits(constant.Float));
                }
                else
                {
                    stream.WriteByte((byte)LuaTier05Opcode.LoadConst);
                    stream.WriteByte((byte)instruction.A);
                    stream.WriteByte((byte)instruction.B);
                }

                return true;
            case LuaIrOpcode.LoadNil:
                if (!compactRegisters || instruction.A > byte.MaxValue)
                {
                    return false;
                }

                if (instruction.B == 1)
                {
                    stream.WriteByte((byte)LuaTier05Opcode.LoadNilOne);
                    stream.WriteByte((byte)instruction.A);
                }
                else if (instruction.B is >= 0 and <= byte.MaxValue)
                {
                    stream.WriteByte((byte)LuaTier05Opcode.LoadNil);
                    stream.WriteByte((byte)instruction.A);
                    stream.WriteByte((byte)instruction.B);
                }
                else
                {
                    return false;
                }

                return true;
            case LuaIrOpcode.Move:
                if (!compactRegisters || !Fits(instruction.A) || !Fits(instruction.B))
                {
                    return false;
                }

                stream.WriteByte((byte)LuaTier05Opcode.Move);
                stream.WriteByte((byte)instruction.A);
                stream.WriteByte((byte)instruction.B);
                return true;
            case LuaIrOpcode.SetTop:
                if (!compactRegisters || instruction.A > byte.MaxValue)
                {
                    return false;
                }

                stream.WriteByte((byte)LuaTier05Opcode.SetTop);
                stream.WriteByte((byte)instruction.A);
                return true;
            case LuaIrOpcode.GetUpvalue:
                if (!compactRegisters || !Fits(instruction.A) ||
                    !compactUpvalues || instruction.B > byte.MaxValue)
                {
                    return false;
                }

                stream.WriteByte((byte)LuaTier05Opcode.GetUpvalue);
                stream.WriteByte((byte)instruction.A);
                stream.WriteByte((byte)instruction.B);
                return true;
            case LuaIrOpcode.SetUpvalue:
                if (!compactRegisters || !Fits(instruction.B) ||
                    !compactUpvalues || instruction.A > byte.MaxValue)
                {
                    return false;
                }

                stream.WriteByte((byte)LuaTier05Opcode.SetUpvalue);
                stream.WriteByte((byte)instruction.A);
                stream.WriteByte((byte)instruction.B);
                return true;
            case LuaIrOpcode.Jump:
                if (instruction.C >= 0)
                {
                    return false;
                }

                stream.WriteByte((byte)LuaTier05Opcode.Jump);
                AddPatch(stream, patches, patchTargets, instruction.B);
                return true;
            case LuaIrOpcode.JumpIfFalse:
            case LuaIrOpcode.JumpIfTrue:
                if (!compactRegisters || !Fits(instruction.A) ||
                    instruction.C > byte.MaxValue || instruction.D is < 0 or > 1)
                {
                    return false;
                }

                stream.WriteByte(instruction.Opcode == LuaIrOpcode.JumpIfFalse
                    ? (byte)LuaTier05Opcode.JumpIfFalse
                    : (byte)LuaTier05Opcode.JumpIfTrue);
                stream.WriteByte((byte)instruction.A);
                stream.WriteByte((byte)instruction.C);
                stream.WriteByte((byte)instruction.D);
                AddPatch(stream, patches, patchTargets, instruction.B);
                return true;
            case LuaIrOpcode.NumericForLoop:
                if (!compactRegisters || !Fits(instruction.A + 3))
                {
                    return false;
                }

                stream.WriteByte((byte)LuaTier05Opcode.NumericForLoop);
                stream.WriteByte((byte)instruction.A);
                AddPatch(stream, patches, patchTargets, instruction.B);
                return true;
            case LuaIrOpcode.GetTable:
            case LuaIrOpcode.SetTable:
                if (!compactRegisters ||
                    !Fits(instruction.A) || !Fits(instruction.B) || !Fits(instruction.C))
                {
                    return false;
                }

                stream.WriteByte(instruction.Opcode == LuaIrOpcode.GetTable
                    ? (byte)LuaTier05Opcode.GetTable
                    : (byte)LuaTier05Opcode.SetTable);
                stream.WriteByte((byte)instruction.A);
                stream.WriteByte((byte)instruction.B);
                stream.WriteByte((byte)instruction.C);
                return true;
            case LuaIrOpcode.Unary:
                if (!compactRegisters || !Fits(instruction.A) || !Fits(instruction.B))
                {
                    return false;
                }

                stream.WriteByte((byte)(UnaryOpcodeBase + (byte)instruction.C));
                stream.WriteByte((byte)instruction.A);
                stream.WriteByte((byte)instruction.B);
                return true;
            case LuaIrOpcode.Binary:
                if (!compactRegisters ||
                    !Fits(instruction.A) || !Fits(instruction.B) || !Fits(instruction.C))
                {
                    return false;
                }

                if ((LuaIrBinaryOperator)instruction.D == LuaIrBinaryOperator.Concatenate)
                {
                    return false;
                }

                stream.WriteByte((byte)(BinaryOpcodeBase + (byte)instruction.D));
                stream.WriteByte((byte)instruction.A);
                stream.WriteByte((byte)instruction.B);
                stream.WriteByte((byte)instruction.C);
                return true;
            default:
                return false;
        }

        bool Fits(int value) => (uint)value <= byte.MaxValue;
    }

    private static void AddPatch(
        MemoryStream stream,
        List<int> patches,
        List<int> patchTargets,
        int target)
    {
        patches.Add((int)stream.Position);
        patchTargets.Add(target);
        WriteInt32(stream, 0);
    }

    private static void WriteInt32(MemoryStream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt64(MemoryStream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }
}
