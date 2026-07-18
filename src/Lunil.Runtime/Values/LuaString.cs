using System.Buffers;
using System.Text;
using Lunil.Runtime.Memory;

namespace Lunil.Runtime.Values;

/// <summary>An immutable binary Lua string. UTF-16 is only a diagnostic view.</summary>
public sealed class LuaString : LuaGcObject, IEquatable<LuaString>
{
    internal const int LogicalOverhead = 32;

    private byte[] _bytes;
    private readonly int _length;
    private readonly int _hashCode;
    private readonly bool _bytesArePooled;

    internal LuaString(LuaHeap owner, ReadOnlySpan<byte> bytes)
        : base(owner, CalculateLogicalSize(bytes.Length))
    {
        _bytes = bytes.ToArray();
        _length = _bytes.Length;
        _hashCode = ComputeHashCode(_bytes);
    }

    internal LuaString(LuaHeap owner, byte[] bytes, int length, bool bytesArePooled)
        : base(owner, CalculateLogicalSize(length))
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, bytes.Length);
        _bytes = bytes;
        _length = length;
        _hashCode = ComputeHashCode(bytes.AsSpan(0, length));
        _bytesArePooled = bytesArePooled;
    }

    public int Length => _length;

    public ReadOnlySpan<byte> AsSpan() => _bytes.AsSpan(0, _length);

    internal ReadOnlyMemory<byte> AsMemory() => _bytes.AsMemory(0, _length);

    public byte[] ToArray() => AsSpan().ToArray();

    public bool Equals(LuaString? other) =>
        other is not null && AsSpan().SequenceEqual(other.AsSpan());

    public override bool Equals(object? obj) => obj is LuaString other && Equals(other);

    public override int GetHashCode() => _hashCode;

    public override string ToString() => Encoding.UTF8.GetString(AsSpan());

    internal override void Traverse(LuaGcVisitor visitor)
    {
    }

    internal override void OnCollected()
    {
        if (_bytesArePooled && _bytes.Length != 0)
        {
            ArrayPool<byte>.Shared.Return(_bytes);
            _bytes = [];
        }
    }

    internal static int ComputeHashCode(ReadOnlySpan<byte> bytes)
    {
        var hash = new HashCode();
        hash.AddBytes(bytes);
        return hash.ToHashCode();
    }

    internal static long CalculateLogicalSize(long byteLength) =>
        checked(LogicalOverhead + byteLength);
}

public sealed class LuaStringPool
{
    private const int MaximumInternedLength = 40;
    private const int IntegerConcatCacheSize = 512;
    private const long MinimumCachedConcatInteger = -4096;
    private const long MaximumCachedConcatInteger = 4096;
    private readonly LuaHeap _heap;
    private readonly Dictionary<int, List<WeakReference<LuaString>>> _shortStrings = [];
    private readonly IntegerConcatCacheEntry[] _integerConcats =
        new IntegerConcatCacheEntry[IntegerConcatCacheSize];

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

    internal LuaString GetOrCreateOwned(byte[] bytes, int length, bool bytesArePooled)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, bytes.Length);
        if (length <= MaximumInternedLength)
        {
            try
            {
                return GetOrCreate(bytes.AsSpan(0, length));
            }
            finally
            {
                if (bytesArePooled && bytes.Length != 0)
                {
                    ArrayPool<byte>.Shared.Return(bytes);
                }
            }
        }

        try
        {
            return new LuaString(_heap, bytes, length, bytesArePooled);
        }
        catch
        {
            if (bytesArePooled && bytes.Length != 0)
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }

            throw;
        }
    }

    /// <summary>
    /// Caches the common short-string/integer concatenation shape. The cache is bounded and only
    /// returns entries whose Lua GC object is still alive; retaining a bounded managed reference
    /// avoids rebuilding the same short values between collections without changing Lua liveness.
    /// </summary>
    internal bool TryGetOrCreateIntegerConcat(
        LuaString text,
        long integer,
        bool textFirst,
        out LuaString result)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!ReferenceEquals(text.Owner, _heap) ||
            !text.IsAlive ||
            text.Length > MaximumInternedLength ||
            integer is < MinimumCachedConcatInteger or > MaximumCachedConcatInteger)
        {
            result = null!;
            return false;
        }

        var slot = GetIntegerConcatCacheSlot(text.ObjectId, integer, textFirst);
        ref var cached = ref _integerConcats[slot];
        if (cached.StringObjectId == text.ObjectId &&
            cached.Integer == integer &&
            cached.TextFirst == textFirst &&
            cached.Value is { } cachedValue)
        {
            if (cachedValue.IsAlive)
            {
                result = cachedValue;
                return true;
            }
        }

        Span<byte> integerBytes = stackalloc byte[LuaValueOperations.MaximumFormattedNumberByteCount];
        var integerLength = LuaValueOperations.FormatNumberUtf8(
            LuaValue.FromInteger(integer),
            integerBytes);
        var totalLength = checked(text.Length + integerLength);
        Span<byte> bytes = stackalloc byte[totalLength];
        if (textFirst)
        {
            text.AsSpan().CopyTo(bytes);
            integerBytes[..integerLength].CopyTo(bytes[text.Length..]);
        }
        else
        {
            integerBytes[..integerLength].CopyTo(bytes);
            text.AsSpan().CopyTo(bytes[integerLength..]);
        }

        result = GetOrCreate(bytes);
        cached = new IntegerConcatCacheEntry(
            text.ObjectId,
            integer,
            textFirst,
            result);

        return true;
    }

    private static int GetIntegerConcatCacheSlot(
        long stringObjectId,
        long integer,
        bool textFirst)
    {
        var hash = unchecked(
            (ulong)stringObjectId * 0x9E3779B185EBCA87UL ^
            (ulong)integer * 0xC2B2AE3D27D4EB4FUL ^
            (textFirst ? 0x165667B19E3779F9UL : 0UL));
        return (int)(hash & (IntegerConcatCacheSize - 1));
    }

    internal long OwnerIdentity => _heap.Identity;

    private readonly record struct IntegerConcatCacheEntry(
        long StringObjectId,
        long Integer,
        bool TextFirst,
        LuaString? Value);
}
