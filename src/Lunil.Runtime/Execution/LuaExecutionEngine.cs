using System.Collections.Immutable;
using System.Text;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Operations;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

/// <summary>Shared resumable scheduler and execution kernel used by all execution backends.</summary>
internal sealed class LuaExecutionEngine
{
    private const int MaximumCStackDepth = 200;

    private readonly LuaInterpreterOptions _options;
    private readonly ILuaInstructionExecutor _instructionExecutor;
    private readonly ILuaFrameInstructionRouter? _frameInstructionRouter;
    private readonly ILuaInstructionObserver? _instructionObserver;
    private readonly LuaInterpreterInstructionExecutor _referenceInstructionExecutor = new();
    private int _schedulerNestingDepth;

    internal LuaExecutionEngine(
        LuaInterpreterOptions? options = null,
        ILuaInstructionExecutor? instructionExecutor = null)
    {
        _options = options ?? LuaInterpreterOptions.Default;
        _instructionExecutor = instructionExecutor ?? _referenceInstructionExecutor;
        _frameInstructionRouter = _instructionExecutor as ILuaFrameInstructionRouter;
        _instructionObserver = _instructionExecutor as ILuaInstructionObserver;
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
        thread.Initialize(LuaValue.FromFunction(closure));
        var result = RunScheduler(state, thread, arguments, yieldableRoot: false);
        if (result.Signal == LuaVmSignal.Error)
        {
            throw new LuaRuntimeException(result.Values[0]);
        }

        return result;
    }

    public LuaExecutionResult ExecuteBinaryChunk(
        LuaState state,
        ReadOnlySpan<byte> binaryChunk,
        ReadOnlySpan<LuaValue> arguments = default,
        Lua54ChunkReaderOptions? readerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Execute(state, state.LoadBinaryChunk(binaryChunk, readerOptions), arguments);
    }

    public LuaExecutionResult Start(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default)
    {
        ValidateThreadEntry(state, thread);
        if (thread.Status != LuaThreadStatus.New || thread.Started)
        {
            throw new LuaRuntimeException("Cannot start a coroutine that has already started.");
        }

        return RunScheduler(state, thread, arguments, yieldableRoot: true);
    }

    public LuaExecutionResult Resume(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default)
    {
        ValidateThreadEntry(state, thread);
        if (thread.Status == LuaThreadStatus.New && !thread.Started)
        {
            return RunScheduler(state, thread, arguments, yieldableRoot: true);
        }

        if (thread.Status != LuaThreadStatus.Suspended)
        {
            throw new LuaRuntimeException($"Cannot resume a {FormatStatus(thread)} coroutine.");
        }

        return RunScheduler(state, thread, arguments, yieldableRoot: true);
    }

    public LuaExecutionResult Close(LuaState state, LuaThread thread)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(thread);
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

    private LuaExecutionResult RunScheduler(
        LuaState state,
        LuaThread root,
        ReadOnlySpan<LuaValue> arguments,
        bool yieldableRoot,
        bool activateThread = true)
    {
        if (_schedulerNestingDepth >= MaximumCStackDepth)
        {
            throw new LuaRuntimeException("C stack overflow");
        }

        _schedulerNestingDepth++;
        state.Heap.AddPermanentRoot(root);
        try
        {
            var scheduler = new LuaScheduler(root, yieldableRoot);
            if (activateThread)
            {
                ActivateThread(state, scheduler, root, arguments);
            }
            else
            {
                root.Status = LuaThreadStatus.Running;
            }
            while (scheduler.Count != 0)
            {
                var activation = scheduler.Current;
                var thread = activation.Thread;
                if (activation.ForcedResult is { } forcedResult)
                {
                    activation.ForcedResult = null;
                    if (CompleteThread(scheduler, thread, forcedResult, out var forcedExecutionResult))
                    {
                        return forcedExecutionResult!;
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
                        return pendingErrorResult!;
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
                            return unwindResult!;
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
                                return closeResult!;
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
                        return rootYieldedResult!;
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
                        var instruction = frame.Closure.Function.Instructions[frame.ProgramCounter];
                        if (TryInvokeDebugHook(state, thread, frame, instruction))
                        {
                            continue;
                        }

                        var executionContext = activation.ExecutionContext;
                        if (executionContext is null)
                        {
                            executionContext = new LuaExecutionContext(
                                this,
                                state,
                                thread,
                                _options.MaximumInstructionCount - activation.InstructionCount);
                            activation.ExecutionContext = executionContext;
                        }
                        else
                        {
                            executionContext.Reset(
                                this,
                                state,
                                thread,
                                _options.MaximumInstructionCount - activation.InstructionCount);
                        }
                        pendingInstructionContext = executionContext;
                        var executor = ShouldUseReferenceInterpreter(frame, instruction)
                            ? _referenceInstructionExecutor
                            : _instructionExecutor;
                        var exit = executor.Execute(
                            this,
                            executionContext,
                            state,
                            thread,
                            frame,
                            instruction);
                        ValidateInstructionAccounting(executionContext, exit);
                        activation.InstructionCount = checked(
                            activation.InstructionCount + exit.InstructionsConsumed);
                        pendingInstructionContext = null;
                        LuaCodegenAbiV1.CommitProgramCounter(frame, exit.ProgramCounter);

                        if (exit.Kind == LuaCompiledExitKind.Deopt)
                        {
                            instruction = frame.Closure.Function.Instructions[frame.ProgramCounter];
                            executionContext.Reset(
                                this,
                                state,
                                thread,
                                _options.MaximumInstructionCount - activation.InstructionCount);
                            pendingInstructionContext = executionContext;
                            exit = _referenceInstructionExecutor.Execute(
                                this,
                                executionContext,
                                state,
                                thread,
                                frame,
                                instruction);
                            ValidateInstructionAccounting(executionContext, exit);
                            activation.InstructionCount = checked(
                                activation.InstructionCount + exit.InstructionsConsumed);
                            pendingInstructionContext = null;
                            LuaCodegenAbiV1.CommitProgramCounter(frame, exit.ProgramCounter);
                        }

                        if (exit.Kind is LuaCompiledExitKind.Call or
                            LuaCompiledExitKind.TailCall or LuaCompiledExitKind.Return)
                        {
                            instruction = frame.Closure.Function.Instructions[exit.ProgramCounter];
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
                                    return budgetResult!;
                                }

                                continue;
                            case LuaCompiledExitKind.Call:
                                _ = ExecuteCall(
                                    state,
                                    scheduler,
                                    thread,
                                    frame,
                                    instruction,
                                    tailCall: false);
                                result = null;
                                break;
                            case LuaCompiledExitKind.TailCall:
                                result = ExecuteCall(
                                    state,
                                    scheduler,
                                    thread,
                                    frame,
                                    instruction,
                                    tailCall: true);
                                break;
                            case LuaCompiledExitKind.Return:
                                result = ExecuteReturn(state, scheduler, thread, frame, instruction);
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
                        activation.InstructionCount = checked(
                            activation.InstructionCount +
                            pendingInstructionContext.InstructionsConsumed);
                    }

                    var enrichedException = EnrichRuntimeException(thread, frame, exception);
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
                        return errorResult!;
                    }

                    continue;
                }

                if (scheduler.Transfer == LuaSchedulerTransfer.Yield)
                {
                    scheduler.Transfer = LuaSchedulerTransfer.None;
                    if (CompleteYield(scheduler, thread, out var yieldedResult))
                    {
                        return yieldedResult!;
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
                        return completedResult!;
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
            state.IsRunningFinalizer = false;
            state.RunningThread = null;
            state.RunningThreadIsYieldable = false;
            state.Heap.RemovePermanentRoot(root);
            _schedulerNestingDepth--;
        }
    }

    internal LuaCompiledExit ExecuteCodegenSlowPath(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int programCounter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(frame);
        if (!ReferenceEquals(context.ExecutionEngine, this) ||
            !ReferenceEquals(context.Thread, thread))
        {
            throw new InvalidOperationException(
                "The execution context does not belong to this execution engine and thread.");
        }

        var instructions = frame.Closure.Function.Instructions;
        ArgumentOutOfRangeException.ThrowIfNegative(programCounter);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            programCounter,
            instructions.Length);
        if (frame.ProgramCounter != programCounter)
        {
            throw new InvalidOperationException(
                "The compiled slow path must start at the committed canonical program counter.");
        }

        return _referenceInstructionExecutor.Execute(
            this,
            context,
            context.State,
            thread,
            frame,
            instructions[programCounter]);
    }

    internal void ObserveCodegenInstruction(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int programCounter)
    {
        if (_instructionObserver is null ||
            !context.TryBeginInstructionObservation(programCounter))
        {
            return;
        }

        var instructions = frame.Closure.Function.Instructions;
        ArgumentOutOfRangeException.ThrowIfNegative(programCounter);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            programCounter,
            instructions.Length);
        _instructionObserver.ObserveInstruction(
            context,
            thread,
            frame,
            programCounter,
            instructions[programCounter]);
    }

