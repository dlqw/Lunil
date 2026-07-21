using Lunil.IR.Canonical;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

public sealed class LuaClosure : LuaGcObject
{
    private readonly LuaUpvalue[] _upvalues;
    private readonly LuaFunctionSlot _functionSlot;
    private LuaValue _legacyEnvironment;

    internal LuaClosure(
        LuaHeap owner,
        LuaModuleRuntimeData runtimeData,
        LuaIrFunction function,
        IReadOnlyList<LuaUpvalue> upvalues)
        : base(
            Validate(owner, runtimeData.Module, function, upvalues),
            checked(64 + upvalues.Count * 8L))
    {
        ArgumentNullException.ThrowIfNull(runtimeData);
        _upvalues = [.. upvalues];
        _functionSlot = new LuaFunctionSlot(runtimeData.GetVersion(function.Id));
    }

    public LuaIrModule Module => FunctionVersion.Module;

    public LuaIrFunction Function => FunctionVersion.Function;

    /// <summary>Gets the immutable code version used by calls entering this closure now.</summary>
    public LuaFunctionVersion FunctionVersion => _functionSlot.Current;

    internal LuaFunctionSlot FunctionSlot => _functionSlot;

    internal bool HasSourceLineInformation => FunctionVersion.HasSourceLineInformation;

    internal int FramelessInstructionCount => FunctionVersion.FramelessInstructionCount;

    public IReadOnlyList<LuaUpvalue> Upvalues => _upvalues;

    internal LuaModuleStringConstants StringConstants =>
        FunctionVersion.RuntimeData.StringConstants;

    internal LuaString GetOrCreateStringConstant(LuaState state, int constantIndex)
    {
        if (!ReferenceEquals(state.Heap, Owner))
        {
            throw new LuaRuntimeException("cannot materialize a constant in another Lua state");
        }

        var value = FunctionVersion.GetOrCreateStringConstant(state, constantIndex);
        Owner.WriteBarrier(this, value);
        return value;
    }

    internal bool TryEnterFramelessCall() => FunctionVersion.TryEnterFramelessCall();

    internal LuaTableAllocationHint GetOrCreateTableAllocationHint(int programCounter)
        => FunctionVersion.GetOrCreateTableAllocationHint(programCounter);

    public LuaUpvalue GetUpvalue(int index) => _upvalues[index];

    /// <summary>
    /// Optional environment for closures that have no <c>_ENV</c> upvalue (Lua 5.1 setfenv).
    /// </summary>
    internal LuaValue LegacyEnvironment
    {
        get => _legacyEnvironment;
        set
        {
            Owner.WriteBarrier(this, value);
            _legacyEnvironment = value;
        }
    }

    public void JoinUpvalue(int index, LuaClosure source, int sourceIndex)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(Owner, source.Owner))
        {
            throw new LuaRuntimeException("cannot join upvalues from different Lua states");
        }

        var upvalue = source._upvalues[sourceIndex];
        Owner.WriteBarrier(this, upvalue);
        _upvalues[index] = upvalue;
    }

    internal void ReplaceUpvalue(int index, LuaUpvalue upvalue)
    {
        ArgumentNullException.ThrowIfNull(upvalue);
        if ((uint)index >= (uint)_upvalues.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (!ReferenceEquals(Owner, upvalue.Owner))
        {
            throw new LuaRuntimeException("cannot install an upvalue from another Lua state");
        }

        Owner.WriteBarrier(this, upvalue);
        _upvalues[index] = upvalue;
    }

    internal override void Traverse(LuaGcVisitor visitor)
    {
        foreach (var upvalue in _upvalues)
        {
            visitor.Visit(upvalue);
        }

        visitor.Visit(_legacyEnvironment);
        FunctionVersion.Traverse(visitor);
    }

    private static LuaHeap Validate(
        LuaHeap owner,
        LuaIrModule module,
        LuaIrFunction function,
        IReadOnlyList<LuaUpvalue> upvalues)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(upvalues);
        if (upvalues.Count != function.Upvalues.Length)
        {
            throw new ArgumentException("The closure upvalue count does not match its function.", nameof(upvalues));
        }

        foreach (var upvalue in upvalues)
        {
            if (!ReferenceEquals(upvalue.Owner, owner))
            {
                throw new LuaRuntimeException("A closure cannot capture an upvalue from another LuaState.");
            }
        }

        return owner;
    }

}

