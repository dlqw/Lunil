using System.ComponentModel;
using System.Runtime.CompilerServices;
using Lunil.Runtime.Execution;

namespace Lunil.Runtime.CodeGen;

/// <summary>Reason a compiled block returned to the shared execution kernel.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public enum LuaCompiledExitKind : byte
{
    Continue,
    Poll,
    Call,
    TailCall,
    Return,
    Deopt,
}

/// <summary>Additional reason attached to poll and deoptimization exits.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public enum LuaCompiledExitReason : byte
{
    None,
    InstructionBudget,
    DebugModeChanged,
    GarbageCollection,
    BackendInvalidated,
    GuardFailure,
    UnsupportedInstruction,
}

/// <summary>
/// Tagged result returned by generated code to the shared scheduler. The frame contains all
/// Lua values and operation windows; this value contains only canonical control state.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly record struct LuaCompiledExit
{
    // Keep the 64-bit counter first and the byte tags last so the complete control payload
    // remains 16 bytes. A declaration-order layout would otherwise pad this value to 24 bytes
    // and force a hidden return buffer on common 64-bit ABIs.
    private readonly long _instructionsConsumed;
    private readonly int _programCounter;
    private readonly LuaCompiledExitKind _kind;
    private readonly LuaCompiledExitReason _reason;

    private LuaCompiledExit(
        LuaCompiledExitKind kind,
        int programCounter,
        long instructionsConsumed,
        LuaCompiledExitReason reason)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(programCounter);
        ArgumentOutOfRangeException.ThrowIfNegative(instructionsConsumed);
        _instructionsConsumed = instructionsConsumed;
        _programCounter = programCounter;
        _kind = kind;
        _reason = reason;
    }

    public LuaCompiledExitKind Kind => _kind;

    /// <summary>The committed or restart program counter in canonical IR coordinates.</summary>
    public int ProgramCounter => _programCounter;

    /// <summary>The number of canonical instructions completed by this entry invocation.</summary>
    public long InstructionsConsumed => _instructionsConsumed;

    public LuaCompiledExitReason Reason => _reason;

    public static LuaCompiledExit Continue(int programCounter, int instructionsConsumed) =>
        Continue(programCounter, (long)instructionsConsumed);

    public static LuaCompiledExit Continue(int programCounter, long instructionsConsumed) =>
        new(LuaCompiledExitKind.Continue, programCounter, instructionsConsumed, LuaCompiledExitReason.None);

    public static LuaCompiledExit Poll(
        int programCounter,
        int instructionsConsumed,
        LuaCompiledExitReason reason) => Poll(programCounter, (long)instructionsConsumed, reason);

    public static LuaCompiledExit Poll(
        int programCounter,
        long instructionsConsumed,
        LuaCompiledExitReason reason) =>
        new(LuaCompiledExitKind.Poll, programCounter, instructionsConsumed, ValidateReason(reason));

    public static LuaCompiledExit Call(int programCounter, int instructionsConsumed) =>
        Call(programCounter, (long)instructionsConsumed);

    public static LuaCompiledExit Call(int programCounter, long instructionsConsumed) =>
        new(LuaCompiledExitKind.Call, programCounter, instructionsConsumed, LuaCompiledExitReason.None);

    public static LuaCompiledExit TailCall(int programCounter, int instructionsConsumed) =>
        TailCall(programCounter, (long)instructionsConsumed);

    public static LuaCompiledExit TailCall(int programCounter, long instructionsConsumed) =>
        new(LuaCompiledExitKind.TailCall, programCounter, instructionsConsumed, LuaCompiledExitReason.None);

    public static LuaCompiledExit Return(int programCounter, int instructionsConsumed) =>
        Return(programCounter, (long)instructionsConsumed);

    public static LuaCompiledExit Return(int programCounter, long instructionsConsumed) =>
        new(LuaCompiledExitKind.Return, programCounter, instructionsConsumed, LuaCompiledExitReason.None);

    public static LuaCompiledExit Deopt(
        int programCounter,
        int instructionsConsumed,
        LuaCompiledExitReason reason) => Deopt(programCounter, (long)instructionsConsumed, reason);

    public static LuaCompiledExit Deopt(
        int programCounter,
        long instructionsConsumed,
        LuaCompiledExitReason reason) =>
        new(LuaCompiledExitKind.Deopt, programCounter, instructionsConsumed, ValidateReason(reason));

    private static LuaCompiledExitReason ValidateReason(LuaCompiledExitReason reason) =>
        reason == LuaCompiledExitReason.None
            ? throw new ArgumentOutOfRangeException(nameof(reason), "A poll or deopt exit requires a reason.")
            : reason;
}

