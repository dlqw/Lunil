using System.Collections.Immutable;
using Luac.IR.Canonical;
using Luac.Runtime.Memory;
using Luac.Runtime.Operations;
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
        state.Heap.ValidateValue(LuaValue.FromFunction(closure));
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
            while (thread.FrameCount != 0 || thread.UnwindState is not null)
            {
                if (++instructionCount > _options.MaximumInstructionCount)
                {
                    throw new LuaRuntimeException("The interpreter instruction budget was exceeded.");
                }

                if (thread.UnwindState is { ActiveCloseCall: null })
                {
                    try
                    {
                        if (ContinueUnwind(state, thread, out var unprotectedError))
                        {
                            throw new LuaRuntimeException(unprotectedError);
                        }
                    }
                    catch (LuaRuntimeException exception)
                    {
                        if (thread.UnwindState is null)
                        {
                            throw;
                        }

                        thread.UnwindState.Error = MaterializeError(state, exception);
                    }

                    continue;
                }

                var frame = thread.CurrentFrame;
                ImmutableArray<LuaValue>? result;
                try
                {
                    if (frame.PendingTailProtectedReturnRegister >= 0)
                    {
                        var returnRegister = frame.PendingTailProtectedReturnRegister;
                        frame.PendingTailProtectedReturnRegister = -1;
                        result = ExecuteReturn(
                            state,
                            thread,
                            frame,
                            new LuaIrInstruction(LuaIrOpcode.Return, returnRegister, -1));
                    }
                    else
                    {
                        var instruction =
                            frame.Closure.Function.Instructions[frame.ProgramCounter];
                        result = ExecuteInstruction(state, thread, frame, instruction);
                    }
                }
                catch (LuaRuntimeException exception)
                {
                    var error = MaterializeError(state, exception);
                    if (thread.UnwindState is not null)
                    {
                        thread.UnwindState.Error = error;
                        thread.UnwindState.ActiveCloseCall = null;
                        continue;
                    }

                    BeginUnwind(thread, error);
                    continue;
                }

                if (result is not null)
                {
                    thread.Status = LuaThreadStatus.Dead;
                    return new LuaExecutionResult(LuaVmSignal.Completed, result.Value);
                }

                state.Heap.SafePoint();
                RunPendingFinalizer(state, thread);
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
            case LuaIrOpcode.SetTop:
                {
                    var newTop = frame.Base + instruction.A;
                    if (frame.Top > newTop)
                    {
                        thread.Stack.Clear(newTop, frame.Top - newTop);
                    }

                    frame.Top = newTop;
                    frame.ProgramCounter++;
                    break;
                }
            case LuaIrOpcode.GetUpvalue:
                Write(thread, frame, instruction.A, frame.Closure.Upvalues[instruction.B].Value);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.SetUpvalue:
                frame.Closure.Upvalues[instruction.A].Value = Read(thread, frame, instruction.B);
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.NewTable:
                Write(thread, frame, instruction.A, LuaValue.FromTable(state.CreateTable()));
                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.GetTable:
                ExecuteOperation(
                    state,
                    thread,
                    frame,
                    LuaRuntimeOperations.GetIndex(
                        state,
                        Read(thread, frame, instruction.B),
                        Read(thread, frame, instruction.C)),
                    frame.Base + instruction.A,
                    expectedResults: 1);
                break;
            case LuaIrOpcode.SetTable:
                ExecuteOperation(
                    state,
                    thread,
                    frame,
                    LuaRuntimeOperations.SetIndex(
                        state,
                        Read(thread, frame, instruction.A),
                        Read(thread, frame, instruction.B),
                        Read(thread, frame, instruction.C)),
                    frame.Top,
                    expectedResults: 0);
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
                ExecuteOperation(
                    state,
                    thread,
                    frame,
                    LuaRuntimeOperations.Unary(
                        state,
                        (LuaIrUnaryOperator)instruction.C,
                        Read(thread, frame, instruction.B)),
                    frame.Base + instruction.A,
                    expectedResults: 1);
                break;
            case LuaIrOpcode.Binary:
                ExecuteOperation(
                    state,
                    thread,
                    frame,
                    LuaRuntimeOperations.Binary(
                        state,
                        (LuaIrBinaryOperator)instruction.D,
                        Read(thread, frame, instruction.B),
                        Read(thread, frame, instruction.C)),
                    frame.Base + instruction.A,
                    expectedResults: 1);
                break;
            case LuaIrOpcode.Jump:
                if (instruction.C >= 0 &&
                    TryCloseFrom(state, thread, frame, instruction.C, LuaValue.Nil))
                {
                    break;
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
                _ = ExecuteCall(state, thread, frame, instruction, tailCall: false);
                break;
            case LuaIrOpcode.TailCall:
                return ExecuteCall(state, thread, frame, instruction, tailCall: true);
            case LuaIrOpcode.Return:
                return ExecuteReturn(state, thread, frame, instruction);
            case LuaIrOpcode.Close:
                if (TryCloseFrom(state, thread, frame, instruction.A, LuaValue.Nil))
                {
                    break;
                }

                frame.ProgramCounter++;
                break;
            case LuaIrOpcode.MarkToBeClosed:
                {
                    var value = Read(thread, frame, instruction.A);
                    if (value.IsTruthy)
                    {
                        var close = LuaRuntimeOperations.GetMetamethod(
                            state,
                            value,
                            LuaMetamethod.Close);
                        if (close.IsNil)
                        {
                            throw new LuaRuntimeException(
                                $"To-be-closed value of kind {value.Kind} has no __close metamethod.");
                        }

                        frame.ToBeClosedSlots.Add(frame.Base + instruction.A);
                    }

                    frame.ProgramCounter++;
                    break;
                }
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

    private ImmutableArray<LuaValue>? ExecuteCall(
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
        LuaOperationResolution resolvedCall;
        if (tailCall && frame.PendingTailCall is { } pending)
        {
            resolvedCall = LuaOperationResolution.Call(
                pending.Callable,
                pending.Arguments);
        }
        else
        {
            var arguments = new LuaValue[argumentCount];
            for (var index = 0; index < argumentCount; index++)
            {
                arguments[index] = thread.Stack[argumentStart + index];
            }

            resolvedCall = LuaRuntimeOperations.ResolveCall(
                state,
                thread.Stack[functionIndex],
                arguments);
            if (tailCall)
            {
                frame.PendingTailCall = new LuaPendingTailCall(
                    resolvedCall.Callable,
                    resolvedCall.Arguments);
                thread.Owner.WriteBarrier(thread, resolvedCall.Callable);
                foreach (var value in resolvedCall.Arguments)
                {
                    thread.Owner.WriteBarrier(thread, value);
                }
            }
        }

        var function = resolvedCall.Callable;
        var resolvedArguments = resolvedCall.Arguments;
        if (tailCall && TryCloseFrom(state, thread, frame, 0, LuaValue.Nil))
        {
            return null;
        }

        frame.PendingTailCall = null;
        if (function.TryGetNativeFunction() is { Kind: not LuaNativeFunctionKind.Normal } intrinsic)
        {
            if (tailCall)
            {
                frame.PendingTailProtectedReturnRegister = instruction.A;
                ExecuteProtectedIntrinsic(
                    state,
                    thread,
                    frame,
                    instruction with { Opcode = LuaIrOpcode.Call, C = -1 },
                    intrinsic,
                    resolvedArguments,
                    functionIndex);
            }
            else
            {
                ExecuteProtectedIntrinsic(
                    state,
                    thread,
                    frame,
                    instruction,
                    intrinsic,
                    resolvedArguments,
                    functionIndex);
            }

            return null;
        }

        var closure = function.TryGetClosure();
        if (closure is not null)
        {
            var returnBase = tailCall ? frame.ReturnBase : functionIndex;
            var expectedResults = tailCall ? frame.ExpectedResults : instruction.C;
            if (tailCall)
            {
                var protectionKind = frame.ProtectionKind;
                var errorHandler = frame.ErrorHandler;
                var isCloseHandler = frame.IsCloseHandler;
                thread.PopFrame();
                var replacement = PushFrame(
                    thread,
                    closure,
                    resolvedArguments,
                    returnBase,
                    expectedResults,
                    protectionKind,
                    errorHandler,
                    isCloseHandler);
                if (ReferenceEquals(thread.UnwindState?.ActiveCloseCall, frame))
                {
                    thread.UnwindState.ActiveCloseCall = replacement;
                }

                return null;
            }

            frame.ProgramCounter++;
            PushFrame(thread, closure, resolvedArguments, returnBase, expectedResults);
            return null;
        }

        var native = function.TryGetNativeFunction() ??
            throw new InvalidOperationException("Resolved callable is not a function.");
        var results = native.Body(state, resolvedArguments);
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

            return ExecuteReturn(state, thread, frame, syntheticReturn);
        }
        else
        {
            WriteCallResults(thread, frame, functionIndex, instruction.C, results);
            frame.ProgramCounter++;
        }

        return null;
    }

    private void ExecuteOperation(
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        LuaOperationResolution resolution,
        int returnBase,
        int expectedResults)
    {
        if (!resolution.RequiresCall)
        {
            if (expectedResults > 0)
            {
                thread.Stack[returnBase] = resolution.Value;
                frame.Top = Math.Max(frame.Top, returnBase + 1);
            }

            frame.ProgramCounter++;
            return;
        }

        var callable = LuaRuntimeOperations.ResolveCall(
            state,
            resolution.Callable,
            resolution.Arguments);
        var closure = callable.Callable.TryGetClosure();
        if (closure is not null)
        {
            frame.PendingResultTransform = resolution.Transform;
            frame.ProgramCounter++;
            PushFrame(
                thread,
                closure,
                callable.Arguments,
                returnBase,
                expectedResults);
            return;
        }

        var native = callable.Callable.TryGetNativeFunction() ??
            throw new InvalidOperationException("Resolved metamethod is not callable.");
        var results = native.Body(state, callable.Arguments);
        WriteCallResults(thread, frame, returnBase, expectedResults, results);
        ApplyPendingTransform(thread, frame, returnBase, resolution.Transform);
        frame.ProgramCounter++;
    }

    private void ExecuteProtectedIntrinsic(
        LuaState state,
        LuaThread thread,
        LuaFrame caller,
        LuaIrInstruction instruction,
        LuaNativeFunction intrinsic,
        LuaValue[] arguments,
        int returnBase)
    {
        var required = intrinsic.Kind == LuaNativeFunctionKind.ProtectedCall ? 1 : 2;
        if (arguments.Length < required)
        {
            throw new LuaRuntimeException($"Bad argument count to {intrinsic.Name}.");
        }

        var target = arguments[0];
        var handler = intrinsic.Kind == LuaNativeFunctionKind.ProtectedCallWithHandler
            ? arguments[1]
            : LuaValue.Nil;
        if (intrinsic.Kind == LuaNativeFunctionKind.ProtectedCallWithHandler &&
            handler.Kind != LuaValueKind.Function)
        {
            throw new LuaRuntimeException(
                $"Bad argument #2 to '{intrinsic.Name}' (function expected, got {handler.Kind}).");
        }

        var targetArguments = arguments.AsSpan(required).ToArray();
        LuaOperationResolution resolved;
        try
        {
            resolved = LuaRuntimeOperations.ResolveCall(state, target, targetArguments);
        }
        catch (LuaRuntimeException exception)
        {
            CompleteProtectedFailure(
                state,
                thread,
                caller,
                returnBase,
                instruction.C,
                intrinsic.Kind,
                handler,
                MaterializeError(state, exception));
            caller.ProgramCounter++;
            return;
        }

        var closure = resolved.Callable.TryGetClosure();
        if (closure is not null)
        {
            caller.ProgramCounter++;
            PushFrame(
                thread,
                closure,
                resolved.Arguments,
                returnBase,
                instruction.C,
                intrinsic.Kind == LuaNativeFunctionKind.ProtectedCall
                    ? LuaProtectedCallKind.ProtectedCall
                    : LuaProtectedCallKind.ProtectedCallWithHandler,
                handler);
            return;
        }

        var native = resolved.Callable.TryGetNativeFunction() ??
            throw new InvalidOperationException("Resolved protected target is not callable.");
        try
        {
            var results = native.Body(state, resolved.Arguments);
            WriteProtectedResults(
                thread,
                caller,
                returnBase,
                instruction.C,
                succeeded: true,
                results);
        }
        catch (LuaRuntimeException exception)
        {
            CompleteProtectedFailure(
                state,
                thread,
                caller,
                returnBase,
                instruction.C,
                intrinsic.Kind,
                handler,
                MaterializeError(state, exception));
        }

        caller.ProgramCounter++;
    }

    private static void BeginUnwind(LuaThread thread, LuaValue error)
    {
        LuaFrame? boundary = null;
        for (var index = thread.Frames.Count - 1; index >= 0; index--)
        {
            if (thread.Frames[index].ProtectionKind != LuaProtectedCallKind.None)
            {
                boundary = thread.Frames[index];
                break;
            }
        }

        thread.UnwindState = new LuaUnwindState(boundary, error);
    }

    private void CompleteProtectedFailure(
        LuaState state,
        LuaThread thread,
        LuaFrame caller,
        int returnBase,
        int expectedResults,
        LuaNativeFunctionKind kind,
        LuaValue handler,
        LuaValue error)
    {
        if (kind == LuaNativeFunctionKind.ProtectedCall)
        {
            WriteProtectedResults(
                thread,
                caller,
                returnBase,
                expectedResults,
                succeeded: false,
                [error]);
            return;
        }

        InvokeErrorHandler(
            state,
            thread,
            caller,
            returnBase,
            expectedResults,
            handler,
            error);
    }

    private bool ContinueUnwind(
        LuaState state,
        LuaThread thread,
        out LuaValue unprotectedError)
    {
        var unwind = thread.UnwindState ??
            throw new InvalidOperationException("No Lua unwind is active.");
        while (thread.FrameCount > 0)
        {
            var frame = thread.CurrentFrame;
            if (TryCloseFrom(state, thread, frame, 0, unwind.Error))
            {
                unprotectedError = LuaValue.Nil;
                return false;
            }

            thread.PopFrame();
            if (!ReferenceEquals(frame, unwind.Boundary))
            {
                continue;
            }

            thread.UnwindState = null;
            if (thread.FrameCount == 0)
            {
                unprotectedError = unwind.Error;
                return true;
            }

            var caller = thread.CurrentFrame;
            if (frame.ProtectionKind == LuaProtectedCallKind.ProtectedCallWithHandler)
            {
                InvokeErrorHandler(
                    state,
                    thread,
                    caller,
                    frame.ReturnBase,
                    frame.ExpectedResults,
                    frame.ErrorHandler,
                    unwind.Error);
            }
            else if (frame.ProtectionKind == LuaProtectedCallKind.Finalizer)
            {
                state.ReportWarning(unwind.Error);
            }
            else if (frame.ProtectionKind == LuaProtectedCallKind.ErrorHandler)
            {
                WriteProtectedResults(
                    thread,
                    caller,
                    frame.ReturnBase,
                    frame.ExpectedResults,
                    succeeded: false,
                    [CreateErrorInErrorHandling(state)]);
            }
            else
            {
                WriteProtectedResults(
                    thread,
                    caller,
                    frame.ReturnBase,
                    frame.ExpectedResults,
                    succeeded: false,
                    [unwind.Error]);
            }

            unprotectedError = LuaValue.Nil;
            return false;
        }

        thread.UnwindState = null;
        unprotectedError = unwind.Error;
        return true;
    }

    private void RunPendingFinalizer(LuaState state, LuaThread thread)
    {
        if (!state.Heap.TryTakePendingFinalizer(out var target, out var finalizer))
        {
            return;
        }

        if (target is not LuaTable table)
        {
            LuaHeap.CompleteFinalizer(target);
            return;
        }

        LuaOperationResolution resolved;
        try
        {
            resolved = LuaRuntimeOperations.ResolveCall(
                state,
                finalizer,
                [LuaValue.FromTable(table)]);
        }
        catch (LuaRuntimeException exception)
        {
            state.ReportWarning(MaterializeError(state, exception));
            LuaHeap.CompleteFinalizer(target);
            return;
        }

        LuaHeap.CompleteFinalizer(target);
        if (resolved.Callable.TryGetClosure() is { } closure)
        {
            var caller = thread.CurrentFrame;
            PushFrame(
                thread,
                closure,
                resolved.Arguments,
                caller.Top,
                expectedResults: 0,
                protectionKind: LuaProtectedCallKind.Finalizer);
            return;
        }

        try
        {
            resolved.Callable.TryGetNativeFunction()!.Body(state, resolved.Arguments);
        }
        catch (LuaRuntimeException exception)
        {
            state.ReportWarning(MaterializeError(state, exception));
        }
    }

    private void InvokeErrorHandler(
        LuaState state,
        LuaThread thread,
        LuaFrame caller,
        int returnBase,
        int expectedResults,
        LuaValue handler,
        LuaValue error)
    {
        LuaOperationResolution resolved;
        try
        {
            resolved = LuaRuntimeOperations.ResolveCall(state, handler, [error]);
        }
        catch (LuaRuntimeException)
        {
            WriteProtectedResults(
                thread,
                caller,
                returnBase,
                expectedResults,
                succeeded: false,
                [CreateErrorInErrorHandling(state)]);
            return;
        }

        if (resolved.Callable.TryGetClosure() is { } closure)
        {
            PushFrame(
                thread,
                closure,
                resolved.Arguments,
                returnBase,
                expectedResults,
                LuaProtectedCallKind.ErrorHandler);
            return;
        }

        var native = resolved.Callable.TryGetNativeFunction() ??
            throw new InvalidOperationException("Resolved error handler is not callable.");
        try
        {
            WriteProtectedResults(
                thread,
                caller,
                returnBase,
                expectedResults,
                succeeded: false,
                native.Body(state, resolved.Arguments));
        }
        catch (LuaRuntimeException)
        {
            WriteProtectedResults(
                thread,
                caller,
                returnBase,
                expectedResults,
                succeeded: false,
                [CreateErrorInErrorHandling(state)]);
        }
    }

    private ImmutableArray<LuaValue>? ExecuteReturn(
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        if (frame.PendingReturnValues is null)
        {
            var start = frame.Base + instruction.A;
            var count = instruction.B < 0 ? Math.Max(0, frame.Top - start) : instruction.B;
            frame.PendingReturnValues = new LuaValue[count];
            for (var index = 0; index < count; index++)
            {
                frame.PendingReturnValues[index] = thread.Stack[start + index];
            }
        }

        if (TryCloseFrom(state, thread, frame, 0, LuaValue.Nil))
        {
            return null;
        }

        var results = frame.PendingReturnValues;
        frame.PendingReturnValues = null;
        thread.PopFrame();
        if (ReferenceEquals(thread.UnwindState?.ActiveCloseCall, frame))
        {
            thread.UnwindState.ActiveCloseCall = null;
        }

        if (thread.FrameCount == 0)
        {
            return results.ToImmutableArray();
        }

        if (frame.ProtectionKind != LuaProtectedCallKind.None)
        {
            WriteProtectedResults(
                thread,
                thread.CurrentFrame,
                frame.ReturnBase,
                frame.ExpectedResults,
                succeeded: frame.ProtectionKind != LuaProtectedCallKind.ErrorHandler,
                results);
            return null;
        }

        WriteCallResults(
            thread,
            thread.CurrentFrame,
            frame.ReturnBase,
            frame.ExpectedResults,
            results);
        ApplyPendingTransform(
            thread,
            thread.CurrentFrame,
            frame.ReturnBase,
            thread.CurrentFrame.PendingResultTransform);
        return null;
    }

    private LuaFrame PushFrame(
        LuaThread thread,
        LuaClosure closure,
        ReadOnlySpan<LuaValue> arguments,
        int returnBase,
        int expectedResults,
        LuaProtectedCallKind protectionKind = LuaProtectedCallKind.None,
        LuaValue errorHandler = default,
        bool isCloseHandler = false)
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
        var frame = new LuaFrame(
            closure,
            @base,
            @base + function.ParameterCount,
            returnBase,
            expectedResults,
            varArgs,
            protectionKind,
            errorHandler,
            isCloseHandler);
        thread.PushFrame(frame);
        return frame;
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

    private static void WriteProtectedResults(
        LuaThread thread,
        LuaFrame caller,
        int returnBase,
        int expectedResults,
        bool succeeded,
        ReadOnlySpan<LuaValue> results)
    {
        var available = checked(results.Length + 1);
        var count = expectedResults < 0 ? available : expectedResults;
        for (var index = 0; index < count; index++)
        {
            thread.Stack[returnBase + index] = index switch
            {
                0 => LuaValue.FromBoolean(succeeded),
                _ when index - 1 < results.Length => results[index - 1],
                _ => LuaValue.Nil,
            };
        }

        caller.Top = returnBase + count;
    }

    private static void ApplyPendingTransform(
        LuaThread thread,
        LuaFrame frame,
        int returnBase,
        LuaResultTransform transform)
    {
        if (transform == LuaResultTransform.LogicalNot)
        {
            thread.Stack[returnBase] = LuaValue.FromBoolean(!thread.Stack[returnBase].IsTruthy);
        }

        frame.PendingResultTransform = LuaResultTransform.None;
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

        return new LuaClosure(parent.Closure.Owner, parent.Closure.Module, function, upvalues);
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
        if (!LuaValueOperations.TryToNumber(initial, out initial) ||
            !LuaValueOperations.TryToNumber(limit, out limit) ||
            !LuaValueOperations.TryToNumber(step, out step))
        {
            throw new LuaRuntimeException("Numeric for values must be numbers or numeric strings.");
        }

        var integerMode = initial.Kind == LuaValueKind.Integer &&
            limit.Kind == LuaValueKind.Integer && step.Kind == LuaValueKind.Integer;
        bool enters;
        if (integerMode)
        {
            Write(thread, frame, instruction.A, initial);
            Write(thread, frame, instruction.A + 1, limit);
            Write(thread, frame, instruction.A + 2, step);
            if (step.AsInteger() == 0)
            {
                throw new LuaRuntimeException("'for' step is zero.");
            }

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
            if (step.AsFloat() == 0)
            {
                throw new LuaRuntimeException("'for' step is zero.");
            }

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
            var currentInteger = index.AsInteger();
            var stepInteger = step.AsInteger();
            var nextInteger = (Int128)currentInteger + stepInteger;
            continues = stepInteger > 0
                ? nextInteger <= limit.AsInteger()
                : nextInteger >= limit.AsInteger();
            index = LuaValue.FromInteger(unchecked(currentInteger + stepInteger));
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

    private bool TryCloseFrom(
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        int register,
        LuaValue error)
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
            frame.ToBeClosedSlots.RemoveAt(index);
            if (!value.IsTruthy)
            {
                continue;
            }

            var metamethod = LuaRuntimeOperations.GetMetamethod(
                state,
                value,
                LuaMetamethod.Close);
            if (metamethod.IsNil)
            {
                throw new LuaRuntimeException(
                    $"To-be-closed value of kind {value.Kind} has no __close metamethod.");
            }

            var resolved = LuaRuntimeOperations.ResolveCall(state, metamethod, [value, error]);
            if (resolved.Callable.TryGetClosure() is { } closure)
            {
                var closeFrame = PushFrame(
                    thread,
                    closure,
                    resolved.Arguments,
                    frame.Top,
                    expectedResults: 0,
                    isCloseHandler: true);
                if (thread.UnwindState is not null)
                {
                    thread.UnwindState.ActiveCloseCall = closeFrame;
                }

                return true;
            }

            var native = resolved.Callable.TryGetNativeFunction() ??
                throw new InvalidOperationException("Resolved __close metamethod is not callable.");
            native.Body(state, resolved.Arguments);
        }

        thread.CloseUpvalues(absolute);
        return false;
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

    private static LuaValue MaterializeError(LuaState state, LuaRuntimeException exception) =>
        exception.HasErrorValue
            ? exception.ErrorValue
            : LuaValue.FromString(state.Strings.GetOrCreate(
                System.Text.Encoding.UTF8.GetBytes(exception.Message)));

    private static LuaValue CreateErrorInErrorHandling(LuaState state) =>
        LuaValue.FromString(state.Strings.GetOrCreate("error in error handling"u8));

    private static LuaValue Read(LuaThread thread, LuaFrame frame, int register) =>
        thread.Stack[frame.Base + register];

    private static void Write(LuaThread thread, LuaFrame frame, int register, LuaValue value)
    {
        var index = frame.Base + register;
        thread.Stack[index] = value;
        frame.Top = Math.Max(frame.Top, index + 1);
    }
}
