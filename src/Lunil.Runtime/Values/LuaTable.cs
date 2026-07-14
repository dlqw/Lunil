using Lunil.Runtime.Memory;

namespace Lunil.Runtime.Values;

/// <summary>
/// Lua table with a dense array part and an open-addressed hash part. Deleted hash
/// keys remain as tombstones so next(table, deletedCurrentKey) can continue.
/// </summary>
public sealed class LuaTable : LuaGcObject
{
    private const int InitialHashCapacity = 8;
    private const long BucketLogicalSize = 40;

    private readonly List<LuaValue> _array = [];
    private Bucket[] _buckets = [];
    private int _hashCount;
    private int _tombstoneCount;
    private LuaTable? _metatable;

    internal LuaTable(LuaHeap owner, int arrayCapacity = 0, int hashCapacity = 0)
        : base(owner, CalculateLogicalSize(arrayCapacity, hashCapacity))
    {
        if (arrayCapacity != 0)
        {
            _array.AddRange(Enumerable.Repeat(LuaValue.Nil, arrayCapacity));
        }

        if (hashCapacity != 0)
        {
            _buckets = new Bucket[NormalizeHashCapacity(hashCapacity)];
        }
    }

    private static long CalculateLogicalSize(int arrayCapacity, int hashCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(arrayCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(hashCapacity);
        var actualHashCapacity = NormalizeHashCapacity(hashCapacity);
        return checked(64 + arrayCapacity * 16L + actualHashCapacity * BucketLogicalSize);
    }

    private static int NormalizeHashCapacity(int requestedCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestedCapacity);
        if (requestedCapacity == 0)
        {
            return 0;
        }

        if (requestedCapacity > 1 << 30)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedCapacity),
                "Lua table hash capacity exceeds the supported range.");
        }

        var capacity = InitialHashCapacity;
        while (capacity < requestedCapacity)
        {
            capacity *= 2;
        }

        return capacity;
    }

    public int ArrayCapacity => _array.Count;

    public int HashCount => _hashCount;

    public int TombstoneCount => _tombstoneCount;

    public ulong StorageVersion { get; private set; }

    public ulong ShapeVersion { get; private set; }

    public ulong MetatableVersion { get; private set; }

    public LuaTable? Metatable => _metatable;

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
        Owner.ValidateValue(key);
        if (key.IsNil || key.Kind == LuaValueKind.Float && double.IsNaN(key.AsFloat()))
        {
            return LuaValue.Nil;
        }

        if (TryGetArrayIndex(key, out var index) && index <= _array.Count)
        {
            return _array[index - 1];
        }

        var bucket = FindBucket(key);
        return bucket >= 0 && _buckets[bucket].State == BucketState.Occupied
            ? _buckets[bucket].Value
            : LuaValue.Nil;
    }

    public void Set(LuaValue key, LuaValue value)
    {
        ValidateKey(key);
        Owner.ValidateValue(key);
        Owner.ValidateValue(value);
        Owner.WriteBarrierBack(this, key);
        Owner.WriteBarrierBack(this, value);

        if (TryGetArrayIndex(key, out var index))
        {
            if (index <= _array.Count)
            {
                SetArray(index - 1, value);
                return;
            }

            if (index == _array.Count + 1 && !value.IsNil)
            {
                Owner.AdjustLogicalSize(this, 16);
                _array.Add(value);
                IncrementShapeVersion();
                IncrementStorageVersion();
                MigrateArrayTail();
                return;
            }
        }

        SetHash(key, value);
    }

    public void SetMetatable(LuaTable? metatable)
    {
        if (metatable is not null)
        {
            Owner.ValidateValue(LuaValue.FromTable(metatable));
            Owner.WriteBarrierBack(this, LuaValue.FromTable(metatable));
        }

        if (ReferenceEquals(_metatable, metatable))
        {
            return;
        }

        _metatable = metatable;
        unchecked
        {
            MetatableVersion++;
            ShapeVersion++;
        }
    }

    /// <summary>
    /// Implements raw next. A nil key starts traversal; a deleted current hash
    /// key remains valid while its tombstone has not been removed by a rehash.
    /// </summary>
    public bool Next(LuaValue key, out LuaValue nextKey, out LuaValue nextValue)
    {
        Owner.ValidateValue(key);
        var arrayStart = 0;
        var hashStart = 0;
        if (!key.IsNil)
        {
            if (TryGetArrayIndex(key, out var arrayIndex) && arrayIndex <= _array.Count)
            {
                arrayStart = arrayIndex;
            }
            else
            {
                var bucket = FindBucket(key);
                if (bucket < 0)
                {
                    throw new LuaRuntimeException("invalid key to 'next'");
                }

                arrayStart = _array.Count;
                hashStart = bucket + 1;
            }
        }

        for (var index = arrayStart; index < _array.Count; index++)
        {
            if (_array[index].IsNil)
            {
                continue;
            }

            nextKey = LuaValue.FromInteger(index + 1L);
            nextValue = _array[index];
            return true;
        }

        for (var index = hashStart; index < _buckets.Length; index++)
        {
            if (_buckets[index].State != BucketState.Occupied)
            {
                continue;
            }

            nextKey = _buckets[index].Key;
            nextValue = _buckets[index].Value;
            return true;
        }

        nextKey = LuaValue.Nil;
        nextValue = LuaValue.Nil;
        return false;
    }

    internal override void Traverse(LuaGcVisitor visitor)
    {
        var weakMode = GetWeakMode();
        if (_metatable is not null)
        {
            visitor.Visit(_metatable);
        }

        foreach (var value in _array)
        {
            if ((weakMode & LuaWeakMode.Values) == 0)
            {
                visitor.Visit(value);
            }
            else
            {
                VisitNonClearedWeakValue(visitor, value);
            }
        }

        foreach (ref readonly var bucket in _buckets.AsSpan())
        {
            if (bucket.State == BucketState.Occupied)
            {
                if ((weakMode & LuaWeakMode.Keys) == 0)
                {
                    visitor.Visit(bucket.Key);
                }
                else
                {
                    VisitNonClearedWeakValue(visitor, bucket.Key);
                }

                if (weakMode == LuaWeakMode.Keys)
                {
                    VisitNonClearedWeakValue(visitor, bucket.Value);
                }
                else if ((weakMode & LuaWeakMode.Values) == 0)
                {
                    visitor.Visit(bucket.Value);
                }
                else
                {
                    VisitNonClearedWeakValue(visitor, bucket.Value);
                }
            }
        }

        if (weakMode != LuaWeakMode.None)
        {
            visitor.VisitWeakTable(this, weakMode);
        }
    }

    internal bool PropagateEphemerons(LuaGcVisitor visitor)
    {
        var changed = false;
        foreach (ref readonly var bucket in _buckets.AsSpan())
        {
            if (bucket.State != BucketState.Occupied || visitor.IsUnreachable(bucket.Key))
            {
                continue;
            }

            var wasUnreachable = visitor.IsUnreachable(bucket.Value);
            visitor.Visit(bucket.Value);
            changed |= wasUnreachable;
        }

        return changed;
    }

    internal void ClearUnreachableWeakEntries(LuaWeakMode mode, LuaGcVisitor visitor)
    {
        if ((mode & LuaWeakMode.Values) != 0)
        {
            for (var index = 0; index < _array.Count; index++)
            {
                if (visitor.IsUnreachable(_array[index]))
                {
                    _array[index] = LuaValue.Nil;
                    IncrementShapeVersion();
                }
            }
        }

        for (var index = 0; index < _buckets.Length; index++)
        {
            ref var bucket = ref _buckets[index];
            if (bucket.State != BucketState.Occupied)
            {
                continue;
            }

            var clearKey = (mode & LuaWeakMode.Keys) != 0 &&
                visitor.IsUnreachable(bucket.Key);
            var clearValue = (mode & LuaWeakMode.Values) != 0 &&
                visitor.IsUnreachable(bucket.Value);
            if (!clearKey && !clearValue)
            {
                continue;
            }

            bucket.State = BucketState.Tombstone;
            bucket.Value = LuaValue.Nil;
            _hashCount--;
            _tombstoneCount++;
            IncrementShapeVersion();
        }
    }

    internal override bool TryGetFinalizer(out LuaValue finalizer)
    {
        finalizer = _metatable?.GetStringField("__gc"u8) ?? LuaValue.Nil;
        return !finalizer.IsNil;
    }

    private LuaWeakMode GetWeakMode()
    {
        var modeValue = _metatable?.GetStringField("__mode"u8) ?? LuaValue.Nil;
        if (modeValue.Kind != LuaValueKind.String)
        {
            return LuaWeakMode.None;
        }

        var mode = LuaWeakMode.None;
        foreach (var value in modeValue.AsString().AsSpan())
        {
            mode |= value switch
            {
                (byte)'k' => LuaWeakMode.Keys,
                (byte)'v' => LuaWeakMode.Values,
                _ => LuaWeakMode.None,
            };
        }

        return mode;
    }

    internal LuaValue GetStringField(ReadOnlySpan<byte> name)
    {
        foreach (ref readonly var bucket in _buckets.AsSpan())
        {
            if (bucket.State == BucketState.Occupied &&
                bucket.Key.Kind == LuaValueKind.String &&
                bucket.Key.AsString().AsSpan().SequenceEqual(name))
            {
                return bucket.Value;
            }
        }

        return LuaValue.Nil;
    }

    private static void VisitNonClearedWeakValue(LuaGcVisitor visitor, LuaValue value)
    {
        if (value.Kind == LuaValueKind.String)
        {
            visitor.Visit(value);
        }
    }

    private void SetArray(int offset, LuaValue value)
    {
        var wasNil = _array[offset].IsNil;
        if (_array[offset] == value)
        {
            return;
        }

        _array[offset] = value;
        if (wasNil != value.IsNil)
        {
            IncrementShapeVersion();
        }
    }

    private void SetHash(LuaValue key, LuaValue value)
    {
        var existing = FindBucket(key);
        if (existing >= 0)
        {
            ref var bucket = ref _buckets[existing];
            if (value.IsNil)
            {
                if (bucket.State == BucketState.Occupied)
                {
                    bucket.State = BucketState.Tombstone;
                    bucket.Value = LuaValue.Nil;
                    _hashCount--;
                    _tombstoneCount++;
                    IncrementShapeVersion();
                }

                return;
            }

            if (bucket.State == BucketState.Tombstone)
            {
                bucket.State = BucketState.Occupied;
                bucket.Value = value;
                _hashCount++;
                _tombstoneCount--;
                IncrementShapeVersion();
            }
            else
            {
                bucket.Value = value;
            }

            return;
        }

        if (value.IsNil)
        {
            return;
        }

        EnsureHashCapacityForInsert();
        var hash = ComputeHash(key);
        var insertion = FindEmptyBucket(hash);
        _buckets[insertion] = new Bucket(key, value, hash, BucketState.Occupied);
        _hashCount++;
        IncrementShapeVersion();
    }

    private void EnsureHashCapacityForInsert()
    {
        if (_buckets.Length == 0)
        {
            ResizeHash(InitialHashCapacity);
            return;
        }

        if ((_hashCount + _tombstoneCount + 1) * 4 >= _buckets.Length * 3)
        {
            var liveLoadIsHigh = (_hashCount + 1) * 2 >= _buckets.Length;
            ResizeHash(liveLoadIsHigh ? checked(_buckets.Length * 2) : _buckets.Length);
        }
    }

    private void ResizeHash(int capacity)
    {
        var oldBuckets = _buckets;
        var oldBytes = checked(oldBuckets.Length * BucketLogicalSize);
        var newBytes = checked(capacity * BucketLogicalSize);
        Owner.AdjustLogicalSize(this, newBytes - oldBytes);
        _buckets = new Bucket[capacity];
        _hashCount = 0;
        _tombstoneCount = 0;
        foreach (ref readonly var bucket in oldBuckets.AsSpan())
        {
            if (bucket.State != BucketState.Occupied)
            {
                continue;
            }

            var index = FindEmptyBucket(bucket.Hash);
            _buckets[index] = bucket;
            _hashCount++;
        }

        IncrementStorageVersion();
    }

    private void MigrateArrayTail()
    {
        while (true)
        {
            var key = LuaValue.FromInteger(_array.Count + 1L);
            var bucketIndex = FindBucket(key);
            if (bucketIndex < 0 || _buckets[bucketIndex].State != BucketState.Occupied)
            {
                return;
            }

            ref var bucket = ref _buckets[bucketIndex];
            Owner.AdjustLogicalSize(this, 16);
            _array.Add(bucket.Value);
            bucket.State = BucketState.Tombstone;
            bucket.Value = LuaValue.Nil;
            _hashCount--;
            _tombstoneCount++;
            IncrementStorageVersion();
        }
    }

    private int FindBucket(LuaValue key)
    {
        if (_buckets.Length == 0)
        {
            return -1;
        }

        var hash = ComputeHash(key);
        var mask = _buckets.Length - 1;
        var index = hash & mask;
        for (var probe = 0; probe < _buckets.Length; probe++)
        {
            ref readonly var bucket = ref _buckets[index];
            if (bucket.State == BucketState.Empty)
            {
                return -1;
            }

            if (bucket.Hash == hash && KeysMatch(bucket, key))
            {
                return index;
            }

            index = (index + 1) & mask;
        }

        return -1;
    }

    private int FindEmptyBucket(int hash)
    {
        var mask = _buckets.Length - 1;
        var index = hash & mask;
        for (var probe = 0; probe < _buckets.Length; probe++)
        {
            if (_buckets[index].State == BucketState.Empty)
            {
                return index;
            }

            index = (index + 1) & mask;
        }

        throw new InvalidOperationException("Hash table has no empty bucket.");
    }

    private int ComputeHash(LuaValue key)
    {
        var hash = unchecked((uint)(key.GetHashCode() ^ Owner.HashSeed));
        hash ^= hash >> 16;
        hash *= 0x7feb352d;
        hash ^= hash >> 15;
        hash *= 0x846ca68b;
        hash ^= hash >> 16;
        return unchecked((int)hash);
    }

    private static bool KeysMatch(in Bucket bucket, LuaValue key)
    {
        if (bucket.State == BucketState.Tombstone &&
            bucket.Key.TryGetGcObject() is { } deletedObject)
        {
            return ReferenceEquals(deletedObject, key.TryGetGcObject());
        }

        return bucket.Key == key;
    }

    private void IncrementStorageVersion() => StorageVersion = unchecked(StorageVersion + 1);

    private void IncrementShapeVersion() => ShapeVersion = unchecked(ShapeVersion + 1);

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

    private enum BucketState : byte
    {
        Empty,
        Occupied,
        Tombstone,
    }

    private struct Bucket
    {
        public Bucket(LuaValue key, LuaValue value, int hash, BucketState state)
        {
            Key = key;
            Value = value;
            Hash = hash;
            State = state;
        }

        public LuaValue Key;

        public LuaValue Value;

        public int Hash;

        public BucketState State;
    }
}