/// <summary>Per-entry state visible to generated code through Runtime ABI v1.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class LuaExecutionContext
{
    private long _instructionCountAtEntry;
    private long _remainingInstructionCount;
    private int _lastObservedProgramCounter;
    private long _lastObservedInstructionCount;
    private LuaBackendGeneration? _backendGeneration;
    private long _backendGenerationToken;
    private LuaFrame? _exitFrame;

    internal LuaExecutionContext(
        LuaState state,
        LuaThread thread,
        long remainingInstructionCount)
    {
        ResetCore(null, state, thread, remainingInstructionCount);
    }

    internal LuaExecutionContext(
        LuaExecutionEngine executionEngine,
        LuaState state,
        LuaThread thread,
        long remainingInstructionCount,
        LuaScheduler? scheduler = null)
    {
        ResetCore(executionEngine, state, thread, remainingInstructionCount, scheduler);
    }

    public LuaState State { get; private set; } = null!;

    public LuaThread Thread { get; private set; } = null!;

    public long RemainingInstructionCount => _remainingInstructionCount;

    public ulong DebugModeVersion { get; private set; }

    public bool HasExactDebugHooks { get; private set; }

    internal long InstructionsConsumed =>
        _instructionCountAtEntry - _remainingInstructionCount;

    internal LuaExecutionEngine? ExecutionEngine { get; private set; }

    internal LuaScheduler? Scheduler { get; private set; }

    /// <summary>
    /// Identifies the live frame that owns a compiled exit. It is normally the frame supplied to
    /// the instruction executor, but a generation-bound direct-call trampoline may consume one or
    /// more nested exits before it returns to the shared scheduler.
    /// </summary>
    internal LuaFrame? ExitFrame => _exitFrame;

    internal void Reset(
        LuaState state,
        LuaThread thread,
        long remainingInstructionCount)
    {
        ResetCore(ExecutionEngine, state, thread, remainingInstructionCount);
    }

    internal void Reset(
        LuaExecutionEngine executionEngine,
        LuaState state,
        LuaThread thread,
        long remainingInstructionCount,
        LuaScheduler? scheduler = null)
    {
        ResetCore(executionEngine, state, thread, remainingInstructionCount, scheduler);
    }

    private void ResetCore(
        LuaExecutionEngine? executionEngine,
        LuaState state,
        LuaThread thread,
        long remainingInstructionCount,
        LuaScheduler? scheduler = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentOutOfRangeException.ThrowIfNegative(remainingInstructionCount);
        state.Heap.ValidateValue(Values.LuaValue.FromThread(thread));
        _instructionCountAtEntry = remainingInstructionCount;
        _lastObservedProgramCounter = -1;
        _lastObservedInstructionCount = -1;
        _exitFrame = null;
        ExecutionEngine = executionEngine;
        Scheduler = scheduler;
        State = state;
        Thread = thread;
        _remainingInstructionCount = remainingInstructionCount;
        DebugModeVersion = thread.DebugModeVersion;
        HasExactDebugHooks = thread.DebugHookMask != LuaDebugHookMask.None;
    }

    public bool TryReserveInstructions(int instructionCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(instructionCount);
        if (RemainingInstructionCount < instructionCount)
        {
            return false;
        }

        _remainingInstructionCount -= instructionCount;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryReserveSingleInterpreterInstruction()
    {
        if (_remainingInstructionCount == 0)
        {
            return false;
        }

        _remainingInstructionCount--;
        return true;
    }

    public bool IsDebugModeCurrent() => DebugModeVersion == Thread.DebugModeVersion;

    /// <summary>
    /// Returns whether the generated delegate still owns the module generation admitted at
    /// entry. Interpreter contexts that do not bind a generation return <see langword="true"/>.
    /// </summary>
    public bool IsBackendGenerationCurrent() => _backendGeneration is null ||
        _backendGeneration.IsCurrent(_backendGenerationToken);

    internal bool TryEnterBackendGeneration(
        LuaBackendGeneration generation,
        long expectedGeneration)
    {
        ArgumentNullException.ThrowIfNull(generation);
        if (_backendGeneration is not null)
        {
            throw new InvalidOperationException(
                "The execution context already owns a backend generation lease.");
        }

        if (!generation.TryEnter(expectedGeneration))
        {
            return false;
        }

        _backendGenerationToken = expectedGeneration;
        _backendGeneration = generation;
        return true;
    }

    internal void ExitBackendGeneration()
    {
        var generation = _backendGeneration ?? throw new InvalidOperationException(
            "The execution context has no backend generation lease.");
        _backendGeneration = null;
        _backendGenerationToken = 0;
        generation.Exit();
    }

    internal bool OwnsBackendGeneration(
        LuaBackendGeneration generation,
        long expectedGeneration) =>
        ReferenceEquals(_backendGeneration, generation) &&
        _backendGenerationToken == expectedGeneration &&
        generation.IsCurrent(expectedGeneration);

    internal void SetExitFrame(LuaFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _exitFrame = frame;
    }

    internal bool TryBeginInstructionObservation(int programCounter)
    {
        if (_lastObservedProgramCounter == programCounter &&
            _lastObservedInstructionCount == InstructionsConsumed)
        {
            return false;
        }

        _lastObservedProgramCounter = programCounter;
        _lastObservedInstructionCount = InstructionsConsumed;
        return true;
    }
}
