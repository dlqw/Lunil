using System.Buffers;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Operations;

namespace Lunil.Runtime.Values;

/// <summary>
/// Lua table with a dense array part and an open-addressed hash part. Deleted hash
/// keys remain as tombstones so next(table, deletedCurrentKey) can continue.
/// </summary>
public sealed class LuaTable : LuaGcObject
{
    private const int InitialHashCapacity = 8;
    private const long BucketLogicalSize = 40;

    private PooledArrayPart _array;
    private readonly LuaTableAllocationHint? _allocationHint;
    private Bucket[] _buckets = [];
    private int _hashCount;
    private int _tombstoneCount;
    private LuaTable? _metatable;
    private long _absentMetamethodMask;
    private long _contentVersion;

    internal LuaTable(
        LuaHeap owner,
        int arrayCapacity = 0,
        int hashCapacity = 0,
        int physicalArrayCapacity = 0,
        LuaTableAllocationHint? allocationHint = null)
        : base(owner, CalculateLogicalSize(arrayCapacity, hashCapacity))
    {
        ArgumentOutOfRangeException.ThrowIfNegative(physicalArrayCapacity);
        _array = new PooledArrayPart(
            arrayCapacity,
            Math.Max(arrayCapacity, physicalArrayCapacity));
        _allocationHint = allocationHint;

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

    internal int ArrayStorageCapacity => _array.Capacity;

    public int HashCount => _hashCount;

    public int TombstoneCount => _tombstoneCount;

    public ulong StorageVersion { get; private set; }

    public ulong ShapeVersion { get; private set; }

    public ulong MetatableVersion { get; private set; }

    /// <summary>
    /// Monotonic version of the table's logical key/value content. This is an internal
    /// code-generation contract used to invalidate metatable caches without exposing the
    /// hash buckets or their storage layout.
    /// </summary>
    internal ulong ContentVersion => unchecked((ulong)Volatile.Read(ref _contentVersion));

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
        return TryGetExistingEntry(key, out var value, out _) ? value : LuaValue.Nil;
    }

    /// <summary>
    /// Reads an index already known to address the dense array part without materializing or
    /// validating a generic Lua key. Callers must perform any surrounding metatable checks.
    /// </summary>
    internal bool TryGetArrayValue(long index, out LuaValue value)
    {
        var offset = index - 1;
        if ((ulong)offset >= (ulong)_array.Count)
        {
            value = LuaValue.Nil;
            return false;
        }

        value = _array[(int)offset];
        return true;
    }

    /// <summary>
    /// Updates an index already known to address the allocated dense array part. The index
    /// check deliberately precedes owner validation so a caller can cheaply fall through to
    /// append or hash handling without validating the value twice.
    /// </summary>
    internal bool TrySetArrayValue(long index, LuaValue value)
    {
        var offset = index - 1;
        if (_metatable is not null || (ulong)offset >= (ulong)_array.Count)
        {
            return false;
        }

        // This path is restricted to ordinary strong tables, so a forward barrier can mark
        // the inserted child directly instead of re-graying and rescanning the entire dense
        // array at the next incremental GC step.
        Owner.WriteBarrier(this, value);
        SetArray((int)offset, value);
        return true;
    }

    /// <summary>
    /// Updates or appends one dense integer slot for an ordinary strong table. Keeping both
    /// cases in one helper avoids a failed update probe before every sequential append.
    /// </summary>
    internal bool TrySetOrAppendArrayValue(long index, LuaValue value)
    {
        if (_metatable is not null)
        {
            return false;
        }

        var offset = index - 1;
        if ((ulong)offset < (ulong)_array.Count)
        {
            Owner.WriteBarrier(this, value);
            SetArray((int)offset, value);
            return true;
        }

        if (offset != _array.Count || value.IsNil)
        {
            return false;
        }

        Owner.WriteBarrier(this, value);
        Owner.AdjustLogicalSize(this, 16);
        EnsureArrayAppendCapacity();
        _array.Add(value);
        IncrementShapeVersion();
        IncrementContentVersion();
        IncrementStorageVersion();
        MigrateArrayTail();
        return true;
    }