/// <summary>
/// Materialized string constants shared by every closure from one loaded module. Lua keeps
/// identical long-string literals in a prototype graph as the same object while still allowing
/// equal long strings produced at runtime to have distinct identities.
/// </summary>
internal sealed class LuaModuleStringConstants
{
    private readonly Dictionary<int, List<LuaString>> _strings = [];

    public LuaString GetOrCreate(LuaState state, ReadOnlySpan<byte> bytes)
    {
        var hashCode = LuaString.ComputeHashCode(bytes);
        if (!_strings.TryGetValue(hashCode, out var bucket))
        {
            bucket = [];
            _strings.Add(hashCode, bucket);
        }

        for (var index = bucket.Count - 1; index >= 0; index--)
        {
            var existing = bucket[index];
            if (!existing.IsAlive)
            {
                bucket.RemoveAt(index);
            }
            else if (existing.AsSpan().SequenceEqual(bytes))
            {
                return existing;
            }
        }

        var candidate = state.Strings.GetOrCreate(bytes);
        bucket.Add(candidate);
        return candidate;
    }

    public void Traverse(LuaGcVisitor visitor)
    {
        foreach (var bucket in _strings.Values)
        {
            foreach (var value in bucket)
            {
                if (value.IsAlive)
                {
                    visitor.Visit(value);
                }
            }
        }
    }
}

public delegate LuaValue[] LuaNativeFunctionBody(
    LuaState state,
    ReadOnlySpan<LuaValue> arguments);

public delegate LuaNativeStep LuaNativeFunctionStepBody(
    LuaNativeCallContext context,
    int continuationId,
    ReadOnlySpan<LuaValue> values);

public enum LuaNativeStepKind : byte
{
    Completed,
    CallLua,
    Yielded,
}

