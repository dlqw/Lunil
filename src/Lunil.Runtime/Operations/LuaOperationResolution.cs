using Lunil.Runtime.Values;

namespace Lunil.Runtime.Operations;

internal enum LuaResultTransform : byte
{
    None,
    LogicalNot,
}

internal readonly record struct LuaOperationResolution
{
    private LuaOperationResolution(
        bool requiresCall,
        LuaValue value,
        LuaValue callable,
        LuaValue[] arguments,
        LuaResultTransform transform)
    {
        RequiresCall = requiresCall;
        Value = value;
        Callable = callable;
        Arguments = arguments;
        Transform = transform;
    }

    public bool RequiresCall { get; }

    public LuaValue Value { get; }

    public LuaValue Callable { get; }

    public LuaValue[] Arguments { get; }

    public LuaResultTransform Transform { get; }

    public static LuaOperationResolution Immediate(LuaValue value) =>
        new(false, value, LuaValue.Nil, [], LuaResultTransform.None);

    public static LuaOperationResolution Call(
        LuaValue callable,
        LuaValue[] arguments,
        LuaResultTransform transform = LuaResultTransform.None) =>
        new(true, LuaValue.Nil, callable, arguments, transform);
}