    /// <summary>
    /// Performs one raw lookup and, when the key is present, returns an opaque handle that can
    /// update that exact entry without probing the table again. The handle never exposes array
    /// or hash storage to code-generation consumers and is valid only until the next table
    /// mutation.
    /// </summary>
    internal bool TryGetExistingEntry(
        LuaValue key,
        out LuaValue value,
        out LuaTableExistingEntry entry)
    {
        Owner.ValidateValue(key);
        if (key.IsNil || key.Kind == LuaValueKind.Float && double.IsNaN(key.AsFloat()))
        {
            value = LuaValue.Nil;
            entry = default;
            return false;
        }

        if (TryGetArrayIndex(key, out var arrayIndex) && arrayIndex <= _array.Count)
        {
            value = _array[arrayIndex - 1];
            if (!value.IsNil)
            {
                entry = LuaTableExistingEntry.Array(arrayIndex - 1);
                return true;
            }

            entry = default;
            return false;
        }

        var bucketIndex = FindBucket(key);
        if (bucketIndex >= 0 && _buckets[bucketIndex].State == BucketState.Occupied)
        {
            value = _buckets[bucketIndex].Value;
            entry = LuaTableExistingEntry.Hash(bucketIndex);
            return true;
        }

        value = LuaValue.Nil;
        entry = default;
        return false;
    }

