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
    private const int MaximumPooledFrames = 256;
    private readonly List<LuaFrame> _frames = [];
    private readonly List<LuaFrame> _retiredFrames = [];
    private readonly Stack<LuaFrame> _framePool = [];
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

    /// <summary>
    /// Gets the borrowed live view of active frames. Removed frames are pooled and references to
    /// them can be reset or reused; callers that need durable data must copy it while the frame is
    /// present in this collection.
    /// </summary>
    public IReadOnlyList<LuaFrame> Frames => _frames;

    public LuaValue DebugHook { get; internal set; }

    public LuaDebugHookMask DebugHookMask { get; internal set; }

    public int DebugHookCount { get; internal set; }

    internal ulong DebugModeVersion { get; set; }

    internal int DebugHookCounter { get; set; }

    internal bool IsRunningDebugHook { get; set; }

    internal LuaValue DebugHookSubjectFunction { get; set; }

    internal LuaValueWindow DebugHookTransferValues { get; } = new();

    internal int DebugHookTransferStart { get; set; } = 1;

    internal bool DebugHookTransferIsNative { get; set; }

    internal LuaFrame CurrentFrame => _frames[^1];

    internal int FrameCount => _frames.Count;

    internal int RetiredFrameCount => _retiredFrames.Count;

    internal int PooledFrameCount => _framePool.Count;

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
        Owner.WriteBarrier(this, frame.Continuation.ProtectionFunction);

        _frames.Add(frame);
    }

    internal LuaFrame PopFrame()
    {
        var frame = _frames[^1];
        _frames.RemoveAt(_frames.Count - 1);
        _retiredFrames.Add(frame);
        return frame;
    }

    internal LuaFrame RentFrame(
        LuaClosure closure,
        int @base,
        int top,
        int returnBase,
        int expectedResults,
        ReadOnlySpan<LuaValue> varArgs,
        LuaProtectedCallKind protectionKind = LuaProtectedCallKind.None,
        LuaValue errorHandler = default,
        bool isCloseHandler = false,
        bool isDebugHook = false,
        bool isHidden = false)
    {
        var frame = _framePool.Count == 0 ? new LuaFrame() : _framePool.Pop();
        frame.Initialize(
            closure,
            @base,
            top,
            returnBase,
            expectedResults,
            varArgs,
            protectionKind,
            errorHandler,
            isCloseHandler,
            isDebugHook,
            isHidden);
        return frame;
    }

    /// <summary>
    /// Makes frames retired by the previous scheduler turn available for reuse. Retirement is
    /// deliberately delayed because return/unwind code can still inspect a frame after PopFrame.
    /// </summary>
    internal void AdvanceFramePoolEpoch()
    {
        foreach (var frame in _retiredFrames)
        {
            frame.ResetForPool();
            if (_framePool.Count < MaximumPooledFrames)
            {
                _framePool.Push(frame);
            }
        }

        _retiredFrames.Clear();
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

    internal bool HasOpenUpvalueAtOrAbove(int stackIndex) =>
        _openUpvalues.Count != 0 && _openUpvalues.Keys[^1] >= stackIndex;

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
        RetireActiveFrames();
        _frames.Clear();
        AdvanceFramePoolEpoch();
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
        DebugHookSubjectFunction = LuaValue.Nil;
        ClearDebugHookTransfer();
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
        RetireActiveFrames();
        _frames.Clear();
        AdvanceFramePoolEpoch();
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
        DebugHookSubjectFunction = LuaValue.Nil;
        ClearDebugHookTransfer();
        Status = LuaThreadStatus.Dead;
    }

    internal override void Traverse(LuaGcVisitor visitor)
    {
        visitor.Visit(Entry);
        visitor.Visit(TerminalError);
        visitor.Visit(DebugHook);
        visitor.Visit(DebugHookSubjectFunction);
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

        foreach (var value in DebugHookTransferValues)
        {
            visitor.Visit(value);
        }

        foreach (var frame in _frames)
        {
            TraverseFrame(visitor, frame, includeStack: true);
        }

        foreach (var frame in _retiredFrames)
        {
            TraverseFrame(visitor, frame, includeStack: true);
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
            visitor.Visit(UnwindState.DebugBoundaryFunction);
            visitor.Visit(UnwindState.ErrorHandler);
        }
    }

    internal void SetDebugHookTransfer(
        ReadOnlySpan<LuaValue> values,
        bool isNative,
        int start = 1)
    {
        SetValueWindow(DebugHookTransferValues, values);
        DebugHookTransferStart = start;
        DebugHookTransferIsNative = isNative;
    }

    internal void ClearDebugHookTransfer()
    {
        DebugHookTransferValues.Clear();
        DebugHookTransferStart = 1;
        DebugHookTransferIsNative = false;
    }

    private void RetireActiveFrames()
    {
        _retiredFrames.AddRange(_frames);
    }

    private void TraverseFrame(
        LuaGcVisitor visitor,
        LuaFrame frame,
        bool includeStack)
    {
        visitor.Visit(frame.Closure);
        if (includeStack)
        {
            Stack.Traverse(frame.Base, frame.Top, visitor);
        }

        foreach (var value in frame.VarArgStorage)
        {
            visitor.Visit(value);
        }

        visitor.Visit(frame.Continuation.Value);
        visitor.Visit(frame.Continuation.ErrorHandler);
        visitor.Visit(frame.Continuation.ProtectionFunction);
        foreach (var value in frame.Continuation.Values)
        {
            visitor.Visit(value);
        }
    }
}

