using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Tests.Memory;

public sealed class LuaHeapOwnershipTests
{
    [Fact]
    public void AssignsStableIdsAndAccountsLogicalAllocations()
    {
        var state = new LuaState();
        var initialObjects = state.Heap.ObjectCount;
        var initialBytes = state.Heap.LogicalBytes;

        var table = state.CreateTable();
        var text = state.Strings.GetOrCreate("owned"u8);

        Assert.Same(state.Heap, table.Owner);
        Assert.Same(state.Heap, text.Owner);
        Assert.True(text.ObjectId > table.ObjectId);
        Assert.Equal(initialObjects + 2, state.Heap.ObjectCount);
        Assert.True(state.Heap.LogicalBytes > initialBytes);
    }

    [Fact]
    public void RejectsCrossStateValuesAtEveryMutableBoundary()
    {
        var first = new LuaState();
        var second = new LuaState();
        var foreignTable = LuaValue.FromTable(second.CreateTable());
        var localTable = first.CreateTable();

        Assert.Throws<LuaRuntimeException>(() =>
            localTable.Set(LuaValue.FromInteger(1), foreignTable));
        Assert.Throws<LuaRuntimeException>(() =>
            localTable.Set(foreignTable, LuaValue.FromInteger(1)));
        Assert.Throws<LuaRuntimeException>(() =>
            first.MainThread.Stack[0] = foreignTable);
        Assert.Throws<LuaRuntimeException>(() => first.CreateHandle(foreignTable));

        var native = LuaValue.FromFunction(new LuaNativeFunction("shared", static (_, _) => []));
        localTable.Set(LuaValue.FromInteger(1), native);
        Assert.Equal(native, localTable.Get(LuaValue.FromInteger(1)));
    }

    [Fact]
    public void HandleRegistersUpdatesAndReleasesAHostRoot()
    {
        var state = new LuaState();
        var first = LuaValue.FromTable(state.CreateTable());
        var second = LuaValue.FromString(state.Strings.GetOrCreate("value"u8));

        using var handle = state.CreateHandle(first);
        Assert.Equal(1, state.Heap.HandleCount);
        Assert.Equal(first, handle.Value);

        handle.Value = second;
        Assert.Equal(second, handle.Value);
        handle.Dispose();

        Assert.Equal(0, state.Heap.HandleCount);
        Assert.Throws<ObjectDisposedException>(() => _ = handle.Value);
    }

    [Fact]
    public void EnforcesLogicalHeapQuotaBeforeClrAllocationBecomesObservable()
    {
        var options = new LuaStateOptions
        {
            Heap = LuaHeapOptions.Default with { MaximumLogicalBytes = 2_400 },
        };
        var state = new LuaState(options);

        Assert.Throws<LuaRuntimeException>(() =>
            state.Strings.GetOrCreate(new byte[100]));
        Assert.Equal(4, state.Heap.ObjectCount);
        Assert.True(state.MemoryErrorString.IsAlive);
    }

    [Fact]
    public void SwapRemovalKeepsEveryLiveObjectDenseSlotConsistent()
    {
        var state = new LuaState();
        var retained = state.CreateTable();
        using var handle = state.CreateHandle(LuaValue.FromTable(retained));
        var collected = Enumerable.Range(0, 64)
            .Select(_ => state.CreateTable())
            .ToArray();

        state.Heap.CollectFull();

        Assert.All(collected, static value =>
        {
            Assert.False(value.IsAlive);
            Assert.Equal(-1, value.HeapIndex);
        });
        for (var index = 0; index < state.Heap.Objects.Count; index++)
        {
            Assert.Equal(index, state.Heap.Objects[index].HeapIndex);
        }

        Assert.True(retained.IsAlive);
    }
}
