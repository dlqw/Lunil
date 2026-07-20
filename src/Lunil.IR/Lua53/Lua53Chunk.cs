using System.Collections.Immutable;

#pragma warning disable CA1720 // Integer and float are exact Lua constant kinds.

namespace Lunil.IR.Lua53;

public sealed record Lua53Chunk(
    Lua53ChunkTarget Target,
    byte MainUpvalueCount,
    Lua53Prototype MainPrototype);

public sealed record Lua53Prototype
{
    public Lua53String? Source { get; init; }
    public int LineDefined { get; init; }
    public int LastLineDefined { get; init; }
    public byte ParameterCount { get; init; }
    public byte VarArgFlags { get; init; }
    public byte MaximumStackSize { get; init; }
    public ImmutableArray<Lua53Instruction> Code { get; init; }
    public ImmutableArray<Lua53Constant> Constants { get; init; }
    public ImmutableArray<Lua53UpvalueDescriptor> Upvalues { get; init; }
    public ImmutableArray<Lua53Prototype> NestedPrototypes { get; init; }
    public ImmutableArray<int> LineInfo { get; init; }
    public ImmutableArray<Lua53LocalVariable> LocalVariables { get; init; }
    public ImmutableArray<Lua53String?> UpvalueNames { get; init; }
}

public readonly record struct Lua53UpvalueDescriptor(byte InStack, byte Index);

public sealed record Lua53LocalVariable(
    Lua53String? Name,
    int StartProgramCounter,
    int EndProgramCounter);

public readonly record struct Lua53String(byte[] Bytes)
{
    public int Length => Bytes.Length;

    public ReadOnlySpan<byte> AsSpan() => Bytes;

    public byte[] ToArray() => [.. Bytes];

    public override string ToString() => System.Text.Encoding.UTF8.GetString(Bytes);
}

public enum Lua53ConstantKind : byte
{
    Nil,
    False,
    True,
    Integer,
    Float,
    ShortString,
    LongString,
}

public sealed record Lua53Constant
{
    public Lua53ConstantKind Kind { get; init; }
    public long IntegerValue { get; init; }
    public double FloatValue { get; init; }
    public Lua53String? StringValue { get; init; }

    public static Lua53Constant Nil { get; } = new() { Kind = Lua53ConstantKind.Nil };
    public static Lua53Constant False { get; } = new() { Kind = Lua53ConstantKind.False };
    public static Lua53Constant True { get; } = new() { Kind = Lua53ConstantKind.True };
    public static Lua53Constant FromBoolean(bool value) => value ? True : False;

    public static Lua53Constant FromInteger(long value) => new()
    {
        Kind = Lua53ConstantKind.Integer,
        IntegerValue = value,
    };
    public static Lua53Constant FromFloat(double value) => new()
    {
        Kind = Lua53ConstantKind.Float,
        FloatValue = value,
    };
    public static Lua53Constant FromString(Lua53String value, bool isShort) => new()
    {
        Kind = isShort ? Lua53ConstantKind.ShortString : Lua53ConstantKind.LongString,
        StringValue = value,
    };
}