/// <summary>A resumable native activation result. No CLR continuation is retained by the VM.</summary>
public readonly struct LuaNativeStep
{
    private LuaNativeStep(
        LuaNativeStepKind kind,
        LuaValue callable,
        LuaValue[] values,
        int continuationId,
        LuaValue[] stateValues,
        bool callIsYieldable,
        bool callIsProtected,
        LuaNativeByteBuffer? byteBuffer,
        bool stateValuesAreReusable)
    {
        Kind = kind;
        Callable = callable;
        Values = values;
        ContinuationId = continuationId;
        StateValues = stateValues;
        CallIsYieldable = callIsYieldable;
        CallIsProtected = callIsProtected;
        ByteBuffer = byteBuffer;
        StateValuesAreReusable = stateValuesAreReusable;
    }

    public LuaNativeStepKind Kind { get; }

    public LuaValue Callable { get; }

    public LuaValue[] Values { get; }

    public int ContinuationId { get; }

    /// <summary>
    /// GC-visible state owned by this native invocation while it is waiting for a Lua call
    /// or a coroutine resume. It is distinct from callback/resume values and is never shared
    /// through descriptor or closure state.
    /// </summary>
    public LuaValue[] StateValues { get; }

    /// <summary>Whether a Lua callback may yield across this native call boundary.</summary>
    public bool CallIsYieldable { get; }

    /// <summary>
    /// Whether callback completion is reported as <c>true, ...</c> and callback errors as
    /// <c>false, error</c> instead of unwinding the native activation.
    /// </summary>
    public bool CallIsProtected { get; }

    /// <summary>
    /// Optional byte-only state retained across a Lua callback or yield. Unlike
    /// <see cref="StateValues"/>, it cannot hide logically collectable Lua objects.
    /// </summary>
    internal LuaNativeByteBuffer? ByteBuffer { get; }

    internal bool StateValuesAreReusable { get; }

    public static LuaNativeStep Completed(params LuaValue[] values) =>
        new(LuaNativeStepKind.Completed, LuaValue.Nil, values, 0, [], true, false, null, false);

    public static LuaNativeStep CallLua(
        LuaValue callable,
        LuaValue[] arguments,
        int continuationId,
        LuaValue[]? stateValues = null,
        bool callIsYieldable = true,
        bool callIsProtected = false) =>
        new(
            LuaNativeStepKind.CallLua,
            callable,
            arguments,
            continuationId,
            stateValues ?? [],
            callIsYieldable,
            callIsProtected,
            null,
            false);

    internal static LuaNativeStep CallLuaWithReusableState(
        LuaValue callable,
        LuaValue[] arguments,
        int continuationId,
        LuaValue[] stateValues,
        bool callIsYieldable,
        bool callIsProtected = false) =>
        new(
            LuaNativeStepKind.CallLua,
            callable,
            arguments,
            continuationId,
            stateValues,
            callIsYieldable,
            callIsProtected,
            null,
            true);

    internal static LuaNativeStep CallLuaWithByteBuffer(
        LuaValue callable,
        LuaValue[] arguments,
        int continuationId,
        LuaValue[] stateValues,
        bool callIsYieldable,
        LuaNativeByteBuffer byteBuffer,
        bool callIsProtected = false) =>
        new(
            LuaNativeStepKind.CallLua,
            callable,
            arguments,
            continuationId,
            stateValues,
            callIsYieldable,
            callIsProtected,
            byteBuffer,
            false);

    internal static LuaNativeStep CallLuaWithReusableStateAndByteBuffer(
        LuaValue callable,
        LuaValue[] arguments,
        int continuationId,
        LuaValue[] stateValues,
        bool callIsYieldable,
        LuaNativeByteBuffer byteBuffer,
        bool callIsProtected = false) =>
        new(
            LuaNativeStepKind.CallLua,
            callable,
            arguments,
            continuationId,
            stateValues,
            callIsYieldable,
            callIsProtected,
            byteBuffer,
            true);

    public static LuaNativeStep Yielded(
        LuaValue[] values,
        int continuationId,
        LuaValue[]? stateValues = null) =>
        new(
            LuaNativeStepKind.Yielded,
            LuaValue.Nil,
            values,
            continuationId,
            stateValues ?? [],
            true,
            false,
            null,
            false);

    internal static LuaNativeStep YieldedWithByteBuffer(
        LuaValue[] values,
        int continuationId,
        LuaValue[] stateValues,
        LuaNativeByteBuffer byteBuffer) =>
        new(
            LuaNativeStepKind.Yielded,
            LuaValue.Nil,
            values,
            continuationId,
            stateValues,
            true,
            false,
            byteBuffer,
            false);
}

/// <summary>Owner-aware context supplied to a resumable native descriptor.</summary>
public readonly struct LuaNativeCallContext
{
    internal LuaNativeCallContext(
        LuaState state,
        LuaThread thread,
        LuaNativeClosure? closure,
        IReadOnlyList<LuaValue>? invocationState = null,
        LuaNativeByteBuffer? byteBuffer = null)
    {
        State = state;
        Thread = thread;
        Closure = closure;
        InvocationState = invocationState ?? [];
        ByteBuffer = byteBuffer;
    }

    public LuaState State { get; }

    public LuaThread Thread { get; }

    public LuaNativeClosure? Closure { get; }

    public IReadOnlyList<LuaValue> Captures => Closure?.Captures ?? [];

    /// <summary>Per-invocation values preserved by the preceding resumable native step.</summary>
    public IReadOnlyList<LuaValue> InvocationState { get; }

    /// <summary>Byte-only state preserved by the preceding resumable native step.</summary>
    internal LuaNativeByteBuffer? ByteBuffer { get; }

    /// <summary>Creates owner-bound byte-only state for a resumable native operation.</summary>
    internal LuaNativeByteBuffer CreateByteBuffer(int initialCapacity = 0) =>
        new(State.Heap, initialCapacity);

    /// <summary>
    /// Creates a context for an immediate continuation of the same native activation.
    /// This keeps state-machine code identical whether a runtime operation completed
    /// immediately or required a Lua callback.
    /// </summary>
    public LuaNativeCallContext WithInvocationState(IReadOnlyList<LuaValue> invocationState) =>
        new(State, Thread, Closure, invocationState, ByteBuffer);
}

