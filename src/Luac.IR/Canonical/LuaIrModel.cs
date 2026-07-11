using System.Collections.Immutable;
using Luac.Core.Text;

namespace Luac.IR.Canonical;

#pragma warning disable CA1720 // Integer, float, and string are exact Lua domain terms.
public enum LuaIrConstantKind : byte
{
    Nil,
    Boolean,
    Integer,
    Float,
    String,
}

/// <summary>A binary-safe immutable canonical constant.</summary>
public readonly record struct LuaIrConstant
{
    private LuaIrConstant(
        LuaIrConstantKind kind,
        long integer,
        double floatingPoint,
        ImmutableArray<byte> bytes)
    {
        Kind = kind;
        Integer = integer;
        Float = floatingPoint;
        Bytes = bytes;
    }

    public LuaIrConstantKind Kind { get; }

    public long Integer { get; }

    public double Float { get; }

    public ImmutableArray<byte> Bytes { get; }

    public bool Boolean => Kind == LuaIrConstantKind.Boolean
        ? Integer != 0
        : throw new InvalidOperationException("The constant is not a boolean.");

    public static LuaIrConstant Nil { get; } = new(LuaIrConstantKind.Nil, 0, 0, []);

    public static LuaIrConstant FromBoolean(bool value) =>
        new(LuaIrConstantKind.Boolean, value ? 1 : 0, 0, []);

    public static LuaIrConstant FromInteger(long value) =>
        new(LuaIrConstantKind.Integer, value, 0, []);

    public static LuaIrConstant FromFloat(double value) =>
        new(LuaIrConstantKind.Float, 0, value, []);

    public static LuaIrConstant FromString(ReadOnlySpan<byte> value) =>
        new(LuaIrConstantKind.String, 0, 0, [.. value]);
}
#pragma warning restore CA1720

public enum LuaIrUpvalueSourceKind : byte
{
    Register,
    Upvalue,
    Environment,
}

public sealed record LuaIrUpvalue(
    string Name,
    int SymbolId,
    LuaIrUpvalueSourceKind SourceKind,
    int SourceIndex);

/// <summary>
/// Four integer operands keep decoding branch-free. Their meaning is defined by the opcode.
/// Every instruction carries its original byte span for tracebacks and later PDB emission.
/// </summary>
public readonly record struct LuaIrInstruction
{
    public LuaIrInstruction(
        LuaIrOpcode opcode,
        int a = 0,
        int b = 0,
        int c = 0,
        int d = 0,
        TextSpan span = default)
    {
        Opcode = opcode;
        A = a;
        B = b;
        C = c;
        D = d;
        Span = span;
    }

    public LuaIrOpcode Opcode { get; init; }

    public int A { get; init; }

    public int B { get; init; }

    public int C { get; init; }

    public int D { get; init; }

    public TextSpan Span { get; init; }

    public LuaIrInstructionEffects Effects => LuaIrInstructionFacts.GetEffects(Opcode);
}

public readonly record struct LuaIrBasicBlock(
    int Start,
    int Length,
    ImmutableArray<int> Successors)
{
    public int End => checked(Start + Length);
}

public sealed record LuaIrFunction
{
    public required int Id { get; init; }

    public int ParentFunctionId { get; init; } = -1;

    public required TextSpan Span { get; init; }

    public int ParameterCount { get; init; }

    public bool IsVarArg { get; init; }

    public int RegisterCount { get; init; }

    public ImmutableArray<LuaIrConstant> Constants { get; init; } = [];

    public ImmutableArray<LuaIrUpvalue> Upvalues { get; init; } = [];

    public ImmutableArray<LuaIrInstruction> Instructions { get; init; } = [];

    public ImmutableArray<LuaIrBasicBlock> BasicBlocks { get; init; } = [];
}

public sealed record LuaIrModule
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public int MainFunctionId { get; init; }

    public ImmutableArray<LuaIrFunction> Functions { get; init; } = [];
}

public static class LuaIrInstructionFacts
{
    public static LuaIrInstructionEffects GetEffects(LuaIrOpcode opcode) => opcode switch
    {
        LuaIrOpcode.NewTable or LuaIrOpcode.Closure =>
            LuaIrInstructionEffects.MayAllocate | LuaIrInstructionEffects.IsGcSafePoint,
        LuaIrOpcode.GetTable or LuaIrOpcode.SetTable or LuaIrOpcode.SetList or
        LuaIrOpcode.Unary or LuaIrOpcode.Binary or LuaIrOpcode.MarkToBeClosed or
        LuaIrOpcode.Close =>
            LuaIrInstructionEffects.MayCall | LuaIrInstructionEffects.MayThrow |
            LuaIrInstructionEffects.IsGcSafePoint,
        LuaIrOpcode.Call or LuaIrOpcode.TailCall =>
            LuaIrInstructionEffects.MayAllocate | LuaIrInstructionEffects.MayCall |
            LuaIrInstructionEffects.MayYield | LuaIrInstructionEffects.MayThrow |
            LuaIrInstructionEffects.IsGcSafePoint,
        LuaIrOpcode.NumericForPrepare or LuaIrOpcode.NumericForLoop =>
            LuaIrInstructionEffects.MayThrow,
        _ => LuaIrInstructionEffects.None,
    };
}
