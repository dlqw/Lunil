namespace Luac.Runtime.Values;

/// <summary>A split array/hash Lua table with Lua numeric-key normalization.</summary>
public sealed class LuaTable
{
    private readonly List<LuaValue> _array = [];
    private readonly Dictionary<LuaValue, LuaValue> _hash = new(LuaValueComparer.Instance);

    public int ArrayLength
    {
        get
        {
            var length = _array.Count;
            while (length > 0 && _array[length - 1].IsNil)
            {
                length--;
            }

            return length;
        }
    }

    public LuaValue Get(LuaValue key)
    {
        ValidateKey(key);
        if (TryGetArrayIndex(key, out var index) && index <= _array.Count)
        {
            return _array[index - 1];
        }

        return _hash.GetValueOrDefault(key);
    }

    public void Set(LuaValue key, LuaValue value)
    {
        ValidateKey(key);
        if (TryGetArrayIndex(key, out var index))
        {
            if (index <= _array.Count)
            {
                _array[index - 1] = value;
                if (value.IsNil)
                {
                    TrimArray();
                }

                return;
            }

            if (index == _array.Count + 1 && !value.IsNil)
            {
                _array.Add(value);
                MigrateArrayTail();
                return;
            }
        }

        if (value.IsNil)
        {
            _hash.Remove(key);
        }
        else
        {
            _hash[key] = value;
        }
    }

    private void MigrateArrayTail()
    {
        while (_hash.Remove(LuaValue.FromInteger(_array.Count + 1L), out var value))
        {
            _array.Add(value);
        }
    }

    private void TrimArray()
    {
        while (_array.Count > 0 && _array[^1].IsNil)
        {
            _array.RemoveAt(_array.Count - 1);
        }
    }

    private static bool TryGetArrayIndex(LuaValue key, out int index)
    {
        if (key.TryGetInteger(out var integer) && integer > 0 && integer <= int.MaxValue)
        {
            index = (int)integer;
            return true;
        }

        index = 0;
        return false;
    }

    private static void ValidateKey(LuaValue key)
    {
        if (key.IsNil)
        {
            throw new LuaRuntimeException("Table index is nil.");
        }

        if (key.Kind == LuaValueKind.Float && double.IsNaN(key.AsFloat()))
        {
            throw new LuaRuntimeException("Table index is NaN.");
        }
    }

    private sealed class LuaValueComparer : IEqualityComparer<LuaValue>
    {
        public static LuaValueComparer Instance { get; } = new();

        public bool Equals(LuaValue x, LuaValue y) => x.Equals(y);

        public int GetHashCode(LuaValue obj) => obj.GetHashCode();
    }
}
