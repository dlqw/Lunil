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
    private void ActivateThread(
        LuaState state,
        LuaScheduler scheduler,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments)
    {
        thread.EnsurePatchGenerationAdmission(state);
        if (thread.Status == LuaThreadStatus.New)
        {
            thread.Started = true;
            thread.Status = LuaThreadStatus.Running;
            thread.SetResumeValues(arguments);
            var closure = thread.Entry.TryGetClosure();
            if (closure is not null)
            {
                PushFrame(thread, closure, arguments, 0, -1);
                thread.SetResumeValues([]);
                return;
            }

            var native = thread.Entry.TryGetNativeFunction() ??
                throw new InvalidOperationException("A coroutine entry is not callable.");
            if (native.Kind == LuaNativeFunctionKind.CoroutineYield)
            {
                thread.RootContinuation.Kind = LuaContinuationKind.CoroutineYield;
                thread.SetYieldedValues(arguments);
                scheduler.RequestYield();
                thread.SetResumeValues([]);
                return;
            }

            if (native.Kind != LuaNativeFunctionKind.Normal)
            {
                PushFrame(
                    thread,
                    CreateNativeCallbackTrampoline(state, thread.Entry),
                    arguments,
                    returnBase: 0,
                    expectedResults: -1,
                    isHidden: true);
                thread.SetResumeValues([]);
                return;
            }

            if (native.StepBody is not null)
            {
                var context = new LuaNativeCallContext(
                    state,
                    thread,
                    thread.Entry.TryGetNativeClosure());
                ContinueNativeRoot(
                    state,
                    scheduler,
                    thread,
                    thread.Entry,
                    InvokeNativeStep(native, context, 0, arguments));
            }
            else
            {
                scheduler.Current.ForcedResult = InvokeNativeBody(state, thread.Entry, arguments)
                    .ToImmutableArray();
            }

            thread.SetResumeValues([]);
            return;
        }

        if (thread.Status != LuaThreadStatus.Suspended)
        {
            throw new LuaRuntimeException($"Cannot resume a {FormatStatus(thread)} coroutine.");
        }

        thread.Status = LuaThreadStatus.Running;
        thread.SetResumeValues(arguments);
        if (thread.FrameCount == 0)
        {
            if (thread.RootContinuation.Kind == LuaContinuationKind.CoroutineYield)
            {
                thread.RootContinuation.Reset();
                scheduler.Current.ForcedResult = ImmutableArray.Create(arguments);
                thread.SetResumeValues([]);
                return;
            }

            if (thread.RootContinuation.Kind == LuaContinuationKind.NativeYield)
            {
                var nativeFunction = thread.RootContinuation.Value;
                var continuationId = thread.RootContinuation.State;
                var invocationState = thread.RootContinuation.Values;
                var byteBuffer = thread.RootContinuation.NativeByteBuffer;
                thread.RootContinuation.Reset();
                var descriptor = nativeFunction.TryGetNativeFunction() ??
                    throw new InvalidOperationException("A root native yield lost its descriptor.");
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
                    descriptor.StepBody!(context, continuationId, arguments));
                thread.SetResumeValues([]);
                return;
            }

            throw new InvalidOperationException("A suspended native coroutine has no continuation.");
        }

        var frame = thread.CurrentFrame;
        if (frame.Continuation.Kind == LuaContinuationKind.NativeYield)
        {
            var continuation = frame.Continuation;
            var nativeFunction = continuation.Value;
            var nativeReturnBase = continuation.Base;
            var nativeExpectedResults = continuation.ExpectedResults;
            var nativeTailCall = (continuation.Count & 1) != 0;
            var nativeOperationTop = continuation.NativeOperationTop;
            var nativeOperationTransform = continuation.Transform;
            var continuationId = continuation.State;
            var invocationState = continuation.Values;
            var byteBuffer = continuation.NativeByteBuffer;
            continuation.Reset();
            var descriptor = nativeFunction.TryGetNativeFunction() ??
                throw new InvalidOperationException("A native yield lost its descriptor.");
            var context = new LuaNativeCallContext(
                state,
                thread,
                nativeFunction.TryGetNativeClosure(),
                invocationState,
                byteBuffer);
            var step = descriptor.StepBody!(context, continuationId, arguments);
            var forcedResult = ContinueNative(
                state,
                scheduler,
                thread,
                frame,
                nativeFunction,
                nativeReturnBase,
                nativeExpectedResults,
                nativeTailCall,
                programCounterAdvanced: !nativeTailCall,
                step,
                nativeOperationTop,
                nativeOperationTransform);
            if (forcedResult is not null)
            {
                scheduler.Current.ForcedResult = forcedResult;
            }
            thread.SetResumeValues([]);
            return;
        }

        // A host debugger pause suspends the thread without a yield continuation; resuming
        // simply continues from the suspended instruction.
        if (thread.DebugPaused)
        {
            thread.DebugPaused = false;
            return;
        }

        if (frame.Continuation.Kind != LuaContinuationKind.CoroutineYield)
        {
            throw new InvalidOperationException("A suspended coroutine has no yield continuation.");
        }

        var returnBase = frame.Continuation.Base;
        var expectedResults = frame.Continuation.ExpectedResults;
        var tailCall = (frame.Continuation.State & 1) != 0;
        var protectedCall = (frame.Continuation.State & 8) != 0;
        var yieldFunction = frame.Continuation.Value;
        if (protectedCall)
        {
            WriteProtectedResults(
                thread,
                frame,
                returnBase,
                expectedResults,
                succeeded: true,
                arguments);
        }
        else
        {
            WriteCallResults(thread, frame, returnBase, expectedResults, arguments);
        }
        frame.Continuation.Reset();
        if (tailCall)
        {
            frame.Continuation.Kind = LuaContinuationKind.ProtectedCall;
            frame.Continuation.Base = returnBase - frame.Base;
        }
        else
        {
            ScheduleNativeReturnHook(thread, frame, yieldFunction, arguments);
        }

        thread.SetResumeValues([]);
    }

    private void ActivateNestedThread(
        LuaState state,
        LuaScheduler scheduler,
        LuaThread resumer,
        LuaThread target,
        ReadOnlySpan<LuaValue> arguments)
    {
        resumer.Status = LuaThreadStatus.Normal;
        resumer.ActiveResumee = target;
        target.Resumer = resumer;
        scheduler.Push(target, isYieldable: true);
        try
        {
            ActivateThread(state, scheduler, target, arguments);
        }
        catch (LuaRuntimeException exception)
        {
            scheduler.Pop();
            target.Resumer = null;
            resumer.ActiveResumee = null;
            resumer.Status = LuaThreadStatus.Running;
            var error = MaterializeError(state, exception);
            target.TerminalError = error;
            target.Owner.WriteBarrier(target, error);
            target.Status = LuaThreadStatus.Error;
            InjectCoroutineResult(scheduler.Current, resumer, succeeded: false, [error]);
        }
        catch
        {
            scheduler.Pop();
            target.Resumer = null;
            resumer.ActiveResumee = null;
            resumer.Status = LuaThreadStatus.Running;
            throw;
        }
    }

    private static bool CompleteDebugPause(
        LuaScheduler scheduler,
        LuaThread thread,
        out LuaExecutionResult? result)
    {
        // Suspend every active thread of the turn. Coroutines keep their frames and resume
        // through Lua-side coroutine.resume after the root thread is resumed by the debugger.
        for (var index = 0; index < scheduler.Count; index++)
        {
            var activeThread = scheduler.GetActivation(index).Thread;
            activeThread.Status = LuaThreadStatus.Suspended;
            activeThread.DebugPaused = true;
        }

        result = new LuaExecutionResult(LuaVmSignal.Paused, []);
        return true;
    }

    private bool CompleteYield(
        LuaScheduler scheduler,
        LuaThread thread,
        out LuaExecutionResult? result)
    {
        thread.Status = LuaThreadStatus.Suspended;
        if (scheduler.Count == 1)
        {
            result = new LuaExecutionResult(
                LuaVmSignal.Yielded,
                ImmutableArray.Create(thread.YieldedSpan));
            return true;
        }

        scheduler.Pop();
        var resumer = scheduler.Current.Thread;
        thread.Resumer = null;
        resumer.ActiveResumee = null;
        resumer.Status = LuaThreadStatus.Running;
        InjectCoroutineResult(
            scheduler.Current,
            resumer,
            succeeded: true,
            thread.YieldedSpan);
        result = null;
        return false;
    }

    private bool CompleteThread(
        LuaScheduler scheduler,
        LuaThread thread,
        ImmutableArray<LuaValue> values,
        out LuaExecutionResult? result)
    {
        thread.Status = LuaThreadStatus.Dead;
        thread.TerminalError = LuaValue.Nil;
        thread.ClearTransferValues();
        if (scheduler.Count == 1)
        {
            result = new LuaExecutionResult(LuaVmSignal.Completed, values);
            return true;
        }

        scheduler.Pop();
        var resumer = scheduler.Current.Thread;
        thread.Resumer = null;
        resumer.ActiveResumee = null;
        resumer.Status = LuaThreadStatus.Running;
        InjectCoroutineResult(scheduler.Current, resumer, succeeded: true, values.AsSpan());
        result = null;
        return false;
    }

    private bool FailThread(
        LuaState state,
        LuaScheduler scheduler,
        LuaThread thread,
        LuaValue error,
        out LuaExecutionResult? result)
    {
        state.Heap.ValidateValue(error);
        thread.TerminalError = error;
        thread.Owner.WriteBarrier(thread, error);
        thread.Status = LuaThreadStatus.Error;
        if (scheduler.Count == 1)
        {
            result = new LuaExecutionResult(LuaVmSignal.Error, [error]);
            return true;
        }

        scheduler.Pop();
        var resumer = scheduler.Current.Thread;
        thread.Resumer = null;
        resumer.ActiveResumee = null;
        resumer.Status = LuaThreadStatus.Running;
        InjectCoroutineResult(scheduler.Current, resumer, succeeded: false, [error]);
        result = null;
        return false;
    }

    private void InjectCoroutineResult(
        LuaActivation activation,
        LuaThread resumer,
        bool succeeded,
        ReadOnlySpan<LuaValue> values)
    {
        var frame = resumer.CurrentFrame;
        var continuation = frame.Continuation;
        if (continuation.Kind != LuaContinuationKind.CoroutineResume)
        {
            throw new InvalidOperationException("The resumer has no coroutine continuation.");
        }

        var returnBase = continuation.Base;
        var expectedResults = continuation.ExpectedResults;
        var tailCall = (continuation.State & 1) != 0;
        var wrap = (continuation.State & 2) != 0;
        var protectedCall = (continuation.State & 8) != 0;
        var function = continuation.Value;
        continuation.Reset();
        if (wrap && !succeeded && !protectedCall)
        {
            activation.PendingError = values[0];
            activation.HasPendingError = true;
            return;
        }

        if (protectedCall && wrap)
        {
            WriteProtectedResults(
                resumer,
                frame,
                returnBase,
                expectedResults,
                succeeded,
                values);
        }
        else if (protectedCall)
        {
            var resumeResults = new LuaValue[values.Length + 1];
            resumeResults[0] = LuaValue.FromBoolean(succeeded);
            values.CopyTo(resumeResults.AsSpan(1));
            WriteProtectedResults(
                resumer,
                frame,
                returnBase,
                expectedResults,
                succeeded: true,
                resumeResults);
        }
        else if (wrap)
        {
            WriteCallResults(resumer, frame, returnBase, expectedResults, values);
        }
        else
        {
            WriteProtectedResults(
                resumer,
                frame,
                returnBase,
                expectedResults,
                succeeded,
                values);
        }

        if (!wrap || succeeded)
        {
            ScheduleNativeReturnHook(resumer, frame, function, values);
        }

        if (tailCall)
        {
            continuation.Kind = LuaContinuationKind.ProtectedCall;
            continuation.Base = returnBase - frame.Base;
        }
    }

    private static bool HasProtectedBoundary(
        LuaThread thread,
        bool includeProtectedNativeCallbacks = true)
    {
        for (var index = thread.Frames.Count - 1; index >= 0; index--)
        {
            var continuation = thread.Frames[index].Continuation;
            if (continuation.ProtectionKind != LuaProtectedCallKind.None ||
                includeProtectedNativeCallbacks &&
                continuation.Kind == LuaContinuationKind.NativeCallLua &&
                continuation.NativeCallbackIsProtected)
            {
                return true;
            }
        }

        return includeProtectedNativeCallbacks &&
            thread.RootContinuation.Kind == LuaContinuationKind.NativeCallLua &&
            thread.RootContinuation.NativeCallbackIsProtected;
    }

    private static bool IsAtNonYieldableBoundary(LuaThread thread)
    {
        if (thread.IsClosing)
        {
            return true;
        }

        if (thread.RootContinuation.IsYieldBarrier)
        {
            return true;
        }

        for (var index = thread.Frames.Count - 1; index >= 0; index--)
        {
            var continuation = thread.Frames[index].Continuation;
            if (continuation.ProtectionKind is LuaProtectedCallKind.Finalizer
                    or LuaProtectedCallKind.ErrorHandler ||
                continuation.IsYieldBarrier)
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateThreadEntry(LuaState state, LuaThread thread)
    {
        LunilGuard.NotNull(state);
        LunilGuard.NotNull(thread);
        state.Heap.ValidateValue(LuaValue.FromThread(thread));
        if (thread.Entry.Kind != LuaValueKind.Function)
        {
            throw new LuaRuntimeException("The coroutine has no entry function.");
        }
    }

    private static void ValidateResumableThread(
        LuaState state,
        LuaThread current,
        LuaThread target)
    {
        state.Heap.ValidateValue(LuaValue.FromThread(target));
        if (ReferenceEquals(current, target) ||
            target.Status is not (LuaThreadStatus.New or LuaThreadStatus.Suspended))
        {
            var status = target.Status is LuaThreadStatus.Dead or LuaThreadStatus.Error
                ? "dead"
                : "non-suspended";
            throw new LuaRuntimeException($"cannot resume {status} coroutine");
        }
    }

    private static void ValidateClosableThread(LuaState state, LuaThread thread)
    {
        state.Heap.ValidateValue(LuaValue.FromThread(thread));
        if (thread.Status == LuaThreadStatus.Normal)
        {
            throw new LuaRuntimeException("cannot close a normal coroutine");
        }

        if (ReferenceEquals(thread, state.MainThread) ||
            thread.Status == LuaThreadStatus.Running)
        {
            throw new LuaRuntimeException("cannot close a running coroutine");
        }
    }

    private static string FormatStatus(LuaThread thread) => thread.Status switch
    {
        LuaThreadStatus.New or LuaThreadStatus.Suspended => "suspended",
        LuaThreadStatus.Running => "running",
        LuaThreadStatus.Normal => "normal",
        LuaThreadStatus.Dead or LuaThreadStatus.Error => "dead",
        _ => throw new InvalidOperationException("Unknown coroutine status."),
    };

    private ImmutableArray<LuaValue>? ExecuteCoroutineIntrinsic(
        LuaState state,
        LuaScheduler scheduler,
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction,
        LuaValue function,
        LuaNativeFunction intrinsic,
        ReadOnlySpan<LuaValue> arguments,
        int returnBase,
        bool tailCall,
        bool protectedCall = false)
    {
        if (intrinsic.Kind == LuaNativeFunctionKind.CoroutineYield)
        {
            if (TryScheduleNativeCallHook(thread, frame, function, arguments))
            {
                return null;
            }

            if (IsAtNonYieldableBoundary(thread))
            {
                throw new LuaRuntimeException(
                    "attempt to yield across a non-yieldable boundary",
                    bypassProtectedNativeCallback: true);
            }

            if (!scheduler.Current.IsYieldable || ReferenceEquals(thread, state.MainThread))
            {
                throw new LuaRuntimeException("attempt to yield from outside a coroutine")
                { Kind = LuaRuntimeErrorKind.AttemptTo };
            }

            frame.Continuation.Kind = LuaContinuationKind.CoroutineYield;
            frame.Continuation.Base = returnBase;
            frame.Continuation.ExpectedResults = tailCall ? -1 : instruction.C;
            frame.Continuation.State = (tailCall ? 1 : 0) | (protectedCall ? 8 : 0);
            frame.Continuation.Value = function;
            thread.Owner.WriteBarrier(thread, function);
            if (!tailCall)
            {
                frame.ProgramCounter++;
            }

            thread.SetYieldedValues(arguments);
            scheduler.RequestYield();
            return null;
        }

        LuaThread target;
        var argumentOffset = 0;
        var wrap = intrinsic.Kind == LuaNativeFunctionKind.CoroutineWrap;
        if (wrap)
        {
            var nativeClosure = function.TryGetNativeClosure() ??
                throw new InvalidOperationException("coroutine.wrap must be a native closure.");
            target = nativeClosure.Captures[0].AsThread();
        }
        else
        {
            if (arguments.Length == 0 || arguments[0].Kind != LuaValueKind.Thread)
            {
                throw new LuaRuntimeException("bad argument #1 to 'resume' (thread expected)")
                { Kind = LuaRuntimeErrorKind.BadArgument };
            }

            target = arguments[0].AsThread();
            argumentOffset = 1;
        }

        state.Heap.ValidateValue(LuaValue.FromThread(target));
        try
        {
            ValidateResumableThread(state, thread, target);
        }
        catch (LuaRuntimeException exception) when (!wrap)
        {
            var failure = new[]
            {
                LuaValue.FromBoolean(false),
                MaterializeError(state, exception),
            };
            if (tailCall)
            {
                EnsureWriteWindow(thread, returnBase, failure.Length);
                for (var index = 0; index < failure.Length; index++)
                {
                    thread.Stack.WriteUnchecked(returnBase + index, failure[index]);
                }

                return ExecuteReturn(
                    state,
                    scheduler,
                    thread,
                    frame,
                    new LuaIrInstruction(
                        LuaIrOpcode.Return,
                        returnBase - frame.Base,
                        failure.Length));
            }

            if (protectedCall)
            {
                WriteProtectedResults(
                    thread,
                    frame,
                    returnBase,
                    instruction.C,
                    succeeded: true,
                    failure);
            }
            else
            {
                WriteCallResults(thread, frame, returnBase, instruction.C, failure);
            }

            frame.ProgramCounter++;
            return null;
        }

        frame.Continuation.Kind = LuaContinuationKind.CoroutineResume;
        frame.Continuation.Base = returnBase;
        frame.Continuation.ExpectedResults = tailCall ? -1 : instruction.C;
        frame.Continuation.State = (tailCall ? 1 : 0) | (wrap ? 2 : 0) |
            (protectedCall ? 8 : 0);
        frame.Continuation.Value = function;
        thread.Owner.WriteBarrier(thread, function);
        if (!tailCall)
        {
            frame.ProgramCounter++;
        }

        scheduler.RequestResume(target, arguments[argumentOffset..]);
        return null;
    }

    private ImmutableArray<LuaValue>? ContinueNative(
        LuaState state,
        LuaScheduler scheduler,
        LuaThread thread,
        LuaFrame frame,
        LuaValue nativeFunction,
        int returnBase,
        int expectedResults,
        bool tailCall,
        bool programCounterAdvanced,
        LuaNativeStep step,
        int operationTop = -1,
        LuaResultTransform operationTransform = LuaResultTransform.None)
    {
        var descriptor = nativeFunction.TryGetNativeFunction() ??
            throw new InvalidOperationException("A native continuation has no descriptor.");
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
                    var nativeProtected = frame.Continuation.IsNativeProtectedBoundary;
                    LuaValue[] completedValues = nativeProtected
                        ? [LuaValue.FromBoolean(true), .. step.Values]
                        : step.Values;
                    frame.Continuation.Reset();
                    if (nativeProtected)
                    {
                        frame.Continuation.ProtectionKind = LuaProtectedCallKind.None;
                        frame.Continuation.ProtectionFunction = LuaValue.Nil;
                        frame.Continuation.ErrorHandler = LuaValue.Nil;
                        frame.Continuation.IsNativeProtectedBoundary = false;
                        frame.Continuation.NativeProtectedReturnBase = 0;
                        frame.Continuation.NativeProtectedExpectedResults = 0;
                        frame.Continuation.NativeProtectedTailCall = false;
                    }

                    if (tailCall)
                    {
                        EnsureWriteWindow(thread, returnBase, completedValues.Length);
                        for (var index = 0; index < completedValues.Length; index++)
                        {
                            thread.Stack.WriteUnchecked(returnBase + index, completedValues[index]);
                        }

                        return ExecuteReturn(
                            state,
                            scheduler,
                            thread,
                            frame,
                            new LuaIrInstruction(
                                LuaIrOpcode.Return,
                                returnBase - frame.Base,
                                completedValues.Length));
                    }

                    if (operationTop >= 0)
                    {
                        WriteOperationResults(
                            thread,
                            frame,
                            returnBase,
                            expectedResults,
                            completedValues,
                            operationTop);
                        ApplyPendingTransform(
                            thread,
                            frame,
                            returnBase,
                            operationTransform);
                    }
                    else
                    {
                        WriteCallResults(
                            thread,
                            frame,
                            returnBase,
                            expectedResults,
                            completedValues);
                    }

                    if (!programCounterAdvanced)
                    {
                        frame.ProgramCounter++;
                    }

                    return null;

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
                        frame.Continuation.Kind = LuaContinuationKind.NativeCallLua;
                        frame.Continuation.State = step.ContinuationId;
                        frame.Continuation.Base = returnBase;
                        frame.Continuation.ExpectedResults = expectedResults;
                        frame.Continuation.Count = tailCall ? 1 : 0;
                        frame.Continuation.NativeOperationTop = operationTop;
                        frame.Continuation.Transform = operationTransform;
                        frame.Continuation.Value = nativeFunction;
                        frame.Continuation.IsYieldBarrier = !step.CallIsYieldable;
                        frame.Continuation.NativeCallbackIsProtected = step.CallIsProtected;
                        SaveNativeInvocationState(
                            thread,
                            frame.Continuation,
                            step.StateValues,
                            step.ByteBuffer,
                            step.StateValuesAreReusable);
                        thread.Owner.WriteBarrier(thread, nativeFunction);
                        if (!tailCall && !programCounterAdvanced)
                        {
                            frame.ProgramCounter++;
                        }

                        PushFrame(
                            thread,
                            closure,
                            resolvedArguments,
                            frame.Top,
                            expectedResults: -1);
                        return null;
                    }

                    var callback = resolved.Callable.TryGetNativeFunction() ??
                        throw new InvalidOperationException("A native callback is not callable.");
                    if (callback.StepBody is not null ||
                        callback.Kind != LuaNativeFunctionKind.Normal)
                    {
                        frame.Continuation.Kind = LuaContinuationKind.NativeCallLua;
                        frame.Continuation.State = step.ContinuationId;
                        frame.Continuation.Base = returnBase;
                        frame.Continuation.ExpectedResults = expectedResults;
                        frame.Continuation.Count = tailCall ? 1 : 0;
                        frame.Continuation.NativeOperationTop = operationTop;
                        frame.Continuation.Transform = operationTransform;
                        frame.Continuation.Value = nativeFunction;
                        frame.Continuation.IsYieldBarrier = !step.CallIsYieldable;
                        frame.Continuation.NativeCallbackIsProtected = step.CallIsProtected;
                        SaveNativeInvocationState(
                            thread,
                            frame.Continuation,
                            step.StateValues,
                            step.ByteBuffer,
                            step.StateValuesAreReusable);
                        thread.Owner.WriteBarrier(thread, nativeFunction);
                        if (!tailCall && !programCounterAdvanced)
                        {
                            frame.ProgramCounter++;
                        }

                        PushFrame(
                            thread,
                            CreateNativeCallbackTrampoline(state, resolved.Callable),
                            resolvedArguments,
                            frame.Top,
                            expectedResults: -1,
                            isHidden: true);
                        return null;
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
                    var context = new LuaNativeCallContext(
                        state,
                        thread,
                        nativeFunction.TryGetNativeClosure(),
                        step.StateValues,
                        step.ByteBuffer);
                    step = descriptor.StepBody!(context, step.ContinuationId, callbackResults);
                    continue;

                case LuaNativeStepKind.Yielded:
                    if (!scheduler.Current.IsYieldable || IsAtNonYieldableBoundary(thread) ||
                        ReferenceEquals(thread, state.MainThread))
                    {
                        throw new LuaRuntimeException(
                            "attempt to yield across a non-yieldable boundary",
                            bypassProtectedNativeCallback: true);
                    }

                    frame.Continuation.Kind = LuaContinuationKind.NativeYield;
                    frame.Continuation.State = step.ContinuationId;
                    frame.Continuation.Base = returnBase;
                    frame.Continuation.ExpectedResults = expectedResults;
                    frame.Continuation.Count = tailCall ? 1 : 0;
                    frame.Continuation.NativeOperationTop = operationTop;
                    frame.Continuation.Transform = operationTransform;
                    frame.Continuation.Value = nativeFunction;
                    SaveNativeInvocationState(
                        thread,
                        frame.Continuation,
                        step.StateValues,
                        step.ByteBuffer,
                        step.StateValuesAreReusable);
                    thread.Owner.WriteBarrier(thread, nativeFunction);
                    if (!tailCall && !programCounterAdvanced)
                    {
                        frame.ProgramCounter++;
                    }

                    thread.SetYieldedValues(step.Values);
                    scheduler.RequestYield();
                    return null;

                default:
                    throw new InvalidOperationException("Unknown native step kind.");
            }
        }
    }
}
