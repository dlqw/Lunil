using System.Collections.Immutable;
using Lunil.CodeGen.Cil.Analysis;
using Lunil.CodeGen.Cil.Emission;
using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.CodeGen.Cil.Jit;

public sealed record LuaJitOsrRegisterMap(int CanonicalRegister, int CompiledSlot);

public sealed record LuaJitOsrEntryMap(
    int HeaderProgramCounter,
    ImmutableArray<LuaJitOsrRegisterMap> Registers,
    bool FrameTopMaterialized,
    bool OpenUpvaluesMaterialized,
    bool ToBeClosedStateMaterialized);

public sealed record LuaJitLoopOsrPlan(
    int FunctionId,
    int HeaderProgramCounter,
    int BackedgeProgramCounter,
    ImmutableArray<int> ProgramCounters,
    LuaJitOsrEntryMap EntryMap);

internal sealed record LuaLoopOsrCompilationResult(
    LuaCompiledMethod? Method,
    LuaJitLoopOsrPlan? Plan,
    long EstimatedCodeBytes,
    ImmutableArray<string> Diagnostics)
{
    public bool Succeeded => Method is not null && Plan is not null && Diagnostics.IsEmpty;
}

internal interface ILuaLoopOsrCompiler
{
    LuaLoopOsrCompilationResult Compile(
        LuaIrModule module,
        LuaJitLoopOsrPlan plan,
        CancellationToken cancellationToken);
}

internal static class LuaLoopOsrAnalyzer
{
    public static ImmutableArray<LuaJitLoopOsrPlan> Analyze(
        LuaIrModule module,
        int functionId)
    {
        ArgumentNullException.ThrowIfNull(module);
        if ((uint)functionId >= (uint)module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        var function = module.Functions[functionId];
        var blocks = function.BasicBlocks.IsDefaultOrEmpty
            ? LuaIrControlFlow.Build(function.Instructions)
            : function.BasicBlocks;
        if (blocks.IsEmpty)
        {
            return [];
        }

        var blockByStart = blocks.ToDictionary(static block => block.Start);
        var predecessors = blocks.ToDictionary(
            static block => block.Start,
            static _ => new HashSet<int>());
        foreach (var block in blocks)
        {
            foreach (var successor in block.Successors)
            {
                if (predecessors.TryGetValue(successor, out var targets))
                {
                    targets.Add(block.Start);
                }
            }
        }

        var dominators = ComputeDominators(blocks, predecessors);
        var liveness = LuaRegisterLiveness.Analyze(module, function);
        var plans = ImmutableArray.CreateBuilder<LuaJitLoopOsrPlan>();
        foreach (var block in blocks)
        {
            var backedgePc = block.End - 1;
            var instruction = function.Instructions[backedgePc];
            if (!IsOsrBackedgeInstruction(instruction, backedgePc) ||
                !blockByStart.ContainsKey(instruction.B) ||
                !dominators[block.Start].Contains(instruction.B))
            {
                continue;
            }

            var loopBlocks = BuildNaturalLoop(
                instruction.B,
                block.Start,
                predecessors,
                dominators);
            var programCounters = loopBlocks
                .SelectMany(start => Enumerable.Range(
                    start,
                    blockByStart[start].Length))
                .Order()
                .ToImmutableArray();
            var registers = liveness.LiveBefore[instruction.B]
                .Select(static register => new LuaJitOsrRegisterMap(register, register))
                .ToImmutableArray();
            plans.Add(new LuaJitLoopOsrPlan(
                functionId,
                instruction.B,
                backedgePc,
                programCounters,
                new LuaJitOsrEntryMap(
                    instruction.B,
                    registers,
                    FrameTopMaterialized: true,
                    OpenUpvaluesMaterialized: true,
                    ToBeClosedStateMaterialized: true)));
        }

        return plans
            .OrderBy(static plan => plan.HeaderProgramCounter)
            .ThenBy(static plan => plan.BackedgeProgramCounter)
            .ToImmutableArray();
    }

    public static bool IsOsrBackedgeInstruction(
        LuaIrInstruction instruction,
        int programCounter) =>
        instruction.B <= programCounter && instruction.Opcode switch
        {
            LuaIrOpcode.Jump => true,
            LuaIrOpcode.JumpIfFalse or LuaIrOpcode.JumpIfTrue or
                LuaIrOpcode.NumericForLoop => true,
            _ => false,
        };

    private static Dictionary<int, HashSet<int>> ComputeDominators(
        ImmutableArray<LuaIrBasicBlock> blocks,
        Dictionary<int, HashSet<int>> predecessors)
    {
        var starts = blocks.Select(static block => block.Start).ToHashSet();
        var entry = blocks[0].Start;
        var result = blocks.ToDictionary(
            static block => block.Start,
            block => block.Start == entry ? new HashSet<int> { entry } : new HashSet<int>(starts));
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var block in blocks.Skip(1))
            {
                var incoming = predecessors[block.Start];
                HashSet<int> next;
                if (incoming.Count == 0)
                {
                    next = [block.Start];
                }
                else
                {
                    using var enumerator = incoming.GetEnumerator();
                    _ = enumerator.MoveNext();
                    next = new HashSet<int>(result[enumerator.Current]);
                    while (enumerator.MoveNext())
                    {
                        next.IntersectWith(result[enumerator.Current]);
                    }

                    next.Add(block.Start);
                }

                if (!result[block.Start].SetEquals(next))
                {
                    result[block.Start] = next;
                    changed = true;
                }
            }
        }

