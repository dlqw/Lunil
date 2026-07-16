using System.Runtime.CompilerServices;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Operations;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Tests.Values;

public sealed class LuaTableTests
{
    [Fact]
    public void RehashesOpenAddressedBucketsWithoutLosingKeys()
    {
        var state = CreateDeterministicState();
        var table = state.CreateTable();
        var initialBytes = table.LogicalSize;

        for (var index = 0; index < 1_000; index++)
        {
            table.Set(String(state, $"key-{index}"), LuaValue.FromInteger(index));
        }

        for (var index = 0; index < 1_000; index++)
        {
            Assert.Equal(LuaValue.FromInteger(index), table.Get(String(state, $"key-{index}")));
        }

        Assert.Equal(1_000, table.HashCount);
        Assert.True(table.StorageVersion > 1);
        Assert.True(table.LogicalSize > initialBytes);
    }

    [Fact]
    public void KeepsDeletedKeysAsNextContinuationTombstones()
    {
        var state = CreateDeterministicState();
        var table = state.CreateTable();
        for (var index = 1; index <= 5; index++)
        {
            table.Set(LuaValue.FromInteger(index), LuaValue.FromInteger(index * 10));
            table.Set(String(state, $"hash-{index}"), LuaValue.FromInteger(index));
        }

        var current = LuaValue.Nil;
        var visited = 0;
        while (table.Next(current, out var key, out _))
        {
            table.Set(key, LuaValue.Nil);
            current = key;
            visited++;
        }

        Assert.Equal(10, visited);
        Assert.Equal(0, table.HashCount);
        Assert.Equal(5, table.TombstoneCount);
        Assert.False(table.Next(LuaValue.Nil, out _, out _));
        Assert.Throws<LuaRuntimeException>(() =>
            table.Next(String(state, "not-present"), out _, out _));
    }

    [Fact]
    public void ReactivatesADeletedKeyAndMigratesContiguousIntegers()
    {
        var state = CreateDeterministicState();
        var table = state.CreateTable();
        var key = String(state, "key");
        table.Set(key, LuaValue.FromInteger(1));
        table.Set(key, LuaValue.Nil);
        table.Set(key, LuaValue.FromInteger(2));

        table.Set(LuaValue.FromInteger(3), LuaValue.FromInteger(30));
        table.Set(LuaValue.FromInteger(2), LuaValue.FromInteger(20));
        table.Set(LuaValue.FromInteger(1), LuaValue.FromInteger(10));

        Assert.Equal(LuaValue.FromInteger(2), table.Get(key));
        Assert.Equal(3, table.ArrayCapacity);
        Assert.Equal(LuaValue.FromInteger(30), table.Get(LuaValue.FromFloat(3.0)));
    }

    [Fact]
    public void SequentialArrayAppendFastPathPreservesTableAndGcContracts()
    {
        var state = CreateDeterministicState();
        var table = state.CreateTable();
        var value = state.CreateTable();
        var initialLogicalSize = table.LogicalSize;
        var initialShapeVersion = table.ShapeVersion;
        var initialContentVersion = table.ContentVersion;
        var initialStorageVersion = table.StorageVersion;

        Assert.False(table.TryAppendArray(2, LuaValue.FromInteger(20)));
        Assert.False(table.TryAppendArray(1, LuaValue.Nil));
        Assert.Equal(initialLogicalSize, table.LogicalSize);
        Assert.Equal(initialShapeVersion, table.ShapeVersion);
        Assert.Equal(initialContentVersion, table.ContentVersion);
        Assert.Equal(initialStorageVersion, table.StorageVersion);

        Assert.True(table.TryAppendArray(1, LuaValue.FromTable(value)));
        Assert.Equal(initialLogicalSize + 16, table.LogicalSize);
        Assert.True(table.ShapeVersion > initialShapeVersion);
        Assert.True(table.ContentVersion > initialContentVersion);
        Assert.True(table.StorageVersion > initialStorageVersion);
        Assert.Equal(LuaValue.FromTable(value), table.Get(LuaValue.FromInteger(1)));

        state.SetGlobal("table", LuaValue.FromTable(table));
        state.Heap.CollectFull();
        Assert.True(value.IsAlive);

        var foreignState = CreateDeterministicState();
        Assert.Throws<LuaRuntimeException>(() =>
            table.TryAppendArray(2, LuaValue.FromTable(foreignState.CreateTable())));
    }