    /// <summary>Updates an entry returned by the immediately preceding raw lookup.</summary>
    internal void SetExistingEntry(
        LuaTableExistingEntry entry,
        LuaValue key,
        LuaValue value)
    {
        ValidateKey(key);
        Owner.ValidateValue(key);
        Owner.ValidateValue(value);
        Owner.WriteBarrierBack(this, key);
        Owner.WriteBarrierBack(this, value);

        if (entry.IsArray)
        {
            if ((uint)entry.Index >= (uint)_array.Count ||
                _array[entry.Index].IsNil ||
                !TryGetArrayIndex(key, out var arrayIndex) ||
                arrayIndex - 1 != entry.Index)
            {
                throw new InvalidOperationException("A stale Lua table entry handle was used.");
            }

            SetArray(entry.Index, value);
            return;
        }

        if ((uint)entry.Index >= (uint)_buckets.Length)
        {
            throw new InvalidOperationException("A stale Lua table entry handle was used.");
        }

        ref var bucket = ref _buckets[entry.Index];
        if (bucket.State != BucketState.Occupied || !KeysMatch(bucket, key))
        {
            throw new InvalidOperationException("A stale Lua table entry handle was used.");
        }

        if (value.IsNil)
        {
            bucket.MakeTombstone();
            _hashCount--;
            _tombstoneCount++;
            IncrementShapeVersion();
            IncrementContentVersion();
        }
        else if (bucket.Value != value)
        {
            bucket.Value = value;
            IncrementContentVersion();
        }
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
                EnsureArrayAppendCapacity();
                _array.Add(value);
                IncrementShapeVersion();
                IncrementContentVersion();
                IncrementStorageVersion();
                MigrateArrayTail();
                return;
            }
        }

        SetHash(key, value);
    }

    /// <summary>
    /// Appends a non-nil value to the next dense array slot without routing an already
    /// normalized integer key through the general table-key machinery. This is an internal
    /// runtime fast path; callers must retain the normal metatable checks around it.
    /// </summary>
    internal bool TryAppendArray(long index, LuaValue value)
    {
        if (_metatable is not null || value.IsNil || index != _array.Count + 1L)
        {
            return false;
        }

        // See TrySetArrayValue: metatable-backed (and therefore potentially weak) tables
        // fall through to Set, while this strong-table path can use a forward barrier.
        Owner.WriteBarrier(this, value);
        Owner.AdjustLogicalSize(this, 16);
        EnsureArrayAppendCapacity();
        _array.Add(value);
        IncrementShapeVersion();
        IncrementContentVersion();
        IncrementStorageVersion();
        MigrateArrayTail();
        return true;
    }

    private void EnsureArrayAppendCapacity()
    {
        if (_array.Count < _array.Capacity)
        {
            return;
        }

        const int widerGrowthLimit = 512;
        var capacity = _array.Count == 0
            ? 8
            : _array.Count <= widerGrowthLimit
                ? checked(_array.Count * 4)
                : checked(_array.Count * 2);
        _array.EnsureCapacity(capacity);
        _allocationHint?.ObserveArrayCapacity(_array.Capacity);
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

        for (var index = 0; index < _array.Count; index++)
        {
            var value = _array[index];
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

    internal override void OnCollected() => _array.Dispose();

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
                    IncrementContentVersion();
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

            bucket.MakeTombstone();
            _hashCount--;
            _tombstoneCount++;
            IncrementShapeVersion();
            IncrementContentVersion();
        }
    }

    internal override bool TryGetFinalizer(out LuaValue finalizer)
    {
        finalizer = _metatable?.GetMetamethodField(LuaMetamethod.GarbageCollect) ?? LuaValue.Nil;
        return !finalizer.IsNil;
    }

    private LuaWeakMode GetWeakMode()
    {
        var modeValue = _metatable?.GetMetamethodField(LuaMetamethod.Mode) ?? LuaValue.Nil;
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
        if (_buckets.Length == 0)
        {
            return LuaValue.Nil;
        }

        var hash = MixHash(LuaString.ComputeHashCode(name));
        var mask = _buckets.Length - 1;
        var index = hash & mask;
        for (var probe = 0; probe < _buckets.Length; probe++)
        {
            ref readonly var bucket = ref _buckets[index];
            if (bucket.State == BucketState.Empty)
            {
                return LuaValue.Nil;
            }

            if (bucket.State == BucketState.Occupied &&
                bucket.Hash == hash &&
                bucket.Key.Kind == LuaValueKind.String &&
                bucket.Key.AsString().AsSpan().SequenceEqual(name))
            {
                return bucket.Value;
            }

            index = (index + 1) & mask;
        }

        return LuaValue.Nil;
    }

    internal LuaValue GetMetamethodField(LuaMetamethod metamethod)
    {
        var bit = 1L << (int)metamethod;
        if ((Volatile.Read(ref _absentMetamethodMask) & bit) != 0)
        {
            return LuaValue.Nil;
        }

        var value = GetStringField(LuaMetamethodFacts.GetName(metamethod));
        if (value.IsNil)
        {
            Interlocked.Or(ref _absentMetamethodMask, bit);
        }

        return value;
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
        IncrementContentVersion();
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
                    bucket.MakeTombstone();
                    _hashCount--;
                    _tombstoneCount++;
                    IncrementShapeVersion();
                    IncrementContentVersion();
                }

                return;
            }

            if (bucket.State == BucketState.Tombstone)
            {
                bucket.Reactivate(key, value);
                _hashCount++;
                _tombstoneCount--;
                IncrementShapeVersion();
                IncrementContentVersion();
            }
            else
            {
                if (bucket.Value == value)
                {
                    return;
                }

                bucket.Value = value;
                IncrementContentVersion();
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
        IncrementContentVersion();
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
            bucket.MakeTombstone();
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

    private int ComputeHash(LuaValue key) => MixHash(key.GetHashCode());

    private int MixHash(int keyHash)
    {
        var hash = unchecked((uint)(keyHash ^ Owner.HashSeed));
        hash ^= hash >> 16;
        hash *= 0x7feb352d;
        hash ^= hash >> 15;
        hash *= 0x846ca68b;
        hash ^= hash >> 16;
        return unchecked((int)hash);
    }

    private static bool KeysMatch(in Bucket bucket, LuaValue key)
    {
        if (bucket.State == BucketState.Tombstone && bucket.TombstoneObjectId != 0)
        {
            return key.TryGetGcObject()?.ObjectId == bucket.TombstoneObjectId;
        }

        return bucket.Key == key;
    }

    private void IncrementStorageVersion() => StorageVersion = unchecked(StorageVersion + 1);

    private void IncrementShapeVersion() => ShapeVersion = unchecked(ShapeVersion + 1);

    private void IncrementContentVersion()
    {
        Interlocked.Increment(ref _contentVersion);
        Volatile.Write(ref _absentMetamethodMask, 0);
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

    private enum BucketState : byte
    {
        Empty,
        Occupied,
        Tombstone,
    }

    private struct PooledArrayPart
    {
        private LuaValue[] _items;

        public PooledArrayPart(int count, int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, count);
            Count = count;
            if (capacity == 0)
            {
                _items = [];
                return;
            }

            _items = ArrayPool<LuaValue>.Shared.Rent(capacity);
            _items.AsSpan(0, count).Clear();
        }

        public int Count { get; private set; }

        public int Capacity => _items.Length;

        public LuaValue this[int index]
        {
            get => _items[index];
            set => _items[index] = value;
        }

        public void Add(LuaValue value)
        {
            if (Count == _items.Length)
            {
                EnsureCapacity(Count == 0 ? 8 : checked(Count * 2));
            }

            _items[Count++] = value;
        }

        public void EnsureCapacity(int capacity)
        {
            if (capacity <= _items.Length)
            {
                return;
            }

            var replacement = ArrayPool<LuaValue>.Shared.Rent(capacity);
            _items.AsSpan(0, Count).CopyTo(replacement);
            ReturnItems();
            _items = replacement;
        }

        public void Dispose()
        {
            ReturnItems();
            _items = [];
            Count = 0;
        }

        private void ReturnItems()
        {
            if (_items.Length != 0)
            {
                ArrayPool<LuaValue>.Shared.Return(_items, clearArray: true);
            }
        }
    }

    private struct Bucket
    {
        public Bucket(LuaValue key, LuaValue value, int hash, BucketState state)
        {
            Key = key;
            Value = value;
            Hash = hash;
            State = state;
            TombstoneObjectId = 0;
        }

        public void MakeTombstone()
        {
            TombstoneObjectId = Key.TryGetGcObject()?.ObjectId ?? 0;
            if (TombstoneObjectId != 0)
            {
                Key = LuaValue.Nil;
            }

            Value = LuaValue.Nil;
            State = BucketState.Tombstone;
        }

        public void Reactivate(LuaValue key, LuaValue value)
        {
            Key = key;
            Value = value;
            TombstoneObjectId = 0;
            State = BucketState.Occupied;
        }

        public LuaValue Key;

        public LuaValue Value;

        public int Hash;

        public BucketState State;

        public long TombstoneObjectId;
    }
}

internal sealed class LuaTableAllocationHint
{
    private const int MaximumArrayCapacity = 4096;
    private int _arrayCapacity;

    public int ArrayCapacity => Volatile.Read(ref _arrayCapacity);

    public void ObserveArrayCapacity(int capacity)
    {
        capacity = Math.Min(capacity, MaximumArrayCapacity);
        var observed = Volatile.Read(ref _arrayCapacity);
        while (capacity > observed)
        {
            var previous = Interlocked.CompareExchange(
                ref _arrayCapacity,
                capacity,
                observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }
}

/// <summary>
/// Opaque, mutation-scoped handle for a raw Lua table entry. Its representation remains an
/// implementation detail of <see cref="LuaTable"/> and is never part of the code-generation ABI.
/// </summary>
internal readonly struct LuaTableExistingEntry
{
    private LuaTableExistingEntry(int index, bool isArray)
    {
        Index = index;
        IsArray = isArray;
    }

    internal int Index { get; }

    internal bool IsArray { get; }

    internal static LuaTableExistingEntry Array(int index) => new(index, isArray: true);

    internal static LuaTableExistingEntry Hash(int index) => new(index, isArray: false);
}