public sealed class LuaNativeFunction
{
    public LuaNativeFunction(string name, LuaNativeFunctionBody body)
        : this(name, body, LuaNativeFunctionKind.Normal)
    {
    }

    public LuaNativeFunction(string name, LuaNativeFunctionStepBody stepBody)
        : this(name, stepBody, LuaNativeFunctionKind.Normal)
    {
    }

    internal LuaNativeFunction(
        string name,
        LuaNativeFunctionBody body,
        LuaNativeFunctionKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(body);
        Name = name;
        Body = body;
        Kind = kind;
    }

    internal LuaNativeFunction(
        string name,
        LuaNativeFunctionStepBody stepBody,
        LuaNativeFunctionKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(stepBody);
        if (!IsCaptureFree(stepBody))
        {
            throw new ArgumentException(
                "A resumable native descriptor must use a static method group; store Lua captures in a LuaNativeClosure.",
                nameof(stepBody));
        }

        Name = name;
        StepBody = stepBody;
        Kind = kind;
    }

    public string Name { get; }

    internal LuaNativeFunctionBody? Body { get; }

    internal LuaNativeFunctionStepBody? StepBody { get; }

    internal LuaNativeFunctionKind Kind { get; }

    private static bool IsCaptureFree(LuaNativeFunctionStepBody stepBody)
        => stepBody.Target is null;
}

/// <summary>A logical-heap native closure with explicitly traced, owner-validated captures.</summary>
public sealed class LuaNativeClosure : LuaGcObject
{
    private readonly LuaValue[] _captures;
    private readonly object[] _captureIdentities;
    private LuaValue _environment;

    internal LuaNativeClosure(
        LuaHeap owner,
        LuaNativeFunction descriptor,
        ReadOnlySpan<LuaValue> captures)
        : base(
            Validate(owner, descriptor, captures),
            checked(64 + captures.Length * 16L))
    {
        Descriptor = descriptor;
        _captures = captures.ToArray();
        _captureIdentities = Enumerable.Range(0, captures.Length).Select(static _ => new object()).ToArray();
        foreach (var value in _captures)
        {
            owner.WriteBarrier(this, value);
        }
    }

    public LuaNativeFunction Descriptor { get; }

    public IReadOnlyList<LuaValue> Captures => _captures;

    public int CaptureCount => _captures.Length;

    /// <summary>Lua 5.1 function environment for this native closure.</summary>
    internal LuaValue Environment
    {
        get => _environment;
        set
        {
            Owner.WriteBarrier(this, value);
            _environment = value;
        }
    }

    public LuaValue GetCapture(int index) => _captures[index];

    public void SetCapture(int index, LuaValue value)
    {
        Owner.ValidateValue(value);
        Owner.WriteBarrier(this, value);
        _captures[index] = value;
    }

    public object GetCaptureIdentity(int index) => _captureIdentities[index];

    internal override void Traverse(LuaGcVisitor visitor)
    {
        foreach (var value in _captures)
        {
            visitor.Visit(value);
        }

        visitor.Visit(_environment);
    }

    private static LuaHeap Validate(
        LuaHeap owner,
        LuaNativeFunction descriptor,
        ReadOnlySpan<LuaValue> captures)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(descriptor);
        foreach (var value in captures)
        {
            owner.ValidateValue(value);
        }

        return owner;
    }
}

internal enum LuaNativeFunctionKind : byte
{
    Normal,
    ProtectedCall,
    ProtectedCallWithHandler,
    CoroutineResume,
    CoroutineYield,
    CoroutineWrap,
    CoroutineClose,
}
