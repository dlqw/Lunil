using Luac.Runtime.Memory;
using Luac.Runtime.Values;

namespace Luac.Runtime.Execution;

/// <summary>Growable explicit Lua stack. Slots are addressed absolutely by open upvalues.</summary>
#pragma warning disable CA1711 // Stack is the exact Lua VM domain term.
public sealed class LuaStack
{
    private readonly LuaThread _owner;
    private LuaValue[] _values;

    internal LuaStack(LuaThread owner, int initialCapacity = 128)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        _owner = owner;
        _values = new LuaValue[initialCapacity];
    }

    public int Capacity => _values.Length;

    public LuaValue this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            EnsureCapacity(index + 1);
            return _values[index];
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            _owner.Owner.ValidateValue(value);
            _owner.Owner.WriteBarrier(_owner, value);
            EnsureCapacity(index + 1);
            _values[index] = value;
        }
    }

    public void EnsureCapacity(int required)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(required);
        if (required <= _values.Length)
        {
            return;
        }

        var capacity = _values.Length;
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }

        _owner.Owner.AdjustLogicalSize(
            _owner,
            checked((capacity - _values.Length) * 16L));
        Array.Resize(ref _values, capacity);
    }

    public void Clear(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0)
        {
            return;
        }

        EnsureCapacity(checked(start + length));
        Array.Clear(_values, start, length);
    }

    internal void Traverse(int start, int end, LuaGcVisitor visitor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        var boundedEnd = Math.Min(end, _values.Length);
        for (var index = start; index < boundedEnd; index++)
        {
            visitor.Visit(_values[index]);
        }
    }
}
#pragma warning restore CA1711
