using System.Collections.Immutable;
using Lunil.Core.Text;

namespace Lunil.IR.Canonical;

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
    int SourceIndex)
{
    /// <summary>The PUC descriptor kind used for regular, constant, and to-be-closed captures.</summary>
    public byte Kind { get; init; }

    /// <summary>The original binary debug name when it cannot be represented losslessly as UTF-16.</summary>
    public ImmutableArray<byte> DebugName { get; init; } = [];
}

/// <summary>A debug-local range in canonical program-counter coordinates.</summary>
public sealed record LuaIrLocalVariable(
    ImmutableArray<byte> Name,
    int StartProgramCounter,
    int EndProgramCounter);

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
        TextSpan span = default,
        int sourceLine = 0,
        int logicalProgramCounter = -1)
    {
        Opcode = opcode;
        A = a;
        B = b;
        C = c;
        D = d;
        Span = span;
        SourceLine = sourceLine;
        LogicalProgramCounter = logicalProgramCounter;
    }

    public LuaIrOpcode Opcode { get; init; }

    public int A { get; init; }

    public int B { get; init; }

    public int C { get; init; }

    public int D { get; init; }

    public TextSpan Span { get; init; }

    /// <summary>One-based source line, or zero when line information is unavailable.</summary>
    public int SourceLine { get; init; }

    /// <summary>The producer's logical instruction number, or -1 for source-lowered IR.</summary>
    public int LogicalProgramCounter { get; init; }

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

    public ImmutableArray<byte> SourceName { get; init; } = [];

    public int LineDefined { get; init; }

    public int LastLineDefined { get; init; }

    public int ParameterCount { get; init; }

    public bool IsVarArg { get; init; }

    public int RegisterCount { get; init; }

    public ImmutableArray<LuaIrConstant> Constants { get; init; } = [];

    public ImmutableArray<LuaIrUpvalue> Upvalues { get; init; } = [];

    public ImmutableArray<LuaIrInstruction> Instructions { get; init; } = [];

    public ImmutableArray<LuaIrLocalVariable> LocalVariables { get; init; } = [];

    public ImmutableArray<LuaIrBasicBlock> BasicBlocks { get; init; } = [];
}

public sealed record LuaIrModule
{
    public const int CurrentFormatVersion = 3;

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
