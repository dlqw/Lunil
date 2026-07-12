using System.Collections.Immutable;
using Lunil.CodeGen.Cil.Analysis;
using Lunil.CodeGen.Cil.Planning;
using Lunil.CodeGen.Cil.Verification;
using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;

namespace Lunil.CodeGen.Cil;

public sealed record LuaCilPlanningResult(
    CilMethodPlan? Plan,
    CilPlanVerificationResult? Verification,
    ImmutableArray<CilPlanDiagnostic> Diagnostics)
{
    public bool Succeeded => Plan is not null && Verification is { Succeeded: true } &&
        Diagnostics.IsEmpty;
}

public static class LuaCilCodeGenerator
{
    public static LuaCilPlanningResult PlanFunction(
        LuaIrModule module,
        int functionId,
        CilPlanLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        var irErrors = LuaIrVerifier.Verify(module);
        if (!irErrors.IsEmpty)
        {
            return new LuaCilPlanningResult(
                null,
                null,
                [.. irErrors.Select(error => new CilPlanDiagnostic("CIL1001", error.Message))]);
        }

        if (functionId < 0 || functionId >= module.Functions.Length)
        {
            return new LuaCilPlanningResult(
                null,
                null,
                [new CilPlanDiagnostic("CIL1002", "Function id is outside the verified module.")]);
        }

        limits ??= CilPlanLimits.Default;
        var function = module.Functions[functionId];
        if (function.Instructions.Length > limits.MaximumInstructions)
        {
            return new LuaCilPlanningResult(
                null,
                null,
                [new CilPlanDiagnostic(
                    "CIL0001",
                    $"Canonical function contains {function.Instructions.Length} instructions; " +
                    $"plan limit is {limits.MaximumInstructions}.")]);
        }

        var plan = LuaCilMethodPlanner.Build(module, function);
        var verification = CilMethodPlanVerifier.Verify(plan, limits);
        return new LuaCilPlanningResult(plan, verification, verification.Diagnostics);
    }
}

internal static class LuaCilMethodPlanner
{
    private const int ContextArgument = 0;
    private const int ThreadArgument = 1;
    private const int FrameArgument = 2;
    private const int ConsumedLocal = 0;
    private const int ProgramCounterLocal = 1;

    public static CilMethodPlan Build(LuaIrModule module, LuaIrFunction function)
    {
        var blockLayout = CilBlockLayout.Build(function);
        var liveness = LuaRegisterLiveness.Analyze(module, function);
        var instructions = ImmutableArray.CreateBuilder<CilPlanInstruction>();
        var sequencePoints = ImmutableArray.CreateBuilder<CilSequencePoint>();
        var pcLabels = Enumerable.Range(0, function.Instructions.Length)
            .Select(static pc => new CilLabel(pc + 1))
            .ToImmutableArray();
        var nextLabel = function.Instructions.Length + 1;

        Emit(instructions, CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 0));
        Emit(instructions, CilPlanInstruction.WithInt32(CilPlanOpCode.StoreLocal, ConsumedLocal));
        Emit(instructions, CilPlanInstruction.WithInt32(CilPlanOpCode.LoadArgument, FrameArgument));
        Emit(instructions, CilPlanInstruction.Call(CilWellKnownCalls.FrameGetProgramCounter));
        Emit(instructions, CilPlanInstruction.WithInt32(CilPlanOpCode.StoreLocal, ProgramCounterLocal));
        Emit(instructions, CilPlanInstruction.WithInt32(CilPlanOpCode.LoadLocal, ProgramCounterLocal));
        Emit(instructions, CilPlanInstruction.Switch(pcLabels));
        EmitDeoptFromLocal(
            instructions,
            ProgramCounterLocal,
            ConsumedLocal,
            LuaCompiledExitReason.UnsupportedInstruction);

