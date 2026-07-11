using Lunil.IR.Canonical;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Lunil.Runtime.Execution;

public sealed class LuaClosure : LuaGcObject
{
    private readonly LuaUpvalue[] _upvalues;

    internal LuaClosure(
        LuaHeap owner,
        LuaIrModule module,
        LuaIrFunction function,
        IReadOnlyList<LuaUpvalue> upvalues)
        : base(Validate(owner, module, function, upvalues), checked(64 + upvalues.Count * 8L))
    {
        Module = module;
        Function = function;
        _upvalues = [.. upvalues];
    }

    public LuaIrModule Module { get; }

    public LuaIrFunction Function { get; }

    public IReadOnlyList<LuaUpvalue> Upvalues => _upvalues;

    internal override void Traverse(LuaGcVisitor visitor)
    {
        foreach (var upvalue in _upvalues)
        {
            visitor.Visit(upvalue);
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
        int continuationId)
    {
        Kind = kind;
        Callable = callable;
        Values = values;
        ContinuationId = continuationId;
    }

    public LuaNativeStepKind Kind { get; }

    public LuaValue Callable { get; }

    public LuaValue[] Values { get; }

    public int ContinuationId { get; }

    public static LuaNativeStep Completed(params LuaValue[] values) =>
        new(LuaNativeStepKind.Completed, LuaValue.Nil, values, 0);

    public static LuaNativeStep CallLua(
        LuaValue callable,
        LuaValue[] arguments,
        int continuationId) =>
        new(LuaNativeStepKind.CallLua, callable, arguments, continuationId);

    public static LuaNativeStep Yielded(LuaValue[] values, int continuationId) =>
        new(LuaNativeStepKind.Yielded, LuaValue.Nil, values, continuationId);
}

/// <summary>Owner-aware context supplied to a resumable native descriptor.</summary>
public readonly struct LuaNativeCallContext
{
    internal LuaNativeCallContext(
        LuaState state,
        LuaThread thread,
        LuaNativeClosure? closure)
    {
        State = state;
        Thread = thread;
        Closure = closure;
    }

    public LuaState State { get; }

    public LuaThread Thread { get; }

    public LuaNativeClosure? Closure { get; }

    public IReadOnlyList<LuaValue> Captures => Closure?.Captures ?? [];
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
                "A resumable native descriptor must use a static delegate; store Lua captures in a LuaNativeClosure.",
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
    {
        if (stepBody.Target is null || stepBody.Method.IsStatic)
        {
            return true;
        }

        var targetType = stepBody.Target.GetType();
        return targetType.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) &&
            targetType.GetFields(BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic).Length == 0;
    }
}

/// <summary>A logical-heap native closure with explicitly traced, owner-validated captures.</summary>
public sealed class LuaNativeClosure : LuaGcObject
{
    private readonly LuaValue[] _captures;

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
