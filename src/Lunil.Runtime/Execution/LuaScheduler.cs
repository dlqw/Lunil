using System.Collections.Immutable;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

internal enum LuaSchedulerTransfer : byte
{
    None,
    Resume,
    Yield,
}

internal sealed class LuaActivation
{
    public LuaActivation(LuaThread thread, bool isYieldable)
    {
        Reset(thread, isYieldable);
    }

    public LuaThread Thread { get; private set; } = null!;

    public bool IsYieldable { get; private set; }

    public long InstructionCount { get; set; }

    public bool HasPendingError { get; set; }

    public LuaValue PendingError { get; set; }

    public ImmutableArray<LuaValue>? ForcedResult { get; set; }

    public LuaExecutionContext? ExecutionContext { get; set; }

    public void Reset(LuaThread thread, bool isYieldable)
    {
        Thread = thread;
        IsYieldable = isYieldable;
        InstructionCount = 0;
        HasPendingError = false;
        PendingError = LuaValue.Nil;
        ForcedResult = null;
    }
}

internal sealed class LuaScheduler
{
    private readonly List<LuaActivation> _activations;
    private int _activeCount;
    private long _completedInstructionCount;

    public LuaScheduler(LuaThread root, bool yieldableRoot, long instructionLimit)
    {
        _activations = [new LuaActivation(root, yieldableRoot)];
        _activeCount = 1;
        InstructionLimit = instructionLimit;
    }

    public int Count => _activeCount;

    public LuaActivation Current => _activations[_activeCount - 1];

    public LuaActivation GetActivation(int index) => _activations[index];

    public long InstructionLimit { get; }

    public long TotalInstructionCount
    {
        get
        {
            var total = _completedInstructionCount;
            for (var index = 0; index < _activeCount; index++)
            {
                total = checked(total + _activations[index].InstructionCount);
            }

            return total;
        }
    }

    public LuaSchedulerTransfer Transfer { get; set; }

    public LuaThread? ResumeTarget { get; private set; }

    public void Push(LuaThread thread, bool isYieldable)
    {
        if (_activeCount == _activations.Count)
        {
            _activations.Add(new LuaActivation(thread, isYieldable));
        }
        else
        {
            _activations[_activeCount].Reset(thread, isYieldable);
        }

        _activeCount++;
    }

    public void Pop()
    {
        _completedInstructionCount = checked(
            _completedInstructionCount + Current.InstructionCount);
        _activeCount--;
    }

    public void RequestYield() => Transfer = LuaSchedulerTransfer.Yield;

    public void RequestResume(LuaThread target, ReadOnlySpan<LuaValue> values)
    {
        ResumeTarget = target;
        target.SetResumeValues(values);
        Transfer = LuaSchedulerTransfer.Resume;
    }

    public void ClearTransfer()
    {
        Transfer = LuaSchedulerTransfer.None;
        ResumeTarget = null;
    }
}
