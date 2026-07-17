using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Operations;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

/// <summary>Reference canonical-instruction executor beneath the shared scheduler.</summary>
internal sealed class LuaInterpreterInstructionExecutor : ILuaInstructionExecutor
{
    private const int CompactSafePointInterval = 32;

    public LuaFrameInstructionRoute GetInitialFrameInstructionRoute(LuaFrame frame) =>
        LuaFrameInstructionRoute.Interpreter;

    public LuaCompiledExit Execute(
        LuaExecutionEngine engine,
        LuaExecutionContext context,
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        if (RequiresExactDebugHookDispatch(thread, frame))
        {
            return ExecuteSingleInstruction(
                engine,
                context,
                state,
                thread,
                frame,
                in instruction);
        }

        var result = ExecuteInstruction(
            engine,
            context,
            state,
            thread,
            frame,
            in instruction);
        var instructions = ImmutableCollectionsMarshal.AsArray(
            frame.Function.Instructions)!;
        var instructionsUntilSafePoint = CompactSafePointInterval;
        var compactStateValidated = false;
        while (true)
        {
            var runSafePoint = --instructionsUntilSafePoint == 0 ||
                state.Heap.RequiresInterpreterSafePoint;
            if (result is not InterpreterInstructionResult.Continue and
                not InterpreterInstructionResult.ContinueWithSchedulerCheck)
            {
                return MaterializeExit(result, context, frame.ProgramCounter);
            }

            bool canContinue;
            if (result == InterpreterInstructionResult.Continue &&
                compactStateValidated && !runSafePoint)
            {
                // Pure canonical instructions cannot change scheduler, frame, continuation, or
                // debug state. Once a full check has established those invariants, a pure chain
                // only needs the next-PC bound until a slow operation or safe point occurs.
                canContinue = (uint)frame.ProgramCounter < (uint)instructions.Length;
            }
            else
            {
                canContinue = engine.TryContinueCompactInterpreterLoop(
                    context,
                    state,
                    thread,
                    frame,
                    runSafePoint);
                compactStateValidated = canContinue &&
                    frame.InstructionRoute == LuaFrameInstructionRoute.Interpreter;
            }

            if (!canContinue)
            {
                return LuaCompiledExit.Continue(
                    frame.ProgramCounter,
                    context.InstructionsConsumed);
            }

            if (runSafePoint)
            {
                instructionsUntilSafePoint = CompactSafePointInterval;
            }

            result = ExecuteInstruction(
                engine,
                context,
                state,
                thread,
                frame,
                in instructions[frame.ProgramCounter]);
        }
    }

    internal static bool RequiresExactDebugHookDispatch(LuaThread thread, LuaFrame frame) =>
        thread.HasDispatchableDebugHook && !frame.IsDebugHook && !frame.IsHidden;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static LuaCompiledExit ExecuteSingleInstruction(
        LuaExecutionEngine engine,
        LuaExecutionContext context,
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        in LuaIrInstruction instruction)
    {
        var result = ExecuteInstruction(
            engine,
            context,
            state,
            thread,
            frame,
            in instruction);
        return MaterializeExit(result, context, frame.ProgramCounter);
    }

