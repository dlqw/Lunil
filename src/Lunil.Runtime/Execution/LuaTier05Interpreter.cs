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
        var values = stack.Values;
        var frameBase = frame.Base;
        var top = frame.Top;
        var heap = state.Heap;
        var upvalues = frame.Closure.Upvalues;
        var instructionCount = instructions.Length;
        var pc = canonicalToOffset[ip];
        var untilSafePoint = CompactSafePointInterval;

        while (true)
        {
            if ((uint)ip >= (uint)instructionCount)
            {
                frame.ProgramCounter = ip;
                frame.Top = top;
                context.SetExitFrame(frame);
                return LuaCompiledExit.Continue(ip, context.InstructionsConsumed);
            }

            if (!context.TryReserveSingleInterpreterInstruction())
            {
                frame.ProgramCounter = ip;
                frame.Top = top;
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
                frame.Top = top;
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

                top = frame.Top;
                values = stack.Values;
            }

            var op = stream[pc];
            switch (op)
            {
                case (int)LuaTier05Opcode.LoadConstInt:
                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = LuaValue.FromInteger(
                            BinaryPrimitives.ReadInt32LittleEndian(stream.AsSpan(pc + 2, 4)));
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }

                        pc += 6;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.LoadConstIntWide:
                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = LuaValue.FromInteger(
                            BinaryPrimitives.ReadInt64LittleEndian(stream.AsSpan(pc + 2, 8)));
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }

                        pc += 10;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.LoadConstFloat:
                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = LuaValue.FromFloat(BitConverter.Int64BitsToDouble(
                            BinaryPrimitives.ReadInt64LittleEndian(stream.AsSpan(pc + 2, 8))));
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }

                        pc += 10;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.LoadConst:
                    {
                        frame.Top = top;
                        var destination = frameBase + stream[pc + 1];
                        stack.WriteUnchecked(
                            destination,
                            LuaExecutionEngine.MaterializeConstant(
                                state,
                                thread,
                                frame,
                                stream[pc + 2]));
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }

                        pc += 3;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.LoadNilOne:
                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = LuaValue.Nil;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }

                        pc += 2;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.LoadNil:
                    {
                        var destination = frameBase + stream[pc + 1];
                        var count = stream[pc + 2];
                        for (var index = 0; index < count; index++)
                        {
                            values[destination + index] = LuaValue.Nil;
                        }

                        if (top < destination + count)
                        {
                            top = destination + count;
                        }

                        pc += 3;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.Move:
                    {
                        var destination = frameBase + stream[pc + 1];
                        stack.WriteUnchecked(destination, values[frameBase + stream[pc + 2]]);
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }

                        pc += 3;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.SetTop:
                    {
                        var newTop = frameBase + stream[pc + 1];
                        if (top > newTop)
                        {
                            Array.Clear(values, newTop, top - newTop);
                        }

                        top = newTop;
                        pc += 2;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.GetUpvalue:
                    {
                        var destination = frameBase + stream[pc + 1];
                        stack.WriteUnchecked(destination, upvalues[stream[pc + 2]].Value);
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }

                        pc += 3;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.SetUpvalue:
                    upvalues[stream[pc + 1]].Value = values[frameBase + stream[pc + 2]];
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
                        var condition = values[frameBase + stream[pc + 1]].IsTruthy;
                        if (stream[pc + 3] != 0)
                        {
                            var newTop = frameBase + stream[pc + 2];
                            if (top > newTop)
                            {
                                Array.Clear(values, newTop, top - newTop);
                            }

                            top = newTop;
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
                        var step = values[frameBase + control + 2];
                        if (!step.IsInteger)
                        {
                            goto SlowPath;
                        }

                        var count = unchecked((ulong)values[frameBase + control + 1].AsInteger());
                        if (count == 0)
                        {
                            pc += 6;
                            ip++;
                            continue;
                        }

                        var index = LuaValue.FromInteger(unchecked(
                            values[frameBase + control].AsInteger() + step.AsInteger()));
                        values[frameBase + control + 1] = LuaValue.FromInteger(
                            unchecked((long)(count - 1)));
                        values[frameBase + control] = index;
                        values[frameBase + control + 3] = index;
                        if (top <= frameBase + control + 3)
                        {
                            top = frameBase + control + 4;
                        }

                        ip = BinaryPrimitives.ReadInt32LittleEndian(stream.AsSpan(pc + 2, 4));
                        pc = canonicalToOffset[ip];
                        continue;
                    }
                case (int)LuaTier05Opcode.GetTable:
                    {
                        frame.Top = top;
                        var target = values[frameBase + stream[pc + 2]];
                        if (target.TryGetTable() is not { } table || table.Metatable is not null)
                        {
                            goto SlowPath;
                        }

                        var destination = frameBase + stream[pc + 1];
                        stack.WriteUnchecked(
                            destination,
                            table.Get(values[frameBase + stream[pc + 3]]));
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }

                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.SetTable:
                    {
                        frame.Top = top;
                        var target = values[frameBase + stream[pc + 1]];
                        var key = values[frameBase + stream[pc + 2]];
                        var value = values[frameBase + stream[pc + 3]];
                        if (target.TryGetTable() is not { } table || table.Metatable is not null)
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
                            values,
                            frameBase,
                            stream,
                            pc,
                            (LuaIrUnaryOperator)(op - (int)LuaTier05Opcode.Negate),
                            out var unaryResult))
                    {
                        goto SlowPath;
                    }

                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = unaryResult;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }
                    }

                    pc += 3;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryAdd:
                    if (!BinaryFast(
                            values,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.Add,
                            out var binaryAdd))
                    {
                        goto SlowPath;
                    }

                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = binaryAdd;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinarySubtract:
                    if (!BinaryFast(
                            values,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.Subtract,
                            out var binarySubtract))
                    {
                        goto SlowPath;
                    }

                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = binarySubtract;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryMultiply:
                    if (!BinaryFast(
                            values,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.Multiply,
                            out var binaryMultiply))
                    {
                        goto SlowPath;
                    }

                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = binaryMultiply;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryDivide:
                    if (!BinaryFast(
                            values,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.Divide,
                            out var binaryDivide))
                    {
                        goto SlowPath;
                    }

                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = binaryDivide;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryFloorDivide:
                    if (!BinaryFast(
                            values,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.FloorDivide,
                            out var binaryFloorDivide))
                    {
                        goto SlowPath;
                    }

                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = binaryFloorDivide;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryModulo:
                    if (!BinaryFast(
                            values,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.Modulo,
                            out var binaryModulo))
                    {
                        goto SlowPath;
                    }

                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = binaryModulo;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryPower:
                    if (!BinaryFast(
                            values,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.Power,
                            out var binaryPower))
                    {
                        goto SlowPath;
                    }

                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = binaryPower;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryEqual:
                    if (!BinaryFast(
                            values,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.Equal,
                            out var binaryEqual))
                    {
                        goto SlowPath;
                    }

                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = binaryEqual;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryNotEqual:
                    if (!BinaryFast(
                            values,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.NotEqual,
                            out var binaryNotEqual))
                    {
                        goto SlowPath;
                    }

                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = binaryNotEqual;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryLessThan:
                    if (!BinaryFast(
                            values,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.LessThan,
                            out var binaryLessThan))
                    {
                        goto SlowPath;
                    }

                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = binaryLessThan;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryLessThanOrEqual:
                    if (!BinaryFast(
                            values,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.LessThanOrEqual,
                            out var binaryLessThanOrEqual))
                    {
                        goto SlowPath;
                    }

                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = binaryLessThanOrEqual;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryGreaterThan:
                    if (!BinaryFast(
                            values,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.GreaterThan,
                            out var binaryGreaterThan))
                    {
                        goto SlowPath;
                    }

                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = binaryGreaterThan;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryGreaterThanOrEqual:
                    if (!BinaryFast(
                            values,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.GreaterThanOrEqual,
                            out var binaryGreaterThanOrEqual))
                    {
                        goto SlowPath;
                    }

                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = binaryGreaterThanOrEqual;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryBitwiseAnd:
                    if (!BinaryFast(
                            values,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.BitwiseAnd,
                            out var binaryBitwiseAnd))
                    {
                        goto SlowPath;
                    }

                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = binaryBitwiseAnd;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryBitwiseOr:
                    if (!BinaryFast(
                            values,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.BitwiseOr,
                            out var binaryBitwiseOr))
                    {
                        goto SlowPath;
                    }

                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = binaryBitwiseOr;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryBitwiseXor:
                    if (!BinaryFast(
                            values,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.BitwiseXor,
                            out var binaryBitwiseXor))
                    {
                        goto SlowPath;
                    }

                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = binaryBitwiseXor;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryShiftLeft:
                    if (!BinaryFast(
                            values,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.ShiftLeft,
                            out var binaryShiftLeft))
                    {
                        goto SlowPath;
                    }

                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = binaryShiftLeft;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.BinaryShiftRight:
                    if (!BinaryFast(
                            values,
                            frameBase,
                            stream,
                            pc,
                            LuaIrBinaryOperator.ShiftRight,
                            out var binaryShiftRight))
                    {
                        goto SlowPath;
                    }

                    {
                        var destination = frameBase + stream[pc + 1];
                        values[destination] = binaryShiftRight;
                        if (top <= destination)
                        {
                            top = destination + 1;
                        }
                    }

                    pc += 4;
                    ip++;
                    continue;
                case (int)LuaTier05Opcode.Call:
                    {
                        if (frame.InstructionRoute != LuaFrameInstructionRoute.Interpreter)
                        {
                            goto SlowPath;
                        }

                        var functionIndex = frameBase + stream[pc + 1];
                        var closure = values[functionIndex].TryGetClosure();
                        if (closure is null)
                        {
                            goto SlowPath;
                        }

                        var calleeCode = closure.FunctionVersion.GetOrCreateTier05Code();
                        if (!calleeCode.HasFastInstructions)
                        {
                            goto SlowPath;
                        }

                        frame.ProgramCounter = ip + 1;
                        frame.Top = top;
                        var encodedArgumentCount = stream[pc + 2];
                        var expectedResults = stream[pc + 3] == 0 ? -1 : stream[pc + 3] - 1;
                        var argumentStart = functionIndex + 1;
                        var argumentCount = encodedArgumentCount == 0
                            ? Math.Max(0, frame.Top - argumentStart)
                            : encodedArgumentCount - 1;
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
                        top = callee.Top;
                        upvalues = callee.Closure.Upvalues;
                        instructions = ImmutableCollectionsMarshal.AsArray(callee.Function.Instructions)!;
                        code = calleeCode;
                        stream = code.Stream;
                        canonicalToOffset = code.CanonicalToOffset;
                        instructionCount = instructions.Length;
                        values = stack.Values;
                        ip = 0;
                        pc = 0;
                        continue;
                    }
                case (int)LuaTier05Opcode.TailCall:
                    {
                        if (frame.InstructionRoute != LuaFrameInstructionRoute.Interpreter ||
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
                        var tailClosure = values[tailFunctionIndex].TryGetClosure();
                        if (tailClosure is null)
                        {
                            goto SlowPath;
                        }

                        var tailCode = tailClosure.FunctionVersion.GetOrCreateTier05Code();
                        if (!tailCode.HasFastInstructions)
                        {
                            goto SlowPath;
                        }

                        frame.Top = top;
                        var tailEncodedArgumentCount = stream[pc + 2];
                        var tailArgumentStart = tailFunctionIndex + 1;
                        var tailArgumentCount = tailEncodedArgumentCount == 0
                            ? Math.Max(0, frame.Top - tailArgumentStart)
                            : tailEncodedArgumentCount - 1;
                        var tailReturnBase = frame.ReturnBase;
                        var tailExpectedResults = frame.ExpectedResults;
                        frame.ProgramCounter = ip;
                        if (thread.HasOpenUpvalueAtOrAbove(frameBase))
                        {
                            thread.CloseUpvalues(frameBase);
                        }

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
                        top = replacement.Top;
                        upvalues = replacement.Closure.Upvalues;
                        instructions = ImmutableCollectionsMarshal.AsArray(
                            replacement.Function.Instructions)!;
                        code = tailCode;
                        stream = code.Stream;
                        canonicalToOffset = code.CanonicalToOffset;
                        instructionCount = instructions.Length;
                        values = stack.Values;
                        ip = 0;
                        pc = 0;
                        continue;
                    }
                default:
                    goto SlowPath;
            }

        SlowPath:
            frame.ProgramCounter = ip;
            frame.Top = top;
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

                if (heap.RequiresInterpreterSafePoint)
                {
                    untilSafePoint = 1;
                }

                top = frame.Top;
                values = stack.Values;
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
        LuaValue[] values,
        int frameBase,
        byte[] stream,
        int pc,
        LuaIrUnaryOperator operation,
        out LuaValue result)
    {
        var operand = values[frameBase + stream[pc + 2]];
        return LuaRuntimeOperations.TryResolvePrimitiveUnary(operation, operand, out result);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool BinaryFast(
        LuaValue[] values,
        int frameBase,
        byte[] stream,
        int pc,
        LuaIrBinaryOperator operation,
        out LuaValue result)
    {
        if (operation == LuaIrBinaryOperator.Concatenate)
        {
            result = LuaValue.Nil;
            return false;
        }

        var left = values[frameBase + stream[pc + 2]];
        var right = values[frameBase + stream[pc + 3]];
        return LuaRuntimeOperations.TryResolvePrimitiveBinary(operation, left, right, out result);
    }
}
