using System.ComponentModel;
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
    private LuaCompiledExit(
        LuaCompiledExitKind kind,
        int programCounter,
        int instructionsConsumed,
        LuaCompiledExitReason reason)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(programCounter);
        ArgumentOutOfRangeException.ThrowIfNegative(instructionsConsumed);
        Kind = kind;
        ProgramCounter = programCounter;
        InstructionsConsumed = instructionsConsumed;
        Reason = reason;
    }

    public LuaCompiledExitKind Kind { get; }

    /// <summary>The committed or restart program counter in canonical IR coordinates.</summary>
    public int ProgramCounter { get; }

    /// <summary>The number of canonical instructions completed by this entry invocation.</summary>
    public int InstructionsConsumed { get; }

    public LuaCompiledExitReason Reason { get; }

    public static LuaCompiledExit Continue(int programCounter, int instructionsConsumed) =>
        new(LuaCompiledExitKind.Continue, programCounter, instructionsConsumed, LuaCompiledExitReason.None);

    public static LuaCompiledExit Poll(
        int programCounter,
        int instructionsConsumed,
        LuaCompiledExitReason reason) =>
        new(LuaCompiledExitKind.Poll, programCounter, instructionsConsumed, ValidateReason(reason));

    public static LuaCompiledExit Call(int programCounter, int instructionsConsumed) =>
        new(LuaCompiledExitKind.Call, programCounter, instructionsConsumed, LuaCompiledExitReason.None);

    public static LuaCompiledExit TailCall(int programCounter, int instructionsConsumed) =>
        new(LuaCompiledExitKind.TailCall, programCounter, instructionsConsumed, LuaCompiledExitReason.None);

    public static LuaCompiledExit Return(int programCounter, int instructionsConsumed) =>
        new(LuaCompiledExitKind.Return, programCounter, instructionsConsumed, LuaCompiledExitReason.None);

    public static LuaCompiledExit Deopt(
        int programCounter,
        int instructionsConsumed,
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
    private int _instructionsConsumed;
    private int _lastObservedProgramCounter;
    private int _lastObservedInstructionCount;

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

    public long RemainingInstructionCount { get; private set; }

    public ulong DebugModeVersion { get; private set; }

    public bool HasExactDebugHooks { get; private set; }

    internal int InstructionsConsumed => _instructionsConsumed;

    internal LuaExecutionEngine? ExecutionEngine { get; private set; }

    internal LuaScheduler? Scheduler { get; private set; }

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
        _instructionsConsumed = 0;
        _lastObservedProgramCounter = -1;
        _lastObservedInstructionCount = -1;
        ExecutionEngine = executionEngine;
        Scheduler = scheduler;
        State = state;
        Thread = thread;
        RemainingInstructionCount = remainingInstructionCount;
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

        RemainingInstructionCount -= instructionCount;
        _instructionsConsumed = checked(_instructionsConsumed + instructionCount);
        return true;
    }

    public bool IsDebugModeCurrent() => DebugModeVersion == Thread.DebugModeVersion;

    internal bool TryBeginInstructionObservation(int programCounter)
    {
        if (_lastObservedProgramCounter == programCounter &&
            _lastObservedInstructionCount == _instructionsConsumed)
        {
            return false;
        }

        _lastObservedProgramCounter = programCounter;
        _lastObservedInstructionCount = _instructionsConsumed;
        return true;
    }
}