        for (var pc = 0; pc < function.Instructions.Length; pc++)
        {
            var instruction = function.Instructions[pc];
            Emit(instructions, CilPlanInstruction.MarkLabel(pcLabels[pc], pc));
            sequencePoints.Add(new CilSequencePoint(
                instructions.Count - 1,
                pc,
                instruction.SourceLine));
            if (!IsSupported(instruction))
            {
                EmitDeopt(instructions, pc, ConsumedLocal, LuaCompiledExitReason.UnsupportedInstruction);
                continue;
            }

            EmitCommitProgramCounter(instructions, pc);
            var reserved = new CilLabel(nextLabel++);
            Emit(instructions, CilPlanInstruction.WithInt32(CilPlanOpCode.LoadArgument, ContextArgument, pc));
            Emit(instructions, CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 1, pc));
            Emit(instructions, CilPlanInstruction.Call(
                CilWellKnownCalls.ContextTryReserveInstructions,
                pc));
            Emit(instructions, CilPlanInstruction.WithLabel(CilPlanOpCode.BranchTrue, reserved, pc));
            EmitExit(
                instructions,
                CilWellKnownCalls.ExitPoll,
                pc,
                ConsumedLocal,
                LuaCompiledExitReason.InstructionBudget);
            Emit(instructions, CilPlanInstruction.MarkLabel(reserved, pc));
            Emit(instructions, CilPlanInstruction.WithInt32(CilPlanOpCode.LoadLocal, ConsumedLocal, pc));
            Emit(instructions, CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 1, pc));
            Emit(instructions, CilPlanInstruction.Simple(CilPlanOpCode.Add, pc));
            Emit(instructions, CilPlanInstruction.WithInt32(CilPlanOpCode.StoreLocal, ConsumedLocal, pc));
            LowerInstruction(instructions, function, instruction, pc, pcLabels, ConsumedLocal);
        }

        return new CilMethodPlan
        {
            Name = $"LuaFunction_{function.Id}",
            FunctionId = function.Id,
            CanonicalInstructionCount = function.Instructions.Length,
            RegisterCount = function.RegisterCount,
            ParameterKinds =
            [
                CilStackValueKind.ExecutionContext,
                CilStackValueKind.Thread,
                CilStackValueKind.Frame,
            ],
            ReturnKind = CilStackValueKind.CompiledExit,
            Locals =
            [
                new CilLocal(ConsumedLocal, CilStackValueKind.Int32, "consumed"),
                new CilLocal(ProgramCounterLocal, CilStackValueKind.Int32, "pc"),
            ],
            Instructions = instructions.ToImmutable(),
            GcMaps = liveness.GcMaps,
            SequencePoints = sequencePoints.ToImmutable(),
            Blocks = blockLayout.Blocks,
        };
    }

    private static bool IsSupported(LuaIrInstruction instruction) => instruction.Opcode switch
    {
        LuaIrOpcode.LoadConstant or LuaIrOpcode.LoadNil or LuaIrOpcode.Move or
        LuaIrOpcode.SetTop or LuaIrOpcode.JumpIfFalse or LuaIrOpcode.JumpIfTrue or
        LuaIrOpcode.Return => true,
        LuaIrOpcode.Jump => instruction.C < 0,
        _ => false,
    };

    private static void LowerInstruction(
        ImmutableArray<CilPlanInstruction>.Builder plan,
        LuaIrFunction function,
        LuaIrInstruction instruction,
        int pc,
        ImmutableArray<CilLabel> pcLabels,
        int consumedLocal)
    {
        switch (instruction.Opcode)
        {
            case LuaIrOpcode.LoadConstant:
                LoadArgument(plan, ThreadArgument, pc);
                LoadArgument(plan, FrameArgument, pc);
                LoadInt32(plan, instruction.A, pc);
                LoadArgument(plan, ContextArgument, pc);
                LoadArgument(plan, FrameArgument, pc);
                LoadInt32(plan, instruction.B, pc);
                Emit(plan, CilPlanInstruction.Call(CilWellKnownCalls.MaterializeConstant, pc));
                Emit(plan, CilPlanInstruction.Call(CilWellKnownCalls.WriteRegister, pc));
                BranchToNext(plan, pc, function, pcLabels);
                break;
            case LuaIrOpcode.LoadNil:
                LoadArgument(plan, ThreadArgument, pc);
                LoadArgument(plan, FrameArgument, pc);
                LoadInt32(plan, instruction.A, pc);
                LoadInt32(plan, instruction.B, pc);
                Emit(plan, CilPlanInstruction.Call(CilWellKnownCalls.ClearRegisters, pc));
                BranchToNext(plan, pc, function, pcLabels);
                break;
            case LuaIrOpcode.Move:
                LoadArgument(plan, ThreadArgument, pc);
                LoadArgument(plan, FrameArgument, pc);
                LoadInt32(plan, instruction.A, pc);
                LoadArgument(plan, ThreadArgument, pc);
                LoadArgument(plan, FrameArgument, pc);
                LoadInt32(plan, instruction.B, pc);
                Emit(plan, CilPlanInstruction.Call(CilWellKnownCalls.ReadRegister, pc));
                Emit(plan, CilPlanInstruction.Call(CilWellKnownCalls.WriteRegister, pc));
                BranchToNext(plan, pc, function, pcLabels);
                break;
            case LuaIrOpcode.SetTop:
                LoadArgument(plan, ThreadArgument, pc);
                LoadArgument(plan, FrameArgument, pc);
                LoadInt32(plan, instruction.A, pc);
                Emit(plan, CilPlanInstruction.Call(CilWellKnownCalls.SetFrameTop, pc));
                BranchToNext(plan, pc, function, pcLabels);
                break;
            case LuaIrOpcode.Jump:
                Emit(plan, CilPlanInstruction.WithLabel(CilPlanOpCode.Branch, pcLabels[instruction.B], pc));
                break;
            case LuaIrOpcode.JumpIfFalse:
            case LuaIrOpcode.JumpIfTrue:
                LoadArgument(plan, ThreadArgument, pc);
                LoadArgument(plan, FrameArgument, pc);
                LoadInt32(plan, instruction.A, pc);
                Emit(plan, CilPlanInstruction.Call(CilWellKnownCalls.ReadRegister, pc));
                Emit(plan, CilPlanInstruction.Call(CilWellKnownCalls.LuaValueIsTruthy, pc));
                Emit(plan, CilPlanInstruction.WithLabel(
                    instruction.Opcode == LuaIrOpcode.JumpIfTrue
                        ? CilPlanOpCode.BranchTrue
                        : CilPlanOpCode.BranchFalse,
                    pcLabels[instruction.B],
                    pc));
                BranchToNext(plan, pc, function, pcLabels);
                break;
            case LuaIrOpcode.Return:
                EmitExit(plan, CilWellKnownCalls.ExitReturn, pc, consumedLocal, reason: null);
                break;
            default:
                EmitDeopt(plan, pc, consumedLocal, LuaCompiledExitReason.UnsupportedInstruction);
                break;
        }
    }

    private static void EmitCommitProgramCounter(
        ImmutableArray<CilPlanInstruction>.Builder plan,
        int pc)
    {
        LoadArgument(plan, FrameArgument, pc);
        LoadInt32(plan, pc, pc);
        Emit(plan, CilPlanInstruction.Call(CilWellKnownCalls.CommitProgramCounter, pc));
    }

    private static void BranchToNext(
        ImmutableArray<CilPlanInstruction>.Builder plan,
        int pc,
        LuaIrFunction function,
        ImmutableArray<CilLabel> labels)
    {
        if (pc + 1 >= function.Instructions.Length)
        {
            EmitDeopt(plan, pc, ConsumedLocal, LuaCompiledExitReason.UnsupportedInstruction);
            return;
        }

        Emit(plan, CilPlanInstruction.WithLabel(CilPlanOpCode.Branch, labels[pc + 1], pc));
    }

    private static void EmitDeoptFromLocal(
        ImmutableArray<CilPlanInstruction>.Builder plan,
        int pc,
        int consumedLocal,
        LuaCompiledExitReason reason)
    {
        LoadInt32(plan, pc, pc);
        Emit(plan, CilPlanInstruction.WithInt32(CilPlanOpCode.LoadLocal, consumedLocal, pc));
        LoadInt32(plan, (int)reason, pc);
        Emit(plan, CilPlanInstruction.Call(CilWellKnownCalls.ExitDeopt, pc));
        Emit(plan, CilPlanInstruction.Simple(CilPlanOpCode.Return, pc));
    }

    private static void EmitDeopt(
        ImmutableArray<CilPlanInstruction>.Builder plan,
        int pcLocal,
        int consumedLocal,
        LuaCompiledExitReason reason)
    {
        Emit(plan, CilPlanInstruction.WithInt32(CilPlanOpCode.LoadLocal, pcLocal));
        Emit(plan, CilPlanInstruction.WithInt32(CilPlanOpCode.LoadLocal, consumedLocal));
        LoadInt32(plan, (int)reason, -1);
        Emit(plan, CilPlanInstruction.Call(CilWellKnownCalls.ExitDeopt));
        Emit(plan, CilPlanInstruction.Simple(CilPlanOpCode.Return));
    }

    private static void EmitExit(
        ImmutableArray<CilPlanInstruction>.Builder plan,
        CilCallTarget target,
        int pc,
        int consumedLocal,
        LuaCompiledExitReason? reason)
    {
        LoadInt32(plan, pc, pc);
        Emit(plan, CilPlanInstruction.WithInt32(CilPlanOpCode.LoadLocal, consumedLocal, pc));
        if (reason is not null)
        {
            LoadInt32(plan, (int)reason.Value, pc);
        }

        Emit(plan, CilPlanInstruction.Call(target, pc));
        Emit(plan, CilPlanInstruction.Simple(CilPlanOpCode.Return, pc));
    }

    private static void LoadArgument(
        ImmutableArray<CilPlanInstruction>.Builder plan,
        int argument,
        int pc) =>
        Emit(plan, CilPlanInstruction.WithInt32(CilPlanOpCode.LoadArgument, argument, pc));

    private static void LoadInt32(
        ImmutableArray<CilPlanInstruction>.Builder plan,
        int value,
        int pc) =>
        Emit(plan, CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, value, pc));

    private static void Emit(
        ImmutableArray<CilPlanInstruction>.Builder plan,
        CilPlanInstruction instruction) =>
        plan.Add(instruction);
}
