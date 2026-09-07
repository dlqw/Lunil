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

/// <summary>Shared resumable scheduler and execution kernel used by all execution backends.</summary>
internal sealed partial class LuaExecutionEngine
{
    private const int MaximumCStackDepth = 120;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LuaIrModule, object> VerifiedModules = new();

    private static readonly object VerifiedMarker = new();

    private readonly LuaInterpreterOptions _options;
    private readonly ILuaInstructionExecutor _instructionExecutor;
    private readonly LuaInterpreterInstructionExecutor _referenceInstructionExecutor = new();
    private int _schedulerNestingDepth;

    internal LuaExecutionEngine(
        LuaInterpreterOptions? options = null,
        ILuaInstructionExecutor? instructionExecutor = null)
    {
        _options = options ?? LuaInterpreterOptions.Default;
        _instructionExecutor = instructionExecutor ?? _referenceInstructionExecutor;
        LunilGuard.Positive(_options.MaximumInstructionCount);
        LunilGuard.Positive(_options.MaximumStackSlots);
        LunilGuard.Positive(_options.MaximumCallDepth);
    }

    public LuaExecutionResult Execute(
        LuaState state,
        LuaClosure closure,
        ReadOnlySpan<LuaValue> arguments = default)
    {
        LunilGuard.NotNull(state);
        LunilGuard.NotNull(closure);
        lock (state.ExecutionGate)
        {
            state.Heap.ValidateValue(LuaValue.FromFunction(closure));
            // Verification is deterministic over the immutable module, so repeated
            // Execute calls (REPL loops, game scripts) verify only once per module.
            if (!VerifiedModules.TryGetValue(closure.Module, out _))
            {
                var verificationErrors = LuaIrVerifier.Verify(closure.Module);
                if (!verificationErrors.IsEmpty)
                {
                    throw new LuaRuntimeException(
                        $"Cannot execute invalid canonical IR: {verificationErrors[0].Message}");
                }

                // A racing Execute may have verified and marked it first; GetValue
                // keeps that benign race from throwing and is available on
                // netstandard2.1, where ConditionalWeakTable has no TryAdd.
                VerifiedModules.GetValue(closure.Module, static _ => VerifiedMarker);
            }

            var thread = state.MainThread;
            thread.Initialize(LuaValue.FromFunction(closure));
            var result = RunScheduler(state, thread, arguments, yieldableRoot: false);
            if (result.Signal == LuaVmSignal.Error)
            {
                throw new LuaRuntimeException(result.Values[0]);
            }

            return result;
        }
    }

    public LuaExecutionResult ExecuteBinaryChunk(
        LuaState state,
        ReadOnlySpan<byte> binaryChunk,
        ReadOnlySpan<LuaValue> arguments = default,
        Lua54ChunkReaderOptions? readerOptions = null)
    {
        LunilGuard.NotNull(state);
        lock (state.ExecutionGate)
        {
            return Execute(state, state.LoadBinaryChunk(binaryChunk, readerOptions), arguments);
        }
    }

    public LuaExecutionResult Start(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default)
        => Start(state, thread, _options.MaximumInstructionCount, arguments);

    public LuaExecutionResult Start(
        LuaState state,
        LuaThread thread,
        long maximumInstructionCount,
        ReadOnlySpan<LuaValue> arguments = default)
    {
        LunilGuard.NotNull(state);
        ValidateInstructionLimit(maximumInstructionCount);
        lock (state.ExecutionGate)
        {
            ValidateThreadEntry(state, thread);
            if (thread.Status != LuaThreadStatus.New || thread.Started)
            {
                throw new LuaRuntimeException("Cannot start a coroutine that has already started.");
            }

            return RunScheduler(
                state,
                thread,
                arguments,
                yieldableRoot: true,
                maximumInstructionCount: maximumInstructionCount);
        }
    }

    public LuaExecutionResult Resume(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default)
        => Resume(state, thread, _options.MaximumInstructionCount, arguments);

    public LuaExecutionResult Resume(
        LuaState state,
        LuaThread thread,
        long maximumInstructionCount,
        ReadOnlySpan<LuaValue> arguments = default)
    {
        LunilGuard.NotNull(state);
        ValidateInstructionLimit(maximumInstructionCount);
        lock (state.ExecutionGate)
        {
            ValidateThreadEntry(state, thread);
            if (thread.Status == LuaThreadStatus.New && !thread.Started)
            {
                return RunScheduler(
                    state,
                    thread,
                    arguments,
                    yieldableRoot: true,
                    maximumInstructionCount: maximumInstructionCount);
            }

            if (thread.Status != LuaThreadStatus.Suspended)
            {
                throw new LuaRuntimeException($"Cannot resume a {FormatStatus(thread)} coroutine.");
            }

            return RunScheduler(
                state,
                thread,
                arguments,
                yieldableRoot: true,
                maximumInstructionCount: maximumInstructionCount);
        }
    }

