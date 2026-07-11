using Luac.Runtime.Values;

namespace Luac.Runtime.Execution;

/// <summary>Growable explicit Lua stack. Slots are addressed absolutely by open upvalues.</summary>
#pragma warning disable CA1711 // Stack is the exact Lua VM domain term.
public sealed class LuaStack
{
    private LuaValue[] _values;

    public LuaStack(int initialCapacity = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        _values = new LuaValue[initialCapacity];
    }

    public int Capacity => _values.Length;

    public ref LuaValue this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            EnsureCapacity(index + 1);
            return ref _values[index];
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
}
#pragma warning restore CA1711