    [Fact]
    public void ExistingArrayUpdateFastPathPreservesMutationAndOwnerContracts()
    {
        var state = CreateDeterministicState();
        var table = state.CreateTable();
        Assert.True(table.TryAppendArray(1, LuaValue.FromInteger(10)));
        var initialShapeVersion = table.ShapeVersion;
        var initialContentVersion = table.ContentVersion;

        Assert.True(table.TrySetArrayValue(1, LuaValue.FromInteger(20)));
        Assert.Equal(LuaValue.FromInteger(20), table.Get(LuaValue.FromInteger(1)));
        Assert.Equal(initialShapeVersion, table.ShapeVersion);
        Assert.True(table.ContentVersion > initialContentVersion);

        var foreignState = CreateDeterministicState();
        var foreign = LuaValue.FromTable(foreignState.CreateTable());
        Assert.Throws<LuaRuntimeException>(() => table.TrySetArrayValue(1, foreign));
        Assert.False(table.TrySetArrayValue(2, foreign));
        Assert.Equal(1, table.ArrayCapacity);

        Assert.True(table.TrySetArrayValue(1, LuaValue.Nil));
        Assert.True(table.Get(LuaValue.FromInteger(1)).IsNil);
        Assert.True(table.ShapeVersion > initialShapeVersion);
    }

    [Fact]
    public void DenseArrayFastPathsRejectMetatableBackedTables()
    {
        var state = CreateDeterministicState();
        var table = state.CreateTable();
        Assert.True(table.TryAppendArray(1, LuaValue.FromInteger(10)));
        table.SetMetatable(state.CreateTable());

        Assert.False(table.TrySetArrayValue(1, LuaValue.FromInteger(20)));
        Assert.False(table.TryAppendArray(2, LuaValue.FromInteger(30)));
        Assert.False(table.TrySetOrAppendArrayValue(1, LuaValue.FromInteger(40)));
        Assert.Equal(LuaValue.FromInteger(10), table.Get(LuaValue.FromInteger(1)));
        Assert.True(table.Get(LuaValue.FromInteger(2)).IsNil);
    }

    [Fact]
    public void CombinedDenseArrayFastPathUpdatesOrAppendsWithOneProbe()
    {
        var state = CreateDeterministicState();
        var table = state.CreateTable();

        Assert.True(table.TrySetOrAppendArrayValue(1, LuaValue.FromInteger(10)));
        Assert.True(table.TrySetOrAppendArrayValue(1, LuaValue.FromInteger(20)));
        Assert.False(table.TrySetOrAppendArrayValue(3, LuaValue.FromInteger(30)));
        Assert.Equal(LuaValue.FromInteger(20), table.Get(LuaValue.FromInteger(1)));

        var foreignState = CreateDeterministicState();
        Assert.Throws<LuaRuntimeException>(() => table.TrySetOrAppendArrayValue(
            2,
            LuaValue.FromTable(foreignState.CreateTable())));
    }

    [Fact]
    public void SequentialArrayAppendFastPathMigratesTheContiguousHashTail()
    {
        var state = CreateDeterministicState();
        var table = state.CreateTable();
        table.Set(LuaValue.FromInteger(3), LuaValue.FromInteger(30));
        table.Set(LuaValue.FromInteger(2), LuaValue.FromInteger(20));

        Assert.True(table.TryAppendArray(1, LuaValue.FromInteger(10)));

        Assert.Equal(3, table.ArrayCapacity);
        Assert.Equal(0, table.HashCount);
        Assert.Equal(LuaValue.FromInteger(10), table.Get(LuaValue.FromInteger(1)));
        Assert.Equal(LuaValue.FromInteger(20), table.Get(LuaValue.FromInteger(2)));
        Assert.Equal(LuaValue.FromInteger(30), table.Get(LuaValue.FromInteger(3)));
    }

    [Fact]
    public void RawArrayReadDistinguishesDenseSlotsFromHashAndOutOfRangeIndices()
    {
        var state = CreateDeterministicState();
        var table = state.CreateTable();
        table.Set(LuaValue.FromInteger(1), LuaValue.FromInteger(10));
        table.Set(LuaValue.FromInteger(3), LuaValue.FromInteger(30));

        Assert.True(table.TryGetArrayValue(1, out var first));
        Assert.Equal(LuaValue.FromInteger(10), first);
        Assert.False(table.TryGetArrayValue(0, out var zero));
        Assert.True(zero.IsNil);
        Assert.False(table.TryGetArrayValue(3, out var hashValue));
        Assert.True(hashValue.IsNil);
        Assert.False(table.TryGetArrayValue(long.MaxValue, out var high));
        Assert.True(high.IsNil);
    }

    [Fact]
    public void AllocationSiteHintReusesOnlyPhysicalArrayCapacity()
    {
        var state = CreateDeterministicState();
        var hint = new LuaTableAllocationHint();
        var previous = new LuaTable(state.Heap, allocationHint: hint);
        for (var index = 1; index <= 1_000; index++)
        {
            previous.Set(LuaValue.FromInteger(index), LuaValue.FromInteger(index));
        }

        Assert.True(hint.ArrayCapacity >= 1_000);
        var logicalBytes = state.Heap.LogicalBytes;
        var replacement = state.CreateTableForAllocationSite(0, 0, hint);

        Assert.Equal(0, replacement.ArrayCapacity);
        Assert.True(replacement.ArrayStorageCapacity >= 1_000);
        Assert.Equal(logicalBytes + 64, state.Heap.LogicalBytes);
        Assert.True(replacement.TryAppendArray(1, LuaValue.FromInteger(1)));
        Assert.Equal(1, replacement.ArrayCapacity);
    }