        return result;
    }

    private static HashSet<int> BuildNaturalLoop(
        int header,
        int source,
        Dictionary<int, HashSet<int>> predecessors,
        Dictionary<int, HashSet<int>> dominators)
    {
        var result = new HashSet<int> { header, source };
        var pending = new Stack<int>();
        if (source != header)
        {
            pending.Push(source);
        }

        while (pending.TryPop(out var block))
        {
            foreach (var predecessor in predecessors[block])
            {
                if (dominators[predecessor].Contains(header) && result.Add(predecessor))
                {
                    pending.Push(predecessor);
                }
            }
        }

        return result;
    }
}

internal sealed class CanonicalLuaLoopOsrCompiler : ILuaLoopOsrCompiler
{
    public static CanonicalLuaLoopOsrCompiler Instance { get; } = new();

    public LuaLoopOsrCompilationResult Compile(
        LuaIrModule module,
        LuaJitLoopOsrPlan plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errors = LuaIrVerifier.Verify(module);
        if (!errors.IsEmpty || (uint)plan.FunctionId >= (uint)module.Functions.Length)
        {
            return new LuaLoopOsrCompilationResult(
                null,
                null,
                0,
                !errors.IsEmpty
                    ? [.. errors.Select(static error => error.Message)]
                    : ["Function id is outside the verified module."]);
        }

        var verifiedPlan = LuaLoopOsrAnalyzer.Analyze(module, plan.FunctionId)
            .FirstOrDefault(candidate =>
                candidate.HeaderProgramCounter == plan.HeaderProgramCounter &&
                candidate.BackedgeProgramCounter == plan.BackedgeProgramCounter);
        if (verifiedPlan is null)
        {
            return new LuaLoopOsrCompilationResult(
                null,
                null,
                0,
                ["The requested edge is not a verified natural-loop backedge."]);
        }

        var program = new LoopOsrProgram(
            module.Functions[plan.FunctionId],
            verifiedPlan);
        return new LuaLoopOsrCompilationResult(
            program.Execute,
            verifiedPlan,
            checked(verifiedPlan.ProgramCounters.Length * 10L +
                verifiedPlan.EntryMap.Registers.Length * 8L + 96L),
            []);
    }

    private sealed class LoopOsrProgram
    {
        private readonly LuaIrFunction _function;
        private readonly LuaJitLoopOsrPlan _plan;
        private readonly bool[] _insideLoop;

        public LoopOsrProgram(LuaIrFunction function, LuaJitLoopOsrPlan plan)
        {
            _function = function;
            _plan = plan;
            _insideLoop = new bool[function.Instructions.Length];
            foreach (var programCounter in plan.ProgramCounters)
            {
                _insideLoop[programCounter] = true;
            }
        }

        public LuaCompiledExit Execute(
            LuaExecutionContext context,
            LuaThread thread,
            LuaFrame frame)
        {
            var entryGuard = GuardEntry(context, thread, frame);
            if (entryGuard is { } guardedExit)
            {
                return guardedExit;
            }

            while ((uint)frame.ProgramCounter < (uint)_insideLoop.Length &&
                _insideLoop[frame.ProgramCounter])
            {
                var pc = frame.ProgramCounter;
                if (pc == _plan.BackedgeProgramCounter)
                {
                    LuaCodegenAbiV1.ObserveLoopOsrBackedge(context, frame, pc);
                }

                if (pc == _plan.HeaderProgramCounter)
                {
                    var loopGuard = GuardLoopHeader(context, thread, frame);
                    if (loopGuard is { } loopExit)
                    {
                        return loopExit;
                    }
                }

                var directExit = ExecuteDirect(
                    context,
                    thread,
                    frame,
                    _function.Instructions[pc]);
                if (directExit is { } result)
                {
                    return result;
                }

                if ((uint)frame.ProgramCounter >= (uint)_insideLoop.Length ||
                    !_insideLoop[frame.ProgramCounter])
                {
                    return LuaCompiledExit.Continue(
                        frame.ProgramCounter,
                        context.InstructionsConsumed);
                }
            }

            return LuaCompiledExit.Continue(
                frame.ProgramCounter,
                context.InstructionsConsumed);
        }