internal sealed class LuaValueWindow : IReadOnlyList<LuaValue>
{
    private LuaValue[] _values = [];

    public int Count { get; private set; }

    internal int Capacity => _values.Length;

    public LuaValue this[int index]
    {
        get => index >= 0 && index < Count
            ? _values[index]
            : throw new ArgumentOutOfRangeException(nameof(index));
        internal set
        {
            if ((uint)index >= (uint)Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            _values[index] = value;
        }
    }

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

    public void Clear(int maximumRetainedCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRetainedCapacity);
        Clear();
        if (_values.Length > maximumRetainedCapacity)
        {
            _values = [];
        }
    }

    public ReadOnlySpan<LuaValue> AsSpan() => _values.AsSpan(0, Count);

    public Enumerator GetEnumerator() => new(_values, Count);

    IEnumerator<LuaValue> IEnumerable<LuaValue>.GetEnumerator() => GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();

    internal struct Enumerator : IEnumerator<LuaValue>
    {
        private readonly LuaValue[] _values;
        private readonly int _count;
        private int _index;

        internal Enumerator(LuaValue[] values, int count)
        {
            _values = values;
            _count = count;
            _index = -1;
        }

        public LuaValue Current => (uint)_index < (uint)_count
            ? _values[_index]
            : throw new InvalidOperationException(
                "The enumerator is not positioned on a vararg value.");

        public bool MoveNext() => ++_index < _count;

        void IDisposable.Dispose()
        {
        }

        object System.Collections.IEnumerator.Current => Current;

        void System.Collections.IEnumerator.Reset() => _index = -1;
    }
}

internal sealed class LuaUnwindState
{
    public LuaUnwindState(
        LuaFrame? boundary,
        LuaValue error,
        LuaValue debugBoundaryFunction,
        LuaValue errorHandler,
        bool skipProtectedNativeCallback = false)
    {
        Boundary = boundary;
        Error = error;
        DebugBoundaryFunction = debugBoundaryFunction;
        ErrorHandler = errorHandler;
        ErrorHandlerPending = !errorHandler.IsNil;
        SkipProtectedNativeCallback = skipProtectedNativeCallback;
    }

    public LuaFrame? Boundary { get; }

    public LuaValue Error { get; set; }

    public LuaValue DebugBoundaryFunction { get; }

    public LuaValue ErrorHandler { get; }

    public bool ErrorHandlerPending { get; set; }

    public LuaFrame? ActiveErrorHandler { get; set; }

    public bool SkipProtectedNativeCallback { get; }

    public LuaFrame? ActiveCloseCall { get; set; }
}
