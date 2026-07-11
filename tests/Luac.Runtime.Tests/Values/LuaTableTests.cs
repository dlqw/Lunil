using Luac.Runtime.Memory;
using Luac.Runtime.Values;

namespace Luac.Runtime.Tests.Values;

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

    private static LuaState CreateDeterministicState() => new(new LuaStateOptions
    {
        Heap = LuaHeapOptions.Default with { HashSeed = 12345 },
    });

    private static LuaValue String(LuaState state, string value) =>
        LuaValue.FromString(state.Strings.GetOrCreate(System.Text.Encoding.UTF8.GetBytes(value)));
}
