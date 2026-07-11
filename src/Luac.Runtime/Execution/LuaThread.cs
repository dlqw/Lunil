using Luac.Runtime.Memory;
using Luac.Runtime.Values;

namespace Luac.Runtime.Execution;

public enum LuaThreadStatus : byte
{
    Suspended,
    Running,
    Dead,
    Error,
}

/// <summary>Owns the persistent Lua stack, logical frames, and identity map for open upvalues.</summary>
public sealed class LuaThread : LuaGcObject
{
    private readonly List<LuaFrame> _frames = [];
    private readonly SortedList<int, LuaUpvalue> _openUpvalues = [];

    internal LuaThread(LuaHeap owner, int initialStackCapacity = 128)
        : base(owner, checked(128 + initialStackCapacity * 16L))
    {
        Stack = new LuaStack(this, initialStackCapacity);
    }

    public LuaStack Stack { get; }

    public LuaThreadStatus Status { get; internal set; } = LuaThreadStatus.Suspended;

    public IReadOnlyList<LuaFrame> Frames => _frames;

    internal LuaFrame CurrentFrame => _frames[^1];

    internal int FrameCount => _frames.Count;

    internal LuaUnwindState? UnwindState { get; set; }

    internal void PushFrame(LuaFrame frame)
    {
        Owner.WriteBarrier(this, frame.Closure);
        foreach (var value in frame.VarArgStorage)
        {
            Owner.WriteBarrier(this, value);
        }

        _frames.Add(frame);
    }

    internal LuaFrame PopFrame()
    {
        var frame = _frames[^1];
        _frames.RemoveAt(_frames.Count - 1);
        return frame;
    }

    internal LuaUpvalue GetOrCreateOpenUpvalue(int stackIndex)
    {
        if (_openUpvalues.TryGetValue(stackIndex, out var upvalue))
        {
            return upvalue;
        }

        upvalue = new LuaUpvalue(this, stackIndex);
        Owner.WriteBarrier(this, upvalue);
        _openUpvalues.Add(stackIndex, upvalue);
        return upvalue;
    }

    internal void CloseUpvalues(int fromStackIndex)
    {
        for (var index = _openUpvalues.Count - 1; index >= 0; index--)
        {
            if (_openUpvalues.Keys[index] < fromStackIndex)
            {
                break;
            }

            _openUpvalues.Values[index].Close();
            _openUpvalues.RemoveAt(index);
        }
    }

    internal void Reset()
    {
        CloseUpvalues(0);
        _frames.Clear();
        UnwindState = null;
        Status = LuaThreadStatus.Suspended;
    }

    internal override void Traverse(LuaGcVisitor visitor)
    {
        foreach (var frame in _frames)
        {
            visitor.Visit(frame.Closure);
            Stack.Traverse(frame.Base, frame.Top, visitor);
            foreach (var value in frame.VarArgStorage)
            {
                visitor.Visit(value);
            }

            if (frame.PendingReturnValues is not null)
            {
                foreach (var value in frame.PendingReturnValues)
                {
                    visitor.Visit(value);
                }
            }

            if (frame.PendingTailCall is not null)
            {
                visitor.Visit(frame.PendingTailCall.Callable);
                foreach (var value in frame.PendingTailCall.Arguments)
                {
                    visitor.Visit(value);
                }
            }
        }

        foreach (var upvalue in _openUpvalues.Values)
        {
            visitor.Visit(upvalue);
        }

        if (UnwindState is not null)
        {
            visitor.Visit(UnwindState.Error);
        }
    }
}

internal sealed class LuaUnwindState
{
    public LuaUnwindState(LuaFrame? boundary, LuaValue error)
    {
        Boundary = boundary;
        Error = error;
    }

    public LuaFrame? Boundary { get; }

    public LuaValue Error { get; set; }

    public LuaFrame? ActiveCloseCall { get; set; }
}