    [Fact]
    public void SeparatesShapeStorageAndMetatableVersions()
    {
        var state = CreateDeterministicState();
        var table = state.CreateTable();
        var key = String(state, "field");
        table.Set(key, LuaValue.FromInteger(1));
        var shapeAfterAdd = table.ShapeVersion;
        var storageAfterAdd = table.StorageVersion;

        table.Set(key, LuaValue.FromInteger(2));
        Assert.Equal(shapeAfterAdd, table.ShapeVersion);
        Assert.Equal(storageAfterAdd, table.StorageVersion);

        var metatable = state.CreateTable();
        table.SetMetatable(metatable);
        Assert.Equal<ulong>(1, table.MetatableVersion);
        Assert.True(table.ShapeVersion > shapeAfterAdd);
    }

    [Fact]
    public void StringFieldUsesTheHashProbeAcrossTombstonesAndRehashes()
    {
        var state = CreateDeterministicState();
        var table = state.CreateTable();
        for (var index = 0; index < 512; index++)
        {
            table.Set(String(state, $"field-{index}"), LuaValue.FromInteger(index));
        }

        for (var index = 0; index < 256; index++)
        {
            table.Set(String(state, $"field-{index}"), LuaValue.Nil);
        }

        for (var index = 256; index < 512; index++)
        {
            Assert.Equal(
                LuaValue.FromInteger(index),
                table.GetStringField(System.Text.Encoding.UTF8.GetBytes($"field-{index}")));
        }

        Assert.True(table.GetStringField("missing"u8).IsNil);
    }

    [Fact]
    public void AbsentMetamethodCacheInvalidatesOnLogicalContentMutation()
    {
        var state = CreateDeterministicState();
        var metatable = state.CreateTable();

        Assert.True(metatable.GetMetamethodField(LuaMetamethod.Index).IsNil);
        Assert.True(metatable.GetMetamethodField(LuaMetamethod.Index).IsNil);
        var before = metatable.ContentVersion;

        var indexValue = LuaValue.FromInteger(42);
        metatable.Set(String(state, "__index"), indexValue);

        Assert.True(metatable.ContentVersion > before);
        Assert.Equal(indexValue, metatable.GetMetamethodField(LuaMetamethod.Index));
        var after = metatable.ContentVersion;
        metatable.Set(String(state, "__index"), indexValue);
        Assert.Equal(after, metatable.ContentVersion);

        metatable.Set(String(state, "__index"), LuaValue.Nil);
        Assert.True(metatable.GetMetamethodField(LuaMetamethod.Index).IsNil);
    }

    [Fact]
    public void TombstoneDoesNotKeepADeletedCollectableKeyAlive()
    {
        var state = CreateDeterministicState();
        var table = state.CreateTable();
        state.SetGlobal("table", LuaValue.FromTable(table));
        var key = state.CreateTable();
        table.Set(LuaValue.FromTable(key), LuaValue.FromInteger(1));
        table.Set(LuaValue.FromTable(key), LuaValue.Nil);

        state.Heap.CollectFull();

        Assert.False(key.IsAlive);
        Assert.Equal(1, table.TombstoneCount);
    }

    [Fact]
    public void TombstoneReleasesDeletedCollectableKeyToTheManagedCollector()
    {
        var state = CreateDeterministicState();
        var table = state.CreateTable();
        state.SetGlobal("table", LuaValue.FromTable(table));
        var weakKey = CreateDeletedKeyWeakReference(state, table);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            ForceManagedCollection();
        }

        Assert.False(weakKey.TryGetTarget(out _));
        Assert.Equal(1, table.TombstoneCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<LuaTable> CreateDeletedKeyWeakReference(
        LuaState state,
        LuaTable table)
    {
        var key = state.CreateTable();
        var weakKey = new WeakReference<LuaTable>(key);
        table.Set(LuaValue.FromTable(key), LuaValue.FromInteger(1));
        table.Set(LuaValue.FromTable(key), LuaValue.Nil);
        state.Heap.CollectFull();
        Assert.False(key.IsAlive);
        return weakKey;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceManagedCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static LuaState CreateDeterministicState() => new(new LuaStateOptions
    {
        Heap = LuaHeapOptions.Default with { HashSeed = 12345 },
    });

    private static LuaValue String(LuaState state, string value) =>
        LuaValue.FromString(state.Strings.GetOrCreate(System.Text.Encoding.UTF8.GetBytes(value)));
}