    internal void ObserveLoopOsrBackedge(LuaFrame frame, int programCounter)
    {
        if (_instructionObserver is ILuaLoopOsrObserver observer)
        {
            observer.ObserveLoopOsrBackedge(frame, programCounter);
        }
    }

    private static void ValidateInstructionAccounting(
        LuaExecutionContext context,
        LuaCompiledExit exit)
    {
        if (exit.InstructionsConsumed != context.InstructionsConsumed)
        {
            throw new InvalidOperationException(
                "An execution backend returned an instruction count that does not match " +
                "the range reserved through Runtime ABI v1.");
        }
    }

    private static LuaValue[] ProtectedNativeCallbackFailure(
        LuaState state,
        LuaRuntimeException exception) =>
        [LuaValue.FromBoolean(false), MaterializeError(state, exception)];

    private bool TryInvokeDebugHook(
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        if (thread.DebugHook.IsNil || thread.DebugHookMask == LuaDebugHookMask.None ||
            thread.IsRunningDebugHook || frame.IsDebugHook || frame.IsHidden)
        {
            return false;
        }

        var resumingHookedInstruction = false;
        if (frame.DebugHookCheckedProgramCounter == frame.ProgramCounter &&
            frame.PendingDebugHookEvent is null)
        {
            frame.DebugHookCheckedProgramCounter = -1;
            var dispatchedEvent = frame.DispatchedDebugHookEvent;
            frame.DispatchedDebugHookEvent = null;
            var hasNativeSubject = !thread.DebugHookSubjectFunction.IsNil;
            var completedLuaCallHook =
                dispatchedEvent is "call" or "tail call" &&
                !hasNativeSubject;
            var completedNativeReturnAtNewLine =
                dispatchedEvent == "return" && hasNativeSubject &&
                frame.NativeCallSourceLine > 0 && instruction.SourceLine > 0 &&
                frame.NativeCallSourceLine != instruction.SourceLine;
            if (!completedLuaCallHook && !completedNativeReturnAtNewLine &&
                (instruction.SourceLine > 0 || !frame.HasSourceLineInformation))
            {
                frame.LastDebugHookProgramCounter = frame.ProgramCounter;
            }

            if (dispatchedEvent == "return" && hasNativeSubject)
            {
                frame.NativeCallSourceLine = -1;
            }

            thread.DebugHookSubjectFunction = LuaValue.Nil;
            thread.ClearDebugHookTransfer();
            resumingHookedInstruction =
                !completedLuaCallHook && !completedNativeReturnAtNewLine;
        }

        string? hookEvent = null;
        var line = instruction.SourceLine > 0 ? instruction.SourceLine : -1;
        var countDue = thread.DebugHookMask.HasFlag(LuaDebugHookMask.Count) &&
            thread.DebugHookCount > 0 && !resumingHookedInstruction &&
            IsCountableDebugInstruction(instruction) &&
            --thread.DebugHookCounter <= 0;
        if (countDue)
        {
            thread.DebugHookCounter = thread.DebugHookCount;
        }

        if (frame.PendingDebugHookEvent is { } pending)
        {
            hookEvent = pending;
            frame.PendingDebugHookEvent = null;
        }
        else if (instruction.Opcode == LuaIrOpcode.Return &&
            thread.DebugHookMask.HasFlag(LuaDebugHookMask.Return) &&
            frame.ToBeClosedSlots.Count == 0 &&
            frame.ReturnHookProgramCounter != frame.ProgramCounter)
        {
            hookEvent = "return";
            frame.ReturnHookProgramCounter = frame.ProgramCounter;
        }
        else if (!resumingHookedInstruction &&
            thread.DebugHookMask.HasFlag(LuaDebugHookMask.Line) &&
            (!frame.HasSourceLineInformation && frame.LastLineHookProgramCounter < 0 ||
                line > 0 &&
                (line != frame.LastDebugHookLine ||
                    frame.LastDebugHookProgramCounter >= 0 &&
                    frame.ProgramCounter <= frame.LastDebugHookProgramCounter &&
                    !WasBackEdgeLineAlreadyReported(frame))))
        {
            hookEvent = "line";
            frame.LastDebugHookLine = line;
            frame.LastLineHookProgramCounter = frame.ProgramCounter;
            if (countDue)
            {
                frame.PendingDebugHookEvent = "count";
            }
        }
        else if (countDue)
        {
            hookEvent = "count";
            line = -1;
        }

        if (hookEvent is null)
        {
            if (line > 0 || !frame.HasSourceLineInformation)
            {
                frame.LastDebugHookProgramCounter = frame.ProgramCounter;
            }

            return false;
        }

        if (hookEvent != "line")
        {
            line = -1;
        }

        if (hookEvent == "return" && instruction.Opcode == LuaIrOpcode.Return &&
            thread.DebugHookSubjectFunction.IsNil)
        {
            if (frame.Continuation.Kind == LuaContinuationKind.ReturnAndClose)
            {
                thread.SetDebugHookTransfer(
                    frame.Continuation.Values,
                    isNative: false);
            }
            else
            {
                var start = frame.Base + instruction.A;
                var count = instruction.B < 0 ? Math.Max(0, frame.Top - start) : instruction.B;
                thread.SetDebugHookTransfer(
                    thread.Stack.AsReadOnlySpan(start, count),
                    isNative: false);
            }
        }
        else if (hookEvent is not ("call" or "tail call" or "return"))
        {
            thread.ClearDebugHookTransfer();
        }

        frame.DebugHookCheckedProgramCounter = frame.ProgramCounter;
        frame.DispatchedDebugHookEvent = hookEvent;
        var arguments = new[]
        {
            LuaValue.FromString(state.Strings.GetOrCreate(
                System.Text.Encoding.UTF8.GetBytes(hookEvent))),
            line < 0 ? LuaValue.Nil : LuaValue.FromInteger(line),
        };
        var resolved = LuaRuntimeOperations.ResolveCall(state, thread.DebugHook, arguments);
        thread.IsRunningDebugHook = true;
        if (resolved.Callable.TryGetClosure() is { } closure)
        {
            var hookFrame = PushFrame(
                thread,
                closure,
                resolved.Arguments,
                Math.Max(frame.Top, frame.Base + frame.Closure.Function.RegisterCount),
                expectedResults: 0,
                isDebugHook: true);
            hookFrame.Continuation.IsYieldBarrier = true;
            return true;
        }

        try
        {
            var native = resolved.Callable.TryGetNativeFunction() ??
                throw new LuaRuntimeException("invalid hook function");
            if (native.StepBody is not null)
            {
                throw new LuaRuntimeException("resumable native functions cannot be debug hooks");
            }

            _ = InvokeNativeBody(state, resolved.Callable, resolved.Arguments);
            return false;
        }
        finally
        {
            thread.IsRunningDebugHook = false;
        }
    }

