using Lunil.Core.Text;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.Runtime.Tests.Values;

public sealed class LuaUserdataTests
{
    [Fact]
    public void FullUserdataOwnsMetatableUserValuesAndHostPayload()
    {
        var state = new LuaState();
        var payload = new object();
        var userdata = state.CreateUserdata(payload, userValueCount: 2, payloadLogicalSize: 32);
        var metatable = state.CreateTable();
        var index = state.CreateTable();
        index.Set(
            LuaValue.FromString(state.Strings.GetOrCreate("answer"u8)),
            LuaValue.FromInteger(42));
        metatable.Set(
            LuaValue.FromString(state.Strings.GetOrCreate("__index"u8)),
            LuaValue.FromTable(index));

        userdata.SetMetatable(metatable);
        userdata.SetUserValue(1, LuaValue.FromTable(index));
        state.SetGlobal("u", LuaValue.FromUserdata(userdata));
        var result = Execute(state, "return u.answer");

        Assert.Same(payload, userdata.GetPayload<object>());
        Assert.Same(metatable, userdata.Metatable);
        Assert.Equal(LuaValue.FromTable(index), userdata.GetUserValue(1));
        Assert.Equal([LuaValue.FromInteger(42)], result);
    }

    [Fact]
    public void RegistryPermanentlyRootsItsValues()
    {
        var state = new LuaState();
        var userdata = state.CreateUserdata();
        state.Registry.Set(LuaValue.FromInteger(1), LuaValue.FromUserdata(userdata));

        state.Heap.CollectFull();

        Assert.True(userdata.IsAlive);
        Assert.Equal(
            LuaValue.FromUserdata(userdata),
            state.Registry.Get(LuaValue.FromInteger(1)));
    }

    [Fact]
    public void CollectedUserdataDisposesPayloadExactlyOnce()
    {
        var state = new LuaState();
        var payload = new CountingDisposable();
        _ = state.CreateUserdata(payload);

        state.Heap.CollectFull();
        state.Heap.CollectFull();

        Assert.Equal(1, payload.DisposeCount);
    }

    [Fact]
    public void InterpreterRunsUserdataGarbageCollectionMetamethod()
    {
        var state = new LuaState();
        var calls = 0;
        var userdata = state.CreateUserdata();
        var metatable = state.CreateTable();
        metatable.Set(
            LuaValue.FromString(state.Strings.GetOrCreate("__gc"u8)),
            LuaValue.FromFunction(new LuaNativeFunction(
                "__gc",
                (_, arguments) =>
                {
                    Assert.Equal(LuaValueKind.Userdata, arguments[0].Kind);
                    calls++;
                    return [];
                })));
        userdata.SetMetatable(metatable);

        state.Heap.CollectFull();
        _ = Execute(state, "local n=0; for i=1,3 do n=n+i end; return n");

        Assert.Equal(1, calls);
        Assert.Equal(LuaGcFinalizationState.Finalized, userdata.FinalizationState);
    }

    [Fact]
    public void UserValuesRejectCrossStateReferences()
    {
        var first = new LuaState();
        var second = new LuaState();
        var userdata = first.CreateUserdata();

        Assert.Throws<LuaRuntimeException>(() =>
            userdata.SetUserValue(0, LuaValue.FromTable(second.CreateTable())));
        Assert.Throws<LuaRuntimeException>(() =>
            userdata.SetMetatable(second.CreateTable()));
    }

    [Fact]
    public void LightUserdataEqualityUsesOpaqueIdentity()
    {
        var identity = new object();
        var first = LuaValue.FromLightUserdata(new LuaLightUserdata(identity));
        var second = LuaValue.FromLightUserdata(new LuaLightUserdata(identity));
        var different = LuaValue.FromLightUserdata(new LuaLightUserdata(new object()));

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, different);
        Assert.Equal(LuaValueKind.LightUserdata, first.Kind);
    }

    private static LuaValue[] Execute(LuaState state, string source)
    {
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        return new LuaInterpreter()
            .Execute(state, state.CreateMainClosure(lowering.Module!))
            .Values
            .ToArray();
    }

    private sealed class CountingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
