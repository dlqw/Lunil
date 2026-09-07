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
    internal void ExecuteOperation(
        LuaState state,
        LuaScheduler scheduler,
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
                thread.Stack.WriteUnchecked(returnBase, resolution.Value);
                frame.Top = Math.Max(frame.Top, AddStackOffset(returnBase, 1));
            }

            frame.ProgramCounter++;
            return;
        }

        var operationTop = frame.Top;
        var argumentStart = Math.Max(
            AddStackOffset(frame.Base, frame.Function.RegisterCount),
            operationTop);
        var argumentCount = resolution.ArgumentCount;
        EnsureScratchWindow(thread, argumentStart, argumentCount);
        CopyOperationArguments(thread, argumentStart, resolution);
        var callable = resolution.Callable;
        if (callable.Kind != LuaValueKind.Function)
        {
            var resolved = LuaRuntimeOperations.ResolveCall(
                state,
                callable,
                thread.Stack.AsReadOnlySpan(argumentStart, argumentCount));
            callable = resolved.Callable;
            argumentCount = resolved.ArgumentCount;
            EnsureScratchWindow(thread, argumentStart, argumentCount);
            CopyOperationArguments(thread, argumentStart, resolved);
        }

        var closure = callable.TryGetClosure();
        if (closure is not null)
        {
            var operation = frame.Function.Instructions[frame.ProgramCounter];
            frame.Continuation.Kind = LuaContinuationKind.LuaCall;
            frame.Continuation.Count = operationTop;
            frame.Continuation.Transform = resolution.Transform;
            frame.ProgramCounter++;
            var callee = PushFrameFromStack(
                thread,
                closure,
                argumentStart,
                argumentCount,
                returnBase,
                expectedResults,
                minimumBase: operationTop);
            SetDebugFunctionName(
                callee,
                GetOperationMetamethodName(operation),
                "metamethod");
            return;
        }

        var native = callable.TryGetNativeFunction() ??
            throw new InvalidOperationException("Resolved metamethod is not callable.");
        if (native.StepBody is not null)
        {
            LuaNativeStep step;
            try
            {
                var context = new LuaNativeCallContext(
                    state,
                    thread,
                    callable.TryGetNativeClosure());
                step = InvokeNativeStep(
                    native,
                    context,
                    0,
                    thread.Stack.AsReadOnlySpan(argumentStart, argumentCount));
            }
            finally
            {
                thread.Stack.Clear(argumentStart, argumentCount);
            }

            _ = ContinueNative(
                state,
                scheduler,
                thread,
                frame,
                callable,
                returnBase,
                expectedResults,
                tailCall: false,
                programCounterAdvanced: false,
                step,
                operationTop,
                resolution.Transform);
            return;
        }

        LuaValue[] results;
        try
        {
            results = InvokeNativeBody(
                state,
                callable,
                thread.Stack.AsReadOnlySpan(argumentStart, argumentCount));
        }
        finally
        {
            thread.Stack.Clear(argumentStart, argumentCount);
        }

        WriteOperationResults(
            thread,
            frame,
            returnBase,
            expectedResults,
            results,
            operationTop);
        ApplyPendingTransform(thread, frame, returnBase, resolution.Transform);
        frame.ProgramCounter++;
    }

    private void ExecuteProtectedIntrinsic(
        LuaState state,
        LuaScheduler scheduler,
        LuaThread thread,
        LuaFrame caller,
        LuaIrInstruction instruction,
        LuaNativeFunction intrinsic,
        ReadOnlySpan<LuaValue> arguments,
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
                $"Bad argument #2 to '{intrinsic.Name}' (function expected, got " +
                $"{LuaValueOperations.TypeName(handler)}).");
        }

        var targetArguments = arguments[required..];
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
        var resolvedArguments = resolved.MaterializeArgumentsForRuntime();

        if (resolved.Callable.TryGetNativeFunction() is
            {
                Kind: LuaNativeFunctionKind.CoroutineResume or
                    LuaNativeFunctionKind.CoroutineWrap or
                    LuaNativeFunctionKind.CoroutineYield,
            } coroutineIntrinsic)
        {
            try
            {
                _ = ExecuteCoroutineIntrinsic(
                    state,
                    scheduler,
                    thread,
                    caller,
                    instruction,
                    resolved.Callable,
                    coroutineIntrinsic,
                    resolvedArguments,
                    returnBase,
                    tailCall: caller.Continuation.Kind == LuaContinuationKind.ProtectedCall,
                    protectedCall: true);
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
            }

            return;
        }

        if (resolved.Callable.TryGetNativeFunction() is
            { Kind: LuaNativeFunctionKind.CoroutineClose })
        {
            if (resolvedArguments.Length == 0 ||
                resolvedArguments[0].Kind != LuaValueKind.Thread)
            {
                CompleteProtectedFailure(
                    state,
                    thread,
                    caller,
                    returnBase,
                    instruction.C,
                    intrinsic.Kind,
                    handler,
                    MaterializeError(
                        state,
                        new LuaRuntimeException(
                            "bad argument #1 to 'close' (thread expected)")));
                caller.ProgramCounter++;
                return;
            }

            var closeTarget = resolvedArguments[0].AsThread();
            try
            {
                ValidateClosableThread(state, closeTarget);
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

            WriteProtectedResults(
                thread,
                caller,
                returnBase,
                instruction.C,
                succeeded: true,
                closeValues);
            caller.ProgramCounter++;
            return;
        }

        var closure = resolved.Callable.TryGetClosure();
        if (closure is not null)
        {
            caller.ProgramCounter++;
            var protectedFrame = PushFrame(
                thread,
                closure,
                resolvedArguments,
                returnBase,
                instruction.C,
                intrinsic.Kind == LuaNativeFunctionKind.ProtectedCall
                    ? LuaProtectedCallKind.ProtectedCall
                    : LuaProtectedCallKind.ProtectedCallWithHandler,
                handler);
            protectedFrame.Continuation.ProtectionFunction = LuaValue.FromFunction(intrinsic);
            thread.Owner.WriteBarrier(thread, protectedFrame.Continuation.ProtectionFunction);
            return;
        }

        var native = resolved.Callable.TryGetNativeFunction() ??
            throw new InvalidOperationException("Resolved protected target is not callable.");
        if (native.Kind != LuaNativeFunctionKind.Normal)
        {
            caller.ProgramCounter++;
            var protectedFrame = PushFrame(
                thread,
                CreateNativeCallbackTrampoline(state, resolved.Callable),
                resolvedArguments,
                returnBase,
                instruction.C,
                intrinsic.Kind == LuaNativeFunctionKind.ProtectedCall
                    ? LuaProtectedCallKind.ProtectedCall
                    : LuaProtectedCallKind.ProtectedCallWithHandler,
                handler,
                isHidden: true);
            protectedFrame.Continuation.ProtectionFunction = LuaValue.FromFunction(intrinsic);
            thread.Owner.WriteBarrier(thread, protectedFrame.Continuation.ProtectionFunction);
            return;
        }

        if (native.StepBody is not null)
        {
            var tailCall = caller.Continuation.Kind == LuaContinuationKind.ProtectedCall;
            caller.Continuation.ProtectionKind =
                intrinsic.Kind == LuaNativeFunctionKind.ProtectedCall
                    ? LuaProtectedCallKind.ProtectedCall
                    : LuaProtectedCallKind.ProtectedCallWithHandler;
            caller.Continuation.ProtectionFunction = LuaValue.FromFunction(intrinsic);
            caller.Continuation.ErrorHandler = handler;
            caller.Continuation.IsNativeProtectedBoundary = true;
            caller.Continuation.NativeProtectedReturnBase = returnBase;
            caller.Continuation.NativeProtectedExpectedResults = instruction.C;
            caller.Continuation.NativeProtectedTailCall = tailCall;
            caller.ProgramCounter++;
            var context = new LuaNativeCallContext(
                state,
                thread,
                resolved.Callable.TryGetNativeClosure());
            var step = InvokeNativeStep(native, context, 0, resolvedArguments);
            _ = ContinueNative(
                state,
                scheduler,
                thread,
                caller,
                resolved.Callable,
                returnBase,
                instruction.C,
                tailCall,
                programCounterAdvanced: true,
                step);
            return;
        }

        try
        {
            var results = InvokeNativeBody(state, resolved.Callable, resolvedArguments);
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

    private static void BeginUnwind(
        LuaThread thread,
        LuaValue error,
        bool skipProtectedNativeCallback = false)
    {
        LuaFrame? boundary = null;
        var debugBoundaryFunction = LuaValue.Nil;
        var errorHandler = LuaValue.Nil;
        for (var index = thread.Frames.Count - 1; index >= 0; index--)
        {
            var continuation = thread.Frames[index].Continuation;
            if (continuation.ProtectionKind != LuaProtectedCallKind.None ||
                !skipProtectedNativeCallback &&
                continuation.Kind == LuaContinuationKind.NativeCallLua &&
                continuation.NativeCallbackIsProtected)
            {
                boundary = thread.Frames[index];
                debugBoundaryFunction = continuation.ProtectionKind != LuaProtectedCallKind.None
                    ? continuation.ProtectionFunction
                    : continuation.Value;
                if (continuation.ProtectionKind ==
                    LuaProtectedCallKind.ProtectedCallWithHandler)
                {
                    errorHandler = continuation.ErrorHandler;
                }
                break;
            }
        }

        if (boundary is null && !skipProtectedNativeCallback &&
            thread.RootContinuation.Kind == LuaContinuationKind.NativeCallLua &&
            thread.RootContinuation.NativeCallbackIsProtected)
        {
            debugBoundaryFunction = thread.RootContinuation.Value;
        }
        else if (boundary is null && TryGetWrapResumerFunction(thread, out var wrapper))
        {
            debugBoundaryFunction = wrapper;
        }

        thread.UnwindState = new LuaUnwindState(
            boundary,
            error,
            debugBoundaryFunction,
            errorHandler,
            skipProtectedNativeCallback);
        thread.Owner.WriteBarrier(thread, error);
        thread.Owner.WriteBarrier(thread, debugBoundaryFunction);
        thread.Owner.WriteBarrier(thread, errorHandler);
    }

    private static bool IsResumedByWrap(LuaThread thread) =>
        TryGetWrapResumerFunction(thread, out _);

    private static bool TryGetWrapResumerFunction(
        LuaThread thread,
        out LuaValue wrapper)
    {
        var resumer = thread.Resumer;
        if (resumer is null || resumer.FrameCount == 0)
        {
            wrapper = LuaValue.Nil;
            return false;
        }

        var continuation = resumer.CurrentFrame.Continuation;
        if (continuation.Kind != LuaContinuationKind.CoroutineResume ||
            (continuation.State & 2) == 0)
        {
            wrapper = LuaValue.Nil;
            return false;
        }

        wrapper = continuation.Value;
        return wrapper.TryGetNativeFunction() is { Kind: LuaNativeFunctionKind.CoroutineWrap };
    }

    private static void RegisterUnwindError(
        LuaState state,
        LuaThread thread,
        LuaUnwindState unwind,
        LuaValue error)
    {
        unwind.ActiveCloseCall = null;
        if (unwind.ActiveErrorHandler is not null)
        {
            unwind.Error = CreateErrorInErrorHandling(state);
            unwind.ActiveErrorHandler = null;
            unwind.ErrorHandlerPending = false;
            thread.Owner.WriteBarrier(thread, unwind.Error);
            return;
        }

        unwind.Error = error;
        unwind.ErrorHandlerPending = !unwind.ErrorHandler.IsNil;
        thread.Owner.WriteBarrier(thread, unwind.Error);
    }

    private void StartUnwindErrorHandler(
        LuaState state,
        LuaThread thread,
        LuaUnwindState unwind)
    {
        unwind.ErrorHandlerPending = false;
        LuaOperationResolution resolved;
        try
        {
            resolved = LuaRuntimeOperations.ResolveCall(
                state,
                unwind.ErrorHandler,
                [unwind.Error]);
        }
        catch (LuaRuntimeException)
        {
            unwind.Error = CreateErrorInErrorHandling(state);
            thread.Owner.WriteBarrier(thread, unwind.Error);
            return;
        }
        var resolvedArguments = resolved.MaterializeArgumentsForRuntime();

        LuaClosure? handlerClosure = resolved.Callable.TryGetClosure();
        var hidden = false;
        if (handlerClosure is null &&
            resolved.Callable.TryGetNativeFunction() is { StepBody: not null })
        {
            handlerClosure = CreateNativeCallbackTrampoline(state, resolved.Callable);
            hidden = true;
        }

        if (handlerClosure is not null)
        {
            var returnBase = thread.CurrentFrame.Top;
            var handlerFrame = PushFrame(
                thread,
                handlerClosure,
                resolvedArguments,
                returnBase,
                expectedResults: 1,
                protectionKind: LuaProtectedCallKind.ErrorHandler,
                isHidden: hidden,
                allowEmergencyCallDepth: true);
            unwind.ActiveErrorHandler = handlerFrame;
            return;
        }

        _ = resolved.Callable.TryGetNativeFunction() ??
            throw new InvalidOperationException("Resolved error handler is not callable.");
        try
        {
            var results = InvokeNativeBody(state, resolved.Callable, resolvedArguments);
            unwind.Error = results.Length == 0 ? LuaValue.Nil : results[0];
            thread.Owner.WriteBarrier(thread, unwind.Error);
        }
        catch (LuaRuntimeException)
        {
            unwind.Error = CreateErrorInErrorHandling(state);
            thread.Owner.WriteBarrier(thread, unwind.Error);
        }
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
        LuaScheduler scheduler,
        LuaThread thread,
        out LuaValue unprotectedError)
    {
        var unwind = thread.UnwindState ??
            throw new InvalidOperationException("No Lua unwind is active.");
        while (thread.FrameCount > 0)
        {
            var frame = thread.CurrentFrame;
            if (ReferenceEquals(frame, unwind.Boundary) &&
                !unwind.SkipProtectedNativeCallback &&
                frame.Continuation.Kind == LuaContinuationKind.NativeCallLua &&
                frame.Continuation.NativeCallbackIsProtected)
            {
                var continuation = frame.Continuation;
                var nativeFunction = continuation.Value;
                var returnBase = continuation.Base;
                var expectedResults = continuation.ExpectedResults;
                var tailCall = (continuation.Count & 1) != 0;
                var operationTop = continuation.NativeOperationTop;
                var operationTransform = continuation.Transform;
                var continuationId = continuation.State;
                var invocationState = continuation.Values;
                var byteBuffer = continuation.NativeByteBuffer;
                var nativeProtectedBoundary = continuation.IsNativeProtectedBoundary;
                var nativeProtectedKind = continuation.ProtectionKind;
                var nativeProtectedFunction = continuation.ProtectionFunction;
                var nativeProtectedHandler = continuation.ErrorHandler;
                var nativeProtectedReturnBase = continuation.NativeProtectedReturnBase;
                var nativeProtectedExpectedResults = continuation.NativeProtectedExpectedResults;
                var nativeProtectedTailCall = continuation.NativeProtectedTailCall;
                continuation.Reset();
                if (nativeProtectedBoundary)
                {
                    continuation.ProtectionKind = nativeProtectedKind;
                    continuation.ProtectionFunction = nativeProtectedFunction;
                    continuation.ErrorHandler = nativeProtectedHandler;
                    continuation.IsNativeProtectedBoundary = true;
                    continuation.NativeProtectedReturnBase = nativeProtectedReturnBase;
                    continuation.NativeProtectedExpectedResults = nativeProtectedExpectedResults;
                    continuation.NativeProtectedTailCall = nativeProtectedTailCall;
                }
                thread.UnwindState = null;
                var descriptor = nativeFunction.TryGetNativeFunction() ??
                    throw new InvalidOperationException(
                        "A protected native callback lost its descriptor.");
                var context = new LuaNativeCallContext(
                    state,
                    thread,
                    nativeFunction.TryGetNativeClosure(),
                    invocationState,
                    byteBuffer);
                try
                {
                    var completed = ContinueNative(
                        state,
                        scheduler,
                        thread,
                        frame,
                        nativeFunction,
                        returnBase,
                        expectedResults,
                        tailCall,
                        programCounterAdvanced: !tailCall,
                        descriptor.StepBody!(
                            context,
                            continuationId,
                            [LuaValue.FromBoolean(false), unwind.Error]),
                        operationTop,
                        operationTransform);
                    if (completed is { } values)
                    {
                        scheduler.Current.ForcedResult = values;
                    }
                }
                catch (LuaRuntimeException)
                {
                    // A resumable native descriptor may reject the protected callback error
                    // itself. Restore the unwind so the native protected boundary (for example
                    // pcall(collectgarbage)) can still convert it to a protected result.
                    thread.UnwindState = unwind;
                    throw;
                }

                unprotectedError = LuaValue.Nil;
                return false;
            }

            if (ReferenceEquals(frame, unwind.Boundary) &&
                frame.Continuation.IsNativeProtectedBoundary)
            {
                var continuation = frame.Continuation;
                var returnBase = continuation.NativeProtectedReturnBase;
                var expectedResults = continuation.NativeProtectedExpectedResults;
                var tailCall = continuation.NativeProtectedTailCall;
                continuation.Reset();
                continuation.ProtectionKind = LuaProtectedCallKind.None;
                continuation.ProtectionFunction = LuaValue.Nil;
                continuation.ErrorHandler = LuaValue.Nil;
                continuation.IsNativeProtectedBoundary = false;
                continuation.NativeProtectedReturnBase = 0;
                continuation.NativeProtectedExpectedResults = 0;
                continuation.NativeProtectedTailCall = false;
                thread.UnwindState = null;
                WriteProtectedResults(
                    thread,
                    frame,
                    returnBase,
                    expectedResults,
                    succeeded: false,
                    [unwind.Error]);
                if (tailCall)
                {
                    continuation.Kind = LuaContinuationKind.ProtectedCall;
                    continuation.Base = returnBase - frame.Base;
                }

                unprotectedError = LuaValue.Nil;
                return false;
            }

            PrepareFrameForErrorClose(frame, unwind.DebugBoundaryFunction);
            if (TryCloseFrom(state, thread, frame, 0, unwind.Error))
            {
                unprotectedError = LuaValue.Nil;
                return false;
            }

            var wasActiveErrorHandler = ReferenceEquals(unwind.ActiveErrorHandler, frame);
            CommitPendingBackedges(frame);
            thread.PopFrame();
            if (frame.IsDebugHook)
            {
                thread.IsRunningDebugHook = false;
            }
            if (wasActiveErrorHandler)
            {
                unwind.ActiveErrorHandler = null;
            }
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
            if (frame.Continuation.ProtectionKind == LuaProtectedCallKind.ProtectedCallWithHandler)
            {
                WriteProtectedResults(
                    thread,
                    caller,
                    frame.ReturnBase,
                    frame.ExpectedResults,
                    succeeded: false,
                    [unwind.Error]);
            }
            else if (frame.Continuation.ProtectionKind == LuaProtectedCallKind.Finalizer)
            {
                state.ReportWarning(unwind.Error);
                state.IsRunningFinalizer = false;
            }
            else if (frame.Continuation.ProtectionKind == LuaProtectedCallKind.ErrorHandler)
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

        if (!unwind.SkipProtectedNativeCallback &&
            thread.RootContinuation.Kind == LuaContinuationKind.NativeCallLua &&
            thread.RootContinuation.NativeCallbackIsProtected)
        {
            var continuation = thread.RootContinuation;
            var nativeFunction = continuation.Value;
            var continuationId = continuation.State;
            var invocationState = continuation.Values;
            var byteBuffer = continuation.NativeByteBuffer;
            continuation.Reset();
            thread.UnwindState = null;
            var descriptor = nativeFunction.TryGetNativeFunction() ??
                throw new InvalidOperationException(
                    "A protected root native callback lost its descriptor.");
            var context = new LuaNativeCallContext(
                state,
                thread,
                nativeFunction.TryGetNativeClosure(),
                invocationState,
                byteBuffer);
            ContinueNativeRoot(
                state,
                scheduler,
                thread,
                nativeFunction,
                descriptor.StepBody!(
                    context,
                    continuationId,
                    [LuaValue.FromBoolean(false), unwind.Error]));
            unprotectedError = LuaValue.Nil;
            return false;
        }

        thread.UnwindState = null;
        unprotectedError = unwind.Error;
        return true;
    }

    internal void RunPendingFinalizer(LuaState state, LuaThread thread)
    {
        if (state.IsRunningFinalizer)
        {
            return;
        }

        for (var index = 0; index < thread.FrameCount; index++)
        {
            if (thread.Frames[index].Continuation.ProtectionKind ==
                LuaProtectedCallKind.Finalizer)
            {
                return;
            }
        }

        while (state.Heap.TryTakePendingFinalizer(out var target, out var finalizer))
        {
            var targetValue = target switch
            {
                LuaTable table => LuaValue.FromTable(table),
                LuaUserdata userdata => LuaValue.FromUserdata(userdata),
                _ => LuaValue.Nil,
            };
            if (targetValue.IsNil)
            {
                LuaHeap.CompleteFinalizer(target);
                continue;
            }

            LuaOperationResolution resolved;
            try
            {
                resolved = LuaRuntimeOperations.ResolveCall(
                    state,
                    finalizer,
                    [targetValue]);
            }
            catch (LuaRuntimeException exception)
            {
                state.ReportWarning(MaterializeError(state, exception));
                LuaHeap.CompleteFinalizer(target);
                continue;
            }

            LuaHeap.CompleteFinalizer(target);
            var resolvedArguments = resolved.MaterializeArgumentsForRuntime();
            if (resolved.Callable.TryGetClosure() is { } closure)
            {
                var caller = thread.CurrentFrame;
                state.IsRunningFinalizer = true;
                var finalizerFrame = PushFrame(
                    thread,
                    closure,
                    resolvedArguments,
                    Math.Max(caller.Top, caller.Base + caller.Function.RegisterCount),
                    expectedResults: 0,
                    protectionKind: LuaProtectedCallKind.Finalizer);
                SetDebugFunctionName(finalizerFrame, "__gc", "metamethod");
                return;
            }

            try
            {
                state.IsRunningFinalizer = true;
                _ = InvokeNativeBody(state, resolved.Callable, resolvedArguments);
            }
            catch (LuaRuntimeException exception)
            {
                state.ReportWarning(MaterializeError(state, exception));
            }
            finally
            {
                state.IsRunningFinalizer = false;
            }
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
        var resolvedArguments = resolved.MaterializeArgumentsForRuntime();

        if (resolved.Callable.TryGetClosure() is { } closure)
        {
            PushFrame(
                thread,
                closure,
                resolvedArguments,
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
                InvokeNativeBody(state, resolved.Callable, resolvedArguments));
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

    internal ImmutableArray<LuaValue>? ExecuteReturn(
        LuaState state,
        LuaScheduler scheduler,
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        if (frame.Continuation.Kind == LuaContinuationKind.None &&
            frame.Continuation.ProtectionKind == LuaProtectedCallKind.None &&
            !frame.Continuation.IsCloseHandler &&
            frame.ToBeClosedSlots.Count == 0 &&
            !frame.IsDebugHook &&
            thread.UnwindState is null &&
            thread.FrameCount > 1 &&
            thread.Frames[^2].Continuation.Kind == LuaContinuationKind.None)
        {
            var start = frame.Base + instruction.A;
            var count = instruction.B < 0 ? Math.Max(0, frame.Top - start) : instruction.B;
            var returnBase = frame.ReturnBase;
            var expectedResults = frame.ExpectedResults;
            var fastCallerFrame = thread.Frames[^2];

            // An ordinary Lua-to-Lua return always moves results towards lower stack slots. The
            // source window therefore remains readable while WriteCallResults copies forward,
            // and the retired frame is deliberately not reset until the next scheduler turn.
            // Keep the resumable close/protection/debug paths on the snapshot-based slow path.
            thread.CloseUpvalues(frame.Base);
            var fastResults = thread.Stack.AsReadOnlySpan(start, count);
            thread.PopFrame();
            WriteCallResults(thread, fastCallerFrame, returnBase, expectedResults, fastResults);
            return null;
        }

        if (frame.Continuation.Kind != LuaContinuationKind.ReturnAndClose)
        {
            var start = frame.Base + instruction.A;
            var count = instruction.B < 0 ? Math.Max(0, frame.Top - start) : instruction.B;
            frame.Continuation.Reset();
            frame.Continuation.Kind = LuaContinuationKind.ReturnAndClose;
            frame.Continuation.Values = new LuaValue[count];
            for (var index = 0; index < count; index++)
            {
                frame.Continuation.Values[index] = thread.Stack.ReadUnchecked(start + index);
            }
        }

        if (TryCloseFrom(state, thread, frame, 0, LuaValue.Nil))
        {
            return null;
        }

        var results = frame.Continuation.Values;
        var protectionKind = frame.Continuation.ProtectionKind;
        frame.Continuation.Reset();
        CommitPendingBackedges(frame);
        thread.PopFrame();
        if (frame.IsDebugHook)
        {
            thread.IsRunningDebugHook = false;
        }
        if (ReferenceEquals(thread.UnwindState?.ActiveCloseCall, frame))
        {
            thread.UnwindState.ActiveCloseCall = null;
        }
        if (ReferenceEquals(thread.UnwindState?.ActiveErrorHandler, frame))
        {
            var unwind = thread.UnwindState!;
            unwind.ActiveErrorHandler = null;
            unwind.Error = results.Length == 0 ? LuaValue.Nil : results[0];
            thread.Owner.WriteBarrier(thread, unwind.Error);
            return null;
        }

        if (thread.FrameCount == 0 &&
            thread.RootContinuation.Kind == LuaContinuationKind.NativeCallLua)
        {
            var nativeFunction = thread.RootContinuation.Value;
            var continuationId = thread.RootContinuation.State;
            var invocationState = thread.RootContinuation.Values;
            var byteBuffer = thread.RootContinuation.NativeByteBuffer;
            var callbackWasProtected = thread.RootContinuation.NativeCallbackIsProtected;
            thread.RootContinuation.Reset();
            var descriptor = nativeFunction.TryGetNativeFunction() ??
                throw new InvalidOperationException("A root native callback lost its descriptor.");
            var context = new LuaNativeCallContext(
                state,
                thread,
                nativeFunction.TryGetNativeClosure(),
                invocationState,
                byteBuffer);
            ContinueNativeRoot(
                state,
                scheduler,
                thread,
                nativeFunction,
                descriptor.StepBody!(
                    context,
                    continuationId,
                    callbackWasProtected
                        ? [LuaValue.FromBoolean(true), .. results]
                        : results));
            return null;
        }

        if (thread.FrameCount == 0)
        {
            return results.ToImmutableArray();
        }

        var callerFrame = thread.CurrentFrame;
        if (callerFrame.Continuation.Kind == LuaContinuationKind.NativeCallLua)
        {
            var continuation = callerFrame.Continuation;
            var nativeFunction = continuation.Value;
            var returnBase = continuation.Base;
            var expectedResults = continuation.ExpectedResults;
            var tailCall = (continuation.Count & 1) != 0;
            var nativeOperationTop = continuation.NativeOperationTop;
            var nativeOperationTransform = continuation.Transform;
            var continuationId = continuation.State;
            var invocationState = continuation.Values;
            var byteBuffer = continuation.NativeByteBuffer;
            var callbackWasProtected = continuation.NativeCallbackIsProtected;
            var nativeProtectedBoundary = continuation.IsNativeProtectedBoundary;
            var nativeProtectedKind = continuation.ProtectionKind;
            var nativeProtectedFunction = continuation.ProtectionFunction;
            var nativeProtectedHandler = continuation.ErrorHandler;
            var nativeProtectedReturnBase = continuation.NativeProtectedReturnBase;
            var nativeProtectedExpectedResults = continuation.NativeProtectedExpectedResults;
            var nativeProtectedTailCall = continuation.NativeProtectedTailCall;
            continuation.Reset();
            if (nativeProtectedBoundary)
            {
                continuation.ProtectionKind = nativeProtectedKind;
                continuation.ProtectionFunction = nativeProtectedFunction;
                continuation.ErrorHandler = nativeProtectedHandler;
                continuation.IsNativeProtectedBoundary = true;
                continuation.NativeProtectedReturnBase = nativeProtectedReturnBase;
                continuation.NativeProtectedExpectedResults = nativeProtectedExpectedResults;
                continuation.NativeProtectedTailCall = nativeProtectedTailCall;
            }
            var descriptor = nativeFunction.TryGetNativeFunction() ??
                throw new InvalidOperationException("A native continuation lost its descriptor.");
            var context = new LuaNativeCallContext(
                state,
                thread,
                nativeFunction.TryGetNativeClosure(),
                invocationState,
                byteBuffer);
            var step = descriptor.StepBody!(
                context,
                continuationId,
                callbackWasProtected
                    ? [LuaValue.FromBoolean(true), .. results]
                    : results);
            return ContinueNative(
                state,
                scheduler,
                thread,
                callerFrame,
                nativeFunction,
                returnBase,
                expectedResults,
                tailCall,
                programCounterAdvanced: !tailCall,
                step,
                nativeOperationTop,
                nativeOperationTransform);
        }

        if (protectionKind != LuaProtectedCallKind.None)
        {
            if (protectionKind == LuaProtectedCallKind.Finalizer)
            {
                state.IsRunningFinalizer = false;
            }

            WriteProtectedResults(
                thread,
                thread.CurrentFrame,
                frame.ReturnBase,
                frame.ExpectedResults,
                succeeded: protectionKind != LuaProtectedCallKind.ErrorHandler,
                results);
            return null;
        }

        var operationTop = callerFrame.Continuation.Kind == LuaContinuationKind.LuaCall
            ? callerFrame.Continuation.Count
            : -1;
        if (operationTop >= 0)
        {
            WriteOperationResults(
                thread,
                callerFrame,
                frame.ReturnBase,
                frame.ExpectedResults,
                results,
                operationTop);
        }
        else
        {
            WriteCallResults(
                thread,
                callerFrame,
                frame.ReturnBase,
                frame.ExpectedResults,
                results);
        }
        ApplyPendingTransform(
            thread,
            callerFrame,
            frame.ReturnBase,
            callerFrame.Continuation.Kind == LuaContinuationKind.LuaCall
                ? callerFrame.Continuation.Transform
                : LuaResultTransform.None);
        if (callerFrame.Continuation.Kind == LuaContinuationKind.LuaCall)
        {
            callerFrame.Continuation.Reset();
        }
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
        bool isCloseHandler = false,
        bool isDebugHook = false,
        bool isHidden = false,
        bool scheduleCallHook = true,
        bool allowEmergencyCallDepth = false)
    {
        const int emergencyCallDepth = 200;
        var callDepthLimit = allowEmergencyCallDepth ||
            thread.UnwindState?.ActiveErrorHandler is not null
                ? (long)_options.MaximumCallDepth + emergencyCallDepth
                : _options.MaximumCallDepth;
        if (thread.FrameCount >= callDepthLimit)
        {
            throw new LuaRuntimeException("stack overflow");
        }

        if (GetActiveCoroutineDepth(thread) >= MaximumCStackDepth ||
            GetActiveCallDepth(thread) >= callDepthLimit)
        {
            throw new LuaRuntimeException("C stack overflow");
        }

        var functionVersion = closure.FunctionVersion;
        var function = functionVersion.Function;
        var @base = thread.FrameCount == 0 ? 0 : AddStackOffset(returnBase, 1);
        var required = AddStackOffset(@base, function.RegisterCount);
        if (required > _options.MaximumStackSlots)
        {
            throw new LuaRuntimeException("The Lua stack slot limit was exceeded.");
        }

        thread.Stack.EnsureCapacity(required);
        thread.Stack.Clear(@base, function.RegisterCount);
        var fixedArguments = Math.Min(arguments.Length, function.ParameterCount);
        for (var index = 0; index < fixedArguments; index++)
        {
            thread.Stack.WriteUnchecked(@base + index, arguments[index]);
        }

        var varArgs = function.IsVarArg && arguments.Length > function.ParameterCount
            ? arguments[function.ParameterCount..]
            : ReadOnlySpan<LuaValue>.Empty;
        var frame = thread.RentFrame(
            closure,
            @base,
            AddStackOffset(@base, function.ParameterCount),
            returnBase,
            expectedResults,
            varArgs,
            protectionKind,
            errorHandler,
            isCloseHandler,
            isDebugHook,
            isHidden,
            functionVersion);
        frame.InstructionRoute = GetInitialFrameInstructionRoute(frame);
        if (scheduleCallHook && !isDebugHook && !isHidden && !thread.IsRunningDebugHook &&
            !thread.DebugHook.IsNil &&
            HasDebugHook(thread, LuaDebugHookMask.Call))
        {
            frame.PendingDebugHookEvent = "call";
            thread.SetDebugHookTransfer(
                thread.Stack.AsReadOnlySpan(@base, function.ParameterCount),
                isNative: false);
        }
        thread.PushFrame(frame);
        return frame;
    }

    private LuaFrame PushFrameFromStack(
        LuaThread thread,
        LuaClosure closure,
        int argumentStart,
        int argumentCount,
        int returnBase,
        int expectedResults,
        int minimumBase = -1)
    {
        const int emergencyCallDepth = 200;
        var callDepthLimit = thread.UnwindState?.ActiveErrorHandler is not null
            ? (long)_options.MaximumCallDepth + emergencyCallDepth
            : _options.MaximumCallDepth;
        if (thread.FrameCount >= callDepthLimit)
        {
            throw new LuaRuntimeException("stack overflow");
        }

        if (GetActiveCoroutineDepth(thread) >= MaximumCStackDepth ||
            GetActiveCallDepth(thread) >= callDepthLimit)
        {
            throw new LuaRuntimeException("C stack overflow");
        }

        var functionVersion = closure.FunctionVersion;
        var function = functionVersion.Function;
        var @base = Math.Max(AddStackOffset(returnBase, 1), minimumBase);
        var required = AddStackOffset(@base, function.RegisterCount);
        if (required > _options.MaximumStackSlots)
        {
            throw new LuaRuntimeException("The Lua stack slot limit was exceeded.");
        }

        var fixedArguments = Math.Min(argumentCount, function.ParameterCount);
        thread.Stack.EnsureCapacity(required);
        var varArgs = function.IsVarArg && argumentCount > function.ParameterCount
            ? thread.Stack.AsReadOnlySpan(
                AddStackOffset(argumentStart, function.ParameterCount),
                argumentCount - function.ParameterCount)
            : ReadOnlySpan<LuaValue>.Empty;
        var frame = thread.RentFrame(
            closure,
            @base,
            @base + function.ParameterCount,
            returnBase,
            expectedResults,
            varArgs,
            functionVersion: functionVersion);
        if (argumentStart != @base)
        {
            if (@base < argumentStart)
            {
                for (var index = 0; index < fixedArguments; index++)
                {
                    thread.Stack.WriteUnchecked(
                        @base + index,
                        thread.Stack.ReadUnchecked(argumentStart + index));
                }
            }
            else
            {
                for (var index = fixedArguments - 1; index >= 0; index--)
                {
                    thread.Stack.WriteUnchecked(
                        @base + index,
                        thread.Stack.ReadUnchecked(argumentStart + index));
                }
            }
        }

        if (function.RegisterCount > fixedArguments)
        {
            thread.Stack.Clear(
                @base + fixedArguments,
                function.RegisterCount - fixedArguments);
        }

        var frameEnd = @base + function.RegisterCount;
        for (var index = 0; index < argumentCount; index++)
        {
            var source = argumentStart + index;
            if (source < @base || source >= frameEnd)
            {
                thread.Stack.WriteUnchecked(source, LuaValue.Nil);
            }
        }

        frame.InstructionRoute = GetInitialFrameInstructionRoute(frame);
        if (!thread.IsRunningDebugHook && !thread.DebugHook.IsNil &&
            HasDebugHook(thread, LuaDebugHookMask.Call))
        {
            frame.PendingDebugHookEvent = "call";
            thread.SetDebugHookTransfer(
                thread.Stack.AsReadOnlySpan(@base, function.ParameterCount),
                isNative: false);
        }

        thread.PushFrame(frame);
        return frame;
    }

    private LuaFrameInstructionRoute GetInitialFrameInstructionRoute(LuaFrame frame) =>
        _instructionExecutor.GetInitialFrameInstructionRoute(frame);

    private void CommitPendingBackedges(LuaFrame frame) =>
        _instructionExecutor.CommitPendingBackedges(frame);

    private bool ShouldUseReferenceInterpreter(
        LuaFrame frame,
        in LuaIrInstruction instruction)
    {
        if (frame.InstructionRoute == LuaFrameInstructionRoute.Interpreter)
        {
            return true;
        }

        if (frame.InstructionRoute != LuaFrameInstructionRoute.InterpreterWithBackedgeProbes)
        {
            return false;
        }

        if (LuaInstructionRouting.IsBackedge(frame.ProgramCounter, instruction))
        {
            frame.UnreportedBackendBackedgeCount++;
            frame.BackendBackedgeProbeCountdown--;
            return frame.BackendBackedgeProbeCountdown > 0;
        }

        if (frame.UnreportedBackendBackedgeCount != 0 &&
            instruction.Opcode is LuaIrOpcode.Return or LuaIrOpcode.TailCall)
        {
            // A terminal instruction has no remaining repeated work for a newly published method.
            // Commit the partial countdown directly rather than entering such a method at Return
            // and incorrectly treating that terminal fragment as a profiled Tier 1 invocation.
            CommitPendingBackedges(frame);
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryContinueCompactInterpreterLoop(
        LuaExecutionContext context,
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        bool runSafePoint)
    {
        if (!CanContinueCompactInterpreterLoop(context, thread, frame))
        {
            return false;
        }

        if (!runSafePoint)
        {
            return true;
        }

        // Bound GC/finalizer latency while avoiding the full scheduler and frame scan for every
        // straight-line T0 instruction. Any exit from the compact loop also returns through the
        // scheduler's ordinary safe point, so short batches do not skip collection progress.
        state.Heap.SafePoint();
        RunPendingFinalizer(state, thread);
        return CanContinueCompactInterpreterLoop(context, thread, frame);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CanContinueCompactInterpreterLoop(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame)
    {
        var scheduler = context.Scheduler;
        if (scheduler is null || scheduler.Transfer != LuaSchedulerTransfer.None ||
            scheduler.Current.HasPendingError || scheduler.Current.ForcedResult is not null ||
            thread.FrameCount == 0 || !ReferenceEquals(thread.CurrentFrame, frame) ||
            thread.UnwindState is not null || thread.IsClosing ||
            frame.Continuation.Kind != LuaContinuationKind.None ||
            frame.InstructionRoute == LuaFrameInstructionRoute.Backend ||
            !context.IsDebugModeCurrent())
        {
            return false;
        }

        // A frame for which hooks cannot fire may stay in the compact loop. Otherwise return to
        // the scheduler so line/count/call/return hooks retain exact per-instruction ordering.
        if (thread.HasDispatchableDebugHook && !frame.IsDebugHook && !frame.IsHidden)
        {
            return false;
        }

        var programCounter = frame.ProgramCounter;
        var instructions = frame.Function.Instructions;
        if ((uint)programCounter >= (uint)instructions.Length)
        {
            return false;
        }

        return frame.InstructionRoute != LuaFrameInstructionRoute.InterpreterWithBackedgeProbes ||
            !LuaInstructionRouting.IsBackedge(programCounter, instructions[programCounter]);
    }

    private static long GetActiveCallDepth(LuaThread thread)
    {
        long depth = 0;
        for (var current = thread; current is not null; current = current.Resumer)
        {
            depth += current.FrameCount;
        }

        return depth;
    }

    private static int GetActiveCoroutineDepth(LuaThread thread)
    {
        var depth = 0;
        for (var current = thread; current is not null; current = current.Resumer)
        {
            depth++;
        }

        return depth;
    }

    private void EnsureScratchWindow(LuaThread thread, int start, int count)
    {
        var required = AddStackOffset(start, count);
        if (required > _options.MaximumStackSlots)
        {
            throw new LuaRuntimeException("The Lua stack slot limit was exceeded.");
        }

        thread.Stack.EnsureCapacity(required);
    }

    private static void CopyOperationArguments(
        LuaThread thread,
        int start,
        LuaOperationResolution resolution)
    {
        for (var index = 0; index < resolution.ArgumentCount; index++)
        {
            thread.Stack.WriteUnchecked(start + index, resolution.GetArgument(index));
        }
    }

    private void EnsureWriteWindow(LuaThread thread, int start, int count)
    {
        if (count != 0)
        {
            EnsureScratchWindow(thread, start, count);
        }
    }

    private static void SetDebugFunctionName(LuaFrame frame, string name, string nameWhat)
    {
        frame.DebugFunctionName = name;
        frame.DebugFunctionNameWhat = nameWhat;
    }

    internal static string GetOperationMetamethodName(LuaIrInstruction instruction) =>
        instruction.Opcode switch
        {
            LuaIrOpcode.GetTable => "index",
            LuaIrOpcode.SetTable => "newindex",
            LuaIrOpcode.Unary => (LuaIrUnaryOperator)instruction.C switch
            {
                LuaIrUnaryOperator.Negate => "unm",
                LuaIrUnaryOperator.BitwiseNot => "bnot",
                LuaIrUnaryOperator.Length => "len",
                _ => throw new InvalidOperationException(
                    "A non-metamethod unary operation requested a Lua call."),
            },
            LuaIrOpcode.Binary => (LuaIrBinaryOperator)instruction.D switch
            {
                LuaIrBinaryOperator.Add => "add",
                LuaIrBinaryOperator.Subtract => "sub",
                LuaIrBinaryOperator.Multiply => "mul",
                LuaIrBinaryOperator.Divide => "div",
                LuaIrBinaryOperator.FloorDivide => "idiv",
                LuaIrBinaryOperator.Modulo => "mod",
                LuaIrBinaryOperator.Power => "pow",
                LuaIrBinaryOperator.Concatenate => "concat",
                LuaIrBinaryOperator.Equal or LuaIrBinaryOperator.NotEqual => "eq",
                LuaIrBinaryOperator.LessThan or LuaIrBinaryOperator.GreaterThan => "lt",
                LuaIrBinaryOperator.LessThanOrEqual or LuaIrBinaryOperator.GreaterThanOrEqual => "le",
                LuaIrBinaryOperator.BitwiseAnd => "band",
                LuaIrBinaryOperator.BitwiseOr => "bor",
                LuaIrBinaryOperator.BitwiseXor => "bxor",
                LuaIrBinaryOperator.ShiftLeft => "shl",
                LuaIrBinaryOperator.ShiftRight => "shr",
                _ => throw new InvalidOperationException(
                    "An unknown binary operation requested a metamethod call."),
            },
            _ => throw new InvalidOperationException(
                $"Instruction {instruction.Opcode} is not a metamethod operation."),
        };

    private void WriteCallResults(
        LuaThread thread,
        LuaFrame caller,
        int returnBase,
        int expectedResults,
        ReadOnlySpan<LuaValue> results)
    {
        var count = expectedResults < 0 ? results.Length : expectedResults;
        EnsureWriteWindow(thread, returnBase, count);
        for (var index = 0; index < count; index++)
        {
            thread.Stack.WriteUnchecked(
                returnBase + index,
                index < results.Length ? results[index] : LuaValue.Nil);
        }

        SetFrameTop(thread, caller, returnBase + count);
    }

    private void WriteOperationResults(
        LuaThread thread,
        LuaFrame caller,
        int returnBase,
        int expectedResults,
        ReadOnlySpan<LuaValue> results,
        int preservedTop)
    {
        var count = expectedResults < 0 ? results.Length : expectedResults;
        EnsureWriteWindow(thread, returnBase, count);
        for (var index = 0; index < count; index++)
        {
            thread.Stack.WriteUnchecked(
                returnBase + index,
                index < results.Length ? results[index] : LuaValue.Nil);
        }

        caller.Top = Math.Max(preservedTop, returnBase + count);
    }

    private void WriteProtectedResults(
        LuaThread thread,
        LuaFrame caller,
        int returnBase,
        int expectedResults,
        bool succeeded,
        ReadOnlySpan<LuaValue> results)
    {
        var available = AddStackOffset(results.Length, 1);
        var count = expectedResults < 0 ? available : expectedResults;
        EnsureWriteWindow(thread, returnBase, count);
        for (var index = 0; index < count; index++)
        {
            thread.Stack.WriteUnchecked(
                returnBase + index,
                index switch
                {
                    0 => LuaValue.FromBoolean(succeeded),
                    _ when index - 1 < results.Length => results[index - 1],
                    _ => LuaValue.Nil,
                });
        }

        SetFrameTop(thread, caller, returnBase + count);
    }

    private static void ApplyPendingTransform(
        LuaThread thread,
        LuaFrame frame,
        int returnBase,
        LuaResultTransform transform)
    {
        if (transform == LuaResultTransform.LogicalNot)
        {
            thread.Stack.WriteUnchecked(
                returnBase,
                LuaValue.FromBoolean(!thread.Stack.ReadUnchecked(returnBase).IsTruthy));
        }

        if (frame.Continuation.Kind == LuaContinuationKind.LuaCall)
        {
            frame.Continuation.Reset();
        }
    }

    internal static LuaClosure CreateClosure(LuaThread thread, LuaFrame parent, int functionId)
    {
        var function = parent.Module.Functions[functionId];
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

        var runtimeData = parent.FunctionVersion.RuntimeData;
        var features = LuaVersionFeatureTable.Get(parent.Module.LanguageVersion);
        if (features.CachesClosuresByUpvalues &&
            runtimeData.GetCachedClosure(functionId, upvalues) is { } cached)
        {
            return cached;
        }

        var closure = new LuaClosure(
            parent.Closure.Owner,
            runtimeData,
            function,
            upvalues);
        if (features.CachesClosuresByUpvalues)
        {
            runtimeData.CacheClosure(functionId, closure);
        }

        return closure;
    }

    internal static void ExecuteSetList(
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
                thread.Stack.ReadUnchecked(source + index));
        }
    }

    internal static void ExecuteVarArg(
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        if (instruction.C != 0)
        {
            throw new InvalidOperationException(
                "A vararg-table instruction requires the active Lua state.");
        }

        ExecuteVarArgCore(null, thread, frame, instruction);
    }

    internal static void ExecuteVarArg(
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction) =>
        ExecuteVarArgCore(state, thread, frame, instruction);

    private static void ExecuteVarArgCore(
        LuaState? state,
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        LuaTable? varArgTable = null;
        var available = frame.VarArgStorage.Count;
        if (instruction.C > 0)
        {
            varArgTable = Read(thread, frame, instruction.C - 1).AsTable();
            var countKey = LuaValue.FromString(
                (state ?? throw new InvalidOperationException(
                    "A vararg table requires the active Lua state."))
                .Strings.GetOrCreate("n"u8));
            var countValue = varArgTable.Get(countKey);
            if (countValue.Kind != LuaValueKind.Integer ||
                countValue.AsIntegerUnchecked() < 0 ||
                countValue.AsIntegerUnchecked() > int.MaxValue / 2)
            {
                throw new LuaRuntimeException("vararg table has no proper 'n'");
            }

            available = checked((int)countValue.AsIntegerUnchecked());
        }

        var count = instruction.B < 0 ? available : instruction.B;
        for (var index = 0; index < count; index++)
        {
            var value = varArgTable is null
                ? index < available ? frame.VarArgStorage[index] : LuaValue.Nil
                : varArgTable.Get(LuaValue.FromInteger(index + 1));
            Write(
                thread,
                frame,
                instruction.A + index,
                value);
        }

        if (instruction.B < 0)
        {
            SetFrameTop(thread, frame, frame.Base + instruction.A + count);
        }
    }

    internal static void ExecuteCreateVarArgTable(
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        var count = frame.VarArgStorage.Count;
        var table = state.CreateTable(count, 1);
        for (var index = 0; index < count; index++)
        {
            table.Set(LuaValue.FromInteger(index + 1), frame.VarArgStorage[index]);
        }

        table.Set(
            LuaValue.FromString(state.Strings.GetOrCreate("n"u8)),
            LuaValue.FromInteger(count));
        Write(thread, frame, instruction.A, LuaValue.FromTable(table));
    }

    internal static void ExecuteGetVarArg(
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        var key = Read(thread, frame, instruction.B);
        LuaValue value;
        if (key.TryGetInteger(out var index) && index >= 1 && index <= frame.VarArgStorage.Count)
        {
            value = frame.VarArgStorage[checked((int)index - 1)];
        }
        else if (key.TryGetString() is { } text && text.AsSpan().SequenceEqual("n"u8))
        {
            value = LuaValue.FromInteger(frame.VarArgStorage.Count);
        }
        else
        {
            value = LuaValue.Nil;
        }

        Write(thread, frame, instruction.A, value);
    }

    internal static void ExecuteErrorIfNotNil(
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        if (Read(thread, frame, instruction.A).IsNil)
        {
            return;
        }

        var name = instruction.B < 0
            ? "?"
            : Encoding.UTF8.GetString(frame.Function.Constants[instruction.B].Bytes.AsSpan());
        throw new LuaRuntimeException($"global '{name}' already defined");
    }

    internal static void SetFrameTop(LuaThread thread, LuaFrame frame, int newTop)
    {
        if (frame.Top > newTop)
        {
            thread.Stack.Clear(newTop, frame.Top - newTop);
        }

        frame.Top = newTop;
    }

    internal static void ExecuteNumericForPrepare(
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        var initial = Read(thread, frame, instruction.A);
        var limit = Read(thread, frame, instruction.A + 1);
        var step = Read(thread, frame, instruction.A + 2);
        bool skipLoop;
        if (initial.Kind == LuaValueKind.Integer && step.Kind == LuaValueKind.Integer)
        {
            var initialInteger = initial.AsInteger();
            var stepInteger = step.AsInteger();
            if (stepInteger == 0)
            {
                throw new LuaRuntimeException("'for' step is zero.");
            }

            Write(thread, frame, instruction.A + 3, initial);
            skipLoop = TryGetIntegerForLimit(limit, stepInteger, out var limitInteger)
                ? stepInteger > 0
                    ? initialInteger > limitInteger
                    : initialInteger < limitInteger
                : LimitOutsideIntegerRange(limit, stepInteger, out limitInteger);
            if (!skipLoop)
            {
                ulong count;
                if (stepInteger > 0)
                {
                    count = unchecked((ulong)limitInteger - (ulong)initialInteger);
                    if (stepInteger != 1)
                    {
                        count /= (ulong)stepInteger;
                    }
                }
                else
                {
                    count = unchecked((ulong)initialInteger - (ulong)limitInteger);
                    count /= unchecked((ulong)(-(stepInteger + 1)) + 1);
                }

                Write(thread, frame, instruction.A + 1, LuaValue.FromInteger(unchecked((long)count)));
            }
        }
        else
        {
            var initialFloat = ToNumericForFloat(initial, "initial value");
            var limitFloat = ToNumericForFloat(limit, "limit");
            var stepFloat = ToNumericForFloat(step, "step");
            if (stepFloat == 0)
            {
                throw new LuaRuntimeException("'for' step is zero.");
            }

            skipLoop = 0 < stepFloat ? limitFloat < initialFloat : initialFloat < limitFloat;
            if (!skipLoop)
            {
                Write(thread, frame, instruction.A, LuaValue.FromFloat(initialFloat));
                Write(thread, frame, instruction.A + 1, LuaValue.FromFloat(limitFloat));
                Write(thread, frame, instruction.A + 2, LuaValue.FromFloat(stepFloat));
                Write(thread, frame, instruction.A + 3, LuaValue.FromFloat(initialFloat));
            }
        }

        if (skipLoop)
        {
            frame.ProgramCounter = instruction.B;
        }
        else
        {
            frame.ProgramCounter++;
        }
    }

    internal static void ExecuteNumericForLoop(
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        var index = Read(thread, frame, instruction.A);
        var counterOrLimit = Read(thread, frame, instruction.A + 1);
        var step = Read(thread, frame, instruction.A + 2);
        bool continues;
        if (step.Kind == LuaValueKind.Integer)
        {
            var count = unchecked((ulong)counterOrLimit.AsInteger());
            var stepInteger = step.AsInteger();
            continues = count > 0;
            if (continues)
            {
                index = LuaValue.FromInteger(unchecked(index.AsInteger() + stepInteger));
                Write(
                    thread,
                    frame,
                    instruction.A + 1,
                    LuaValue.FromInteger(unchecked((long)(count - 1))));
            }
        }
        else
        {
            index = LuaValue.FromFloat(index.AsFloat() + step.AsFloat());
            continues = 0 < step.AsFloat()
                ? index.AsFloat() <= counterOrLimit.AsFloat()
                : counterOrLimit.AsFloat() <= index.AsFloat();
        }

        if (continues)
        {
            Write(thread, frame, instruction.A, index);
            Write(thread, frame, instruction.A + 3, index);
            frame.ProgramCounter = instruction.B;
        }
        else
        {
            frame.ProgramCounter++;
        }
    }

    private static bool TryGetIntegerForLimit(LuaValue value, long step, out long result)
    {
        if (value.Kind == LuaValueKind.Integer)
        {
            result = value.AsInteger();
            return true;
        }

        if (!LuaValueOperations.TryToNumber(value, out var number))
        {
            throw NumericForTypeError("limit", value);
        }

        if (number.Kind == LuaValueKind.Integer)
        {
            result = number.AsInteger();
            return true;
        }

        var floatingPoint = number.AsFloat();
        var rounded = step < 0 ? Math.Ceiling(floatingPoint) : Math.Floor(floatingPoint);
        if (double.IsFinite(rounded) &&
            rounded >= long.MinValue &&
            rounded < 9_223_372_036_854_775_808d)
        {
            result = (long)rounded;
            return true;
        }

        result = 0;
        return false;
    }

    private static bool LimitOutsideIntegerRange(LuaValue value, long step, out long limit)
    {
        if (!LuaValueOperations.TryToNumber(value, out var number))
        {
            throw NumericForTypeError("limit", value);
        }

        var floatingPoint = number.AsFloat();
        if (0 < floatingPoint)
        {
            limit = long.MaxValue;
            return step < 0;
        }

        limit = long.MinValue;
        return step > 0;
    }

    private static double ToNumericForFloat(LuaValue value, string role)
    {
        if (!LuaValueOperations.TryToNumber(value, out var number))
        {
            throw NumericForTypeError(role, value);
        }

        return number.AsFloat();
    }

    private static LuaRuntimeException NumericForTypeError(string role, LuaValue value) =>
        new($"bad 'for' {role} (number expected, got {LuaValueOperations.TypeName(value)})");

    internal bool TryCloseFrom(
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

            var value = thread.Stack.ReadUnchecked(slot);
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
                    "attempt to call a nil value (metamethod 'close')");
            }

            LuaOperationResolution resolved;
            try
            {
                resolved = LuaRuntimeOperations.ResolveCall(state, metamethod, [value, error]);
            }
            catch (LuaRuntimeException)
            {
                throw new LuaRuntimeException(
                    $"attempt to call a {LuaValueOperations.TypeName(metamethod)} value " +
                    "(metamethod 'close')");
            }
            var resolvedArguments = resolved.MaterializeArgumentsForRuntime();
            if (resolved.Callable.TryGetClosure() is { } closure)
            {
                var closeFrame = PushFrame(
                    thread,
                    closure,
                    resolvedArguments,
                    frame.Top,
                    expectedResults: 0,
                    isCloseHandler: true);
                SetDebugFunctionName(closeFrame, "close", "metamethod");
                if (thread.UnwindState is not null)
                {
                    thread.UnwindState.ActiveCloseCall = closeFrame;
                }

                return true;
            }

            _ = resolved.Callable.TryGetNativeFunction() ??
                throw new InvalidOperationException("Resolved __close metamethod is not callable.");
            var nativeCloseFrame = PushFrame(
                thread,
                CreateNativeCallbackTrampoline(state, resolved.Callable),
                resolvedArguments,
                frame.Top,
                expectedResults: 0,
                isCloseHandler: true,
                isHidden: true);
            SetDebugFunctionName(nativeCloseFrame, "close", "metamethod");
            if (thread.UnwindState is not null)
            {
                thread.UnwindState.ActiveCloseCall = nativeCloseFrame;
            }

            return true;
        }

        thread.CloseUpvalues(absolute);
        return false;
    }

    private static void PrepareFrameForErrorClose(
        LuaFrame frame,
        LuaValue debugBoundaryFunction)
    {
        if (debugBoundaryFunction.TryGetNativeFunction() is not { } native)
        {
            return;
        }

        frame.DebugFunctionOverride = debugBoundaryFunction;
        SetDebugFunctionName(frame, native.Name, "global");
    }

    internal static LuaValue MaterializeConstant(
        LuaState state,
        LuaClosure closure,
        LuaIrConstant constant) =>
        constant.Kind switch
        {
            LuaIrConstantKind.Nil => LuaValue.Nil,
            LuaIrConstantKind.Boolean => LuaValue.FromBoolean(constant.Boolean),
            LuaIrConstantKind.Integer => LuaValue.FromInteger(constant.Integer),
            LuaIrConstantKind.Float => LuaValue.FromFloat(constant.Float),
            LuaIrConstantKind.String => LuaValue.FromString(
                MaterializeStringConstant(state, closure, constant.Bytes.AsSpan())),
            _ => throw new InvalidOperationException("Unknown canonical constant kind."),
        };

    internal static LuaValue MaterializeConstant(
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        int constantIndex)
    {
        var constant = frame.Function.Constants[constantIndex];
        return
        constant.Kind switch
        {
            LuaIrConstantKind.Nil => LuaValue.Nil,
            LuaIrConstantKind.Boolean => LuaValue.FromBoolean(constant.Boolean),
            LuaIrConstantKind.Integer => LuaValue.FromInteger(constant.Integer),
            LuaIrConstantKind.Float => LuaValue.FromFloat(constant.Float),
            LuaIrConstantKind.String => LuaValue.FromString(
                frame.GetOrCreateStringConstant(state, thread, constantIndex)),
            _ => throw new InvalidOperationException("Unknown canonical constant kind."),
        };
    }

    private static LuaString MaterializeStringConstant(
        LuaState state,
        LuaClosure closure,
        ReadOnlySpan<byte> bytes)
    {
        if (!ReferenceEquals(state.Heap, closure.Owner))
        {
            throw new LuaRuntimeException("cannot materialize a constant in another Lua state");
        }

        var value = closure.StringConstants.GetOrCreate(state, bytes);
        closure.Owner.WriteBarrier(closure, value);
        return value;
    }
}
