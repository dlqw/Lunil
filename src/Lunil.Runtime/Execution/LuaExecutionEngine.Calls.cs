using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Operations;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

internal sealed partial class LuaExecutionEngine
{
    private void ContinueNativeRoot(
        LuaState state,
        LuaScheduler scheduler,
        LuaThread thread,
        LuaValue nativeFunction,
        LuaNativeStep step)
    {
        var descriptor = nativeFunction.TryGetNativeFunction() ??
            throw new InvalidOperationException("A root native continuation has no descriptor.");
        while (true)
        {
            foreach (var value in step.Values)
            {
                state.Heap.ValidateValue(value);
            }

            foreach (var value in step.StateValues)
            {
                state.Heap.ValidateValue(value);
            }

            step.ByteBuffer?.ValidateOwner(state.Heap);

            switch (step.Kind)
            {
                case LuaNativeStepKind.Completed:
                    thread.RootContinuation.Reset();
                    scheduler.Current.ForcedResult = step.Values.ToImmutableArray();
                    return;

                case LuaNativeStepKind.CallLua:
                    state.Heap.ValidateValue(step.Callable);
                    LuaOperationResolution resolved;
                    try
                    {
                        resolved = LuaRuntimeOperations.ResolveCall(
                            state,
                            step.Callable,
                            step.Values);
                    }
                    catch (LuaRuntimeException exception) when (step.CallIsProtected)
                    {
                        var failedContext = new LuaNativeCallContext(
                            state,
                            thread,
                            nativeFunction.TryGetNativeClosure(),
                            step.StateValues,
                            step.ByteBuffer);
                        step = descriptor.StepBody!(
                            failedContext,
                            step.ContinuationId,
                            ProtectedNativeCallbackFailure(state, exception));
                        continue;
                    }
                    var resolvedArguments = resolved.MaterializeArgumentsForRuntime();
                    if (resolved.Callable.TryGetClosure() is { } closure)
                    {
                        thread.RootContinuation.Kind = LuaContinuationKind.NativeCallLua;
                        thread.RootContinuation.State = step.ContinuationId;
                        thread.RootContinuation.Value = nativeFunction;
                        thread.RootContinuation.IsYieldBarrier = !step.CallIsYieldable;
                        thread.RootContinuation.NativeCallbackIsProtected = step.CallIsProtected;
                        SaveNativeInvocationState(
                            thread,
                            thread.RootContinuation,
                            step.StateValues,
                            step.ByteBuffer,
                            step.StateValuesAreReusable);
                        thread.Owner.WriteBarrier(thread, nativeFunction);
                        PushFrame(thread, closure, resolvedArguments, 0, expectedResults: -1);
                        return;
                    }

                    var callback = resolved.Callable.TryGetNativeFunction() ??
                        throw new InvalidOperationException("A root native callback is not callable.");
                    if (callback.StepBody is not null ||
                        callback.Kind != LuaNativeFunctionKind.Normal)
                    {
                        thread.RootContinuation.Kind = LuaContinuationKind.NativeCallLua;
                        thread.RootContinuation.State = step.ContinuationId;
                        thread.RootContinuation.Value = nativeFunction;
                        thread.RootContinuation.IsYieldBarrier = !step.CallIsYieldable;
                        thread.RootContinuation.NativeCallbackIsProtected = step.CallIsProtected;
                        SaveNativeInvocationState(
                            thread,
                            thread.RootContinuation,
                            step.StateValues,
                            step.ByteBuffer,
                            step.StateValuesAreReusable);
                        thread.Owner.WriteBarrier(thread, nativeFunction);
                        PushFrame(
                            thread,
                            CreateNativeCallbackTrampoline(state, resolved.Callable),
                            resolvedArguments,
                            0,
                            expectedResults: -1,
                            isHidden: true);
                        return;
                    }

                    LuaValue[] callbackResults;
                    try
                    {
                        callbackResults = InvokeNativeBody(
                            state,
                            resolved.Callable,
                            resolvedArguments);
                        if (step.CallIsProtected)
                        {
                            callbackResults =
                            [LuaValue.FromBoolean(true), .. callbackResults];
                        }
                    }
                    catch (LuaRuntimeException exception) when (step.CallIsProtected)
                    {
                        callbackResults = ProtectedNativeCallbackFailure(state, exception);
                    }
                    var callContext = new LuaNativeCallContext(
                        state,
                        thread,
                        nativeFunction.TryGetNativeClosure(),
                        step.StateValues,
                        step.ByteBuffer);
                    step = descriptor.StepBody!(
                        callContext,
                        step.ContinuationId,
                        callbackResults);
                    continue;

                case LuaNativeStepKind.Yielded:
                    if (!scheduler.Current.IsYieldable || IsAtNonYieldableBoundary(thread) ||
                        ReferenceEquals(thread, state.MainThread))
                    {
                        throw new LuaRuntimeException(
                            "attempt to yield across a non-yieldable boundary",

                            bypassProtectedNativeCallback: true);
                    }

                    thread.RootContinuation.Kind = LuaContinuationKind.NativeYield;
                    thread.RootContinuation.State = step.ContinuationId;
                    thread.RootContinuation.Value = nativeFunction;
                    SaveNativeInvocationState(
                        thread,
                        thread.RootContinuation,
                        step.StateValues,
                        step.ByteBuffer,
                        step.StateValuesAreReusable);
                    thread.Owner.WriteBarrier(thread, nativeFunction);
                    thread.SetYieldedValues(step.Values);
                    scheduler.RequestYield();
                    return;

                default:
                    throw new InvalidOperationException("Unknown native step kind.");
            }
        }
    }

