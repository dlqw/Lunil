using System.Text;
using Lunil.Core.Text;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.Runtime.Tests.Memory;

public sealed class LuaWeakTableAndFinalizerTests
{
    [Fact]
    public void WeakValuesAreClearedWithoutRetainingTheirObjects()
    {
        var state = new LuaState();
        var weak = CreateWeakTable(state, "v");
        state.SetGlobal("weak", LuaValue.FromTable(weak));
        var value = state.CreateTable();
        weak.Set(LuaValue.FromInteger(1), LuaValue.FromTable(value));

        state.Heap.CollectFull();

        Assert.False(value.IsAlive);
        Assert.True(weak.Get(LuaValue.FromInteger(1)).IsNil);
    }

    [Fact]
    public void WeakKeysUseEphemeronReachability()
    {
        var state = new LuaState();
        var weak = CreateWeakTable(state, "k");
        state.SetGlobal("weak", LuaValue.FromTable(weak));
        var firstKey = state.CreateTable();
        var secondKey = state.CreateTable();
        var finalValue = state.CreateTable();
        weak.Set(LuaValue.FromTable(firstKey), LuaValue.FromTable(secondKey));
        weak.Set(LuaValue.FromTable(secondKey), LuaValue.FromTable(finalValue));
        state.SetGlobal("key", LuaValue.FromTable(firstKey));

        state.Heap.CollectFull();

        Assert.True(secondKey.IsAlive);
        Assert.True(finalValue.IsAlive);

        state.SetGlobal("key", LuaValue.Nil);
        state.Heap.CollectFull();
        Assert.False(firstKey.IsAlive);
        Assert.False(secondKey.IsAlive);
        Assert.False(finalValue.IsAlive);
        Assert.Equal(0, weak.HashCount);
    }

    [Fact]
    public void WeakKeysAndValuesClearWhenEitherSideDies()
    {
        var state = new LuaState();
        var weak = CreateWeakTable(state, "kv");
        state.SetGlobal("weak", LuaValue.FromTable(weak));
        var key = state.CreateTable();
        var value = state.CreateTable();
        weak.Set(LuaValue.FromTable(key), LuaValue.FromTable(value));
        state.SetGlobal("key", LuaValue.FromTable(key));

        state.Heap.CollectFull();

        Assert.True(key.IsAlive);
        Assert.False(value.IsAlive);
        Assert.Equal(0, weak.HashCount);
    }

    [Fact]
    public void StringEntriesRemainStrongInWeakTablesLikePucLua()
    {
        var state = new LuaState();
        var weak = CreateWeakTable(state, "kv");
        state.SetGlobal("weak", LuaValue.FromTable(weak));
        var key = String(state, "key");
        var value = String(state, "value");
        weak.Set(key, value);

        state.Heap.CollectFull();

        Assert.True(key.AsString().IsAlive);
        Assert.True(value.AsString().IsAlive);
        Assert.Equal(value, weak.Get(key));
    }

    [Fact]
    public void FinalizerRunsOnceAndCanResurrectItsTarget()
    {
        var state = new LuaState();
        var metatable = state.CreateTable();
        var finalizer = LuaValue.FromFunction(new LuaNativeFunction("__gc", static (_, _) => []));
        metatable.Set(String(state, "__gc"), finalizer);
        var target = state.CreateTable();
        target.SetMetatable(metatable);
        LuaHandle? resurrection = null;

        state.Heap.CollectFull();
        Assert.Equal(1, state.Heap.PendingFinalizerCount);
        Assert.True(target.IsAlive);

        var count = state.Heap.RunPendingFinalizers((value, callable) =>
        {
            Assert.Same(target, value);
            Assert.Equal(finalizer, callable);
            resurrection = state.CreateHandle(LuaValue.FromTable((LuaTable)value));
        });

        Assert.Equal(1, count);
        Assert.Equal(LuaGcFinalizationState.Finalized, target.FinalizationState);
        state.Heap.CollectFull();
        Assert.True(target.IsAlive);

        resurrection!.Dispose();
        state.Heap.CollectFull();
        Assert.False(target.IsAlive);
        Assert.Equal(0, state.Heap.PendingFinalizerCount);
    }

    [Fact]
    public void InterpreterRunsLuaClosureFinalizersAtSafePoints()
    {
        const string source = """
            local log = "pending"
            do
                local mt = {}
                function mt.__gc() log = "finalized" end
                local value = setmetatable({}, mt)
            end
            collect()
            return log
            """;
        var state = new LuaState();
        state.SetGlobal(
            "setmetatable",
            LuaValue.FromFunction(new LuaNativeFunction(
                "setmetatable",
                static (_, arguments) =>
                {
                    arguments[0].AsTable().SetMetatable(arguments[1].AsTable());
                    return [arguments[0]];
                })));
        state.SetGlobal(
            "collect",
            LuaValue.FromFunction(new LuaNativeFunction(
                "collect",
                static (runtime, _) =>
                {
                    runtime.Heap.CollectFull();
                    return [];
                })));
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));

        var result = new LuaInterpreter().Execute(
            state,
            state.CreateMainClosure(lowering.Module!));

        Assert.Equal("finalized", result.Values[0].AsString().ToString());
    }

    private static LuaTable CreateWeakTable(LuaState state, string mode)
    {
        var metatable = state.CreateTable();
        metatable.Set(String(state, "__mode"), String(state, mode));
        var table = state.CreateTable();
        table.SetMetatable(metatable);
        return table;
    }

    private static LuaValue String(LuaState state, string value) =>
        LuaValue.FromString(state.Strings.GetOrCreate(Encoding.UTF8.GetBytes(value)));
}
