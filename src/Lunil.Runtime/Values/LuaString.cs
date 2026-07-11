using System.Text;
using Lunil.Runtime.Memory;

namespace Lunil.Runtime.Values;

/// <summary>An immutable binary Lua string. UTF-16 is only a diagnostic view.</summary>
public sealed class LuaString : LuaGcObject, IEquatable<LuaString>
{
    private readonly byte[] _bytes;
    private readonly int _hashCode;

    internal LuaString(LuaHeap owner, ReadOnlySpan<byte> bytes)
        : base(owner, checked(32 + bytes.Length))
    {
        _bytes = bytes.ToArray();
        _hashCode = ComputeHashCode(_bytes);
    }

    public int Length => _bytes.Length;

    public ReadOnlySpan<byte> AsSpan() => _bytes;

    public byte[] ToArray() => (byte[])_bytes.Clone();

    public bool Equals(LuaString? other) =>
        other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => obj is LuaString other && Equals(other);

    public override int GetHashCode() => _hashCode;

    public override string ToString() => Encoding.UTF8.GetString(_bytes);

    internal override void Traverse(LuaGcVisitor visitor)
    {
    }

    internal static int ComputeHashCode(ReadOnlySpan<byte> bytes)
    {
        var hash = new HashCode();
        hash.AddBytes(bytes);
        return hash.ToHashCode();
    }
}

public sealed class LuaStringPool
{
    private const int MaximumInternedLength = 40;
    private readonly LuaHeap _heap;
    private readonly Dictionary<int, List<WeakReference<LuaString>>> _shortStrings = [];

    internal LuaStringPool(LuaHeap heap)
    {
        _heap = heap;
    }

    public LuaString GetOrCreate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumInternedLength)
        {
            return new LuaString(_heap, bytes);
        }

        var hashCode = LuaString.ComputeHashCode(bytes);
        if (!_shortStrings.TryGetValue(hashCode, out var bucket))
        {
            bucket = [];
            _shortStrings.Add(hashCode, bucket);
        }

        for (var index = bucket.Count - 1; index >= 0; index--)
        {
            if (!bucket[index].TryGetTarget(out var existing) || !existing.IsAlive)
            {
                bucket.RemoveAt(index);
            }
            else if (existing.AsSpan().SequenceEqual(bytes))
            {
                return existing;
            }
        }

        var candidate = new LuaString(_heap, bytes);
        bucket.Add(new WeakReference<LuaString>(candidate));
        return candidate;
    }
}
