using System.Collections.Immutable;
using Luac.IR.Canonical;
using Luac.Runtime.Values;

namespace Luac.Runtime.Execution;

/// <summary>A resumable baseline interpreter over canonical IR using explicit Lua frames.</summary>
public sealed class LuaInterpreter
{
    private readonly LuaInterpreterOptions _options;

    public LuaInterpreter(LuaInterpreterOptions? options = null)
    {
        _options = options ?? LuaInterpreterOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumInstructionCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumStackSlots);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumCallDepth);
    }

    public LuaExecutionResult Execute(
        LuaState state,
        LuaClosure closure,
        ReadOnlySpan<LuaValue> arguments = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(closure);
        var verificationErrors = LuaIrVerifier.Verify(closure.Module);
        if (!verificationErrors.IsEmpty)
        {
            throw new LuaRuntimeException(
                $"Cannot execute invalid canonical IR: {verificationErrors[0].Message}");
        }

        var thread = state.MainThread;
        thread.Reset();
        PushFrame(thread, closure, arguments, 0, -1);
        thread.Status = LuaThreadStatus.Running;
        var instructionCount = 0L;

        try
        {
            while (thread.FrameCount != 0)
            {
                if (++instructionCount > _options.MaximumInstructionCount)
                {
                    throw new LuaRuntimeException("The interpreter instruction budget was exceeded.");
                }

                var frame = thread.CurrentFrame;
                var instruction = frame.Closure.Function.Instructions[frame.ProgramCounter];
                var result = ExecuteInstruction(state, thread, frame, instruction);
                if (result is not null)
                {
                    thread.Status = LuaThreadStatus.Dead;
                    return new LuaExecutionResult(LuaVmSignal.Completed, result.Value);
                }
            }

            throw new InvalidOperationException("The Lua frame stack became empty without a result.");
        }
        catch
        {
            thread.Status = LuaThreadStatus.Error;
            throw;
        }
    }

    private ImmutableArray<LuaValue>? ExecuteInstruction(
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        switch (instruction.Opcode)
        {
            case LuaIrOpcode.LoadConstant:
                Write(thread, frame, instruction.A, MaterializeConstant(
                    state,
                    frame.Closure.Function.Constants[instruction.B]));
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.LoadNil:
                for (var index = 0; index < instruction.B; index++)
                {
                    Write(thread, frame, instruction.A + index, LuaValue.Nil);
                }

                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.Move:
                Write(thread, frame, instruction.A, Read(thread, frame, instruction.B));
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.GetUpvalue:
                Write(thread, frame, instruction.A, frame.Closure.Upvalues[instruction.B].Value);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.SetUpvalue:
                frame.Closure.Upvalues[instruction.A].Value = Read(thread, frame, instruction.B);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.NewTable:
                Write(thread, frame, instruction.A, LuaValue.FromTable(new LuaTable()));
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.GetTable:
                Write(
                    thread,
                    frame,
                    instruction.A,
                    Read(thread, frame, instruction.B).AsTable().Get(
                        Read(thread, frame, instruction.C)));
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.SetTable:
                Read(thread, frame, instruction.A).AsTable().Set(
                    Read(thread, frame, instruction.B),
                    Read(thread, frame, instruction.C));
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.SetList:
                ExecuteSetList(thread, frame, instruction);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.Closure:
                Write(thread, frame, instruction.A, LuaValue.FromFunction(
                    CreateClosure(thread, frame, instruction.B)));
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.VarArg:
                ExecuteVarArg(thread, frame, instruction);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.Unary:
                Write(
                    thread,
                    frame,
                    instruction.A,
                    LuaValueOperations.Unary(
                        (LuaIrUnaryOperator)instruction.C,
                        Read(thread, frame, instruction.B)));
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.Binary:
                Write(
                    thread,
                    frame,
                    instruction.A,
                    LuaValueOperations.Binary(
                        state,
                        (LuaIrBinaryOperator)instruction.D,
                        Read(thread, frame, instruction.B),
                        Read(thread, frame, instruction.C)));
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.Jump:
                if (instruction.C >= 0)
                {
                    CloseFrom(thread, frame, instruction.C);
                }

                frame.ProgramCounter = instruction.B;
                break;
            case LuaIrOpcode.JumpIfFalse:
                frame.ProgramCounter = Read(thread, frame, instruction.A).IsTruthy
                    ? frame.ProgramCounter + 1
                    : instruction.B;
                break;
            case LuaIrOpcode.JumpIfTrue:
                frame.ProgramCounter = Read(thread, frame, instruction.A).IsTruthy
                    ? instruction.B
                    : frame.ProgramCounter + 1;
                break;
            case LuaIrOpcode.Call:
                ExecuteCall(state, thread, frame, instruction, tailCall: false);
                break;
            case LuaIrOpcode.TailCall:
                ExecuteCall(state, thread, frame, instruction, tailCall: true);
                break;
            case LuaIrOpcode.Return:
                return ExecuteReturn(thread, frame, instruction);
            case LuaIrOpcode.Close:
                CloseFrom(thread, frame, instruction.A);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.MarkToBeClosed:
                frame.ToBeClosedSlots.Add(frame.Base + instruction.A);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.NumericForPrepare:
                ExecuteNumericForPrepare(thread, frame, instruction);
                break;
            case LuaIrOpcode.NumericForLoop:
                ExecuteNumericForLoop(thread, frame, instruction);
                break;
            default:
                throw new InvalidOperationException($"Unsupported canonical opcode {instruction.Opcode}.");
        }

        return null;
    }

    private void ExecuteCall(
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction,
        bool tailCall)
    {
        var functionIndex = frame.Base + instruction.A;
        var argumentStart = functionIndex + 1;
        var argumentCount = instruction.B < 0
            ? Math.Max(0, frame.Top - argumentStart)
            : instruction.B;
        var arguments = new LuaValue[argumentCount];
        for (var index = 0; index < argumentCount; index++)
        {
            arguments[index] = thread.Stack[argumentStart + index];
        }

        var function = thread.Stack[functionIndex];
        var closure = function.TryGetClosure();
        if (closure is not null)
        {
            var returnBase = tailCall ? frame.ReturnBase : functionIndex;
            var expectedResults = tailCall ? frame.ExpectedResults : instruction.C;
            if (tailCall)
            {
                CloseFrom(thread, frame, 0);
                thread.PopFrame();
            }
            else
            {
                frame.ProgramCounter++;
            }

            PushFrame(thread, closure, arguments, returnBase, expectedResults);
            return;
        }

        var native = function.TryGetNativeFunction() ??
            throw new LuaRuntimeException($"Attempt to call a {function.Kind} value.");
        var results = native.Body(state, arguments);
        if (tailCall)
        {
            var syntheticReturn = new LuaIrInstruction(
                LuaIrOpcode.Return,
                instruction.A,
                results.Length,
                span: instruction.Span);
            for (var index = 0; index < results.Length; index++)
            {
                thread.Stack[functionIndex + index] = results[index];
            }

            ExecuteReturn(thread, frame, syntheticReturn);
        }
        else
        {
            WriteCallResults(thread, frame, functionIndex, instruction.C, results);
            frame.ProgramCounter++;
        }
    }

    private static ImmutableArray<LuaValue>? ExecuteReturn(
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        var start = frame.Base + instruction.A;
        var count = instruction.B < 0 ? Math.Max(0, frame.Top - start) : instruction.B;
        var results = new LuaValue[count];
        for (var index = 0; index < count; index++)
        {
            results[index] = thread.Stack[start + index];
        }

        CloseFrom(thread, frame, 0);
        thread.PopFrame();
        if (thread.FrameCount == 0)
        {
            return results.ToImmutableArray();
        }

        WriteCallResults(
            thread,
            thread.CurrentFrame,
            frame.ReturnBase,
            frame.ExpectedResults,
            results);
        return null;
    }

    private void PushFrame(
        LuaThread thread,
        LuaClosure closure,
        ReadOnlySpan<LuaValue> arguments,
        int returnBase,
        int expectedResults)
    {
        if (thread.FrameCount >= _options.MaximumCallDepth)
        {
            throw new LuaRuntimeException("The Lua call depth limit was exceeded.");
        }

        var function = closure.Function;
        var @base = thread.FrameCount == 0 ? 0 : returnBase + 1;
        var required = checked(@base + function.RegisterCount);
        if (required > _options.MaximumStackSlots)
        {
            throw new LuaRuntimeException("The Lua stack slot limit was exceeded.");
        }

        thread.Stack.EnsureCapacity(required);
        thread.Stack.Clear(@base, function.RegisterCount);
        var fixedArguments = Math.Min(arguments.Length, function.ParameterCount);
        for (var index = 0; index < fixedArguments; index++)
        {
            thread.Stack[@base + index] = arguments[index];
        }

        var varArgs = function.IsVarArg && arguments.Length > function.ParameterCount
            ? arguments[function.ParameterCount..].ToArray()
            : [];
        thread.PushFrame(new LuaFrame(
            closure,
            @base,
            @base + function.ParameterCount,
            returnBase,
            expectedResults,
            varArgs));
    }

    private static void WriteCallResults(
        LuaThread thread,
        LuaFrame caller,
        int returnBase,
        int expectedResults,
        ReadOnlySpan<LuaValue> results)
    {
        var count = expectedResults < 0 ? results.Length : expectedResults;
        for (var index = 0; index < count; index++)
        {
            thread.Stack[returnBase + index] = index < results.Length ? results[index] : LuaValue.Nil;
        }

        caller.Top = returnBase + count;
    }

    private static LuaClosure CreateClosure(LuaThread thread, LuaFrame parent, int functionId)
    {
        var function = parent.Closure.Module.Functions[functionId];
        var upvalues = new LuaUpvalue[function.Upvalues.Length];
        for (var index = 0; index < function.Upvalues.Length; index++)
        {
            var descriptor = function.Upvalues[index];
            upvalues[index] = descriptor.SourceKind switch
            {
                LuaIrUpvalueSourceKind.Register =>
                    thread.GetOrCreateOpenUpvalue(parent.Base + descriptor.SourceIndex),
                LuaIrUpvalueSourceKind.Upvalue => parent.Closure.Upvalues[descriptor.SourceIndex],
                _ => throw new InvalidOperationException("A nested closure cannot import a host environment directly."),
            };
        }

        return new LuaClosure(parent.Closure.Module, function, upvalues);
    }

    private static void ExecuteSetList(
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        var table = Read(thread, frame, instruction.A).AsTable();
        var source = frame.Base + instruction.C;
        var count = instruction.D < 0 ? Math.Max(0, frame.Top - source) : instruction.D;
        for (var index = 0; index < count; index++)
        {
            table.Set(
                LuaValue.FromInteger(instruction.B + index),
                thread.Stack[source + index]);
        }
    }

    private static void ExecuteVarArg(
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        var count = instruction.B < 0 ? frame.VarArgStorage.Length : instruction.B;
        for (var index = 0; index < count; index++)
        {
            Write(
                thread,
                frame,
                instruction.A + index,
                index < frame.VarArgStorage.Length ? frame.VarArgStorage[index] : LuaValue.Nil);
        }

        if (instruction.B < 0)
        {
            frame.Top = frame.Base + instruction.A + count;
        }
    }

    private static void ExecuteNumericForPrepare(
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        var initial = Read(thread, frame, instruction.A);
        var limit = Read(thread, frame, instruction.A + 1);
        var step = Read(thread, frame, instruction.A + 2);
        var integerMode = initial.Kind == LuaValueKind.Integer &&
            limit.Kind == LuaValueKind.Integer && step.Kind == LuaValueKind.Integer;
        bool enters;
        if (integerMode)
        {
            enters = step.AsInteger() >= 0
                ? initial.AsInteger() <= limit.AsInteger()
                : initial.AsInteger() >= limit.AsInteger();
        }
        else
        {
            initial = LuaValue.FromFloat(initial.AsFloat());
            limit = LuaValue.FromFloat(limit.AsFloat());
            step = LuaValue.FromFloat(step.AsFloat());
            Write(thread, frame, instruction.A, initial);
            Write(thread, frame, instruction.A + 1, limit);
            Write(thread, frame, instruction.A + 2, step);
            enters = step.AsFloat() >= 0
                ? initial.AsFloat() <= limit.AsFloat()
                : initial.AsFloat() >= limit.AsFloat();
        }

        if (enters)
        {
            Write(thread, frame, instruction.A + 3, initial);
            frame.ProgramCounter++;
        }
        else
        {
            frame.ProgramCounter = instruction.B;
        }
    }

    private static void ExecuteNumericForLoop(
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        var index = Read(thread, frame, instruction.A);
        var limit = Read(thread, frame, instruction.A + 1);
        var step = Read(thread, frame, instruction.A + 2);
        bool continues;
        if (index.Kind == LuaValueKind.Integer && limit.Kind == LuaValueKind.Integer &&
            step.Kind == LuaValueKind.Integer)
        {
            index = LuaValue.FromInteger(unchecked(index.AsInteger() + step.AsInteger()));
            continues = step.AsInteger() >= 0
                ? index.AsInteger() <= limit.AsInteger()
                : index.AsInteger() >= limit.AsInteger();
        }
        else
        {
            index = LuaValue.FromFloat(index.AsFloat() + step.AsFloat());
            continues = step.AsFloat() >= 0
                ? index.AsFloat() <= limit.AsFloat()
                : index.AsFloat() >= limit.AsFloat();
        }

        Write(thread, frame, instruction.A, index);
        if (continues)
        {
            Write(thread, frame, instruction.A + 3, index);
            frame.ProgramCounter = instruction.B;
        }
        else
        {
            frame.ProgramCounter++;
        }
    }

    private static void CloseFrom(LuaThread thread, LuaFrame frame, int register)
    {
        var absolute = frame.Base + register;
        for (var index = frame.ToBeClosedSlots.Count - 1; index >= 0; index--)
        {
            var slot = frame.ToBeClosedSlots[index];
            if (slot < absolute)
            {
                continue;
            }

            var value = thread.Stack[slot];
            if (!value.IsNil)
            {
                throw new LuaRuntimeException(
                    $"To-be-closed value of kind {value.Kind} has no __close metamethod.");
            }

            frame.ToBeClosedSlots.RemoveAt(index);
        }

        thread.CloseUpvalues(absolute);
    }

    private static LuaValue MaterializeConstant(LuaState state, LuaIrConstant constant) =>
        constant.Kind switch
        {
            LuaIrConstantKind.Nil => LuaValue.Nil,
            LuaIrConstantKind.Boolean => LuaValue.FromBoolean(constant.Boolean),
            LuaIrConstantKind.Integer => LuaValue.FromInteger(constant.Integer),
            LuaIrConstantKind.Float => LuaValue.FromFloat(constant.Float),
            LuaIrConstantKind.String => LuaValue.FromString(
                state.Strings.GetOrCreate(constant.Bytes.AsSpan())),
            _ => throw new InvalidOperationException("Unknown canonical constant kind."),
        };

    private static LuaValue Read(LuaThread thread, LuaFrame frame, int register) =>
        thread.Stack[frame.Base + register];

    private static void Write(LuaThread thread, LuaFrame frame, int register, LuaValue value)
    {
        var index = frame.Base + register;
        thread.Stack[index] = value;
        frame.Top = Math.Max(frame.Top, index + 1);
    }
}
