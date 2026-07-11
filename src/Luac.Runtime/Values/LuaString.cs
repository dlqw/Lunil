using System.Text;

namespace Luac.Runtime.Values;

/// <summary>An immutable binary Lua string. UTF-16 is only a diagnostic view.</summary>
public sealed class LuaString : IEquatable<LuaString>
{
    private readonly byte[] _bytes;
    private readonly int _hashCode;

    public LuaString(ReadOnlySpan<byte> bytes)
    {
        _bytes = bytes.ToArray();
        var hash = new HashCode();
        hash.AddBytes(_bytes);
        _hashCode = hash.ToHashCode();
    }

    public int Length => _bytes.Length;

    public ReadOnlySpan<byte> AsSpan() => _bytes;

    public byte[] ToArray() => (byte[])_bytes.Clone();

    public bool Equals(LuaString? other) =>
        other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => obj is LuaString other && Equals(other);

    public override int GetHashCode() => _hashCode;

    public override string ToString() => Encoding.UTF8.GetString(_bytes);
}

public sealed class LuaStringPool
{
    private const int MaximumInternedLength = 40;
    private readonly Dictionary<int, List<WeakReference<LuaString>>> _shortStrings = [];

    public LuaString GetOrCreate(ReadOnlySpan<byte> bytes)
    {
        var candidate = new LuaString(bytes);
        if (bytes.Length > MaximumInternedLength)
        {
            return candidate;
        }

        var hashCode = candidate.GetHashCode();
        if (!_shortStrings.TryGetValue(hashCode, out var bucket))
        {
            bucket = [];
            _shortStrings.Add(hashCode, bucket);
        }

        for (var index = bucket.Count - 1; index >= 0; index--)
        {
            if (!bucket[index].TryGetTarget(out var existing))
            {
                bucket.RemoveAt(index);
            }
            else if (existing.Equals(candidate))
            {
                return existing;
            }
        }

        bucket.Add(new WeakReference<LuaString>(candidate));
        return candidate;
    }
}
