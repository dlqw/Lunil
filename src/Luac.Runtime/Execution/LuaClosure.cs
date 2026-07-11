using Luac.IR.Canonical;
using Luac.Runtime.Values;

namespace Luac.Runtime.Execution;

public sealed class LuaClosure
{
    public LuaClosure(LuaIrModule module, LuaIrFunction function, IReadOnlyList<LuaUpvalue> upvalues)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(upvalues);
        if (upvalues.Count != function.Upvalues.Length)
        {
            throw new ArgumentException("The closure upvalue count does not match its function.", nameof(upvalues));
        }

        Module = module;
        Function = function;
        Upvalues = [.. upvalues];
    }

    public LuaIrModule Module { get; }

    public LuaIrFunction Function { get; }

    public LuaUpvalue[] Upvalues { get; }
}

public delegate LuaValue[] LuaNativeFunctionBody(
    LuaState state,
    ReadOnlySpan<LuaValue> arguments);

public sealed class LuaNativeFunction
{
    public LuaNativeFunction(string name, LuaNativeFunctionBody body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(body);
        Name = name;
        Body = body;
    }

    public string Name { get; }

    internal LuaNativeFunctionBody Body { get; }
}
