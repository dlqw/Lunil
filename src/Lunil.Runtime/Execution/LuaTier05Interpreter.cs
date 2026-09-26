using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Operations;
using Lunil.Runtime.Values;
using static Lunil.Runtime.Execution.LuaInterpreterInstructionExecutor;

namespace Lunil.Runtime.Execution;

internal static class LuaTier05Interpreter
{
    private static readonly bool DisableInlineCall =
        Environment.GetEnvironmentVariable("LUNIL_T05_DISABLE_CALL") is not null;

    private static readonly bool EnableInlineReturn =
        Environment.GetEnvironmentVariable("LUNIL_T05_INLINE_RETURN") is not null;

    internal static LuaCompiledExit Run(
        LuaExecutionEngine engine,
        LuaExecutionContext context,
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction[] instructions,
        LuaTier05Code code)
    {
        var ip = frame.ProgramCounter;
        try
        {
            return RunLoop(engine, context, state, thread, ref frame, instructions, code, ref ip);
        }
        catch
        {
            frame.ProgramCounter = ip;
            context.SetExitFrame(frame);
            throw;
        }
    }

    private static LuaCompiledExit RunLoop(
        LuaExecutionEngine engine,
        LuaExecutionContext context,
        LuaState state,
        LuaThread thread,
        ref LuaFrame frame,
        LuaIrInstruction[] instructions,
        LuaTier05Code code,
        ref int ip)
    {
        var stream = code.Stream;
        var canonicalToOffset = code.CanonicalToOffset;
        var stack = thread.Stack;
        var frameBase = frame.Base;
        var heap = state.Heap;
        var upvalues = frame.Closure.Upvalues;
        var instructionCount = instructions.Length;
        var pc = canonicalToOffset[ip];
        var untilSafePoint = CompactSafePointInterval;
        var inlineDepth = 0;

        while (true)
        {
            if ((uint)ip >= (uint)instructionCount)
            {
                frame.ProgramCounter = ip;
                context.SetExitFrame(frame);
                return LuaCompiledExit.Continue(ip, context.InstructionsConsumed);
            }

            if (!context.TryReserveSingleInterpreterInstruction())
            {
                frame.ProgramCounter = ip;
                context.SetExitFrame(frame);
                return MaterializeExit(
                    InterpreterInstructionResult.InstructionBudget,
                    context,
                    ip);
            }

            if (--untilSafePoint == 0 || heap.RequiresInterpreterSafePoint)
            {
                untilSafePoint = CompactSafePointInterval;
                frame.ProgramCounter = ip;
                thread.AdvanceFramePoolEpoch();
                if (!engine.TryContinueCompactInterpreterLoop(
                        context,
                        state,
                        thread,
                        frame,
                        runSafePoint: true))
                {
                    context.SetExitFrame(frame);
                    return LuaCompiledExit.Continue(
                        frame.ProgramCounter,
                        context.InstructionsConsumed);
                }
            }

            var op = stream[pc];
            switch (op)
            {
                case (int)LuaTier05Opcode.LoadConstInt:
                    WriteRegister(
                        stack,
                        frame,
                        frameBase,
                        stream[pc + 1],
                        LuaValue.FromInteger(
                            BinaryPrimitives.ReadInt32LittleEndian(stream.AsSpan(pc + 2, 4))));
                    pc += 6;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.LoadConstIntWide:
                    WriteRegister(
                        stack,
                        frame,
                        frameBase,
                        stream[pc + 1],
                        LuaValue.FromInteger(
                            BinaryPrimitives.ReadInt64LittleEndian(stream.AsSpan(pc + 2, 8))));
                    pc += 10;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.LoadConstFloat:
                    WriteRegister(
                        stack,
                        frame,
                        frameBase,
                        stream[pc + 1],
                        LuaValue.FromFloat(BitConverter.Int64BitsToDouble(
                            BinaryPrimitives.ReadInt64LittleEndian(stream.AsSpan(pc + 2, 8)))));
                    pc += 10;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.LoadConst:
                    WriteRegister(
                        stack,
                        frame,
                        frameBase,
                        stream[pc + 1],
                        LuaExecutionEngine.MaterializeConstant(
                            state,
                            thread,
                            frame,
                            stream[pc + 2]));
                    pc += 3;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.LoadNilOne:
                    WriteRegister(stack, frame, frameBase, stream[pc + 1], LuaValue.Nil);
                    pc += 2;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.LoadNil:
                    for (var index = 0; index < stream[pc + 2]; index++)
                    {
                        WriteRegister(
                            stack,
                            frame,
                            frameBase,
                            stream[pc + 1] + index,
                            LuaValue.Nil);
                    }

                    pc += 3;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.Move:
                    WriteRegister(
                        stack,
                        frame,
                        frameBase,
                        stream[pc + 1],
                        ReadRegister(stack, frameBase, stream[pc + 2]));
                    pc += 3;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.SetTop:
                    LuaExecutionEngine.SetFrameTop(thread, frame, frameBase + stream[pc + 1]);
                    pc += 2;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.GetUpvalue:
                    WriteRegister(
                        stack,
                        frame,
                        frameBase,
                        stream[pc + 1],
                        upvalues[stream[pc + 2]].Value);
                    pc += 3;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.SetUpvalue:
                    upvalues[stream[pc + 1]].Value =
                        ReadRegister(stack, frameBase, stream[pc + 2]);
                    pc += 3;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.Jump:
                    ip = BinaryPrimitives.ReadInt32LittleEndian(stream.AsSpan(pc + 1, 4));
                    pc = canonicalToOffset[ip];
                    continue;
                case (int)LuaTier05Opcode.JumpIfFalse:
                case (int)LuaTier05Opcode.JumpIfTrue:
                    {
                        var condition = ReadRegister(stack, frameBase, stream[pc + 1]).IsTruthy;
                        if (stream[pc + 3] != 0)
                        {
                            LuaExecutionEngine.SetFrameTop(
                                thread,
                                frame,
                                frameBase + stream[pc + 2]);
                        }

                        var branchTaken = op == (int)LuaTier05Opcode.JumpIfTrue
                            ? condition
                            : !condition;
                        if (branchTaken)
                        {
                            ip = BinaryPrimitives.ReadInt32LittleEndian(stream.AsSpan(pc + 4, 4));
                            pc = canonicalToOffset[ip];
                        }
                        else
                        {
                            pc += 8;
                            ip++;
                        }

                        continue;
                    }
                case (int)LuaTier05Opcode.NumericForLoop:
                    {
                        var control = stream[pc + 1];
                        var step = ReadRegister(stack, frameBase, control + 2);
                        if (!step.IsInteger)
                        {
                            goto SlowPath;
                        }

                        var counter = ReadRegister(stack, frameBase, control + 1);
                        var count = unchecked((ulong)counter.AsInteger());
                        if (count == 0)
                        {
                            pc += 6;
                            ip++;
                            continue;
                        }

                        var index = ReadRegister(stack, frameBase, control);
                        index = LuaValue.FromInteger(
                            unchecked(index.AsInteger() + step.AsInteger()));
                        WriteRegister(
                            stack,
                            frame,
                            frameBase,
                            control + 1,
                            LuaValue.FromInteger(unchecked((long)(count - 1))));
                        WriteRegister(stack, frame, frameBase, control, index);
                        WriteRegister(stack, frame, frameBase, control + 3, index);
                        ip = BinaryPrimitives.ReadInt32LittleEndian(stream.AsSpan(pc + 2, 4));
                        pc = canonicalToOffset[ip];
                        continue;
                    }
                case (int)LuaTier05Opcode.GetTable:
                    {
                        var target = ReadRegister(stack, frameBase, stream[pc + 2]);
                        if (target.TryGetTable() is not { } table || table.Metatable is not null)
                        {
                            goto SlowPath;
                        }

                        WriteRegister(
                            stack,
                            frame,
                            frameBase,
                            stream[pc + 1],
                            table.Get(ReadRegister(stack, frameBase, stream[pc + 3])));
                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.SetTable:
                    {
                        var target = ReadRegister(stack, frameBase, stream[pc + 1]);
                        var key = ReadRegister(stack, frameBase, stream[pc + 2]);
                        var value = ReadRegister(stack, frameBase, stream[pc + 3]);
                        if (target.TryGetTable() is not { } table || table.Metatable is null)
                        {
                            goto SlowPath;
                        }

                        if (table.TryGetExistingEntry(key, out _, out var entry))
                        {
                            table.SetExistingEntry(entry, key, value);
                        }
                        else
                        {
                            table.Set(key, value);
                        }

                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.Negate:
                case (int)LuaTier05Opcode.BitwiseNot:
                case (int)LuaTier05Opcode.LogicalNot:
                case (int)LuaTier05Opcode.Length:
                    if (!UnaryFast(
                            stack,
                            frame,
                            frameBase,
                            stream,
                            pc,
                            (LuaIrUnaryOperator)(op - (int)LuaTier05Opcode.Negate)))
                    {
                        goto SlowPath;
                    }

                    pc += 3;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryAdd:
                    if (!BinaryFast(
                            stack,
                            frame,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.Add))
                    {
                        goto SlowPath;
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinarySubtract:
                    if (!BinaryFast(
                            stack,
                            frame,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.Subtract))
                    {
                        goto SlowPath;
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryMultiply:
                    if (!BinaryFast(
                            stack,
                            frame,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.Multiply))
                    {
                        goto SlowPath;
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryDivide:
                    if (!BinaryFast(
                            stack,
                            frame,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.Divide))
                    {
                        goto SlowPath;
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryFloorDivide:
                    if (!BinaryFast(
                            stack,
                            frame,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.FloorDivide))
                    {
                        goto SlowPath;
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryModulo:
                    if (!BinaryFast(
                            stack,
                            frame,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.Modulo))
                    {
                        goto SlowPath;
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryPower:
                    if (!BinaryFast(
                            stack,
                            frame,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.Power))
                    {
                        goto SlowPath;
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryEqual:
                    if (!BinaryFast(
                            stack,
                            frame,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.Equal))
                    {
                        goto SlowPath;
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryNotEqual:
                    if (!BinaryFast(
                            stack,
                            frame,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.NotEqual))
                    {
                        goto SlowPath;
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryLessThan:
                    if (!BinaryFast(
                            stack,
                            frame,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.LessThan))
                    {
                        goto SlowPath;
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryLessThanOrEqual:
                    if (!BinaryFast(
                            stack,
                            frame,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.LessThanOrEqual))
                    {
                        goto SlowPath;
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryGreaterThan:
                    if (!BinaryFast(
                            stack,
                            frame,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.GreaterThan))
                    {
                        goto SlowPath;
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryGreaterThanOrEqual:
                    if (!BinaryFast(
                            stack,
                            frame,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.GreaterThanOrEqual))
                    {
                        goto SlowPath;
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryBitwiseAnd:
                    if (!BinaryFast(
                            stack,
                            frame,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.BitwiseAnd))
                    {
                        goto SlowPath;
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryBitwiseOr:
                    if (!BinaryFast(
                            stack,
                            frame,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.BitwiseOr))
                    {
                        goto SlowPath;
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryBitwiseXor:
                    if (!BinaryFast(
                            stack,
                            frame,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.BitwiseXor))
                    {
                        goto SlowPath;
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryShiftLeft:
                    if (!BinaryFast(
                            stack,
                            frame,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.ShiftLeft))
                    {
                        goto SlowPath;
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryShiftRight:
                    if (!BinaryFast(
                            stack,
                            frame,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.ShiftRight))
                    {
                        goto SlowPath;
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.Call:
                    {
                        if (DisableInlineCall ||
                            frame.InstructionRoute != LuaFrameInstructionRoute.Interpreter)
                        {
                            goto SlowPath;
                        }

                        var functionIndex = frameBase + stream[pc + 1];
                        var closure = ReadRegister(stack, frameBase, stream[pc + 1]).TryGetClosure();
                        if (closure is null)
                        {
                            goto SlowPath;
                        }

                        var calleeCode = closure.FunctionVersion.GetOrCreateTier05Code();
                        if (!calleeCode.HasFastInstructions)
                        {
                            goto SlowPath;
                        }

                        var encodedArgumentCount = stream[pc + 2];
                        var expectedResults = stream[pc + 3] == 0 ? -1 : stream[pc + 3] - 1;
                        var argumentStart = functionIndex + 1;
                        var argumentCount = encodedArgumentCount == 0
                            ? Math.Max(0, frame.Top - argumentStart)
                            : encodedArgumentCount - 1;
                        frame.ProgramCounter = ip + 1;
                        var callee = engine.PushFrameFromStack(
                            thread,
                            closure,
                            argumentStart,
                            argumentCount,
                            functionIndex,
                            expectedResults);
                        if (callee.InstructionRoute != LuaFrameInstructionRoute.Interpreter)
                        {
                            context.SetExitFrame(callee);
                            return LuaCompiledExit.Continue(0, context.InstructionsConsumed);
                        }

                        frame = callee;
                        frameBase = callee.Base;
                        upvalues = callee.Closure.Upvalues;
                        instructions = ImmutableCollectionsMarshal.AsArray(callee.Function.Instructions)!;
                        code = calleeCode;
                        stream = code.Stream;
                        canonicalToOffset = code.CanonicalToOffset;
                        instructionCount = instructions.Length;
                        inlineDepth++;
                        ip = 0;
                        pc = 0;
                        continue;
                    }
                case (int)LuaTier05Opcode.Return:
                    {
                        if (!EnableInlineReturn ||
                            inlineDepth == 0 ||
                            frame.Continuation.Kind != LuaContinuationKind.None ||
                            frame.Continuation.ProtectionKind != LuaProtectedCallKind.None ||
                            frame.Continuation.IsCloseHandler ||
                            frame.ToBeClosedSlots.Count != 0 ||
                            frame.IsDebugHook ||
                            thread.UnwindState is not null ||
                            thread.FrameCount <= 1 ||
                            thread.Frames[thread.FrameCount - 2].Continuation.Kind !=
                                LuaContinuationKind.None)
                        {
                            goto SlowPath;
                        }

                        var start = frameBase + stream[pc + 1];
                        var encodedResultCount = stream[pc + 2];
                        var count = encodedResultCount == 0
                            ? Math.Max(0, frame.Top - start)
                            : encodedResultCount - 1;
                        var returnBase = frame.ReturnBase;
                        var expectedResults = frame.ExpectedResults;
                        var callerFrame = thread.Frames[thread.FrameCount - 2];
                        thread.CloseUpvalues(frameBase);
                        var results = thread.Stack.AsReadOnlySpan(start, count);
                        thread.PopFrame();
                        engine.WriteCallResults(
                            thread,
                            callerFrame,
                            returnBase,
                            expectedResults,
                            results);
                        inlineDepth--;
                        frame = callerFrame;
                        frameBase = callerFrame.Base;
                        upvalues = callerFrame.Closure.Upvalues;
                        instructions = ImmutableCollectionsMarshal.AsArray(
                            callerFrame.Function.Instructions)!;
                        code = callerFrame.FunctionVersion.GetOrCreateTier05Code();
                        stream = code.Stream;
                        canonicalToOffset = code.CanonicalToOffset;
                        instructionCount = instructions.Length;
                        ip = callerFrame.ProgramCounter;
                        pc = canonicalToOffset[ip];
                        continue;
                    }
                case (int)LuaTier05Opcode.TailCall:
                    {
                        if (DisableInlineCall ||
                            frame.InstructionRoute != LuaFrameInstructionRoute.Interpreter ||
                            frame.Continuation.Kind != LuaContinuationKind.None ||
                            frame.Continuation.ProtectionKind != LuaProtectedCallKind.None ||
                            frame.Continuation.IsCloseHandler ||
                            frame.ToBeClosedSlots.Count != 0 ||
                            frame.IsDebugHook ||
                            thread.UnwindState is not null)
                        {
                            goto SlowPath;
                        }

                        var tailFunctionIndex = frameBase + stream[pc + 1];
                        var tailClosure =
                            ReadRegister(stack, frameBase, stream[pc + 1]).TryGetClosure();
                        if (tailClosure is null)
                        {
                            goto SlowPath;
                        }

                        var tailCode = tailClosure.FunctionVersion.GetOrCreateTier05Code();
                        if (!tailCode.HasFastInstructions)
                        {
                            goto SlowPath;
                        }

                        var tailEncodedArgumentCount = stream[pc + 2];
                        var tailArgumentStart = tailFunctionIndex + 1;
                        var tailArgumentCount = tailEncodedArgumentCount == 0
                            ? Math.Max(0, frame.Top - tailArgumentStart)
                            : tailEncodedArgumentCount - 1;
                        var tailReturnBase = frame.ReturnBase;
                        var tailExpectedResults = frame.ExpectedResults;
                        frame.ProgramCounter = ip;
                        thread.CloseUpvalues(frameBase);
                        thread.PopFrame();
                        var replacement = engine.PushFrameFromStack(
                            thread,
                            tailClosure,
                            tailArgumentStart,
                            tailArgumentCount,
                            tailReturnBase,
                            tailExpectedResults);
                        if (replacement.InstructionRoute != LuaFrameInstructionRoute.Interpreter)
                        {
                            context.SetExitFrame(replacement);
                            return LuaCompiledExit.Continue(0, context.InstructionsConsumed);
                        }

                        frame = replacement;
                        frameBase = replacement.Base;
                        upvalues = replacement.Closure.Upvalues;
                        instructions = ImmutableCollectionsMarshal.AsArray(
                            replacement.Function.Instructions)!;
                        code = tailCode;
                        stream = code.Stream;
                        canonicalToOffset = code.CanonicalToOffset;
                        instructionCount = instructions.Length;
                        ip = 0;
                        pc = 0;
                        continue;
                    }
                default:
                    goto SlowPath;
            }

SlowPath:
            frame.ProgramCounter = ip;
            var slowResult = ExecuteInstructionCore(
                engine,
                context,
                state,
                thread,
                frame,
                stack,
                frameBase,
                in instructions[ip]);
            if (slowResult == InterpreterInstructionResult.Continue)
            {
                ip = frame.ProgramCounter;
                if ((uint)ip >= (uint)instructionCount)
                {
                    context.SetExitFrame(frame);
                    return LuaCompiledExit.Continue(ip, context.InstructionsConsumed);
                }

                pc = canonicalToOffset[ip];
                continue;
            }

            if (slowResult == InterpreterInstructionResult.ContinueWithSchedulerCheck)
            {
                context.SetExitFrame(frame);
                return LuaCompiledExit.Continue(
                    frame.ProgramCounter,
                    context.InstructionsConsumed);
            }

            frame.ProgramCounter = ip;
            context.SetExitFrame(frame);
            return MaterializeExit(slowResult, context, ip);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool UnaryFast(
        LuaStack stack,
        LuaFrame frame,
        int frameBase,
        byte[] stream,
        int pc,
        LuaIrUnaryOperator operation)
    {
        var operand = ReadRegister(stack, frameBase, stream[pc + 2]);
        if (!LuaRuntimeOperations.TryResolvePrimitiveUnary(operation, operand, out var result))
        {
            return false;
        }

        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool BinaryFast(
        LuaStack stack,
        LuaFrame frame,
        int frameBase,
        byte[] stream,
        int pc,
        LuaIrBinaryOperator operation)
    {
        var left = ReadRegister(stack, frameBase, stream[pc + 2]);
        var right = ReadRegister(stack, frameBase, stream[pc + 3]);
        if (!LuaRuntimeOperations.TryResolvePrimitiveBinary(operation, left, right, out var result))
        {
            return false;
        }

        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
        return true;
    }
}
