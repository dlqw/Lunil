using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Operations;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

/// <summary>Reference canonical-instruction executor beneath the shared scheduler.</summary>
internal sealed class LuaInterpreterInstructionExecutor : ILuaInstructionExecutor
{
    public LuaCompiledExit Execute(
        LuaExecutionEngine engine,
        LuaExecutionContext context,
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        var programCounter = frame.ProgramCounter;
        if (!context.TryReserveInstructions(1))
        {
            return LuaCompiledExit.Poll(
                programCounter,
                instructionsConsumed: 0,
                LuaCompiledExitReason.InstructionBudget);
        }

        switch (instruction.Opcode)
        {
            case LuaIrOpcode.LoadConstant:
                LuaExecutionEngine.Write(thread, frame, instruction.A, LuaExecutionEngine.MaterializeConstant(
                    state,
                    frame.Closure,
                    frame.Closure.Function.Constants[instruction.B]));
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
                var allocationHint = frame.Closure.GetOrCreateTableAllocationHint(
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
                break;
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
                break;
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
                break;
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
                break;
            case LuaIrOpcode.Jump:
                if (instruction.C >= 0 &&
                    engine.TryCloseFrom(state, thread, frame, instruction.C, LuaValue.Nil))
                {
                    break;
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
                return LuaCompiledExit.Call(programCounter, context.InstructionsConsumed);
            case LuaIrOpcode.TailCall:
                return LuaCompiledExit.TailCall(programCounter, context.InstructionsConsumed);
            case LuaIrOpcode.Return:
                return LuaCompiledExit.Return(programCounter, context.InstructionsConsumed);
            case LuaIrOpcode.Close:
                if (engine.TryCloseFrom(state, thread, frame, instruction.A, LuaValue.Nil))
                {
                    break;
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

        return LuaCompiledExit.Continue(frame.ProgramCounter, context.InstructionsConsumed);
    }
}
