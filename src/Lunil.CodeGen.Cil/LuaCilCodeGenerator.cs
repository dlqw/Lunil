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

        var plan = LuaCilMethodPlanner.Build(
            function,
            startProgramCounter: 0,
            function.Instructions.Length,
            $"LuaFunction_{function.Id}",
            CilBlockLayout.Build(function),
            LuaRegisterLiveness.Analyze(module, function));
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

    public static CilMethodPlan Build(
        LuaIrFunction function,
        int startProgramCounter,
        int instructionCount,
        string methodName,
        CilBlockLayout blockLayout,
        LuaRegisterLivenessResult liveness)
    {
        var endProgramCounter = checked(startProgramCounter + instructionCount);
        var instructions = ImmutableArray.CreateBuilder<CilPlanInstruction>();
        var sequencePoints = ImmutableArray.CreateBuilder<CilSequencePoint>();
        var pcLabels = Enumerable.Range(0, instructionCount)
            .Select(static index => new CilLabel(index + 1))
            .ToImmutableArray();
        var nextLabel = instructionCount + 1;

        Emit(instructions, CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 0));
        Emit(instructions, CilPlanInstruction.WithInt32(CilPlanOpCode.StoreLocal, ConsumedLocal));
        Emit(instructions, CilPlanInstruction.WithInt32(CilPlanOpCode.LoadArgument, FrameArgument));
        Emit(instructions, CilPlanInstruction.Call(CilWellKnownCalls.FrameGetProgramCounter));
        Emit(instructions, CilPlanInstruction.WithInt32(CilPlanOpCode.StoreLocal, ProgramCounterLocal));
        var backendReady = new CilLabel(nextLabel++);
        Emit(instructions, CilPlanInstruction.WithInt32(CilPlanOpCode.LoadArgument, ContextArgument));
        Emit(instructions, CilPlanInstruction.Call(CilWellKnownCalls.CanExecuteCompiled));
        Emit(instructions, CilPlanInstruction.WithLabel(CilPlanOpCode.BranchTrue, backendReady));
        EmitDeoptFromLocal(
            instructions,
            ProgramCounterLocal,
            ConsumedLocal,
            LuaCompiledExitReason.DebugModeChanged);
        Emit(instructions, CilPlanInstruction.MarkLabel(backendReady));
        Emit(instructions, CilPlanInstruction.WithInt32(CilPlanOpCode.LoadLocal, ProgramCounterLocal));
        Emit(instructions, CilPlanInstruction.WithInt32(
            CilPlanOpCode.LoadInt32,
            startProgramCounter));
        Emit(instructions, CilPlanInstruction.Simple(CilPlanOpCode.Subtract));
        Emit(instructions, CilPlanInstruction.Switch(pcLabels));
        EmitDeoptFromLocal(
            instructions,
            ProgramCounterLocal,
            ConsumedLocal,
            LuaCompiledExitReason.BackendInvalidated);

        for (var pc = startProgramCounter; pc < endProgramCounter; pc++)
        {
            var instruction = function.Instructions[pc];
            Emit(instructions, CilPlanInstruction.MarkLabel(
                pcLabels[pc - startProgramCounter],
                pc));
            sequencePoints.Add(new CilSequencePoint(
                instructions.Count - 1,
                pc,
                instruction.SourceLine,
                instruction.LogicalProgramCounter));
            if (!IsDirectlyLowered(instruction))
            {
                EmitCommitProgramCounter(instructions, pc);
                LoadArgument(instructions, ContextArgument, pc);
                LoadArgument(instructions, ThreadArgument, pc);
                LoadArgument(instructions, FrameArgument, pc);
                LoadInt32(instructions, pc, pc);
                Emit(instructions, CilPlanInstruction.Call(
                    CilWellKnownCalls.ExecuteCanonicalInstruction,
                    pc));
                Emit(instructions, CilPlanInstruction.Simple(CilPlanOpCode.Return, pc));
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
            LowerInstruction(
                instructions,
                function,
                instruction,
                pc,
                startProgramCounter,
                endProgramCounter,
                pcLabels,
                ConsumedLocal,
                ref nextLabel);
        }

        return new CilMethodPlan
        {
            Name = methodName,
            FunctionId = function.Id,
            StartProgramCounter = startProgramCounter,
            CanonicalInstructionCount = instructionCount,
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
            GcMaps = [.. liveness.GcMaps.Where(map =>
                map.CanonicalProgramCounter >= startProgramCounter &&
                map.CanonicalProgramCounter < endProgramCounter)],
            SequencePoints = sequencePoints.ToImmutable(),
            Blocks = SliceBlocks(
                blockLayout.Blocks,
                startProgramCounter,
                endProgramCounter),
        };
    }

    private static bool IsDirectlyLowered(LuaIrInstruction instruction) => instruction.Opcode switch
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
        int startProgramCounter,
        int endProgramCounter,
        ImmutableArray<CilLabel> pcLabels,
        int consumedLocal,
        ref int nextLabel)
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
                BranchToNext(
                    plan,
                    pc,
                    function,
                    startProgramCounter,
                    endProgramCounter,
                    pcLabels,
                    consumedLocal);
                break;
            case LuaIrOpcode.LoadNil:
                LoadArgument(plan, ThreadArgument, pc);
                LoadArgument(plan, FrameArgument, pc);
                LoadInt32(plan, instruction.A, pc);
                LoadInt32(plan, instruction.B, pc);
                Emit(plan, CilPlanInstruction.Call(CilWellKnownCalls.ClearRegisters, pc));
                BranchToNext(
                    plan,
                    pc,
                    function,
                    startProgramCounter,
                    endProgramCounter,
                    pcLabels,
                    consumedLocal);
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
                BranchToNext(
                    plan,
                    pc,
                    function,
                    startProgramCounter,
                    endProgramCounter,
                    pcLabels,
                    consumedLocal);
                break;
            case LuaIrOpcode.SetTop:
                LoadArgument(plan, ThreadArgument, pc);
                LoadArgument(plan, FrameArgument, pc);
                LoadInt32(plan, instruction.A, pc);
                Emit(plan, CilPlanInstruction.Call(CilWellKnownCalls.SetFrameTop, pc));
                BranchToNext(
                    plan,
                    pc,
                    function,
                    startProgramCounter,
                    endProgramCounter,
                    pcLabels,
                    consumedLocal);
                break;
            case LuaIrOpcode.Jump:
                BranchOrContinue(
                    plan,
                    instruction.B,
                    pc,
                    startProgramCounter,
                    endProgramCounter,
                    pcLabels,
                    consumedLocal);
                break;
            case LuaIrOpcode.JumpIfFalse:
            case LuaIrOpcode.JumpIfTrue:
                LoadArgument(plan, ThreadArgument, pc);
                LoadArgument(plan, FrameArgument, pc);
                LoadInt32(plan, instruction.A, pc);
                Emit(plan, CilPlanInstruction.Call(CilWellKnownCalls.ReadRegister, pc));
                Emit(plan, CilPlanInstruction.Call(CilWellKnownCalls.LuaValueIsTruthy, pc));
                if (IsInRange(instruction.B, startProgramCounter, endProgramCounter))
                {
                    Emit(plan, CilPlanInstruction.WithLabel(
                        instruction.Opcode == LuaIrOpcode.JumpIfTrue
                            ? CilPlanOpCode.BranchTrue
                            : CilPlanOpCode.BranchFalse,
                        LabelFor(instruction.B, startProgramCounter, pcLabels),
                        pc));
                    BranchToNext(
                        plan,
                        pc,
                        function,
                        startProgramCounter,
                        endProgramCounter,
                        pcLabels,
                        consumedLocal);
                }
                else
                {
                    var exitLabel = new CilLabel(nextLabel++);
                    Emit(plan, CilPlanInstruction.WithLabel(
                        instruction.Opcode == LuaIrOpcode.JumpIfTrue
                            ? CilPlanOpCode.BranchTrue
                            : CilPlanOpCode.BranchFalse,
                        exitLabel,
                        pc));
                    BranchToNext(
                        plan,
                        pc,
                        function,
                        startProgramCounter,
                        endProgramCounter,
                        pcLabels,
                        consumedLocal);
                    Emit(plan, CilPlanInstruction.MarkLabel(exitLabel, pc));
                    EmitExit(
                        plan,
                        CilWellKnownCalls.ExitContinue,
                        instruction.B,
                        consumedLocal,
                        reason: null);
                }

                break;
            case LuaIrOpcode.Return:
                EmitExit(plan, CilWellKnownCalls.ExitReturn, pc, consumedLocal, reason: null);
                break;
            default:
                throw new InvalidOperationException(
                    $"Canonical opcode {instruction.Opcode} has no CIL lowering.");
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
        int startProgramCounter,
        int endProgramCounter,
        ImmutableArray<CilLabel> labels,
        int consumedLocal)
    {
        if (pc + 1 >= function.Instructions.Length)
        {
            EmitDeopt(plan, consumedLocal, LuaCompiledExitReason.BackendInvalidated);
            return;
        }

        BranchOrContinue(
            plan,
            pc + 1,
            pc,
            startProgramCounter,
            endProgramCounter,
            labels,
            consumedLocal);
    }

    private static void BranchOrContinue(
        ImmutableArray<CilPlanInstruction>.Builder plan,
        int targetProgramCounter,
        int sourceProgramCounter,
        int startProgramCounter,
        int endProgramCounter,
        ImmutableArray<CilLabel> labels,
        int consumedLocal)
    {
        if (IsInRange(targetProgramCounter, startProgramCounter, endProgramCounter))
        {
            Emit(plan, CilPlanInstruction.WithLabel(
                CilPlanOpCode.Branch,
                LabelFor(targetProgramCounter, startProgramCounter, labels),
                sourceProgramCounter));
            return;
        }

        EmitExit(
            plan,
            CilWellKnownCalls.ExitContinue,
            targetProgramCounter,
            consumedLocal,
            reason: null);
    }

    private static bool IsInRange(
        int programCounter,
        int startProgramCounter,
        int endProgramCounter) =>
        programCounter >= startProgramCounter && programCounter < endProgramCounter;

    private static CilLabel LabelFor(
        int programCounter,
        int startProgramCounter,
        ImmutableArray<CilLabel> labels) =>
        labels[programCounter - startProgramCounter];

    private static ImmutableArray<CilCanonicalBlock> SliceBlocks(
        ImmutableArray<CilCanonicalBlock> blocks,
        int startProgramCounter,
        int endProgramCounter)
    {
        var result = ImmutableArray.CreateBuilder<CilCanonicalBlock>();
        foreach (var block in blocks)
        {
            var blockEnd = checked(block.StartProgramCounter + block.Length);
            var start = Math.Max(block.StartProgramCounter, startProgramCounter);
            var end = Math.Min(blockEnd, endProgramCounter);
            if (start >= end)
            {
                continue;
            }

            var successors = end < blockEnd
                ? end < endProgramCounter ? ImmutableArray.Create(end) : []
                : [.. block.Successors.Where(successor =>
                    successor >= startProgramCounter && successor < endProgramCounter)];
            result.Add(new CilCanonicalBlock(start, end - start, successors));
        }

        return result.ToImmutable();
    }

    private static void EmitDeoptFromLocal(
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

    private static void EmitDeopt(
        ImmutableArray<CilPlanInstruction>.Builder plan,
        int consumedLocal,
        LuaCompiledExitReason reason)
    {
        Emit(plan, CilPlanInstruction.WithInt32(
            CilPlanOpCode.LoadLocal,
            ProgramCounterLocal));
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
