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
            Heap = LuaHeapOptions.Default with { MaximumLogicalBytes = 2_300 },
        };
        var state = new LuaState(options);

        Assert.Throws<LuaRuntimeException>(() =>
            state.Strings.GetOrCreate(new byte[100]));
        Assert.Equal(2, state.Heap.ObjectCount);
    }
}