        private LuaCompiledExit? GuardEntry(
            LuaExecutionContext context,
            LuaThread thread,
            LuaFrame frame)
        {
            if (frame.ProgramCounter != _plan.HeaderProgramCounter ||
                !ReferenceEquals(thread.CurrentFrame, frame) ||
                thread.UnwindState is not null ||
                thread.IsClosing ||
                frame.Continuation.Kind != LuaContinuationKind.None ||
                frame.Top < frame.Base ||
                frame.Top > frame.Base + frame.Closure.Function.RegisterCount ||
                frame.ToBeClosedSlots.Any(slot => slot < frame.Base || slot >= frame.Top))
            {
                return GuardFailure(context, frame.ProgramCounter);
            }

            return GuardLoopHeader(context, thread, frame);
        }

        private static LuaCompiledExit? GuardLoopHeader(
            LuaExecutionContext context,
            LuaThread thread,
            LuaFrame frame)
        {
            if (!LuaCodegenAbiV1.CanExecuteCompiled(context) ||
                !ReferenceEquals(context.Thread, thread) ||
                thread.UnwindState is not null || thread.IsClosing)
            {
                return GuardFailure(context, frame.ProgramCounter);
            }

            if (context.RemainingInstructionCount == 0)
            {
                return LuaCompiledExit.Poll(
                    frame.ProgramCounter,
                    context.InstructionsConsumed,
                    LuaCompiledExitReason.InstructionBudget);
            }

            context.State.Heap.SafePoint();
            return context.State.Heap.PendingFinalizerCount == 0
                ? null
                : LuaCompiledExit.Poll(
                    frame.ProgramCounter,
                    context.InstructionsConsumed,
                    LuaCompiledExitReason.GarbageCollection);
        }

        private LuaCompiledExit? ExecuteDirect(
            LuaExecutionContext context,
            LuaThread thread,
            LuaFrame frame,
            LuaIrInstruction instruction)
        {
            var pc = frame.ProgramCounter;
            switch (instruction.Opcode)
            {
                case LuaIrOpcode.LoadConstant when
                    _function.Constants[instruction.B].Kind != LuaIrConstantKind.String:
                    if (!context.TryReserveInstructions(1))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV1.WriteRegister(
                        thread,
                        frame,
                        instruction.A,
                        LuaCodegenAbiV1.MaterializeConstant(
                            context,
                            frame,
                            instruction.B));
                    frame.ProgramCounter++;
                    return null;
                case LuaIrOpcode.LoadNil:
                    if (!context.TryReserveInstructions(1))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV1.ClearRegisters(
                        thread,
                        frame,
                        instruction.A,
                        instruction.B);
                    frame.ProgramCounter++;
                    return null;
                case LuaIrOpcode.Move:
                    if (!context.TryReserveInstructions(1))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV1.WriteRegister(
                        thread,
                        frame,
                        instruction.A,
                        LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.B));
                    frame.ProgramCounter++;
                    return null;
                case LuaIrOpcode.SetTop:
                    if (!context.TryReserveInstructions(1))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV1.SetFrameTop(thread, frame, instruction.A);
                    frame.ProgramCounter++;
                    return null;
                case LuaIrOpcode.GetUpvalue:
                    if (!context.TryReserveInstructions(1))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV1.WriteRegister(
                        thread,
                        frame,
                        instruction.A,
                        LuaCodegenAbiV1.ReadUpvalue(frame, instruction.B));
                    frame.ProgramCounter++;
                    return null;
                case LuaIrOpcode.SetUpvalue:
                    if (!context.TryReserveInstructions(1))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV1.WriteUpvalue(
                        frame,
                        instruction.A,
                        LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.B));
                    frame.ProgramCounter++;
                    return null;
                case LuaIrOpcode.Jump:
                    if (!context.TryReserveInstructions(1))
                    {
                        return BudgetPoll(context, pc);
                    }

                    if (instruction.C >= 0)
                    {
                        var engine = context.ExecutionEngine;
                        if (engine is null)
                        {
                            return GuardFailure(context, pc);
                        }

                        if (engine.TryCloseFrom(
                            context.State,
                            thread,
                            frame,
                            instruction.C,
                            LuaValue.Nil))
                        {
                            return LuaCompiledExit.Continue(
                                frame.ProgramCounter,
                                context.InstructionsConsumed);
                        }
                    }

