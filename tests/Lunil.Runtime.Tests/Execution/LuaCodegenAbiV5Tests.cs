using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Tests.Execution;

public sealed class LuaCodegenAbiV5Tests
{
    [Fact]
    public void RuntimeAbiV5KeepsEarlierContractsVersioned()
    {
        Assert.Equal(1, LuaCodegenAbiV1.RuntimeAbiVersion);
        Assert.Equal(2, LuaCodegenAbiV2.RuntimeAbiVersion);
        Assert.Equal(3, LuaCodegenAbiV3.RuntimeAbiVersion);
        Assert.Equal(4, LuaCodegenAbiV4.RuntimeAbiVersion);
        Assert.Equal(5, LuaCodegenAbiV5.RuntimeAbiVersion);
    }

    [Fact]
    public void NumericRegionIntegerFastPathPreservesDenseAndMetatableSemantics()
    {
        var state = new LuaState();
        var table = state.CreateTable();
        LuaTable? cachedTable = null;
        var target = LuaValue.FromTable(table);
        var cache = new LuaCodegenTableSiteCache();

        Assert.True(LuaCodegenAbiV5.TrySetCompilerProvenIntegerTableIntegerValue(
            ref cachedTable,
            target,
            cache,
            1,
            10));
        Assert.True(LuaCodegenAbiV5.TrySetCompilerProvenIntegerTableBooleanValue(
            ref cachedTable,
            target,
            cache,
            2,
            true));
        Assert.True(LuaCodegenAbiV5.TryGetCompilerProvenIntegerTableValue(
            ref cachedTable,
            target,
            cache,
            1,
            out var first));
        Assert.Equal(LuaValue.FromInteger(10), first);

        var metatable = state.CreateTable();
        metatable.Set(String(state, "__index"), LuaValue.FromInteger(42));
        table.SetMetatable(metatable);
        Assert.False(LuaCodegenAbiV5.TryGetCompilerProvenIntegerTableValue(
            ref cachedTable,
            target,
            cache,
            3,
            out _));
        Assert.False(LuaCodegenAbiV5.TrySetCompilerProvenIntegerTableIntegerValue(
            ref cachedTable,
            target,
            cache,
            3,
            30));
        Assert.False(LuaCodegenAbiV5.TrySetBoundIntegerTableBooleanValue(table, 3, true));

        table.SetMetatable(null);
        Assert.True(LuaCodegenAbiV5.TrySetBoundIntegerTableFloatValue(table, 3, 1.5));
        Assert.Equal(LuaValue.FromFloat(1.5), table.Get(LuaValue.FromInteger(3)));
        Assert.True(LuaCodegenAbiV5.TrySetBoundIntegerTableIntegerValue(table, 3, 4));
        Assert.Equal(LuaValue.FromInteger(4), table.Get(LuaValue.FromInteger(3)));
    }

    [Fact]
    public void NumericRegionStringHandleFailsClosedAcrossMutationAndMetatableChanges()
    {
        var state = new LuaState();
        var table = state.CreateTable();
        var key = String(state, "field");
        var target = LuaValue.FromTable(table);
        var cache = new LuaCodegenTableSiteCache();
        LuaTable? cachedTable = null;
        var regionSite = new LuaCodegenTableRegionSite();

        Assert.True(LuaCodegenAbiV5.TrySetCompilerProvenStringTableIntegerValue(
            ref cachedTable,
            target,
            cache,
            ref regionSite,
            key,
            1));
        Assert.True(LuaCodegenAbiV5.TryGetCompilerProvenStringTableValue(
            ref cachedTable,
            target,
            cache,
            ref regionSite,
            key,
            out var value));
        Assert.Equal(LuaValue.FromInteger(1), value);

        Assert.True(LuaCodegenAbiV5.TrySetBoundStringTableFloatValue(
            table,
            ref regionSite,
            2.5));
        Assert.True(LuaCodegenAbiV5.TryGetCompilerProvenStringTableValue(
            ref cachedTable,
            target,
            cache,
            ref regionSite,
            key,
            out value));
        Assert.Equal(LuaValue.FromFloat(2.5), value);

        Assert.True(LuaCodegenAbiV5.TrySetBoundStringTableBooleanValue(
            table,
            ref regionSite,
            true));
        Assert.True(LuaCodegenAbiV5.TryGetCompilerProvenStringTableValue(
            ref cachedTable,
            target,
            cache,
            ref regionSite,
            key,
            out value));
        Assert.Equal(LuaValue.FromBoolean(true), value);

        Assert.True(LuaCodegenAbiV5.TrySetBoundStringTableIntegerValue(
            table,
            ref regionSite,
            3));
        Assert.Equal(LuaValue.FromInteger(3), table.Get(key));

        table.Set(key, LuaValue.Nil);
        Assert.False(LuaCodegenAbiV5.TrySetBoundStringTableIntegerValue(
            table,
            ref regionSite,
            4));
        Assert.True(LuaCodegenAbiV5.TryGetCompilerProvenStringTableValue(
            ref cachedTable,
            target,
            cache,
            ref regionSite,
            key,
            out value));
        Assert.True(value.IsNil);

        var metatable = state.CreateTable();
        metatable.Set(String(state, "__index"), LuaValue.FromInteger(2));
        table.SetMetatable(metatable);
        Assert.False(LuaCodegenAbiV5.TryGetCompilerProvenStringTableValue(
            ref cachedTable,
            target,
            cache,
            ref regionSite,
            key,
            out _));
    }

    private static LuaValue String(LuaState state, string value) =>
        LuaValue.FromString(state.Strings.GetOrCreate(System.Text.Encoding.UTF8.GetBytes(value)));
}
