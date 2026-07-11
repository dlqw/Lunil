using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

public enum LuaThreadStatus : byte
{
    New,
    Suspended,
    Running,
    Normal,
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

    public LuaThreadStatus Status { get; internal set; } = LuaThreadStatus.New;

    public LuaValue Entry { get; private set; }

    public bool Started { get; internal set; }

    public LuaValue TerminalError { get; internal set; }

    public IReadOnlyList<LuaValue> YieldedValues => _yieldedValues;

    public IReadOnlyList<LuaValue> ResumeValues => _resumeValues;

    internal LuaThread? Resumer { get; set; }

    internal LuaThread? ActiveResumee { get; set; }

    internal bool IsClosing { get; set; }

    internal bool CloseHadError { get; set; }

    private readonly LuaValueWindow _yieldedValues = new();
    private readonly LuaValueWindow _resumeValues = new();

    internal ReadOnlySpan<LuaValue> YieldedSpan => _yieldedValues.AsSpan();

    internal ReadOnlySpan<LuaValue> ResumeSpan => _resumeValues.AsSpan();

    public IReadOnlyList<LuaFrame> Frames => _frames;

    public LuaValue DebugHook { get; internal set; }

    public LuaDebugHookMask DebugHookMask { get; internal set; }

    public int DebugHookCount { get; internal set; }

    internal int DebugHookCounter { get; set; }

    internal bool IsRunningDebugHook { get; set; }

    internal LuaFrame CurrentFrame => _frames[^1];

    internal int FrameCount => _frames.Count;

    internal LuaUnwindState? UnwindState { get; set; }

    internal LuaContinuation RootContinuation { get; } = new();

    internal void PushFrame(LuaFrame frame)
    {
        Owner.WriteBarrier(this, frame.Closure);
        foreach (var value in frame.VarArgStorage)
        {
            Owner.WriteBarrier(this, value);
        }

        Owner.WriteBarrier(this, frame.Continuation.ErrorHandler);

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
        RootContinuation.Reset();
        Entry = LuaValue.Nil;
        Started = false;
        TerminalError = LuaValue.Nil;
        Resumer = null;
        ActiveResumee = null;
        IsClosing = false;
        CloseHadError = false;
        _yieldedValues.Clear();
        _resumeValues.Clear();
        Status = LuaThreadStatus.New;
        IsRunningDebugHook = false;
    }

    internal void Initialize(LuaValue entry)
    {
        if (entry.Kind != LuaValueKind.Function)
        {
            throw new LuaRuntimeException("A coroutine entry must be a function.");
        }

        Owner.ValidateValue(entry);
        Reset();
        Entry = entry;
        Owner.WriteBarrier(this, entry);
    }

    internal void SetYieldedValues(ReadOnlySpan<LuaValue> values) =>
        SetValueWindow(_yieldedValues, values);

    internal void SetResumeValues(ReadOnlySpan<LuaValue> values) =>
        SetValueWindow(_resumeValues, values);

    internal void ClearTransferValues()
    {
        _yieldedValues.Clear();
        _resumeValues.Clear();
    }

    private void SetValueWindow(LuaValueWindow window, ReadOnlySpan<LuaValue> values)
    {
        foreach (var value in values)
        {
            Owner.ValidateValue(value);
            Owner.WriteBarrier(this, value);
        }

        window.Set(values);
    }

    internal void FinishClosed()
    {
        var top = 0;
        foreach (var frame in _frames)
        {
            top = Math.Max(top, frame.Top);
            frame.Continuation.Reset();
        }

        CloseUpvalues(0);
        _frames.Clear();
        if (top > 0)
        {
            Stack.Clear(0, top);
        }

        UnwindState = null;
        RootContinuation.Reset();
        Entry = LuaValue.Nil;
        TerminalError = LuaValue.Nil;
        Resumer = null;
        ActiveResumee = null;
        IsClosing = false;
        CloseHadError = false;
        _yieldedValues.Clear();
        _resumeValues.Clear();
        Status = LuaThreadStatus.Dead;
    }

    internal override void Traverse(LuaGcVisitor visitor)
    {
        visitor.Visit(Entry);
        visitor.Visit(TerminalError);
        visitor.Visit(DebugHook);
        if (Resumer is not null)
        {
            visitor.Visit(Resumer);
        }

        if (ActiveResumee is not null)
        {
            visitor.Visit(ActiveResumee);
        }

        foreach (var value in _yieldedValues)
        {
            visitor.Visit(value);
        }

        foreach (var value in _resumeValues)
        {
            visitor.Visit(value);
        }

        foreach (var frame in _frames)
        {
            visitor.Visit(frame.Closure);
            Stack.Traverse(frame.Base, frame.Top, visitor);
            foreach (var value in frame.VarArgStorage)
            {
                visitor.Visit(value);
            }

            visitor.Visit(frame.Continuation.Value);
            visitor.Visit(frame.Continuation.ErrorHandler);
            foreach (var value in frame.Continuation.Values)
            {
                visitor.Visit(value);
            }
        }

        visitor.Visit(RootContinuation.Value);
        foreach (var value in RootContinuation.Values)
        {
            visitor.Visit(value);
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

internal sealed class LuaValueWindow : IReadOnlyList<LuaValue>
{
    private LuaValue[] _values = [];

    public int Count { get; private set; }

    public LuaValue this[int index] => index >= 0 && index < Count
        ? _values[index]
        : throw new ArgumentOutOfRangeException(nameof(index));

    public void Set(ReadOnlySpan<LuaValue> values)
    {
        if (_values.Length < values.Length)
        {
            var capacity = Math.Max(4, _values.Length);
            while (capacity < values.Length)
            {
                capacity = checked(capacity * 2);
            }

            Array.Resize(ref _values, capacity);
        }

        if (Count > values.Length)
        {
            Array.Clear(_values, values.Length, Count - values.Length);
        }

        values.CopyTo(_values);
        Count = values.Length;
    }

    public void Clear()
    {
        if (Count != 0)
        {
            Array.Clear(_values, 0, Count);
            Count = 0;
        }
    }

    public ReadOnlySpan<LuaValue> AsSpan() => _values.AsSpan(0, Count);

    public IEnumerator<LuaValue> GetEnumerator()
    {
        for (var index = 0; index < Count; index++)
        {
            yield return _values[index];
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();
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
