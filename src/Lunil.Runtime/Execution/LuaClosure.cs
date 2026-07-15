using Lunil.IR.Canonical;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

public sealed class LuaClosure : LuaGcObject
{
    private readonly LuaUpvalue[] _upvalues;
    private readonly LuaString?[] _materializedStringConstants;
    private readonly LuaTableAllocationHint?[] _tableAllocationHints;
    private readonly object _constantGate = new();
    private int _framelessCallEntries;

    internal LuaClosure(
        LuaHeap owner,
        LuaIrModule module,
        LuaIrFunction function,
        IReadOnlyList<LuaUpvalue> upvalues,
        LuaModuleStringConstants stringConstants)
        : base(Validate(owner, module, function, upvalues), checked(64 + upvalues.Count * 8L))
    {
        ArgumentNullException.ThrowIfNull(stringConstants);
        Module = module;
        Function = function;
        HasSourceLineInformation = function.Instructions.Any(
            static instruction => instruction.SourceLine > 0);
        _upvalues = [.. upvalues];
        _materializedStringConstants = new LuaString?[function.Constants.Length];
        _tableAllocationHints = new LuaTableAllocationHint?[function.Instructions.Length];
        StringConstants = stringConstants;
        FramelessInstructionCount = GetFramelessInstructionCount(function);
    }

    public LuaIrModule Module { get; }

    public LuaIrFunction Function { get; }

    internal bool HasSourceLineInformation { get; }

    internal int FramelessInstructionCount { get; }

    internal bool SupportsFramelessCall => FramelessInstructionCount != 0;

    public IReadOnlyList<LuaUpvalue> Upvalues => _upvalues;

    internal LuaModuleStringConstants StringConstants { get; }

    internal LuaString GetOrCreateStringConstant(LuaState state, int constantIndex)
    {
        var existing = Volatile.Read(ref _materializedStringConstants[constantIndex]);
        if (existing is not null && existing.IsAlive)
        {
            return existing;
        }

        lock (_constantGate)
        {
            existing = _materializedStringConstants[constantIndex];
            if (existing is not null && existing.IsAlive)
            {
                return existing;
            }

            var constant = Function.Constants[constantIndex];
            if (constant.Kind != LuaIrConstantKind.String)
            {
                throw new InvalidOperationException("The cached constant is not a string.");
            }

            if (!ReferenceEquals(state.Heap, Owner))
            {
                throw new LuaRuntimeException("cannot materialize a constant in another Lua state");
            }

            existing = StringConstants.GetOrCreate(state, constant.Bytes.AsSpan());
            Owner.WriteBarrier(this, existing);
            Volatile.Write(ref _materializedStringConstants[constantIndex], existing);
            return existing;
        }
    }

    internal bool TryEnterFramelessCall()
    {
        if (!SupportsFramelessCall)
        {
            return false;
        }

        // Preserve the normal frame/backend entry for the first two invocations so profiling,
        // tier installation, and diagnostics observe the function before the leaf fast path
        // starts bypassing scheduler frames. Once warm, avoid an atomic write on every leaf call
        // and keep the counter from wrapping in a long-lived state.
        if (Volatile.Read(ref _framelessCallEntries) > 2)
        {
            return true;
        }

        return Interlocked.Increment(ref _framelessCallEntries) > 2;
    }

    internal LuaTableAllocationHint GetOrCreateTableAllocationHint(int programCounter)
    {
        var existing = Volatile.Read(ref _tableAllocationHints[programCounter]);
        if (existing is not null)
        {
            return existing;
        }

        var candidate = new LuaTableAllocationHint();
        return Interlocked.CompareExchange(
            ref _tableAllocationHints[programCounter],
            candidate,
            null) ?? candidate;
    }

    public LuaUpvalue GetUpvalue(int index) => _upvalues[index];

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

    internal override void Traverse(LuaGcVisitor visitor)
    {
        foreach (var upvalue in _upvalues)
        {
            visitor.Visit(upvalue);
        }

        StringConstants.Traverse(visitor);
        foreach (var constant in _materializedStringConstants)
        {
            if (constant is not null && constant.IsAlive)
            {
                visitor.Visit(constant);
            }
        }
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

    private static int GetFramelessInstructionCount(LuaIrFunction function)
    {
        const int maximumInstructions = 16;
        const int maximumRegisters = 32;
        if (function.IsVarArg || function.Upvalues.Length != 0 ||
            function.Instructions.Length is 0 or > maximumInstructions ||
            function.RegisterCount > maximumRegisters)
        {
            return 0;
        }

        for (var programCounter = 0; programCounter < function.Instructions.Length;
             programCounter++)
        {
            var instruction = function.Instructions[programCounter];
            switch (instruction.Opcode)
            {
                case LuaIrOpcode.Move:
                case LuaIrOpcode.LoadNil:
                    break;
                case LuaIrOpcode.Unary when
                    (LuaIrUnaryOperator)instruction.C is LuaIrUnaryOperator.Negate or
                        LuaIrUnaryOperator.BitwiseNot or LuaIrUnaryOperator.LogicalNot:
                    break;
                case LuaIrOpcode.Binary when
                    (LuaIrBinaryOperator)instruction.D is not
                        (LuaIrBinaryOperator.Concatenate or LuaIrBinaryOperator.FloorDivide or
                            LuaIrBinaryOperator.Modulo):
                    break;
                case LuaIrOpcode.Return when instruction.B >= 0:
                    return programCounter + 1;
                default:
                    return 0;
            }
        }

        return 0;
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
        LuaNativeByteBuffer? byteBuffer)
    {
        Kind = kind;
        Callable = callable;
        Values = values;
        ContinuationId = continuationId;
        StateValues = stateValues;
        CallIsYieldable = callIsYieldable;
        CallIsProtected = callIsProtected;
        ByteBuffer = byteBuffer;
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

    public static LuaNativeStep Completed(params LuaValue[] values) =>
        new(LuaNativeStepKind.Completed, LuaValue.Nil, values, 0, [], true, false, null);

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
            null);

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
            byteBuffer);

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
            null);

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
            byteBuffer);
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
