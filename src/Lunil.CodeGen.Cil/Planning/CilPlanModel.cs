using System.Collections.Immutable;

namespace Lunil.CodeGen.Cil.Planning;

#pragma warning disable CA1720 // These names are exact ECMA-335 evaluation-stack categories.
public enum CilStackValueKind : byte
{
    Void,
    Int32,
    Int64,
    Float,
    Object,
    LuaValue,
    ExecutionContext,
    Thread,
    Frame,
    CompiledExit,
}
#pragma warning restore CA1720

public enum CilPlanOpCode : byte
{
    MarkLabel,
    Nop,
    LoadArgument,
    LoadLocal,
    StoreLocal,
    LoadInt32,
    LoadInt64,
    ConvertInt64,
    Add,
    Subtract,
    Call,
    Branch,
    BranchTrue,
    BranchFalse,
    Switch,
    Return,
}

public readonly record struct CilLabel(int Id);

public sealed record CilCallTarget(
    string Id,
    ImmutableArray<CilStackValueKind> ParameterKinds,
    CilStackValueKind ReturnKind,
    bool IsGcSafePoint = false);

public sealed record CilLocal(int Index, CilStackValueKind Kind, string Name);

public readonly record struct CilPlanInstruction
{
    private CilPlanInstruction(
        CilPlanOpCode opCode,
        int int32Operand,
        long int64Operand,
        CilLabel label,
        ImmutableArray<CilLabel> labels,
        CilCallTarget? callTarget,
        int canonicalProgramCounter)
    {
        OpCode = opCode;
        Int32Operand = int32Operand;
        Int64Operand = int64Operand;
        Label = label;
        Labels = labels;
        CallTarget = callTarget;
        CanonicalProgramCounter = canonicalProgramCounter;
    }

    public CilPlanOpCode OpCode { get; }

    public int Int32Operand { get; }

    public long Int64Operand { get; }

    public CilLabel Label { get; }

    public ImmutableArray<CilLabel> Labels { get; }

    public CilCallTarget? CallTarget { get; }

    public int CanonicalProgramCounter { get; }

    public static CilPlanInstruction MarkLabel(CilLabel label, int canonicalProgramCounter = -1) =>
        new(CilPlanOpCode.MarkLabel, 0, 0, label, [], null, canonicalProgramCounter);

    public static CilPlanInstruction Simple(
        CilPlanOpCode opCode,
        int canonicalProgramCounter = -1) =>
        new(opCode, 0, 0, default, [], null, canonicalProgramCounter);

    public static CilPlanInstruction WithInt32(
        CilPlanOpCode opCode,
        int operand,
        int canonicalProgramCounter = -1) =>
        new(opCode, operand, 0, default, [], null, canonicalProgramCounter);

    public static CilPlanInstruction WithInt64(
        CilPlanOpCode opCode,
        long operand,
        int canonicalProgramCounter = -1) =>
        new(opCode, 0, operand, default, [], null, canonicalProgramCounter);

    public static CilPlanInstruction WithLabel(
        CilPlanOpCode opCode,
        CilLabel label,
        int canonicalProgramCounter = -1) =>
        new(opCode, 0, 0, label, [], null, canonicalProgramCounter);

    public static CilPlanInstruction Switch(
        ImmutableArray<CilLabel> labels,
        int canonicalProgramCounter = -1) =>
        new(CilPlanOpCode.Switch, 0, 0, default, labels, null, canonicalProgramCounter);

    public static CilPlanInstruction Call(
        CilCallTarget target,
        int canonicalProgramCounter = -1) =>
        new(CilPlanOpCode.Call, 0, 0, default, [], target, canonicalProgramCounter);
}

public sealed record CilGcMap(
    int CanonicalProgramCounter,
    ImmutableArray<int> LiveRegisters);

public sealed record CilSequencePoint(
    int PlanInstructionIndex,
    int CanonicalProgramCounter,
    int SourceLine,
    int LogicalProgramCounter = -1);

public sealed record CilCanonicalBlock(
    int StartProgramCounter,
    int Length,
    ImmutableArray<int> Successors);

public sealed record CilMethodPlan
{
    public required string Name { get; init; }

    public required int FunctionId { get; init; }

    public int CanonicalInstructionCount { get; init; }

    public int StartProgramCounter { get; init; }

    public int RegisterCount { get; init; }

    /// <summary>
    /// Number of canonical instructions whose semantics are emitted directly into this plan.
    /// This is structural coverage metadata and does not include runtime guard outcomes.
    /// </summary>
    public int DirectCanonicalInstructionCount { get; init; }

    /// <summary>
    /// Number of canonical instructions that return through the shared interpreter slow path.
    /// </summary>
    public int SlowPathCanonicalInstructionCount { get; init; }

    public ImmutableArray<int> DirectCanonicalProgramCounters { get; init; } = [];

    public ImmutableArray<int> SlowPathCanonicalProgramCounters { get; init; } = [];

    public ImmutableArray<CilStackValueKind> ParameterKinds { get; init; } = [];

    public CilStackValueKind ReturnKind { get; init; } = CilStackValueKind.Void;

    public ImmutableArray<CilLocal> Locals { get; init; } = [];

    public ImmutableArray<CilPlanInstruction> Instructions { get; init; } = [];

    public ImmutableArray<CilGcMap> GcMaps { get; init; } = [];

    public ImmutableArray<CilSequencePoint> SequencePoints { get; init; } = [];

    public ImmutableArray<CilCanonicalBlock> Blocks { get; init; } = [];
}

public sealed record CilPlanLimits
{
    public static CilPlanLimits Default { get; } = new();

    public int MaximumInstructions { get; init; } = 1_000_000;

    public int MaximumLabels { get; init; } = 250_000;

    public int MaximumEvaluationStack { get; init; } = 1_024;

    public int MaximumBranchInstructions { get; init; } = 250_000;

    public int MaximumMetadataReferences { get; init; } = 100_000;
}

public sealed record CilPlanDiagnostic(
    string Code,
    string Message,
    int InstructionIndex = -1,
    int CanonicalProgramCounter = -1);

public sealed record CilPlanVerificationResult(
    ImmutableArray<CilPlanDiagnostic> Diagnostics,
    int MaximumEvaluationStack)
{
    public bool Succeeded => Diagnostics.IsEmpty;
}