    private static bool WasBackEdgeLineAlreadyReported(LuaFrame frame)
    {
        var instructions = frame.Closure.Function.Instructions;
        var previousPc = frame.LastDebugHookProgramCounter;
        var hookPc = frame.LastLineHookProgramCounter;
        if (previousPc < 0 || previousPc >= instructions.Length ||
            hookPc < 0 || hookPc >= instructions.Length)
        {
            return false;
        }

        var previous = instructions[previousPc];
        var reported = instructions[hookPc];
        return previous.SourceLine == reported.SourceLine &&
            previous.Span.Equals(reported.Span);
    }

    private static bool IsCountableDebugInstruction(LuaIrInstruction instruction) =>
        instruction.Opcode is not (
            LuaIrOpcode.Move or
            LuaIrOpcode.SetTop or
            LuaIrOpcode.GetUpvalue or
            LuaIrOpcode.Close);

    private void ActivateThread(
        LuaState state,
        LuaScheduler scheduler,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments)
    {
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
                    native.StepBody(context, 0, arguments));
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
                scheduler.Current.ForcedResult = arguments.ToArray().ToImmutableArray();
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
                step);
            if (forcedResult is not null)
            {
                scheduler.Current.ForcedResult = forcedResult;
            }
            thread.SetResumeValues([]);
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

    private static bool CompleteYield(
        LuaScheduler scheduler,
        LuaThread thread,
        out LuaExecutionResult? result)
    {
        thread.Status = LuaThreadStatus.Suspended;
        if (scheduler.Count == 1)
        {
            result = new LuaExecutionResult(
                LuaVmSignal.Yielded,
                thread.YieldedSpan.ToArray().ToImmutableArray());
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

    private static bool CompleteThread(
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

    private static bool FailThread(
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

    private static void InjectCoroutineResult(
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
            if (continuation.ProtectionKind == LuaProtectedCallKind.Finalizer ||
                continuation.IsYieldBarrier)
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateThreadEntry(LuaState state, LuaThread thread)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(thread);
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
                throw new LuaRuntimeException("attempt to yield from outside a coroutine");
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
                throw new LuaRuntimeException("bad argument #1 to 'resume' (thread expected)");
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
                for (var index = 0; index < failure.Length; index++)
                {
                    thread.Stack[returnBase + index] = failure[index];
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
        LuaNativeStep step)
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
                    var completedValues = nativeProtected
                        ? new[] { LuaValue.FromBoolean(true) }.Concat(step.Values).ToArray()
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
                        for (var index = 0; index < completedValues.Length; index++)
                        {
                            thread.Stack[returnBase + index] = completedValues[index];
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

                    WriteCallResults(thread, frame, returnBase, expectedResults, completedValues);
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
                    if (resolved.Callable.TryGetClosure() is { } closure)
                    {
                        frame.Continuation.Kind = LuaContinuationKind.NativeCallLua;
                        frame.Continuation.State = step.ContinuationId;
                        frame.Continuation.Base = returnBase;
                        frame.Continuation.ExpectedResults = expectedResults;
                        frame.Continuation.Count = tailCall ? 1 : 0;
                        frame.Continuation.Value = nativeFunction;
                        frame.Continuation.IsYieldBarrier = !step.CallIsYieldable;
                        frame.Continuation.NativeCallbackIsProtected = step.CallIsProtected;
                        SaveNativeInvocationState(
                            thread,
                            frame.Continuation,
                            step.StateValues,
                            step.ByteBuffer);
                        thread.Owner.WriteBarrier(thread, nativeFunction);
                        if (!tailCall && !programCounterAdvanced)
                        {
                            frame.ProgramCounter++;
                        }

                        PushFrame(
                            thread,
                            closure,
                            resolved.Arguments,
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
                        frame.Continuation.Value = nativeFunction;
                        frame.Continuation.IsYieldBarrier = !step.CallIsYieldable;
                        frame.Continuation.NativeCallbackIsProtected = step.CallIsProtected;
                        SaveNativeInvocationState(
                            thread,
                            frame.Continuation,
                            step.StateValues,
                            step.ByteBuffer);
                        thread.Owner.WriteBarrier(thread, nativeFunction);
                        if (!tailCall && !programCounterAdvanced)
                        {
                            frame.ProgramCounter++;
                        }

                        PushFrame(
                            thread,
                            CreateNativeCallbackTrampoline(state, resolved.Callable),
                            resolved.Arguments,
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
                            resolved.Arguments);
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
                    frame.Continuation.Value = nativeFunction;
                    SaveNativeInvocationState(
                        thread,
                        frame.Continuation,
                        step.StateValues,
                        step.ByteBuffer);
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
                            step.ByteBuffer);
                        thread.Owner.WriteBarrier(thread, nativeFunction);
                        PushFrame(thread, closure, resolved.Arguments, 0, expectedResults: -1);
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
                            step.ByteBuffer);
                        thread.Owner.WriteBarrier(thread, nativeFunction);
                        PushFrame(
                            thread,
                            CreateNativeCallbackTrampoline(state, resolved.Callable),
                            resolved.Arguments,
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
                            resolved.Arguments);
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
                        step.ByteBuffer);
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
        ReadOnlySpan<LuaValue> stateValues,
        LuaNativeByteBuffer? byteBuffer)
    {
        byteBuffer?.ValidateOwner(thread.Owner);
        continuation.Values = stateValues.ToArray();
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
        var directFunction = thread.Stack[functionIndex];
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
            var arguments = new LuaValue[argumentCount];
            for (var index = 0; index < argumentCount; index++)
            {
                arguments[index] = thread.Stack[argumentStart + index];
            }

            var resolvedCall = LuaRuntimeOperations.ResolveCall(
                state,
                thread.Stack[functionIndex],
                arguments);
            function = resolvedCall.Callable;
            resolvedArguments = resolvedCall.Arguments;
            if (tailCall)
            {
                frame.Continuation.Kind = LuaContinuationKind.TailCall;
                frame.Continuation.Value = function;
                frame.Continuation.Values = resolvedCall.Arguments;
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
                throw new LuaRuntimeException("bad argument #1 to 'close' (thread expected)");
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
                for (var index = 0; index < closeValues.Length; index++)
                {
                    thread.Stack[functionIndex + index] = closeValues[index];
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
            if (tailCall)
            {
                var protectionKind = frame.Continuation.ProtectionKind;
                var protectionFunction = frame.Continuation.ProtectionFunction;
                var errorHandler = frame.Continuation.ErrorHandler;
                var isCloseHandler = frame.Continuation.IsCloseHandler;
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
                    thread.DebugHookMask.HasFlag(LuaDebugHookMask.Call))
                {
                    replacement.PendingDebugHookEvent = "tail call";
                    thread.SetDebugHookTransfer(
                        thread.Stack.AsReadOnlySpan(
                            replacement.Base,
                            closure.Function.ParameterCount),
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
            var step = native.StepBody(context, 0, resolvedArguments);
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
            for (var index = 0; index < results.Length; index++)
            {
                thread.Stack[functionIndex + index] = results[index];
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
            !thread.DebugHookMask.HasFlag(LuaDebugHookMask.Call))
        {
            return false;
        }

        frame.NativeCallHookProgramCounter = frame.ProgramCounter;
        frame.NativeCallSourceLine = frame.Closure.Function
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
            !thread.DebugHookMask.HasFlag(LuaDebugHookMask.Return))
        {
            return;
        }

        if (frame.NativeCallSourceLine <= 0)
        {
            var instructions = frame.Closure.Function.Instructions;
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

    internal void ExecuteOperation(
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

        var operationTop = frame.Top;
        var argumentStart = Math.Max(
            checked(frame.Base + frame.Closure.Function.RegisterCount),
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
            var operation = frame.Closure.Function.Instructions[frame.ProgramCounter];
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

        _ = callable.TryGetNativeFunction() ??
            throw new InvalidOperationException("Resolved metamethod is not callable.");
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

        var targetArguments = arguments[required..].ToArray();
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
                    resolved.Arguments,
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
            if (resolved.Arguments.Length == 0 ||
                resolved.Arguments[0].Kind != LuaValueKind.Thread)
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

            var closeTarget = resolved.Arguments[0].AsThread();
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
                resolved.Arguments,
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
                resolved.Arguments,
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
            var step = native.StepBody(context, 0, resolved.Arguments);
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
            var results = InvokeNativeBody(state, resolved.Callable, resolved.Arguments);
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
                resolved.Arguments,
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
            var results = InvokeNativeBody(state, resolved.Callable, resolved.Arguments);
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
                var continuationId = continuation.State;
                var invocationState = continuation.Values;
                var byteBuffer = continuation.NativeByteBuffer;
                continuation.Reset();
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
                        [LuaValue.FromBoolean(false), unwind.Error]));
                if (completed is { } values)
                {
                    scheduler.Current.ForcedResult = values;
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

    private void RunPendingFinalizer(LuaState state, LuaThread thread)
    {
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
            if (resolved.Callable.TryGetClosure() is { } closure)
            {
                var caller = thread.CurrentFrame;
                state.IsRunningFinalizer = true;
                var finalizerFrame = PushFrame(
                    thread,
                    closure,
                    resolved.Arguments,
                    Math.Max(caller.Top, caller.Base + caller.Closure.Function.RegisterCount),
                    expectedResults: 0,
                    protectionKind: LuaProtectedCallKind.Finalizer);
                SetDebugFunctionName(finalizerFrame, "__gc", "metamethod");
                return;
            }

            try
            {
                state.IsRunningFinalizer = true;
                _ = InvokeNativeBody(state, resolved.Callable, resolved.Arguments);
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
                InvokeNativeBody(state, resolved.Callable, resolved.Arguments));
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
        if (frame.Continuation.Kind != LuaContinuationKind.ReturnAndClose)
        {
            var start = frame.Base + instruction.A;
            var count = instruction.B < 0 ? Math.Max(0, frame.Top - start) : instruction.B;
            frame.Continuation.Reset();
            frame.Continuation.Kind = LuaContinuationKind.ReturnAndClose;
            frame.Continuation.Values = new LuaValue[count];
            for (var index = 0; index < count; index++)
            {
                frame.Continuation.Values[index] = thread.Stack[start + index];
            }
        }

        if (TryCloseFrom(state, thread, frame, 0, LuaValue.Nil))
        {
            return null;
        }

        var results = frame.Continuation.Values;
        var protectionKind = frame.Continuation.ProtectionKind;
        frame.Continuation.Reset();
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
            var continuationId = continuation.State;
            var invocationState = continuation.Values;
            var byteBuffer = continuation.NativeByteBuffer;
            var callbackWasProtected = continuation.NativeCallbackIsProtected;
            continuation.Reset();
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
                step);
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
            isCloseHandler,
            isDebugHook,
            isHidden);
        frame.InstructionRoute = GetInitialFrameInstructionRoute(closure);
        if (scheduleCallHook && !isDebugHook && !isHidden && !thread.IsRunningDebugHook &&
            !thread.DebugHook.IsNil &&
            thread.DebugHookMask.HasFlag(LuaDebugHookMask.Call))
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

        var function = closure.Function;
        var @base = Math.Max(returnBase + 1, minimumBase);
        var required = checked(@base + function.RegisterCount);
        if (required > _options.MaximumStackSlots)
        {
            throw new LuaRuntimeException("The Lua stack slot limit was exceeded.");
        }

        var fixedArguments = Math.Min(argumentCount, function.ParameterCount);
        var varArgs = function.IsVarArg && argumentCount > function.ParameterCount
            ? thread.Stack.AsReadOnlySpan(
                argumentStart + function.ParameterCount,
                argumentCount - function.ParameterCount).ToArray()
            : [];
        thread.Stack.EnsureCapacity(required);
        if (argumentStart != @base)
        {
            if (@base < argumentStart)
            {
                for (var index = 0; index < fixedArguments; index++)
                {
                    thread.Stack[@base + index] = thread.Stack[argumentStart + index];
                }
            }
            else
            {
                for (var index = fixedArguments - 1; index >= 0; index--)
                {
                    thread.Stack[@base + index] = thread.Stack[argumentStart + index];
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
                thread.Stack[source] = LuaValue.Nil;
            }
        }

        var frame = new LuaFrame(
            closure,
            @base,
            @base + function.ParameterCount,
            returnBase,
            expectedResults,
            varArgs);
        frame.InstructionRoute = GetInitialFrameInstructionRoute(closure);
        if (!thread.IsRunningDebugHook && !thread.DebugHook.IsNil &&
            thread.DebugHookMask.HasFlag(LuaDebugHookMask.Call))
        {
            frame.PendingDebugHookEvent = "call";
            thread.SetDebugHookTransfer(
                thread.Stack.AsReadOnlySpan(@base, function.ParameterCount),
                isNative: false);
        }

        thread.PushFrame(frame);
        return frame;
    }

    private LuaFrameInstructionRoute GetInitialFrameInstructionRoute(LuaClosure closure) =>
        _frameInstructionRouter?.GetInitialFrameInstructionRoute(closure) ??
        LuaFrameInstructionRoute.Backend;

    private static bool ShouldUseReferenceInterpreter(
        LuaFrame frame,
        LuaIrInstruction instruction) => frame.InstructionRoute switch
        {
            LuaFrameInstructionRoute.Interpreter => true,
            LuaFrameInstructionRoute.InterpreterWithBackedgeProbes =>
                !LuaInstructionRouting.IsBackedge(frame.ProgramCounter, instruction),
            _ => false,
        };

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
        var required = checked(start + count);
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
            thread.Stack[start + index] = resolution.GetArgument(index);
        }
    }

    private static void SetDebugFunctionName(LuaFrame frame, string name, string nameWhat)
    {
        frame.DebugFunctionName = name;
        frame.DebugFunctionNameWhat = nameWhat;
    }

    private static string GetOperationMetamethodName(LuaIrInstruction instruction) =>
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

        SetFrameTop(thread, caller, returnBase + count);
    }

    private static void WriteOperationResults(
        LuaThread thread,
        LuaFrame caller,
        int returnBase,
        int expectedResults,
        ReadOnlySpan<LuaValue> results,
        int preservedTop)
    {
        var count = expectedResults < 0 ? results.Length : expectedResults;
        for (var index = 0; index < count; index++)
        {
            thread.Stack[returnBase + index] = index < results.Length ? results[index] : LuaValue.Nil;
        }

        caller.Top = Math.Max(preservedTop, returnBase + count);
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
            thread.Stack[returnBase] = LuaValue.FromBoolean(!thread.Stack[returnBase].IsTruthy);
        }

        if (frame.Continuation.Kind == LuaContinuationKind.LuaCall)
        {
            frame.Continuation.Reset();
        }
    }

    internal static LuaClosure CreateClosure(LuaThread thread, LuaFrame parent, int functionId)
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

        return new LuaClosure(
            parent.Closure.Owner,
            parent.Closure.Module,
            function,
            upvalues,
            parent.Closure.StringConstants);
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
                thread.Stack[source + index]);
        }
    }

    internal static void ExecuteVarArg(
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
            SetFrameTop(thread, frame, frame.Base + instruction.A + count);
        }
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
            if (resolved.Callable.TryGetClosure() is { } closure)
            {
                var closeFrame = PushFrame(
                    thread,
                    closure,
                    resolved.Arguments,
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
                resolved.Arguments,
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

    private static LuaRuntimeException EnrichRuntimeException(
        LuaThread thread,
        LuaFrame frame,
        LuaRuntimeException exception)
    {
        if (exception.HasErrorValue)
        {
            return exception;
        }

        var isAttemptError = exception.Message.StartsWith("attempt to ", StringComparison.Ordinal);
        var isIntegerConversionError = exception.Message.Contains(
            "has no integer representation",
            StringComparison.Ordinal);
        var isNumericForError = exception.Message.StartsWith("bad 'for' ", StringComparison.Ordinal);
        var isBadArgumentError = exception.Message.StartsWith("bad argument #", StringComparison.Ordinal);
        var isAssertionError = string.Equals(exception.Message, "assertion failed!", StringComparison.Ordinal);
        if (!isAttemptError && !isIntegerConversionError && !isNumericForError &&
            !isBadArgumentError && !isAssertionError)
        {
            return exception;
        }

        var instructions = frame.Closure.Function.Instructions;
        if (instructions.IsEmpty)
        {
            return exception;
        }

        var pc = Math.Clamp(frame.ProgramCounter, 0, instructions.Length - 1);
        var instruction = instructions[pc];
        var message = exception.Message;
        if (isBadArgumentError &&
            instruction.Opcode is LuaIrOpcode.Call or LuaIrOpcode.TailCall &&
            Read(thread, frame, instruction.A).TryGetNativeFunction() is { } native &&
            native.Name.Contains('.', StringComparison.Ordinal))
        {
            var marker = " to '";
            var nameStart = message.IndexOf(marker, StringComparison.Ordinal);
            if (nameStart >= 0)
            {
                nameStart += marker.Length;
                var nameEnd = message.IndexOf('\'', nameStart);
                if (nameEnd >= 0)
                {
                    message = message[..nameStart] + native.Name + message[nameEnd..];
                }
            }

            return new LuaRuntimeException(FormatRuntimeErrorLocation(frame, instruction, message));
        }

        if (isAttemptError && !message.Contains(" (", StringComparison.Ordinal) &&
            message.StartsWith("attempt to call ", StringComparison.Ordinal) &&
            instruction.Opcode is LuaIrOpcode.GetTable or LuaIrOpcode.SetTable or
                LuaIrOpcode.Unary or LuaIrOpcode.Binary)
        {
            message = $"{message} (metamethod '{GetOperationMetamethodName(instruction)}')";
            return new LuaRuntimeException(FormatRuntimeErrorLocation(frame, instruction, message));
        }

        var register = isAssertionError
            ? -1
            : isIntegerConversionError && !isBadArgumentError
                ? GetIntegerConversionOperandRegister(thread, frame, instruction)
                : GetErrorOperandRegister(thread, frame, instruction);
        if (register >= 0 &&
            TryDescribeRegisterOrigin(frame, pc, register, depth: 0, out var origin))
        {
            if (instruction.Opcode is LuaIrOpcode.Call or LuaIrOpcode.TailCall &&
                origin.Kind == LuaValueOriginKind.Field &&
                origin.SourceRegister == instruction.A + 1)
            {
                origin = origin with { Kind = LuaValueOriginKind.Method };
            }

            const string badSelfPrefix = "bad argument #1 to '";
            if (isBadArgumentError &&
                origin.Kind == LuaValueOriginKind.Method &&
                message.StartsWith(badSelfPrefix, StringComparison.Ordinal) &&
                message.IndexOf('\'', badSelfPrefix.Length) is var functionNameEnd &&
                functionNameEnd >= 0)
            {
                var functionName = message[badSelfPrefix.Length..functionNameEnd];
                message = $"calling '{functionName}' on bad self" + message[(functionNameEnd + 1)..];
            }
            else if (isBadArgumentError && origin.Kind == LuaValueOriginKind.Method)
            {
                const string argumentPrefix = "bad argument #";
                var numberEnd = message.IndexOf(' ', argumentPrefix.Length);
                if (numberEnd > argumentPrefix.Length &&
                    int.TryParse(message.AsSpan(argumentPrefix.Length, numberEnd - argumentPrefix.Length),
                        out var argumentNumber) &&
                    argumentNumber > 1)
                {
                    message = argumentPrefix + (argumentNumber - 1) + message[numberEnd..];
                }
            }
            else if (!message.Contains(" (", StringComparison.Ordinal) &&
                FormatOrigin(origin) is { } formattedOrigin)
            {
                message = isIntegerConversionError &&
                    message.StartsWith("number has ", StringComparison.Ordinal)
                        ? "number (" + formattedOrigin + ") " + message["number ".Length..]
                        : $"{message} ({formattedOrigin})";
            }
        }

        return new LuaRuntimeException(FormatRuntimeErrorLocation(frame, instruction, message));
    }

    private static int GetIntegerConversionOperandRegister(
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        if (instruction.Opcode == LuaIrOpcode.Unary)
        {
            return instruction.B;
        }

        if (instruction.Opcode != LuaIrOpcode.Binary)
        {
            return -1;
        }

        return Read(thread, frame, instruction.B).TryGetInteger(out _)
            ? instruction.C
            : instruction.B;
    }

    private static string? FormatOrigin(LuaValueOrigin origin) => origin.Kind switch
    {
        LuaValueOriginKind.Local => $"local '{origin.Name}'",
        LuaValueOriginKind.Upvalue => $"upvalue '{origin.Name}'",
        LuaValueOriginKind.Global => $"global '{origin.Name}'",
        LuaValueOriginKind.Field => $"field '{origin.Name}'",
        LuaValueOriginKind.Method => $"method '{origin.Name}'",
        _ => null,
    };

    private static string FormatRuntimeErrorLocation(
        LuaFrame frame,
        LuaIrInstruction instruction,
        string message)
    {
        if (instruction.SourceLine <= 0 || frame.Closure.Function.SourceName.IsEmpty)
        {
            return $"?:-1: {message}";
        }

        const int maximumLength = 59;
        var source = Encoding.UTF8.GetString(frame.Closure.Function.SourceName.AsSpan());
        string shortSource;
        if (source.StartsWith('@'))
        {
            source = source[1..];
            shortSource = source.Length <= maximumLength
                ? source
                : "..." + source[^(maximumLength - 3)..];
        }
        else if (source.StartsWith('='))
        {
            source = source[1..];
            shortSource = source.Length <= maximumLength ? source : source[..maximumLength];
        }
        else
        {
            const string prefix = "[string \"";
            const string suffix = "\"]";
            var available = maximumLength - prefix.Length - suffix.Length;
            var newLine = source.IndexOf('\n');
            if (newLine >= 0 || source.Length > available)
            {
                var end = newLine < 0 ? source.Length : newLine;
                source = source[..Math.Min(end, available - 3)] + "...";
            }

            shortSource = prefix + source + suffix;
        }

        return $"{shortSource}:{instruction.SourceLine}: {message}";
    }

    private static int GetErrorOperandRegister(
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        switch (instruction.Opcode)
        {
            case LuaIrOpcode.Call:
            case LuaIrOpcode.TailCall:
                return instruction.A;
            case LuaIrOpcode.GetTable:
                return instruction.B;
            case LuaIrOpcode.SetTable:
                return instruction.A;
            case LuaIrOpcode.Unary:
                return instruction.B;
            case LuaIrOpcode.Binary:
                var operation = (LuaIrBinaryOperator)instruction.D;
                var left = Read(thread, frame, instruction.B);
                if (IsArithmeticErrorOperation(operation))
                {
                    return LuaValueOperations.TryToNumber(left, out _)
                        ? instruction.C
                        : instruction.B;
                }

                if (IsBitwiseErrorOperation(operation))
                {
                    return left.Kind is LuaValueKind.Integer or LuaValueKind.Float
                        ? instruction.C
                        : instruction.B;
                }

                if (operation == LuaIrBinaryOperator.Concatenate)
                {
                    return left.Kind is LuaValueKind.String or LuaValueKind.Integer or LuaValueKind.Float
                        ? instruction.C
                        : instruction.B;
                }

                return -1;
            default:
                return -1;
        }
    }

    private static bool TryDescribeRegisterOrigin(
        LuaFrame frame,
        int pc,
        int register,
        int depth,
        out LuaValueOrigin origin)
    {
        origin = default;
        if (depth >= 16 || register < 0 || register >= frame.Closure.Function.RegisterCount)
        {
            return false;
        }

        var function = frame.Closure.Function;
        var activeLocals = function.LocalVariables.Where(local =>
            local.StartProgramCounter <= pc && pc < local.EndProgramCounter).ToArray();
        if (register < activeLocals.Length)
        {
            var name = Encoding.UTF8.GetString(activeLocals[register].Name.AsSpan());
            origin = string.Equals(name, "_ENV", StringComparison.Ordinal)
                ? new LuaValueOrigin(LuaValueOriginKind.Environment, name)
                : new LuaValueOrigin(LuaValueOriginKind.Local, name);
            return true;
        }

        for (var candidatePc = Math.Min(pc - 1, function.Instructions.Length - 1);
             candidatePc >= 0;
             candidatePc--)
        {
            var candidate = function.Instructions[candidatePc];
            if (!WritesRegister(candidate, register))
            {
                continue;
            }

            if (IsShortCircuitMerge(function, candidatePc, pc, register))
            {
                // The producer belongs to an arm that a forward conditional jump can
                // skip while preserving the same register. Lua deliberately omits a
                // variable name because either arm may be the source. A producer at or
                // after the jump target is instead an ordinary register reuse following
                // a statement-level branch and must retain its origin.
                return false;
            }

            switch (candidate.Opcode)
            {
                case LuaIrOpcode.Move:
                    return TryDescribeRegisterOrigin(
                        frame,
                        candidatePc,
                        candidate.B,
                        depth + 1,
                        out origin);
                case LuaIrOpcode.GetUpvalue:
                    if ((uint)candidate.B >= (uint)function.Upvalues.Length)
                    {
                        return false;
                    }

                    var upvalueName = function.Upvalues[candidate.B].Name;
                    origin = string.Equals(upvalueName, "_ENV", StringComparison.Ordinal)
                        ? new LuaValueOrigin(LuaValueOriginKind.Environment, upvalueName)
                        : new LuaValueOrigin(LuaValueOriginKind.Upvalue, upvalueName);
                    return true;
                case LuaIrOpcode.GetTable:
                    var hasKey = TryGetStringConstantProducer(
                            function,
                            candidatePc,
                            candidate.C,
                            out var key);
                    if (!hasKey)
                    {
                        return false;
                    }

                    _ = TryDescribeRegisterOrigin(
                        frame,
                        candidatePc,
                        candidate.B,
                        depth + 1,
                        out var tableOrigin);
                    origin = tableOrigin.Kind == LuaValueOriginKind.Environment
                        ? new LuaValueOrigin(LuaValueOriginKind.Global, key, candidate.B)
                        : new LuaValueOrigin(LuaValueOriginKind.Field, key, candidate.B);
                    return true;
                default:
                    return false;
            }
        }

        return false;
    }

    private static bool IsShortCircuitMerge(
        LuaIrFunction function,
        int producerPc,
        int consumerPc,
        int register)
    {
        for (var branchPc = 0; branchPc < consumerPc; branchPc++)
        {
            var branch = function.Instructions[branchPc];
            if (branch.Opcode is LuaIrOpcode.JumpIfFalse or LuaIrOpcode.JumpIfTrue &&
                branch.A == register &&
                branch.B > producerPc &&
                branch.B <= consumerPc)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetStringConstantProducer(
        LuaIrFunction function,
        int pc,
        int register,
        out string value)
    {
        for (var candidatePc = Math.Min(pc - 1, function.Instructions.Length - 1);
             candidatePc >= 0;
             candidatePc--)
        {
            var candidate = function.Instructions[candidatePc];
            if (!WritesRegister(candidate, register))
            {
                continue;
            }

            if (candidate.Opcode == LuaIrOpcode.Move)
            {
                return TryGetStringConstantProducer(function, candidatePc, candidate.B, out value);
            }

            if (candidate.Opcode == LuaIrOpcode.LoadConstant &&
                (uint)candidate.B < (uint)function.Constants.Length &&
                function.Constants[candidate.B] is { Kind: LuaIrConstantKind.String } constant)
            {
                value = Encoding.UTF8.GetString(constant.Bytes.AsSpan());
                return true;
            }

            break;
        }

        value = string.Empty;
        return false;
    }

    private static bool WritesRegister(LuaIrInstruction instruction, int register) =>
        instruction.Opcode switch
        {
            LuaIrOpcode.LoadNil => register >= instruction.A &&
                register < instruction.A + Math.Max(instruction.B, 1),
            LuaIrOpcode.VarArg => register == instruction.A ||
                instruction.B > 1 && register >= instruction.A &&
                register < instruction.A + instruction.B,
            LuaIrOpcode.Call => register == instruction.A ||
                instruction.C > 1 && register >= instruction.A &&
                register < instruction.A + instruction.C,
            LuaIrOpcode.LoadConstant or LuaIrOpcode.Move or LuaIrOpcode.GetUpvalue or
                LuaIrOpcode.NewTable or LuaIrOpcode.GetTable or LuaIrOpcode.Closure or
                LuaIrOpcode.Unary or LuaIrOpcode.Binary => register == instruction.A,
            _ => false,
        };

    private static bool IsArithmeticErrorOperation(LuaIrBinaryOperator operation) =>
        operation is LuaIrBinaryOperator.Add or LuaIrBinaryOperator.Subtract or
            LuaIrBinaryOperator.Multiply or LuaIrBinaryOperator.Divide or
            LuaIrBinaryOperator.FloorDivide or LuaIrBinaryOperator.Modulo or
            LuaIrBinaryOperator.Power;

    private static bool IsBitwiseErrorOperation(LuaIrBinaryOperator operation) =>
        operation is LuaIrBinaryOperator.BitwiseAnd or LuaIrBinaryOperator.BitwiseOr or
            LuaIrBinaryOperator.BitwiseXor or LuaIrBinaryOperator.ShiftLeft or
            LuaIrBinaryOperator.ShiftRight;

    private enum LuaValueOriginKind : byte
    {
        None,
        Environment,
        Local,
        Upvalue,
        Global,
        Field,
        Method,
    }

    private readonly record struct LuaValueOrigin(
        LuaValueOriginKind Kind,
        string Name,
        int SourceRegister = -1);

    private static LuaValue MaterializeError(LuaState state, LuaRuntimeException exception) =>
        exception.HasErrorValue
            ? exception.ErrorValue
            : LuaValue.FromString(state.Strings.GetOrCreate(
                System.Text.Encoding.UTF8.GetBytes(exception.Message)));

    private static LuaValue CreateErrorInErrorHandling(LuaState state) =>
        LuaValue.FromString(state.Strings.GetOrCreate("error in error handling"u8));

    internal static LuaValue Read(LuaThread thread, LuaFrame frame, int register) =>
        thread.Stack[frame.Base + register];

    internal static void Write(LuaThread thread, LuaFrame frame, int register, LuaValue value)
    {
        var index = frame.Base + register;
        thread.Stack[index] = value;
        frame.Top = Math.Max(frame.Top, index + 1);
    }

    private static LuaValue[] InvokeNativeBody(
        LuaState state,
        LuaValue function,
        ReadOnlySpan<LuaValue> arguments)
    {
        var previous = state.RunningNativeFunction;
        state.RunningNativeFunction = function;
        try
        {
            return function.TryGetNativeFunction()?.Body!(state, arguments) ??
                throw new InvalidOperationException("The callable is not a native function.");
        }
        finally
        {
            state.RunningNativeFunction = previous;
        }
    }

    private static LuaClosure CreateNativeCallbackTrampoline(
        LuaState state,
        LuaValue callable)
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.GetUpvalue, 0, 0),
            new LuaIrInstruction(LuaIrOpcode.VarArg, 1, -1),
            new LuaIrInstruction(LuaIrOpcode.TailCall, 0, -1, -1));
        var function = new LuaIrFunction
        {
            Id = 0,
            Span = default,
            ParameterCount = 0,
            IsVarArg = true,
            RegisterCount = 2,
            Upvalues =
            [
                new LuaIrUpvalue("(callback)", 0, LuaIrUpvalueSourceKind.Environment, 0),
            ],
            Instructions = instructions,
            BasicBlocks = LuaIrControlFlow.Build(instructions),
        };
        var module = new LuaIrModule
        {
            MainFunctionId = 0,
            Functions = [function],
        };
        return new LuaClosure(
            state.Heap,
            module,
            function,
            [new LuaUpvalue(state.Heap, callable)],
            new LuaModuleStringConstants());
    }

}