    /// <summary>
    /// Resumes a turn suspended by a host debugger pause. The root thread and the suspended
    /// coroutine chain (root → ActiveResumee → …) are reactivated together so pending
    /// <c>coroutine.resume</c> calls complete with their results.
    /// </summary>
    public LuaExecutionResult ResumeDebuggedTurn(
        LuaState state,
        LuaThread root,
        ReadOnlySpan<LuaValue> arguments = default)
    {
        LunilGuard.NotNull(state);
        lock (state.ExecutionGate)
        {
            ValidateThreadEntry(state, root);
            if (root.Status != LuaThreadStatus.Suspended || !root.DebugPaused)
            {
                throw new LuaRuntimeException("Cannot resume a thread that is not debugger-paused.");
            }

            var chain = new List<LuaThread> { root };
            while (chain[^1].ActiveResumee is { } nested)
            {
                chain.Add(nested);
            }

            for (var index = 1; index < chain.Count; index++)
            {
                if (chain[index].Status != LuaThreadStatus.Suspended || !chain[index].DebugPaused)
                {
                    throw new LuaRuntimeException("The suspended coroutine chain is inconsistent.");
                }
            }

            // The innermost thread executes; its debug pause is cleared by ActivateThread.
            // The outer threads become waiting resumers and are reactivated when the inner
            // thread completes and injects its results.
            for (var index = 0; index < chain.Count - 1; index++)
            {
                chain[index].DebugPaused = false;
            }

            return RunDebuggedTurn(state, chain, arguments);
        }
    }

    private LuaExecutionResult RunDebuggedTurn(
        LuaState state,
        List<LuaThread> chain,
        ReadOnlySpan<LuaValue> arguments)
    {
        // chain[0] is the root resumer (waiting), chain[^1] is the innermost thread to execute.
        // Rebuild the nested scheduler so coroutine completion injects results up the chain.
        var innermost = chain[^1];
        var scheduler = new LuaScheduler(chain[0], yieldableRoot: true, _options.MaximumInstructionCount);
        if (chain.Count > 1)
        {
            chain[0].Status = LuaThreadStatus.Normal;
        }

        for (var index = 1; index < chain.Count; index++)
        {
            chain[index].Resumer = chain[index - 1];
            chain[index - 1].ActiveResumee = chain[index];
            if (index < chain.Count - 1)
            {
                chain[index].Status = LuaThreadStatus.Normal;
            }

            scheduler.Push(chain[index], isYieldable: true);
        }

        return RunScheduler(
            state,
            innermost,
            arguments,
            yieldableRoot: true,
            schedulerOverride: scheduler);
    }

    public LuaExecutionResult Close(LuaState state, LuaThread thread)
    {
        LunilGuard.NotNull(state);
        LunilGuard.NotNull(thread);
        lock (state.ExecutionGate)
        {
            state.Heap.ValidateValue(LuaValue.FromThread(thread));
            ValidateClosableThread(state, thread);

            if (thread.Status is LuaThreadStatus.New or LuaThreadStatus.Dead)
            {
                thread.FinishClosed();
                return new LuaExecutionResult(LuaVmSignal.Completed, []);
            }

            var failed = thread.Status == LuaThreadStatus.Error;
            var error = failed ? thread.TerminalError : LuaValue.Nil;
            thread.IsClosing = true;
            thread.CloseHadError = failed;
            thread.UnwindState = new LuaUnwindState(
                boundary: null,
                error,
                debugBoundaryFunction: LuaValue.Nil,
                errorHandler: LuaValue.Nil);
            var closeResult = RunScheduler(
                state,
                thread,
                arguments: [],
                yieldableRoot: false,
                activateThread: false);
            var closeError = closeResult.Signal == LuaVmSignal.Error && closeResult.Values.Length != 0
                ? closeResult.Values[0]
                : LuaValue.Nil;
            failed = thread.CloseHadError;
            thread.FinishClosed();
            return failed
                ? new LuaExecutionResult(LuaVmSignal.Error, [closeError])
                : new LuaExecutionResult(LuaVmSignal.Completed, []);
        }
    }