    private static InterpreterInstructionResult ExecuteInstruction(
        LuaExecutionEngine engine,
        LuaExecutionContext context,
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        in LuaIrInstruction instruction)
    {
        if (!context.TryReserveSingleInterpreterInstruction())
        {
            return InterpreterInstructionResult.InstructionBudget;
        }

        switch (instruction.Opcode)
        {
            case LuaIrOpcode.LoadConstant:
                LuaExecutionEngine.Write(thread, frame, instruction.A, LuaExecutionEngine.MaterializeConstant(
                    state,
                    thread,
                    frame,
                    instruction.B));
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.LoadNil:
                for (var index = 0; index < instruction.B; index++)
                {
                    LuaExecutionEngine.Write(thread, frame, instruction.A + index, LuaValue.Nil);
                }

                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.Move:
                LuaExecutionEngine.Write(
                    thread,
                    frame,
                    instruction.A,
                    LuaExecutionEngine.Read(thread, frame, instruction.B));
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.SetTop:
                LuaExecutionEngine.SetFrameTop(thread, frame, frame.Base + instruction.A);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.GetUpvalue:
                LuaExecutionEngine.Write(
                    thread,
                    frame,
                    instruction.A,
                    frame.Closure.Upvalues[instruction.B].Value);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.SetUpvalue:
                frame.Closure.Upvalues[instruction.A].Value =
                    LuaExecutionEngine.Read(thread, frame, instruction.B);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.NewTable:
                var allocationHint = frame.GetOrCreateTableAllocationHint(
                    frame.ProgramCounter);
                LuaExecutionEngine.Write(
                    thread,
                    frame,
                    instruction.A,
                    LuaValue.FromTable(state.CreateTableForAllocationSite(
                        instruction.C,
                        instruction.B == 0 ? 0 : 1 << (instruction.B - 1),
                        allocationHint)));
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.GetTable:
                engine.ExecuteOperation(
                    state,
                    context.Scheduler ??
                        throw new InvalidOperationException("The interpreter scheduler is unavailable."),
                    thread,
                    frame,
                    LuaRuntimeOperations.GetIndex(
                        state,
                        LuaExecutionEngine.Read(thread, frame, instruction.B),
                        LuaExecutionEngine.Read(thread, frame, instruction.C)),
                    frame.Base + instruction.A,
                    expectedResults: 1);
                return InterpreterInstructionResult.ContinueWithSchedulerCheck;
            case LuaIrOpcode.SetTable:
                engine.ExecuteOperation(
                    state,
                    context.Scheduler ??
                        throw new InvalidOperationException("The interpreter scheduler is unavailable."),
                    thread,
                    frame,
                    LuaRuntimeOperations.SetIndex(
                        state,
                        LuaExecutionEngine.Read(thread, frame, instruction.A),
                        LuaExecutionEngine.Read(thread, frame, instruction.B),
                        LuaExecutionEngine.Read(thread, frame, instruction.C)),
                    frame.Top,
                    expectedResults: 0);
                return InterpreterInstructionResult.ContinueWithSchedulerCheck;
            case LuaIrOpcode.SetList:
                LuaExecutionEngine.ExecuteSetList(thread, frame, instruction);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.Closure:
                LuaExecutionEngine.Write(thread, frame, instruction.A, LuaValue.FromFunction(
                    LuaExecutionEngine.CreateClosure(thread, frame, instruction.B)));
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.VarArg:
                LuaExecutionEngine.ExecuteVarArg(thread, frame, instruction);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.Unary:
                engine.ExecuteOperation(
                    state,
                    context.Scheduler ??
                        throw new InvalidOperationException("The interpreter scheduler is unavailable."),
                    thread,
                    frame,
                    LuaRuntimeOperations.Unary(
                        state,
                        (LuaIrUnaryOperator)instruction.C,
                        LuaExecutionEngine.Read(thread, frame, instruction.B)),
                    frame.Base + instruction.A,
                    expectedResults: 1);
                return InterpreterInstructionResult.ContinueWithSchedulerCheck;
            case LuaIrOpcode.Binary:
                engine.ExecuteOperation(
                    state,
                    context.Scheduler ??
                        throw new InvalidOperationException("The interpreter scheduler is unavailable."),
                    thread,
                    frame,
                    LuaRuntimeOperations.Binary(
                        state,
                        (LuaIrBinaryOperator)instruction.D,
                        LuaExecutionEngine.Read(thread, frame, instruction.B),
                        LuaExecutionEngine.Read(thread, frame, instruction.C)),
                    frame.Base + instruction.A,
                    expectedResults: 1);
                return InterpreterInstructionResult.ContinueWithSchedulerCheck;
            case LuaIrOpcode.Jump:
                if (instruction.C >= 0 &&
                    engine.TryCloseFrom(state, thread, frame, instruction.C, LuaValue.Nil))
                {
                    return InterpreterInstructionResult.ContinueWithSchedulerCheck;
                }

                frame.ProgramCounter = instruction.B;
                break;
            case LuaIrOpcode.JumpIfFalse:
                var falseCondition = LuaExecutionEngine.Read(thread, frame, instruction.A).IsTruthy;
                if (instruction.D != 0)
                {
                    LuaExecutionEngine.SetFrameTop(thread, frame, frame.Base + instruction.C);
                }

                frame.ProgramCounter = falseCondition
                    ? frame.ProgramCounter + 1
                    : instruction.B;
                break;
            case LuaIrOpcode.JumpIfTrue:
                var trueCondition = LuaExecutionEngine.Read(thread, frame, instruction.A).IsTruthy;
                if (instruction.D != 0)
                {
                    LuaExecutionEngine.SetFrameTop(thread, frame, frame.Base + instruction.C);
                }

                frame.ProgramCounter = trueCondition
                    ? instruction.B
                    : frame.ProgramCounter + 1;
                break;
            case LuaIrOpcode.Call:
                return InterpreterInstructionResult.Call;
            case LuaIrOpcode.TailCall:
                return InterpreterInstructionResult.TailCall;
            case LuaIrOpcode.Return:
                return InterpreterInstructionResult.Return;
            case LuaIrOpcode.Close:
                if (engine.TryCloseFrom(state, thread, frame, instruction.A, LuaValue.Nil))
                {
                    return InterpreterInstructionResult.ContinueWithSchedulerCheck;
                }

                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.MarkToBeClosed:
                {
                    var value = LuaExecutionEngine.Read(thread, frame, instruction.A);
                    if (value.IsTruthy)
                    {
                        var close = LuaRuntimeOperations.GetMetamethod(
                            state,
                            value,
                            LuaMetamethod.Close);
                        if (close.IsNil)
                        {
                            var local = LuaDebugApi.GetLocal(
                                thread,
                                frame,
                                instruction.A + 1);
                            throw new LuaRuntimeException(
                                $"variable '{local?.Name ?? "?"}' got a non-closable value");
                        }

                        frame.ToBeClosedSlots.Add(frame.Base + instruction.A);
                    }

                    frame.ProgramCounter++;
                    break;
                }
            case LuaIrOpcode.NumericForPrepare:
                LuaExecutionEngine.ExecuteNumericForPrepare(thread, frame, instruction);
                break;
            case LuaIrOpcode.NumericForLoop:
                LuaExecutionEngine.ExecuteNumericForLoop(thread, frame, instruction);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported canonical opcode {instruction.Opcode}.");
        }

        return InterpreterInstructionResult.Continue;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static LuaCompiledExit MaterializeExit(
        InterpreterInstructionResult result,
        LuaExecutionContext context,
        int programCounter) => result switch
        {
            InterpreterInstructionResult.Continue or
                InterpreterInstructionResult.ContinueWithSchedulerCheck => LuaCompiledExit.Continue(
                programCounter,
                context.InstructionsConsumed),
            InterpreterInstructionResult.Call => LuaCompiledExit.Call(
                programCounter,
                context.InstructionsConsumed),
            InterpreterInstructionResult.TailCall => LuaCompiledExit.TailCall(
                programCounter,
                context.InstructionsConsumed),
            InterpreterInstructionResult.Return => LuaCompiledExit.Return(
                programCounter,
                context.InstructionsConsumed),
            InterpreterInstructionResult.InstructionBudget => LuaCompiledExit.Poll(
                programCounter,
                context.InstructionsConsumed,
                LuaCompiledExitReason.InstructionBudget),
            _ => throw new InvalidOperationException(
                $"Unknown interpreter instruction result {result}."),
        };

    private enum InterpreterInstructionResult : byte
    {
        Continue,
        ContinueWithSchedulerCheck,
        Call,
        TailCall,
        Return,
        InstructionBudget,
    }
}
