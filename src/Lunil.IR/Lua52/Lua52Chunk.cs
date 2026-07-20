using System.Collections.Immutable;

#pragma warning disable CA1720 // Lua's constant tags intentionally use the public term String.

namespace Lunil.IR.Lua52;

public enum Lua52ByteOrder : byte
{
    LittleEndian,
    BigEndian,
}

public readonly record struct Lua52ChunkTarget(
    Lua52ByteOrder ByteOrder,
    int SizeOfInt,
    int SizeOfSizeT,
    int InstructionSize,
    int NumberSize)
{
    public static Lua52ChunkTarget Host { get; } = new(
        BitConverter.IsLittleEndian ? Lua52ByteOrder.LittleEndian : Lua52ByteOrder.BigEndian,
        4,
        IntPtr.Size,
        4,
        8);
}

public sealed record Lua52Chunk(
    Lua52ChunkTarget Target,
    Lua52Prototype MainPrototype);

public sealed record Lua52Prototype
{
    public Lua52String? Source { get; init; }
    public int LineDefined { get; init; }
    public int LastLineDefined { get; init; }
    public byte ParameterCount { get; init; }
    public byte VarArgFlags { get; init; }
    public byte MaximumStackSize { get; init; }
    public ImmutableArray<Lua52Instruction> Code { get; init; }
    public ImmutableArray<Lua52Constant> Constants { get; init; }
    public ImmutableArray<Lua52UpvalueDescriptor> Upvalues { get; init; }
    public ImmutableArray<Lua52Prototype> NestedPrototypes { get; init; }
    public ImmutableArray<int> LineInfo { get; init; }
    public ImmutableArray<Lua52LocalVariable> LocalVariables { get; init; }
    public ImmutableArray<Lua52String?> UpvalueNames { get; init; }
}

public readonly record struct Lua52UpvalueDescriptor(byte InStack, byte Index);

public sealed record Lua52LocalVariable(
    Lua52String? Name,
    int StartProgramCounter,
    int EndProgramCounter);

public readonly record struct Lua52String(byte[] Bytes)
{
    public int Length => Bytes.Length;
    public ReadOnlySpan<byte> AsSpan() => Bytes;
    public byte[] ToArray() => [.. Bytes];
    public override string ToString() => System.Text.Encoding.UTF8.GetString(Bytes);
}

public enum Lua52ConstantKind : byte
{
    Nil,
    False,
    True,
    Number,
    String,
}

public sealed record Lua52Constant
{
    public Lua52ConstantKind Kind { get; init; }
    public double NumberValue { get; init; }
    public Lua52String? StringValue { get; init; }

    public static Lua52Constant Nil { get; } = new() { Kind = Lua52ConstantKind.Nil };
    public static Lua52Constant False { get; } = new() { Kind = Lua52ConstantKind.False };
    public static Lua52Constant True { get; } = new() { Kind = Lua52ConstantKind.True };
    public static Lua52Constant FromBoolean(bool value) => value ? True : False;
    public static Lua52Constant FromNumber(double value) => new()
    {
        Kind = Lua52ConstantKind.Number,
        NumberValue = value,
    };
    public static Lua52Constant FromString(Lua52String value) => new()
    {
        Kind = Lua52ConstantKind.String,
        StringValue = value,
    };
}
