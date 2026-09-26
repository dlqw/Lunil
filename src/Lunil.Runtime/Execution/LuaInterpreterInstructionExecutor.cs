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
    internal const int CompactSafePointInterval = 32;

    public LuaFrameInstructionRoute GetInitialFrameInstructionRoute(LuaFrame frame) =>
        LuaFrameInstructionRoute.Interpreter;

    public LuaCompiledExit Execute(
        LuaExecutionEngine engine,
        LuaExecutionContext context,
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        in LuaIrInstruction instruction)
    {
        if (RequiresExactDebugHookDispatch(thread, frame, state))
        {
            return ExecuteSingleInstruction(
                engine,
                context,
                state,
                thread,
                frame,
                in instruction);
        }

        var tier05Code = frame.FunctionVersion.GetOrCreateTier05Code();
        if (tier05Code.HasFastInstructions)
        {
            return LuaTier05Interpreter.Run(
                engine,
                context,
                state,
                thread,
                frame,
                ImmutableCollectionsMarshal.AsArray(frame.Function.Instructions)!,
                tier05Code);
        }

        // The stack object identity, the frame base, and the heap reference are stable for
        // the whole compact run: the stack grows its internal array in place, the base is
        // fixed between frame pushes, and the heap pointer never changes on the state.
        var stack = thread.Stack;
        var frameBase = frame.Base;
        var heap = state.Heap;
        var result = ExecuteInstruction(
            engine,
            context,
            state,
            thread,
            frame,
            stack,
            frameBase,
            in instruction);
        var instructions = ImmutableCollectionsMarshal.AsArray(
            frame.Function.Instructions)!;
        var instructionsUntilSafePoint = CompactSafePointInterval;
        var compactStateValidated = false;
        while (true)
        {
            var runSafePoint = --instructionsUntilSafePoint == 0 ||
                heap.RequiresInterpreterSafePoint;
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
                stack,
                frameBase,
                in instructions[frame.ProgramCounter]);
        }
    }

    internal static bool RequiresExactDebugHookDispatch(
        LuaThread thread,
        LuaFrame frame,
        LuaState state) =>
        (thread.HasDispatchableDebugHook || state.DebugSession is not null) &&
        !frame.IsDebugHook && !frame.IsHidden;

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
            thread.Stack,
            frame.Base,
            in instruction);
        return MaterializeExit(result, context, frame.ProgramCounter);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static LuaValue ReadRegister(LuaStack stack, int frameBase, int register) =>
        stack.ReadUnchecked(frameBase + register);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void WriteRegister(
        LuaStack stack,
        LuaFrame frame,
        int frameBase,
        int register,
        LuaValue value)
    {
        var index = frameBase + register;
        stack.WriteUnchecked(index, value);
        if (frame.Top <= index)
        {
            frame.Top = index + 1;
        }
    }

    private static InterpreterInstructionResult ExecuteInstruction(
        LuaExecutionEngine engine,
        LuaExecutionContext context,
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        LuaStack stack,
        int frameBase,
        in LuaIrInstruction instruction)
    {
        if (!context.TryReserveSingleInterpreterInstruction())
        {
            return InterpreterInstructionResult.InstructionBudget;
        }

        return ExecuteInstructionCore(
            engine,
            context,
            state,
            thread,
            frame,
            stack,
            frameBase,
            in instruction);
    }

    internal static InterpreterInstructionResult ExecuteInstructionCore(
        LuaExecutionEngine engine,
        LuaExecutionContext context,
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        LuaStack stack,
        int frameBase,
        in LuaIrInstruction instruction)
    {
        if (!context.TryReserveSingleInterpreterInstruction())
        {
            return InterpreterInstructionResult.InstructionBudget;
        }

        switch (instruction.Opcode)
        {
            case LuaIrOpcode.LoadConstant:
                WriteRegister(
                    stack,
                    frame,
                    frameBase,
                    instruction.A,
                    LuaExecutionEngine.MaterializeConstant(
                        state,
                        thread,
                        frame,
                        instruction.B));
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.LoadNil:
                for (var index = 0; index < instruction.B; index++)
                {
                    WriteRegister(stack, frame, frameBase, instruction.A + index, LuaValue.Nil);
                }

                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.Move:
                WriteRegister(
                    stack,
                    frame,
                    frameBase,
                    instruction.A,
                    ReadRegister(stack, frameBase, instruction.B));
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.SetTop:
                LuaExecutionEngine.SetFrameTop(thread, frame, frameBase + instruction.A);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.GetUpvalue:
                WriteRegister(
                    stack,
                    frame,
                    frameBase,
                    instruction.A,
                    frame.Closure.Upvalues[instruction.B].Value);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.SetUpvalue:
                frame.Closure.Upvalues[instruction.A].Value =
                    ReadRegister(stack, frameBase, instruction.B);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.NewTable:
                var allocationHint = frame.GetOrCreateTableAllocationHint(
                    frame.ProgramCounter);
                WriteRegister(
                    stack,
                    frame,
                    frameBase,
                    instruction.A,
                    LuaValue.FromTable(state.CreateTableForAllocationSite(
                        instruction.C,
                        instruction.B == 0 ? 0 : 1 << (instruction.B - 1),
                        allocationHint)));
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.GetTable:
                {
                    var target = ReadRegister(stack, frameBase, instruction.B);
                    if (target.TryGetTable() is { } table && table.Metatable is null)
                    {
                        // Without a metatable the index cannot reach __index, so the plain
                        // lookup result (value or nil) is final and the compact loop can
                        // continue without scheduler revalidation.
                        WriteRegister(
                            stack,
                            frame,
                            frameBase,
                            instruction.A,
                            table.Get(ReadRegister(stack, frameBase, instruction.C)));
                        frame.ProgramCounter++;
                        break;
                    }

                    var getIndexResolution = LuaRuntimeOperations.GetIndex(
                        state,
                        target,
                        ReadRegister(stack, frameBase, instruction.C));
                    if (!getIndexResolution.RequiresCall)
                    {
                        // Immediate resolutions (including metatable-backed hits) commit the
                        // result inline and stay in the compact loop; only metamethod calls
                        // pay the scheduler revalidation below.
                        WriteRegister(
                            stack,
                            frame,
                            frameBase,
                            instruction.A,
                            getIndexResolution.Value);
                        frame.ProgramCounter++;
                        break;
                    }

                    engine.ExecuteOperation(
                        state,
                        context.Scheduler ??
                            throw new InvalidOperationException("The interpreter scheduler is unavailable."),
                        thread,
                        frame,
                        getIndexResolution,
                        frame.Base + instruction.A,
                        expectedResults: 1);
                    return InterpreterInstructionResult.ContinueWithSchedulerCheck;
                }
            case LuaIrOpcode.SetTable:
                {
                    var target = ReadRegister(stack, frameBase, instruction.A);
                    var key = ReadRegister(stack, frameBase, instruction.B);
                    var value = ReadRegister(stack, frameBase, instruction.C);
                    if (target.TryGetTable() is { } table && table.Metatable is null)
                    {
                        if (table.TryGetExistingEntry(key, out _, out var entry))
                        {
                            table.SetExistingEntry(entry, key, value);
                        }
                        else
                        {
                            table.Set(key, value);
                        }

                        frame.ProgramCounter++;
                        break;
                    }

                    var setIndexResolution = LuaRuntimeOperations.SetIndex(state, target, key, value);
                    if (!setIndexResolution.RequiresCall)
                    {
                        frame.ProgramCounter++;
                        break;
                    }

                    engine.ExecuteOperation(
                        state,
                        context.Scheduler ??
                            throw new InvalidOperationException("The interpreter scheduler is unavailable."),
                        thread,
                        frame,
                        setIndexResolution,
                        frame.Top,
                        expectedResults: 0);
                    return InterpreterInstructionResult.ContinueWithSchedulerCheck;
                }
            case LuaIrOpcode.SetList:
                LuaExecutionEngine.ExecuteSetList(thread, frame, instruction);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.Closure:
                WriteRegister(
                    stack,
                    frame,
                    frameBase,
                    instruction.A,
                    LuaValue.FromFunction(
                        LuaExecutionEngine.CreateClosure(thread, frame, instruction.B)));
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.VarArg:
                LuaExecutionEngine.ExecuteVarArg(state, thread, frame, instruction);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.CreateVarArgTable:
                LuaExecutionEngine.ExecuteCreateVarArgTable(state, thread, frame, instruction);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.GetVarArg:
                LuaExecutionEngine.ExecuteGetVarArg(thread, frame, instruction);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.ErrorIfNotNil:
                LuaExecutionEngine.ExecuteErrorIfNotNil(thread, frame, instruction);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.Unary:
                {
                    var operand = ReadRegister(stack, frameBase, instruction.B);
                    if (LuaRuntimeOperations.TryResolvePrimitiveUnary(
                            (LuaIrUnaryOperator)instruction.C,
                            operand,
                            out var unaryResult))
                    {
                        WriteRegister(stack, frame, frameBase, instruction.A, unaryResult);
                        frame.ProgramCounter++;
                        break;
                    }

                    var unaryResolution = LuaRuntimeOperations.Unary(
                        state,
                        (LuaIrUnaryOperator)instruction.C,
                        operand);
                    if (!unaryResolution.RequiresCall)
                    {
                        WriteRegister(stack, frame, frameBase, instruction.A, unaryResolution.Value);
                        frame.ProgramCounter++;
                        break;
                    }

                    engine.ExecuteOperation(
                        state,
                        context.Scheduler ??
                            throw new InvalidOperationException("The interpreter scheduler is unavailable."),
                        thread,
                        frame,
                        unaryResolution,
                        frame.Base + instruction.A,
                        expectedResults: 1);
                    return InterpreterInstructionResult.ContinueWithSchedulerCheck;
                }
            case LuaIrOpcode.Binary:
                {
                    var left = ReadRegister(stack, frameBase, instruction.B);
                    var right = ReadRegister(stack, frameBase, instruction.C);
                    if (LuaRuntimeOperations.TryResolvePrimitiveBinary(
                            (LuaIrBinaryOperator)instruction.D,
                            left,
                            right,
                            out var binaryResult))
                    {
                        WriteRegister(stack, frame, frameBase, instruction.A, binaryResult);
                        frame.ProgramCounter++;
                        break;
                    }

                    var binaryResolution = LuaRuntimeOperations.Binary(
                        state,
                        (LuaIrBinaryOperator)instruction.D,
                        left,
                        right);
                    if (!binaryResolution.RequiresCall)
                    {
                        WriteRegister(stack, frame, frameBase, instruction.A, binaryResolution.Value);
                        frame.ProgramCounter++;
                        break;
                    }

                    engine.ExecuteOperation(
                        state,
                        context.Scheduler ??
                            throw new InvalidOperationException("The interpreter scheduler is unavailable."),
                        thread,
                        frame,
                        binaryResolution,
                        frame.Base + instruction.A,
                        expectedResults: 1);
                    return InterpreterInstructionResult.ContinueWithSchedulerCheck;
                }
            case LuaIrOpcode.Jump:
                if (instruction.C >= 0 &&
                    engine.TryCloseFrom(state, thread, frame, instruction.C, LuaValue.Nil))
                {
                    return InterpreterInstructionResult.ContinueWithSchedulerCheck;
                }

                frame.ProgramCounter = instruction.B;
                break;
            case LuaIrOpcode.JumpIfFalse:
                var falseCondition = ReadRegister(stack, frameBase, instruction.A).IsTruthy;
                if (instruction.D != 0)
                {
                    LuaExecutionEngine.SetFrameTop(thread, frame, frame.Base + instruction.C);
                }

                frame.ProgramCounter = falseCondition
                    ? frame.ProgramCounter + 1
                    : instruction.B;
                break;
            case LuaIrOpcode.JumpIfTrue:
                var trueCondition = ReadRegister(stack, frameBase, instruction.A).IsTruthy;
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
                    var value = ReadRegister(stack, frameBase, instruction.A);
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
    internal static LuaCompiledExit MaterializeExit(
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

    internal enum InterpreterInstructionResult : byte
    {
        Continue,
        ContinueWithSchedulerCheck,
        Call,
        TailCall,
        Return,
        InstructionBudget,
    }
}