    private static void SaveNativeInvocationState(
        LuaThread thread,
        LuaContinuation continuation,
        LuaValue[] stateValues,
        LuaNativeByteBuffer? byteBuffer,
        bool stateValuesAreReusable)
    {
        byteBuffer?.ValidateOwner(thread.Owner);
        continuation.Values = stateValuesAreReusable ? stateValues : stateValues.ToArray();
        continuation.NativeByteBuffer = byteBuffer;
        foreach (var value in continuation.Values)
        {
            thread.Owner.WriteBarrier(thread, value);
        }
    }

    internal ImmutableArray<LuaValue>? ExecuteCall(
        LuaState state,
        LuaScheduler scheduler,
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
        var directFunction = thread.Stack.ReadUnchecked(functionIndex);
        if (directFunction.TryGetNativeFunction() is
            {
                Kind: LuaNativeFunctionKind.CoroutineResume or
                    LuaNativeFunctionKind.CoroutineWrap or
                    LuaNativeFunctionKind.CoroutineYield,
            } directCoroutineIntrinsic)
        {
            if (tailCall && TryCloseFrom(state, thread, frame, 0, LuaValue.Nil))
            {
                return null;
            }

            frame.Continuation.Reset();
            return ExecuteCoroutineIntrinsic(
                state,
                scheduler,
                thread,
                frame,
                instruction,
                directFunction,
                directCoroutineIntrinsic,
                thread.Stack.AsReadOnlySpan(argumentStart, argumentCount),
                functionIndex,
                tailCall);
        }

        LuaValue function;
        ReadOnlySpan<LuaValue> resolvedArguments;
        var argumentsInCallerStack = false;
        var callMetamethod = false;
        if (tailCall && frame.Continuation.Kind == LuaContinuationKind.TailCall)
        {
            function = frame.Continuation.Value;
            resolvedArguments = frame.Continuation.Values;
        }
        else if (directFunction.Kind == LuaValueKind.Function)
        {
            function = directFunction;
            resolvedArguments = thread.Stack.AsReadOnlySpan(argumentStart, argumentCount);
            argumentsInCallerStack = !tailCall;
            if (tailCall)
            {
                var snapshot = resolvedArguments.ToArray();
                frame.Continuation.Kind = LuaContinuationKind.TailCall;
                frame.Continuation.Value = function;
                frame.Continuation.Values = snapshot;
                thread.Owner.WriteBarrier(thread, function);
                foreach (var value in snapshot)
                {
                    thread.Owner.WriteBarrier(thread, value);
                }

                resolvedArguments = snapshot;
            }
        }
        else
        {
            callMetamethod = true;
            var resolvedCall = LuaRuntimeOperations.ResolveCall(
                state,
                thread.Stack.ReadUnchecked(functionIndex),
                thread.Stack.AsReadOnlySpan(argumentStart, argumentCount));
            function = resolvedCall.Callable;
            var resolvedArgumentSnapshot = resolvedCall.MaterializeArgumentsForRuntime();
            resolvedArguments = resolvedArgumentSnapshot;
            if (tailCall)
            {
                frame.Continuation.Kind = LuaContinuationKind.TailCall;
                frame.Continuation.Value = function;
                frame.Continuation.Values = resolvedArgumentSnapshot;
                thread.Owner.WriteBarrier(thread, function);
                foreach (var value in resolvedArguments)
                {
                    thread.Owner.WriteBarrier(thread, value);
                }
            }
        }
        if (tailCall && TryCloseFrom(state, thread, frame, 0, LuaValue.Nil))
        {
            return null;
        }

        frame.Continuation.Reset();
        if (function.TryGetNativeFunction() is { Kind: LuaNativeFunctionKind.CoroutineClose })
        {
            if (resolvedArguments.Length == 0 ||
                resolvedArguments[0].Kind != LuaValueKind.Thread)
            {
                throw new LuaRuntimeException("bad argument #1 to 'close' (thread expected)")
                { Kind = LuaRuntimeErrorKind.BadArgument };
            }

            var closeTarget = resolvedArguments[0].AsThread();
            ValidateClosableThread(state, closeTarget);
            LuaValue[] closeValues;
            try
            {
                var closeResult = Close(state, closeTarget);
                closeValues = closeResult.Signal == LuaVmSignal.Completed
                    ? [LuaValue.FromBoolean(true)]
                    : [LuaValue.FromBoolean(false), closeResult.Values[0]];
            }
            catch (LuaRuntimeException exception)
            {
                closeValues =
                [
                    LuaValue.FromBoolean(false),
                    MaterializeError(state, exception),
                ];
            }

            if (tailCall)
            {
                EnsureWriteWindow(thread, functionIndex, closeValues.Length);
                for (var index = 0; index < closeValues.Length; index++)
                {
                    thread.Stack.WriteUnchecked(functionIndex + index, closeValues[index]);
                }

                return ExecuteReturn(
                    state,
                    scheduler,
                    thread,
                    frame,
                    new LuaIrInstruction(
                        LuaIrOpcode.Return,
                        instruction.A,
                        closeValues.Length));
            }

            WriteCallResults(thread, frame, functionIndex, instruction.C, closeValues);
            frame.ProgramCounter++;
            return null;
        }

        if (function.TryGetNativeFunction() is { } coroutineIntrinsic &&
            coroutineIntrinsic.Kind is LuaNativeFunctionKind.CoroutineResume or
                LuaNativeFunctionKind.CoroutineWrap or LuaNativeFunctionKind.CoroutineYield)
        {
            return ExecuteCoroutineIntrinsic(
                state,
                scheduler,
                thread,
                frame,
                instruction,
                function,
                coroutineIntrinsic,
                resolvedArguments,
                functionIndex,
                tailCall);
        }

        if (function.TryGetNativeFunction() is { Kind: not LuaNativeFunctionKind.Normal } intrinsic)
        {
            if (tailCall)
            {
                frame.Continuation.Kind = LuaContinuationKind.ProtectedCall;
                frame.Continuation.Base = instruction.A;
                ExecuteProtectedIntrinsic(
                    state,
                    scheduler,
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
                    scheduler,
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
            if (!tailCall && argumentsInCallerStack &&
                TryExecuteFramelessCall(
                    state,
                    null,
                    scheduler,
                    thread,
                    frame,
                    closure,
                    argumentStart,
                    argumentCount,
                    returnBase,
                    expectedResults))
            {
                frame.ProgramCounter++;
                return null;
            }

            if (tailCall)
            {
                var protectionKind = frame.Continuation.ProtectionKind;
                var protectionFunction = frame.Continuation.ProtectionFunction;
                var errorHandler = frame.Continuation.ErrorHandler;
                var isCloseHandler = frame.Continuation.IsCloseHandler;
                CommitPendingBackedges(frame);
                thread.PopFrame();
                var replacement = PushFrame(
                    thread,
                    closure,
                    resolvedArguments,
                    returnBase,
                    expectedResults,
                    protectionKind,
                    errorHandler,
                    isCloseHandler,
                    frame.IsDebugHook,
                    frame.IsHidden,
                    scheduleCallHook: false);
                replacement.Continuation.ProtectionFunction = protectionFunction;
                replacement.IsTailCall = true;
                if (callMetamethod)
                {
                    SetDebugFunctionName(replacement, "call", "metamethod");
                }
                if (!replacement.IsDebugHook && !replacement.IsHidden &&
                    !thread.IsRunningDebugHook && !thread.DebugHook.IsNil &&
                    HasDebugHook(thread, LuaDebugHookMask.Call))
                {
                    replacement.PendingDebugHookEvent = "tail call";
                    thread.SetDebugHookTransfer(
                        thread.Stack.AsReadOnlySpan(
                            replacement.Base,
                            replacement.Function.ParameterCount),
                        isNative: false);
                }
                if (frame.IsDebugHook)
                {
                    replacement.Continuation.IsYieldBarrier = true;
                }
                if (ReferenceEquals(thread.UnwindState?.ActiveCloseCall, frame))
                {
                    thread.UnwindState.ActiveCloseCall = replacement;
                }
                if (ReferenceEquals(thread.UnwindState?.ActiveErrorHandler, frame))
                {
                    thread.UnwindState.ActiveErrorHandler = replacement;
                }

                return null;
            }

            frame.ProgramCounter++;
            if (argumentsInCallerStack)
            {
                PushFrameFromStack(
                    thread,
                    closure,
                    argumentStart,
                    argumentCount,
                    returnBase,
                    expectedResults);
            }
            else
            {
                var callee = PushFrame(
                    thread,
                    closure,
                    resolvedArguments,
                    returnBase,
                    expectedResults);
                if (callMetamethod)
                {
                    SetDebugFunctionName(callee, "call", "metamethod");
                }
            }

            return null;
        }

        var native = function.TryGetNativeFunction() ??
            throw new InvalidOperationException("Resolved callable is not a function.");
        if (TryScheduleNativeCallHook(thread, frame, function, resolvedArguments))
        {
            return null;
        }

        if (native.StepBody is not null)
        {
            var context = new LuaNativeCallContext(
                state,
                thread,
                function.TryGetNativeClosure());
            var step = InvokeNativeStep(native, context, 0, resolvedArguments);
            return ContinueNative(
                state,
                scheduler,
                thread,
                frame,
                function,
                functionIndex,
                instruction.C,
                tailCall,
                programCounterAdvanced: false,
                step);
        }

        var results = InvokeNativeBody(state, function, resolvedArguments);
        if (tailCall)
        {
            var syntheticReturn = new LuaIrInstruction(
                LuaIrOpcode.Return,
                instruction.A,
                results.Length,
                span: instruction.Span);
            EnsureWriteWindow(thread, functionIndex, results.Length);
            for (var index = 0; index < results.Length; index++)
            {
                thread.Stack.WriteUnchecked(functionIndex + index, results[index]);
            }

            return ExecuteReturn(state, scheduler, thread, frame, syntheticReturn);
        }
        else
        {
            WriteCallResults(thread, frame, functionIndex, instruction.C, results);
            frame.ProgramCounter++;
            ScheduleNativeReturnHook(thread, frame, function, results);
        }

        return null;
    }

    internal bool TryExecuteFramelessCall(
        LuaState state,
        LuaExecutionContext? context,
        LuaScheduler? scheduler,
        LuaThread thread,
        LuaFrame caller,
        LuaClosure closure,
        int argumentStart,
        int argumentCount,
        int returnBase,
        int expectedResults)
    {
        var functionVersion = closure.FunctionVersion;
        if (!functionVersion.TryEnterFramelessCall() || !thread.DebugHook.IsNil ||
            thread.IsRunningDebugHook || thread.UnwindState is not null ||
            state.IsRunningFinalizer)
        {
            return false;
        }

        var function = functionVersion.Function;
        var instructionCost = functionVersion.FramelessInstructionCount;
        var remainingInstructions = context?.RemainingInstructionCount ??
            scheduler!.InstructionLimit - scheduler.TotalInstructionCount;
        if (remainingInstructions < instructionCost)
        {
            // Enter the ordinary frame so a short remaining budget retains the exact callee PC.
            return false;
        }

        // A preceding variable-arity call can lower frame.Top while canonical registers above
        // it remain live. Frameless scratch must stay outside the caller's entire register file,
        // not merely outside its current result window, or clearing scratch can corrupt locals.
        var callerRegisterEnd = AddStackOffset(caller.Base, caller.Function.RegisterCount);
        var scratchBase = Math.Max(
            Math.Max(caller.Top, callerRegisterEnd),
            AddStackOffset(argumentStart, argumentCount));
        var registerCount = function.RegisterCount;
        var scratchEnd = AddStackOffset(scratchBase, registerCount);
        if (scratchEnd > _options.MaximumStackSlots)
        {
            return false;
        }

        thread.Stack.EnsureCapacity(scratchEnd);
        thread.Stack.Clear(scratchBase, registerCount);
        var fixedArguments = Math.Min(argumentCount, function.ParameterCount);
        for (var index = 0; index < fixedArguments; index++)
        {
            thread.Stack.WriteUnchecked(
                scratchBase + index,
                thread.Stack.ReadUnchecked(argumentStart + index));
        }

        for (var programCounter = 0; programCounter < instructionCost; programCounter++)
        {
            var instruction = function.Instructions[programCounter];
            switch (instruction.Opcode)
            {
                case LuaIrOpcode.Move:
                    thread.Stack.WriteUnchecked(
                        scratchBase + instruction.A,
                        thread.Stack.ReadUnchecked(scratchBase + instruction.B));
                    break;
                case LuaIrOpcode.LoadNil:
                    for (var register = instruction.A; register <= instruction.B; register++)
                    {
                        thread.Stack.WriteUnchecked(scratchBase + register, LuaValue.Nil);
                    }

                    break;
                case LuaIrOpcode.Unary:
                    {
                        var operand = thread.Stack.ReadUnchecked(scratchBase + instruction.B);
                        var operation = (LuaIrUnaryOperator)instruction.C;
                        LuaValue result;
                        if (operation == LuaIrUnaryOperator.LogicalNot)
                        {
                            result = LuaValue.FromBoolean(!operand.IsTruthy);
                        }
                        else if (operand.IsInteger)
                        {
                            result = LuaValueOperations.UnaryIntegerSpecialized(operation, operand);
                        }
                        else if (operand.IsFloat && operation == LuaIrUnaryOperator.Negate)
                        {
                            result = LuaValueOperations.UnaryFloatSpecialized(operation, operand);
                        }
                        else
                        {
                            thread.Stack.Clear(scratchBase, registerCount);
                            return false;
                        }

                        thread.Stack.WriteUnchecked(scratchBase + instruction.A, result);
                        break;
                    }
                case LuaIrOpcode.Binary:
                    {
                        var left = thread.Stack.ReadUnchecked(scratchBase + instruction.B);
                        var right = thread.Stack.ReadUnchecked(scratchBase + instruction.C);
                        var operation = (LuaIrBinaryOperator)instruction.D;
                        LuaValue result;
                        if (left.IsInteger && right.IsInteger)
                        {
                            result = LuaValueOperations.BinaryIntegerSpecialized(
                                operation,
                                left,
                                right);
                        }
                        else if (left.IsFloat && right.IsFloat && !IsIntegerOnly(operation))
                        {
                            result = LuaValueOperations.BinaryFloatSpecialized(
                                operation,
                                left,
                                right);
                        }
                        else if (left.Kind is LuaValueKind.Integer or LuaValueKind.Float &&
                                 right.Kind is LuaValueKind.Integer or LuaValueKind.Float &&
                                 !IsIntegerOnly(operation))
                        {
                            result = LuaValueOperations.BinaryMixedNumericSpecialized(
                                operation,
                                left,
                                right);
                        }
                        else
                        {
                            thread.Stack.Clear(scratchBase, registerCount);
                            return false;
                        }

                        thread.Stack.WriteUnchecked(scratchBase + instruction.A, result);
                        break;
                    }
                case LuaIrOpcode.Return:
                    {
                        var results = thread.Stack.AsReadOnlySpan(
                            scratchBase + instruction.A,
                            instruction.B);
                        WriteCallResults(
                            thread,
                            caller,
                            returnBase,
                            expectedResults,
                            results);
                        if (context is not null)
                        {
                            if (!context.TryReserveInstructions(instructionCost))
                            {
                                throw new InvalidOperationException(
                                    "The prechecked frameless instruction budget changed.");
                            }
                        }
                        else
                        {
                            scheduler!.Current.InstructionCount = unchecked(
                                scheduler.Current.InstructionCount + instructionCost);
                        }
                        thread.Stack.Clear(scratchBase, registerCount);
                        // This call replaced a scheduler turn, so preserve its logical-GC poll.
                        // The result window and caller top are materialized and scratch no longer
                        // contains the only reference to a Lua object before collection starts.
                        state.Heap.SafePoint();

                        return true;
                    }
                default:
                    throw new InvalidOperationException(
                        $"Unsupported frameless instruction {instruction.Opcode}.");
            }
        }

        throw new InvalidOperationException("A frameless leaf function did not return.");

        static bool IsIntegerOnly(LuaIrBinaryOperator operation) => operation is
            LuaIrBinaryOperator.BitwiseAnd or LuaIrBinaryOperator.BitwiseOr or
            LuaIrBinaryOperator.BitwiseXor or LuaIrBinaryOperator.ShiftLeft or
            LuaIrBinaryOperator.ShiftRight;
    }

    internal void ExecuteKnownClosureCall(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        LuaClosure closure,
        int functionRegister,
        int argumentCount,
        int expectedResults)
    {
        LunilGuard.NotNull(thread);
        LunilGuard.NotNull(frame);
        LunilGuard.NotNull(closure);
        if (!ReferenceEquals(thread.CurrentFrame, frame) ||
            !ReferenceEquals(
                LuaCodegenAbiV2.ReadRegisterUnchecked(
                    thread,
                    frame,
                    functionRegister).TryGetClosure(),
                closure))
        {
            throw new InvalidOperationException("Known-closure call guard was not preserved.");
        }

        var functionIndex = frame.Base + functionRegister;
        var argumentStart = functionIndex + 1;
        var actualArgumentCount = argumentCount < 0
            ? Math.Max(0, frame.Top - argumentStart)
            : argumentCount;
        if (TryExecuteFramelessCall(
                context.State,
                context,
                context.Scheduler,
                thread,
                frame,
                closure,
                argumentStart,
                actualArgumentCount,
                functionIndex,
                expectedResults))
        {
            frame.ProgramCounter++;
            return;
        }

        frame.ProgramCounter++;
        PushFrameFromStack(
            thread,
            closure,
            argumentStart,
            actualArgumentCount,
            functionIndex,
            expectedResults);
    }

    internal void ExecuteKnownClosureTailCall(
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        LuaClosure closure,
        int functionRegister,
        int argumentCount)
    {
        LunilGuard.NotNull(state);
        LunilGuard.NotNull(thread);
        LunilGuard.NotNull(frame);
        LunilGuard.NotNull(closure);
        if (!ReferenceEquals(thread.CurrentFrame, frame) ||
            !ReferenceEquals(
                LuaCodegenAbiV2.ReadRegisterUnchecked(
                    thread,
                    frame,
                    functionRegister).TryGetClosure(),
                closure))
        {
            throw new InvalidOperationException("Known-closure tail-call guard was not preserved.");
        }

        var functionIndex = frame.Base + functionRegister;
        var argumentStart = functionIndex + 1;
        if (frame.Continuation.Kind != LuaContinuationKind.TailCall)
        {
            var actualArgumentCount = argumentCount < 0
                ? Math.Max(0, frame.Top - argumentStart)
                : argumentCount;
            var snapshot = thread.Stack
                .AsReadOnlySpan(argumentStart, actualArgumentCount)
                .ToArray();
            frame.Continuation.Kind = LuaContinuationKind.TailCall;
            frame.Continuation.Value = LuaValue.FromFunction(closure);
            frame.Continuation.Values = snapshot;
            thread.Owner.WriteBarrier(thread, frame.Continuation.Value);
            foreach (var value in snapshot)
            {
                thread.Owner.WriteBarrier(thread, value);
            }
        }

        if (TryCloseFrom(state, thread, frame, 0, LuaValue.Nil))
        {
            return;
        }

        var arguments = frame.Continuation.Values;
        var returnBase = frame.ReturnBase;
        var expectedResults = frame.ExpectedResults;
        var protectionKind = frame.Continuation.ProtectionKind;
        var protectionFunction = frame.Continuation.ProtectionFunction;
        var errorHandler = frame.Continuation.ErrorHandler;
        var isCloseHandler = frame.Continuation.IsCloseHandler;
        var isDebugHook = frame.IsDebugHook;
        var isHidden = frame.IsHidden;
        var wasYieldBarrier = frame.Continuation.IsYieldBarrier;
        var wasActiveCloseCall = ReferenceEquals(thread.UnwindState?.ActiveCloseCall, frame);
        var wasActiveErrorHandler = ReferenceEquals(
            thread.UnwindState?.ActiveErrorHandler,
            frame);
        frame.Continuation.Reset();
        thread.PopFrame();
        var replacement = PushFrame(
            thread,
            closure,
            arguments,
            returnBase,
            expectedResults,
            protectionKind,
            errorHandler,
            isCloseHandler,
            isDebugHook,
            isHidden,
            scheduleCallHook: false);
        replacement.Continuation.ProtectionFunction = protectionFunction;
        replacement.IsTailCall = true;
        if (!replacement.IsDebugHook && !replacement.IsHidden &&
            !thread.IsRunningDebugHook && !thread.DebugHook.IsNil &&
            HasDebugHook(thread, LuaDebugHookMask.Call))
        {
            replacement.PendingDebugHookEvent = "tail call";
            thread.SetDebugHookTransfer(
                thread.Stack.AsReadOnlySpan(
                    replacement.Base,
                    replacement.Function.ParameterCount),
                isNative: false);
        }

        if (wasYieldBarrier)
        {
            replacement.Continuation.IsYieldBarrier = true;
        }

        if (wasActiveCloseCall)
        {
            thread.UnwindState!.ActiveCloseCall = replacement;
        }

        if (wasActiveErrorHandler)
        {
            thread.UnwindState!.ActiveErrorHandler = replacement;
        }
    }

    private static bool TryScheduleNativeCallHook(
        LuaThread thread,
        LuaFrame frame,
        LuaValue function,
        ReadOnlySpan<LuaValue> arguments)
    {
        if (frame.NativeCallHookProgramCounter == frame.ProgramCounter)
        {
            frame.NativeCallHookProgramCounter = -1;
            return false;
        }

        if (thread.DebugHook.IsNil || thread.IsRunningDebugHook ||
            !HasDebugHook(thread, LuaDebugHookMask.Call))
        {
            return false;
        }

        frame.NativeCallHookProgramCounter = frame.ProgramCounter;
        frame.NativeCallSourceLine = frame.Function
            .Instructions[frame.ProgramCounter].SourceLine;
        frame.PendingDebugHookEvent = "call";
        thread.DebugHookSubjectFunction = function;
        thread.Owner.WriteBarrier(thread, function);
        thread.SetDebugHookTransfer(arguments, isNative: true);
        return true;
    }

    private static void ScheduleNativeReturnHook(
        LuaThread thread,
        LuaFrame frame,
        LuaValue function,
        ReadOnlySpan<LuaValue> results)
    {
        if (thread.DebugHook.IsNil || thread.IsRunningDebugHook ||
            !HasDebugHook(thread, LuaDebugHookMask.Return))
        {
            return;
        }

        if (frame.NativeCallSourceLine <= 0)
        {
            var instructions = frame.Function.Instructions;
            var callProgramCounter = Math.Clamp(
                frame.ProgramCounter - 1,
                0,
                instructions.Length - 1);
            frame.NativeCallSourceLine = instructions[callProgramCounter].SourceLine;
        }

        frame.PendingDebugHookEvent = "return";
        thread.DebugHookSubjectFunction = function;
        thread.Owner.WriteBarrier(thread, function);
        thread.SetDebugHookTransfer(results, isNative: true);
    }
}