    private LuaExecutionResult RunScheduler(
        LuaState state,
        LuaThread root,
        ReadOnlySpan<LuaValue> arguments,
        bool yieldableRoot,
        bool activateThread = true,
        long? maximumInstructionCount = null,
        LuaScheduler? schedulerOverride = null)
    {
        if (_schedulerNestingDepth >= MaximumCStackDepth)
        {
            throw new LuaRuntimeException("C stack overflow");
        }

        var previousRunningThread = state.RunningThread;
        var previousRunningThreadIsYieldable = state.RunningThreadIsYieldable;
        var previousIsRunningFinalizer = state.IsRunningFinalizer;
        // The debug session is attached between turns; a turn never changes it, so the
        // checkpoint below can test a hoisted local instead of re-reading the state field on
        // every instruction of the hot loop.
        var hasDebugSession = state.DebugSession is not null;
        _schedulerNestingDepth++;
        state.Heap.AddPermanentRoot(root);
        try
        {
            var scheduler = schedulerOverride ?? new LuaScheduler(
                root,
                yieldableRoot,
                maximumInstructionCount ?? _options.MaximumInstructionCount);
            if (schedulerOverride is null)
            {
                if (activateThread)
                {
                    ActivateThread(state, scheduler, root, arguments);
                }
                else
                {
                    root.Status = LuaThreadStatus.Running;
                }
            }
            else if (activateThread)
            {
                ActivateThread(state, scheduler, root, arguments);
            }
            while (scheduler.Count != 0)
            {
                var activation = scheduler.Current;
                var thread = activation.Thread;
                thread.AdvanceFramePoolEpoch();
                if (activation.ForcedResult is { } forcedResult)
                {
                    activation.ForcedResult = null;
                    if (CompleteThread(scheduler, thread, forcedResult, out var forcedExecutionResult))
                    {
                        return RecordInstructionCount(forcedExecutionResult!, scheduler);
                    }

                    continue;
                }

                state.RunningThread = thread;
                state.RunningThreadIsYieldable = activation.IsYieldable &&
                    !IsAtNonYieldableBoundary(thread);
                if (activation.HasPendingError)
                {
                    var pendingError = activation.PendingError;
                    activation.HasPendingError = false;
                    activation.PendingError = LuaValue.Nil;
                    if (thread.UnwindState is { } unwind)
                    {
                        RegisterUnwindError(state, thread, unwind, pendingError);
                        if (thread.IsClosing)
                        {
                            thread.CloseHadError = true;
                        }
                    }
                    else if (HasProtectedBoundary(thread) || ReferenceEquals(thread, state.MainThread))
                    {
                        BeginUnwind(thread, pendingError);
                    }
                    else if (FailThread(
                        state,
                        scheduler,
                        thread,
                        pendingError,
                        out var pendingErrorResult))
                    {
                        return RecordInstructionCount(pendingErrorResult!, scheduler);
                    }

                    continue;
                }

                if (thread.UnwindState is
                    {
                        ErrorHandlerPending: true,
                        ActiveErrorHandler: null,
                    } pendingHandlerUnwind)
                {
                    StartUnwindErrorHandler(state, thread, pendingHandlerUnwind);
                    continue;
                }

                if (thread.UnwindState is
                    {
                        ActiveCloseCall: null,
                        ActiveErrorHandler: null,
                    })
                {
                    try
                    {
                        if (ContinueUnwind(state, scheduler, thread, out var unprotectedError) &&
                            FailThread(
                                state,
                                scheduler,
                                thread,
                                unprotectedError,
                                out var unwindResult))
                        {
                            return RecordInstructionCount(unwindResult!, scheduler);
                        }
                    }
                    catch (LuaRuntimeException exception)
                    {
                        if (thread.UnwindState is null)
                        {
                            if (FailThread(
                                state,
                                scheduler,
                                thread,
                                MaterializeError(state, exception),
                                out var closeResult))
                            {
                                return RecordInstructionCount(closeResult!, scheduler);
                            }

                            continue;
                        }

                        RegisterUnwindError(
                            state,
                            thread,
                            thread.UnwindState,
                            MaterializeError(state, exception));
                        if (thread.IsClosing)
                        {
                            thread.CloseHadError = true;
                        }
                    }

                    continue;
                }

                if (thread.FrameCount == 0 && scheduler.Transfer == LuaSchedulerTransfer.Yield)
                {
                    scheduler.Transfer = LuaSchedulerTransfer.None;
                    if (CompleteYield(scheduler, thread, out var rootYieldedResult))
                    {
                        return RecordInstructionCount(rootYieldedResult!, scheduler);
                    }

                    continue;
                }

                var frame = thread.CurrentFrame;
                ImmutableArray<LuaValue>? result;
                LuaExecutionContext? pendingInstructionContext = null;
                try
                {
                    if (frame.Continuation.Kind == LuaContinuationKind.ProtectedCall)
                    {
                        var returnRegister = frame.Continuation.Base;
                        frame.Continuation.Reset();
                        result = ExecuteReturn(
                            state,
                            scheduler,
                            thread,
                            frame,
                            new LuaIrInstruction(LuaIrOpcode.Return, returnRegister, -1));
                    }
                    else
                    {
                        var instructionArray = ImmutableCollectionsMarshal.AsArray(
                            frame.Function.Instructions)!;
                        ref readonly var initialInstruction =
                            ref instructionArray[frame.ProgramCounter];
                        if (TryInvokeDebugHook(state, thread, frame, in initialInstruction))
                        {
                            continue;
                        }

                        // Host debugger checkpoint: suspend the whole scheduling turn when an
                        // attached session requests a pause (breakpoint hit, step target, or an
                        // asynchronous pause request). Hook frames are skipped.
                        if (!frame.IsDebugHook && hasDebugSession &&
                            state.DebugSession is { } debugSession &&
                            debugSession.EvaluatePause(thread, initialInstruction.SourceLine))
                        {
                            if (CompleteDebugPause(scheduler, thread, out var debugPauseResult))
                            {
                                return RecordInstructionCount(debugPauseResult!, scheduler);
                            }

                            continue;
                        }

                        var executionContext = activation.ExecutionContext;
                        if (executionContext is null)
                        {
                            executionContext = new LuaExecutionContext(
                                this,
                                state,
                                thread,
                                scheduler.InstructionLimit - scheduler.TotalInstructionCount,
                                scheduler);
                            activation.ExecutionContext = executionContext;
                        }
                        else
                        {
                            executionContext.Reset(
                                this,
                                state,
                                thread,
                                scheduler.InstructionLimit - scheduler.TotalInstructionCount,
                                scheduler);
                        }
                        pendingInstructionContext = executionContext;
                        LuaCompiledExit exit;
                        if (ShouldUseReferenceInterpreter(frame, in initialInstruction))
                        {
                            // Keep the reference path concrete so the runtime can enter the
                            // compact interpreter loop without an interface dispatch.
                            exit = LuaInterpreterInstructionExecutor.RequiresExactDebugHookDispatch(
                                thread,
                                frame,
                                state)
                                ? LuaInterpreterInstructionExecutor.ExecuteSingleInstruction(
                                    this,
                                    executionContext,
                                    state,
                                    thread,
                                    frame,
                                    in initialInstruction)
                                : _referenceInstructionExecutor.Execute(
                                    this,
                                    executionContext,
                                    state,
                                    thread,
                                    frame,
                                    in initialInstruction);
                        }
                        else
                        {
                            exit = _instructionExecutor.Execute(
                                this,
                                executionContext,
                                state,
                                thread,
                                frame,
                                in initialInstruction);
                        }
                        ValidateInstructionAccounting(executionContext, exit);
                        activation.InstructionCount = unchecked(
                            activation.InstructionCount + exit.InstructionsConsumed);
                        var exitFrame = executionContext.ExitFrame ?? frame;
                        pendingInstructionContext = null;
                        LuaCodegenAbiV1.CommitProgramCounter(exitFrame, exit.ProgramCounter);

                        if (exit.Kind == LuaCompiledExitKind.Deopt)
                        {
                            var deoptInstructions = ImmutableCollectionsMarshal.AsArray(
                                exitFrame.Function.Instructions)!;
                            ref readonly var deoptInstruction =
                                ref deoptInstructions[exitFrame.ProgramCounter];
                            executionContext.Reset(
                                this,
                                state,
                                thread,
                                scheduler.InstructionLimit - scheduler.TotalInstructionCount,
                                scheduler);
                            pendingInstructionContext = executionContext;
                            exit = _referenceInstructionExecutor.Execute(
                                this,
                                executionContext,
                                state,
                                thread,
                                exitFrame,
                                in deoptInstruction);
                            ValidateInstructionAccounting(executionContext, exit);
                            activation.InstructionCount = unchecked(
                                activation.InstructionCount + exit.InstructionsConsumed);
                            exitFrame = executionContext.ExitFrame ?? exitFrame;
                            pendingInstructionContext = null;
                            LuaCodegenAbiV1.CommitProgramCounter(exitFrame, exit.ProgramCounter);
                        }

                        LuaIrInstruction controlInstruction = default;
                        if (exit.Kind is LuaCompiledExitKind.Call or
                            LuaCompiledExitKind.TailCall or LuaCompiledExitKind.Return)
                        {
                            var controlInstructions = ImmutableCollectionsMarshal.AsArray(
                                exitFrame.Function.Instructions)!;
                            controlInstruction = controlInstructions[exit.ProgramCounter];
                        }

                        switch (exit.Kind)
                        {
                            case LuaCompiledExitKind.Continue:
                            case LuaCompiledExitKind.Poll when
                                exit.Reason != LuaCompiledExitReason.InstructionBudget:
                                result = null;
                                break;
                            case LuaCompiledExitKind.Poll:
                                if (FailThread(
                                    state,
                                    scheduler,
                                    thread,
                                    MaterializeError(state, new LuaRuntimeException(
                                        "The interpreter instruction budget was exceeded.")),
                                    out var budgetResult))
                                {
                                    return RecordInstructionCount(budgetResult!, scheduler);
                                }

                                continue;
                            case LuaCompiledExitKind.Call:
                                _ = ExecuteCall(
                                    state,
                                    scheduler,
                                    thread,
                                    exitFrame,
                                    controlInstruction,
                                    tailCall: false);
                                result = null;
                                break;
                            case LuaCompiledExitKind.TailCall:
                                result = ExecuteCall(
                                    state,
                                    scheduler,
                                    thread,
                                    exitFrame,
                                    controlInstruction,
                                    tailCall: true);
                                break;
                            case LuaCompiledExitKind.Return:
                                result = ExecuteReturn(
                                    state,
                                    scheduler,
                                    thread,
                                    exitFrame,
                                    controlInstruction);
                                break;
                            case LuaCompiledExitKind.Deopt:
                            default:
                                throw new InvalidOperationException(
                                    $"Invalid backend exit {exit.Kind} after interpreter fallback.");
                        }
                    }
                }
                catch (LuaRuntimeException exception)
                {
                    if (pendingInstructionContext is not null)
                    {
                        activation.InstructionCount = unchecked(
                            activation.InstructionCount +
                            pendingInstructionContext.InstructionsConsumed);
                    }

                    var exceptionFrame = pendingInstructionContext?.ExitFrame ?? frame;
                    var enrichedException = LuaRuntimeErrorForensics.EnrichRuntimeException(
                        thread,
                        exceptionFrame,
                        exception);
                    var error = MaterializeError(state, enrichedException);
                    if (thread.UnwindState is { } unwind)
                    {
                        RegisterUnwindError(state, thread, unwind, error);
                        if (thread.IsClosing)
                        {
                            thread.CloseHadError = true;
                        }

                        continue;
                    }

                    var includeProtectedNativeCallbacks =
                        !exception.BypassProtectedNativeCallback;
                    if (HasProtectedBoundary(thread, includeProtectedNativeCallbacks) ||
                        ReferenceEquals(thread, state.MainThread) ||
                        IsResumedByWrap(thread))
                    {
                        BeginUnwind(
                            thread,
                            error,
                            skipProtectedNativeCallback: !includeProtectedNativeCallbacks);
                        continue;
                    }

                    if (FailThread(state, scheduler, thread, error, out var errorResult))
                    {
                        return RecordInstructionCount(errorResult!, scheduler);
                    }

                    continue;
                }

                if (scheduler.Transfer == LuaSchedulerTransfer.Yield)
                {
                    scheduler.Transfer = LuaSchedulerTransfer.None;
                    if (CompleteYield(scheduler, thread, out var yieldedResult))
                    {
                        return RecordInstructionCount(yieldedResult!, scheduler);
                    }

                    continue;
                }

                if (scheduler.Transfer == LuaSchedulerTransfer.Resume)
                {
                    var target = scheduler.ResumeTarget!;
                    scheduler.ClearTransfer();
                    ActivateNestedThread(
                        state,
                        scheduler,
                        thread,
                        target,
                        target.ResumeSpan);
                    continue;
                }

                if (result is not null)
                {
                    if (CompleteThread(scheduler, thread, result.Value, out var completedResult))
                    {
                        return RecordInstructionCount(completedResult!, scheduler);
                    }

                    state.Heap.SafePoint();
                    RunPendingFinalizer(state, thread);
                    continue;
                }

                state.Heap.SafePoint();
                RunPendingFinalizer(state, thread);
            }

            throw new InvalidOperationException("The Lua scheduler stopped without a result.");
        }
        finally
        {
            state.IsRunningFinalizer = previousIsRunningFinalizer;
            state.RunningThread = previousRunningThread;
            state.RunningThreadIsYieldable = previousRunningThreadIsYieldable;
            state.Heap.RemovePermanentRoot(root);
            _schedulerNestingDepth--;
        }
    }
}
