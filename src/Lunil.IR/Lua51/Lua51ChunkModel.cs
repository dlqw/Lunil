using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Collections.Generic;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua53;

#pragma warning disable CA1720

namespace Lunil.IR.Lua51;

public enum Lua51ByteOrder : byte { LittleEndian, BigEndian }

public readonly record struct Lua51ChunkTarget(
    Lua51ByteOrder ByteOrder, int SizeOfInt, int SizeOfSizeT, int InstructionSize, int NumberSize)
{
    public static Lua51ChunkTarget Host { get; } = new(
        BitConverter.IsLittleEndian ? Lua51ByteOrder.LittleEndian : Lua51ByteOrder.BigEndian,
        4, IntPtr.Size, 4, 8);
}

public sealed record Lua51Chunk(Lua51ChunkTarget Target, Lua51Prototype MainPrototype);

public sealed record Lua51Prototype
{
    public Lua51String? Source { get; init; }
    public int LineDefined { get; init; }
    public int LastLineDefined { get; init; }
    public byte UpvalueCount { get; init; }
    public byte ParameterCount { get; init; }
    public byte VarArgFlags { get; init; }
    public byte MaximumStackSize { get; init; }
    public ImmutableArray<Lua51Instruction> Code { get; init; }
    public ImmutableArray<Lua51Constant> Constants { get; init; }
    public ImmutableArray<Lua51Prototype> NestedPrototypes { get; init; }
    public ImmutableArray<int> LineInfo { get; init; }
    public ImmutableArray<Lua51LocalVariable> LocalVariables { get; init; }
    public ImmutableArray<Lua51String?> UpvalueNames { get; init; }
}

public sealed record Lua51LocalVariable(Lua51String? Name, int StartProgramCounter, int EndProgramCounter);

public readonly record struct Lua51String(byte[] Bytes)
{
    public int Length => Bytes.Length;
    public byte[] ToArray() => [.. Bytes];
    public ReadOnlySpan<byte> AsSpan() => Bytes;
    public override string ToString() => System.Text.Encoding.UTF8.GetString(Bytes);
}

public enum Lua51ConstantKind : byte { Nil, False, True, Number, String }

public sealed record Lua51Constant
{
    public Lua51ConstantKind Kind { get; init; }
    public double NumberValue { get; init; }
    public Lua51String? StringValue { get; init; }
    public static Lua51Constant Nil { get; } = new() { Kind = Lua51ConstantKind.Nil };
    public static Lua51Constant False { get; } = new() { Kind = Lua51ConstantKind.False };
    public static Lua51Constant True { get; } = new() { Kind = Lua51ConstantKind.True };
    public static Lua51Constant FromBoolean(bool value) => value ? True : False;
    public static Lua51Constant FromNumber(double value) => new() { Kind = Lua51ConstantKind.Number, NumberValue = value };
    public static Lua51Constant FromString(Lua51String value) => new() { Kind = Lua51ConstantKind.String, StringValue = value };
}

/// <summary>Lua 5.1's six-bit opcode and 8/9/9 operand encoding.</summary>
public enum Lua51Opcode : byte
{
    Move, LoadConstant, LoadBoolean, LoadNil, GetUpvalue, GetGlobal, GetTable, SetGlobal,
    SetUpvalue, SetTable, NewTable, Self, Add, Subtract, Multiply, Divide, Modulo, Power,
    UnaryMinus, LogicalNot, Length, Concatenate, Jump, Equal, LessThan, LessOrEqual, Test,
    TestSet, Call, TailCall, Return, NumericForLoop, NumericForPrepare, GenericForLoop,
    SetList, Close, Closure, VarArg,
}

public readonly record struct Lua51Instruction(uint RawValue)
{
    public const int MaximumA = Lua51GeneratedInstructionCodec.MaximumA;
    public const int MaximumB = Lua51GeneratedInstructionCodec.MaximumB;
    public const int MaximumC = Lua51GeneratedInstructionCodec.MaximumC;
    public const int MaximumBx = Lua51GeneratedInstructionCodec.MaximumBx;
    public const int SignedBxOffset = Lua51GeneratedInstructionCodec.SignedBxOffset;
    public Lua51Opcode Opcode => Lua51GeneratedInstructionCodec.DecodeOpcode(RawValue);
    public int A => Lua51GeneratedInstructionCodec.DecodeA(RawValue);
    public int C => Lua51GeneratedInstructionCodec.DecodeC(RawValue);
    public int B => Lua51GeneratedInstructionCodec.DecodeB(RawValue);
    public int Bx => Lua51GeneratedInstructionCodec.DecodeBx(RawValue);
    public int SignedBx => Bx - SignedBxOffset;
    public bool IsConstantB => B >= 1 << 8;
    public bool IsConstantC => C >= 1 << 8;
    public static Lua51Instruction CreateAbc(Lua51Opcode opcode, int a, int b, int c) =>
        new(Lua51GeneratedInstructionCodec.EncodeAbc(opcode, Checked(a, MaximumA), Checked(b, MaximumB), Checked(c, MaximumC)));
    public static Lua51Instruction CreateABx(Lua51Opcode opcode, int a, int bx) =>
        new(Lua51GeneratedInstructionCodec.EncodeABx(opcode, Checked(a, MaximumA), Checked(bx, MaximumBx)));
    public static Lua51Instruction CreateASignedBx(Lua51Opcode opcode, int a, int sbx) =>
        CreateABx(opcode, a, checked(sbx + SignedBxOffset));
    private static int Checked(int value, int maximum) => (uint)value <= (uint)maximum ? value :
        throw new ArgumentOutOfRangeException(nameof(value));
}
