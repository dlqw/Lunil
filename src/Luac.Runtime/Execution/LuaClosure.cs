using Luac.IR.Canonical;
using Luac.Runtime.Memory;
using Luac.Runtime.Values;

namespace Luac.Runtime.Execution;

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

public sealed class LuaNativeFunction
{
    public LuaNativeFunction(string name, LuaNativeFunctionBody body)
        : this(name, body, LuaNativeFunctionKind.Normal)
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

    public string Name { get; }

    internal LuaNativeFunctionBody Body { get; }

    internal LuaNativeFunctionKind Kind { get; }
}

internal enum LuaNativeFunctionKind : byte
{
    Normal,
    ProtectedCall,
    ProtectedCallWithHandler,
}