                    frame.ProgramCounter = instruction.B;
                    return null;
                case LuaIrOpcode.JumpIfFalse:
                case LuaIrOpcode.JumpIfTrue:
                    if (!context.TryReserveInstructions(1))
                    {
                        return BudgetPoll(context, pc);
                    }

                    var truthy = LuaCodegenAbiV1.ReadRegister(
                        thread,
                        frame,
                        instruction.A).IsTruthy;
                    var taken = instruction.Opcode == LuaIrOpcode.JumpIfTrue
                        ? truthy
                        : !truthy;
                    frame.ProgramCounter = taken ? instruction.B : pc + 1;
                    return null;
                case LuaIrOpcode.Unary:
                    {
                        var operand = LuaCodegenAbiV1.ReadRegister(
                            thread,
                            frame,
                            instruction.B);
                        var operation = (LuaIrUnaryOperator)instruction.C;
                        if (operation == LuaIrUnaryOperator.Length &&
                            operand.Kind != LuaValueKind.String ||
                            operation is LuaIrUnaryOperator.Negate or
                                LuaIrUnaryOperator.BitwiseNot &&
                            operand.Kind is not LuaValueKind.Integer and
                                not LuaValueKind.Float)
                        {
                            goto SlowPath;
                        }

                        if (!context.TryReserveInstructions(1))
                        {
                            return BudgetPoll(context, pc);
                        }

                        LuaCodegenAbiV1.WriteRegister(
                            thread,
                            frame,
                            instruction.A,
                            LuaValueOperations.Unary(operation, operand));
                        frame.ProgramCounter++;
                        return null;
                    }
                case LuaIrOpcode.Binary:
                    {
                        var operation = (LuaIrBinaryOperator)instruction.D;
                        var left = LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.B);
                        var right = LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.C);
                        if (operation == LuaIrBinaryOperator.Concatenate ||
                            left.Kind is not LuaValueKind.Integer and not LuaValueKind.Float ||
                            right.Kind is not LuaValueKind.Integer and not LuaValueKind.Float)
                        {
                            goto SlowPath;
                        }

                        if (!context.TryReserveInstructions(1))
                        {
                            return BudgetPoll(context, pc);
                        }

                        LuaCodegenAbiV1.WriteRegister(
                            thread,
                            frame,
                            instruction.A,
                            LuaValueOperations.Binary(
                                context.State,
                                operation,
                                left,
                                right));
                        frame.ProgramCounter++;
                        return null;
                    }
                case LuaIrOpcode.NumericForPrepare:
                    if (!context.TryReserveInstructions(1))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaExecutionEngine.ExecuteNumericForPrepare(thread, frame, instruction);
                    return null;
                case LuaIrOpcode.NumericForLoop:
                    if (!context.TryReserveInstructions(1))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaExecutionEngine.ExecuteNumericForLoop(thread, frame, instruction);
                    return null;
                case LuaIrOpcode.Close:
                    if (!context.TryReserveInstructions(1))
                    {
                        return BudgetPoll(context, pc);
                    }

                    var closeEngine = context.ExecutionEngine;
                    if (closeEngine is null)
                    {
                        return GuardFailure(context, pc);
                    }

                    if (closeEngine.TryCloseFrom(
                        context.State,
                        thread,
                        frame,
                        instruction.A,
                        LuaValue.Nil))
                    {
                        return LuaCompiledExit.Continue(
                            frame.ProgramCounter,
                            context.InstructionsConsumed);
                    }

                    frame.ProgramCounter++;
                    context.State.Heap.SafePoint();
                    return context.State.Heap.PendingFinalizerCount == 0
                        ? null
                        : LuaCompiledExit.Poll(
                            frame.ProgramCounter,
                            context.InstructionsConsumed,
                            LuaCompiledExitReason.GarbageCollection);
                default:
                SlowPath:
                    LuaCodegenAbiV1.CommitProgramCounter(frame, pc);
                    return LuaCodegenAbiV1.ExecuteCanonicalInstruction(
                        context,
                        thread,
                        frame,
                        pc);
            }
        }

        private static LuaCompiledExit BudgetPoll(
            LuaExecutionContext context,
            int programCounter) => LuaCompiledExit.Poll(
                programCounter,
                context.InstructionsConsumed,
                LuaCompiledExitReason.InstructionBudget);

        private static LuaCompiledExit GuardFailure(
            LuaExecutionContext context,
            int programCounter) => LuaCompiledExit.Deopt(
                programCounter,
                context.InstructionsConsumed,
                LuaCompiledExitReason.GuardFailure);
    }
}
