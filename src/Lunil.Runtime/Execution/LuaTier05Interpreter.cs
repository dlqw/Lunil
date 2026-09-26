using System.Buffers.Binary;
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
            return RunLoop(engine, context, state, thread, frame, instructions, code, ref ip);
        }
        catch
        {
            frame.ProgramCounter = ip;
            throw;
        }
    }

    private static LuaCompiledExit RunLoop(
        LuaExecutionEngine engine,
        LuaExecutionContext context,
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
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
        var untilSafePoint = 32;

        while (true)
        {
            if ((uint)ip >= (uint)instructionCount)
            {
                frame.ProgramCounter = ip;
                return LuaCompiledExit.Continue(ip, context.InstructionsConsumed);
            }

            if (!context.TryReserveSingleInterpreterInstruction())
            {
                frame.ProgramCounter = ip;
                return MaterializeExit(
                    InterpreterInstructionResult.InstructionBudget,
                    context,
                    ip);
            }

            if (--untilSafePoint == 0 || heap.RequiresInterpreterSafePoint)
            {
                untilSafePoint = 32;
                frame.ProgramCounter = ip;
                if (!engine.TryContinueCompactInterpreterLoop(
                        context,
                        state,
                        thread,
                        frame,
                        runSafePoint: true))
                {
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
                    LuaExecutionEngine.SetFrameTop(
                        thread,
                        frame,
                        frameBase + stream[pc + 1]);
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
                    {
                        var condition = ReadRegister(stack, frameBase, stream[pc + 1]).IsTruthy;
                        if (stream[pc + 3] != 0)
                        {
                            LuaExecutionEngine.SetFrameTop(
                                thread,
                                frame,
                                frameBase + stream[pc + 2]);
                        }

                        if (condition)
                        {
                            pc += 8;
                            ip++;
                        }
                        else
                        {
                            ip = BinaryPrimitives.ReadInt32LittleEndian(stream.AsSpan(pc + 4, 4));
                            pc = canonicalToOffset[ip];
                        }

                        continue;
                    }
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

                        if (condition)
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
                    {
                        var operand = ReadRegister(stack, frameBase, stream[pc + 2]);
                        if (!LuaRuntimeOperations.TryResolvePrimitiveUnary(
                                (LuaIrUnaryOperator)(op - (int)LuaTier05Opcode.Negate),
                                operand,
                                out var unaryResult))
                        {
                            goto SlowPath;
                        }

                        WriteRegister(stack, frame, frameBase, stream[pc + 1], unaryResult);
                        pc += 3;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.BinaryAdd:
                    {
                        var left = ReadRegister(stack, frameBase, stream[pc + 2]);
                        var right = ReadRegister(stack, frameBase, stream[pc + 3]);
                        if (!LuaRuntimeOperations.TryResolvePrimitiveBinary(
                                LuaIrBinaryOperator.Add,
                                left,
                                right,
                                out var result))
                        {
                            goto SlowPath;
                        }

                        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.BinarySubtract:
                    {
                        var left = ReadRegister(stack, frameBase, stream[pc + 2]);
                        var right = ReadRegister(stack, frameBase, stream[pc + 3]);
                        if (!LuaRuntimeOperations.TryResolvePrimitiveBinary(
                                LuaIrBinaryOperator.Subtract,
                                left,
                                right,
                                out var result))
                        {
                            goto SlowPath;
                        }

                        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.BinaryMultiply:
                    {
                        var left = ReadRegister(stack, frameBase, stream[pc + 2]);
                        var right = ReadRegister(stack, frameBase, stream[pc + 3]);
                        if (!LuaRuntimeOperations.TryResolvePrimitiveBinary(
                                LuaIrBinaryOperator.Multiply,
                                left,
                                right,
                                out var result))
                        {
                            goto SlowPath;
                        }

                        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.BinaryDivide:
                    {
                        var left = ReadRegister(stack, frameBase, stream[pc + 2]);
                        var right = ReadRegister(stack, frameBase, stream[pc + 3]);
                        if (!LuaRuntimeOperations.TryResolvePrimitiveBinary(
                                LuaIrBinaryOperator.Divide,
                                left,
                                right,
                                out var result))
                        {
                            goto SlowPath;
                        }

                        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.BinaryFloorDivide:
                    {
                        var left = ReadRegister(stack, frameBase, stream[pc + 2]);
                        var right = ReadRegister(stack, frameBase, stream[pc + 3]);
                        if (!LuaRuntimeOperations.TryResolvePrimitiveBinary(
                                LuaIrBinaryOperator.FloorDivide,
                                left,
                                right,
                                out var result))
                        {
                            goto SlowPath;
                        }

                        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.BinaryModulo:
                    {
                        var left = ReadRegister(stack, frameBase, stream[pc + 2]);
                        var right = ReadRegister(stack, frameBase, stream[pc + 3]);
                        if (!LuaRuntimeOperations.TryResolvePrimitiveBinary(
                                LuaIrBinaryOperator.Modulo,
                                left,
                                right,
                                out var result))
                        {
                            goto SlowPath;
                        }

                        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.BinaryPower:
                    {
                        var left = ReadRegister(stack, frameBase, stream[pc + 2]);
                        var right = ReadRegister(stack, frameBase, stream[pc + 3]);
                        if (!LuaRuntimeOperations.TryResolvePrimitiveBinary(
                                LuaIrBinaryOperator.Power,
                                left,
                                right,
                                out var result))
                        {
                            goto SlowPath;
                        }

                        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.BinaryEqual:
                    {
                        var left = ReadRegister(stack, frameBase, stream[pc + 2]);
                        var right = ReadRegister(stack, frameBase, stream[pc + 3]);
                        if (!LuaRuntimeOperations.TryResolvePrimitiveBinary(
                                LuaIrBinaryOperator.Equal,
                                left,
                                right,
                                out var result))
                        {
                            goto SlowPath;
                        }

                        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.BinaryNotEqual:
                    {
                        var left = ReadRegister(stack, frameBase, stream[pc + 2]);
                        var right = ReadRegister(stack, frameBase, stream[pc + 3]);
                        if (!LuaRuntimeOperations.TryResolvePrimitiveBinary(
                                LuaIrBinaryOperator.NotEqual,
                                left,
                                right,
                                out var result))
                        {
                            goto SlowPath;
                        }

                        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.BinaryLessThan:
                    {
                        var left = ReadRegister(stack, frameBase, stream[pc + 2]);
                        var right = ReadRegister(stack, frameBase, stream[pc + 3]);
                        if (!LuaRuntimeOperations.TryResolvePrimitiveBinary(
                                LuaIrBinaryOperator.LessThan,
                                left,
                                right,
                                out var result))
                        {
                            goto SlowPath;
                        }

                        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.BinaryLessThanOrEqual:
                    {
                        var left = ReadRegister(stack, frameBase, stream[pc + 2]);
                        var right = ReadRegister(stack, frameBase, stream[pc + 3]);
                        if (!LuaRuntimeOperations.TryResolvePrimitiveBinary(
                                LuaIrBinaryOperator.LessThanOrEqual,
                                left,
                                right,
                                out var result))
                        {
                            goto SlowPath;
                        }

                        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.BinaryGreaterThan:
                    {
                        var left = ReadRegister(stack, frameBase, stream[pc + 2]);
                        var right = ReadRegister(stack, frameBase, stream[pc + 3]);
                        if (!LuaRuntimeOperations.TryResolvePrimitiveBinary(
                                LuaIrBinaryOperator.GreaterThan,
                                left,
                                right,
                                out var result))
                        {
                            goto SlowPath;
                        }

                        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.BinaryGreaterThanOrEqual:
                    {
                        var left = ReadRegister(stack, frameBase, stream[pc + 2]);
                        var right = ReadRegister(stack, frameBase, stream[pc + 3]);
                        if (!LuaRuntimeOperations.TryResolvePrimitiveBinary(
                                LuaIrBinaryOperator.GreaterThanOrEqual,
                                left,
                                right,
                                out var result))
                        {
                            goto SlowPath;
                        }

                        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.BinaryBitwiseAnd:
                    {
                        var left = ReadRegister(stack, frameBase, stream[pc + 2]);
                        var right = ReadRegister(stack, frameBase, stream[pc + 3]);
                        if (!LuaRuntimeOperations.TryResolvePrimitiveBinary(
                                LuaIrBinaryOperator.BitwiseAnd,
                                left,
                                right,
                                out var result))
                        {
                            goto SlowPath;
                        }

                        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.BinaryBitwiseOr:
                    {
                        var left = ReadRegister(stack, frameBase, stream[pc + 2]);
                        var right = ReadRegister(stack, frameBase, stream[pc + 3]);
                        if (!LuaRuntimeOperations.TryResolvePrimitiveBinary(
                                LuaIrBinaryOperator.BitwiseOr,
                                left,
                                right,
                                out var result))
                        {
                            goto SlowPath;
                        }

                        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.BinaryBitwiseXor:
                    {
                        var left = ReadRegister(stack, frameBase, stream[pc + 2]);
                        var right = ReadRegister(stack, frameBase, stream[pc + 3]);
                        if (!LuaRuntimeOperations.TryResolvePrimitiveBinary(
                                LuaIrBinaryOperator.BitwiseXor,
                                left,
                                right,
                                out var result))
                        {
                            goto SlowPath;
                        }

                        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.BinaryShiftLeft:
                    {
                        var left = ReadRegister(stack, frameBase, stream[pc + 2]);
                        var right = ReadRegister(stack, frameBase, stream[pc + 3]);
                        if (!LuaRuntimeOperations.TryResolvePrimitiveBinary(
                                LuaIrBinaryOperator.ShiftLeft,
                                left,
                                right,
                                out var result))
                        {
                            goto SlowPath;
                        }

                        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
                        pc += 4;
                        ip++;
                        continue;
                    }
                case (int)LuaTier05Opcode.BinaryShiftRight:
                    {
                        var left = ReadRegister(stack, frameBase, stream[pc + 2]);
                        var right = ReadRegister(stack, frameBase, stream[pc + 3]);
                        if (!LuaRuntimeOperations.TryResolvePrimitiveBinary(
                                LuaIrBinaryOperator.ShiftRight,
                                left,
                                right,
                                out var result))
                        {
                            goto SlowPath;
                        }

                        WriteRegister(stack, frame, frameBase, stream[pc + 1], result);
                        pc += 4;
                        ip++;
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
                    return LuaCompiledExit.Continue(ip, context.InstructionsConsumed);
                }

                pc = canonicalToOffset[ip];
                continue;
            }

            if (slowResult == InterpreterInstructionResult.ContinueWithSchedulerCheck)
            {
                return LuaCompiledExit.Continue(
                    frame.ProgramCounter,
                    context.InstructionsConsumed);
            }

            frame.ProgramCounter = ip;
            return MaterializeExit(slowResult, context, ip);
        }
    }
}
