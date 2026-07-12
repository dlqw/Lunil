using Lunil.Runtime.Values;

namespace Lunil.Runtime.Operations;

public enum LuaResultTransform : byte
{
    None,
    LogicalNot,
}

/// <summary>
/// Result of a primitive Runtime operation. The common one-to-three metamethod arguments are
/// stored inline so the execution kernel can copy them directly to a Lua stack window.
/// </summary>
public readonly record struct LuaOperationResolution
{
    private readonly LuaValue _argument0;
    private readonly LuaValue _argument1;
    private readonly LuaValue _argument2;
    private readonly LuaValue[]? _overflowArguments;

    private LuaOperationResolution(
        bool requiresCall,
        LuaValue value,
        LuaValue callable,
        ReadOnlySpan<LuaValue> arguments,
        LuaResultTransform transform)
    {
        RequiresCall = requiresCall;
        Value = value;
        Callable = callable;
        ArgumentCount = arguments.Length;
        Transform = transform;
        _argument0 = arguments.Length > 0 ? arguments[0] : LuaValue.Nil;
        _argument1 = arguments.Length > 1 ? arguments[1] : LuaValue.Nil;
        _argument2 = arguments.Length > 2 ? arguments[2] : LuaValue.Nil;
        _overflowArguments = arguments.Length > 3 ? arguments.ToArray() : null;
    }

    public bool RequiresCall { get; }

    public LuaValue Value { get; }

    public LuaValue Callable { get; }

    public int ArgumentCount { get; }

    /// <summary>
    /// Compatibility materialization for APIs that retain callback arguments. The execution
    /// kernel uses <see cref="GetArgument"/> and does not allocate this array.
    /// </summary>
    public LuaValue[] Arguments
    {
        get
        {
            var arguments = new LuaValue[ArgumentCount];
            for (var index = 0; index < arguments.Length; index++)
            {
                arguments[index] = GetArgument(index);
            }

            return arguments;
        }
    }

    public LuaResultTransform Transform { get; }

    public LuaValue GetArgument(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ArgumentCount);
        if (_overflowArguments is not null)
        {
            return _overflowArguments[index];
        }

        return index switch
        {
            0 => _argument0,
            1 => _argument1,
            2 => _argument2,
            _ => throw new InvalidOperationException("Invalid inline operation argument."),
        };
    }

    public static LuaOperationResolution Immediate(LuaValue value) =>
        new(false, value, LuaValue.Nil, [], LuaResultTransform.None);

    public static LuaOperationResolution Call(
        LuaValue callable,
        LuaValue argument,
        LuaResultTransform transform = LuaResultTransform.None) =>
        new(true, LuaValue.Nil, callable, [argument], transform);

    public static LuaOperationResolution Call(
        LuaValue callable,
        LuaValue argument0,
        LuaValue argument1,
        LuaResultTransform transform = LuaResultTransform.None) =>
        new(true, LuaValue.Nil, callable, [argument0, argument1], transform);

    public static LuaOperationResolution Call(
        LuaValue callable,
        LuaValue argument0,
        LuaValue argument1,
        LuaValue argument2,
        LuaResultTransform transform = LuaResultTransform.None) =>
        new(true, LuaValue.Nil, callable, [argument0, argument1, argument2], transform);

    public static LuaOperationResolution Call(
        LuaValue callable,
        LuaValue[] arguments,
        LuaResultTransform transform = LuaResultTransform.None)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new(true, LuaValue.Nil, callable, arguments, transform);
    }

    public static LuaOperationResolution Call(
        LuaValue callable,
        ReadOnlySpan<LuaValue> arguments,
        LuaResultTransform transform = LuaResultTransform.None) =>
        new(true, LuaValue.Nil, callable, arguments, transform);
}
