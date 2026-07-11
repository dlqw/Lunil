using Luac.Core.Text;
using Luac.Runtime.Execution;
using Luac.Runtime.Memory;
using Luac.Runtime.Values;
using Luac.Semantics.Binding;
using Luac.Semantics.Lowering;
using Luac.Syntax.Parsing;

namespace Luac.Runtime.Tests.Operations;

public sealed class LuaMetamethodTests
{
    [Fact]
    public void ExecutesLuaClosureArithmeticLengthAndConcatenationMetamethods()
    {
        const string source = """
            local mt = {}
            function mt.__add(a, b) return a.value + b.value end
            function mt.__len(a) return a.value end
            function mt.__concat(a, b) return a.value .. b.value end
            local a = setmetatable({ value = 4 }, mt)
            local b = setmetatable({ value = 6 }, mt)
            return a + b, #a, a .. b
            """;

        var values = Execute(source);

        Assert.Equal(LuaValue.FromInteger(10), values[0]);
        Assert.Equal(LuaValue.FromInteger(4), values[1]);
        Assert.Equal("46", values[2].AsString().ToString());
    }

    [Fact]
    public void ExecutesIndexAndNewIndexFunctionsThroughExplicitFrames()
    {
        const string source = """
            local storage = {}
            local mt = {}
            function mt.__index(_, key) return storage[key] or "missing" end
            function mt.__newindex(_, key, value) storage[key] = value * 2 end
            local object = setmetatable({}, mt)
            object.answer = 21
            return object.answer, object.unknown
            """;

        var values = Execute(source);

        Assert.Equal(LuaValue.FromInteger(42), values[0]);
        Assert.Equal("missing", values[1].AsString().ToString());
    }

    [Fact]
    public void SupportsTableIndexChainsAndCallableObjects()
    {
        const string source = """
            local fallback = { answer = 42 }
            local indexMt = { __index = fallback }
            local callableMt = {}
            function callableMt.__call(self, a, b) return self.base + a + b end
            local indexed = setmetatable({}, indexMt)
            local callable = setmetatable({ base = 10 }, callableMt)
            return indexed.answer, callable(2, 3)
            """;

        Assert.Equal(
            [LuaValue.FromInteger(42), LuaValue.FromInteger(15)],
            Execute(source));
    }

    [Fact]
    public void ImplementsEqualityOrderingAndLessEqualFallbackRules()
    {
        const string source = """
            local mt = {}
            function mt.__eq(a, b) return a.value == b.value end
            function mt.__lt(a, b) return a.value < b.value end
            local a = setmetatable({ value = 1 }, mt)
            local b = setmetatable({ value = 2 }, mt)
            local c = setmetatable({ value = 1 }, mt)
            return a == c, a ~= c, a < b, a <= b, b <= a
            """;

        Assert.Equal(
            [
                LuaValue.FromBoolean(true),
                LuaValue.FromBoolean(false),
                LuaValue.FromBoolean(true),
                LuaValue.FromBoolean(true),
                LuaValue.FromBoolean(false),
            ],
            Execute(source));
    }

    [Fact]
    public void PrimitiveTypeMetatablesAreStateOwnedRoots()
    {
        var state = CreateStateWithSetMetatable();
        var metatable = state.CreateTable();
        metatable.Set(
            String(state, "__index"),
            LuaValue.FromFunction(new LuaNativeFunction(
                "number.__index",
                static (_, arguments) => [arguments[1]])));
        state.SetTypeMetatable(LuaValueKind.Integer, metatable);

        var result = Execute("return (1).field", state);
        state.Heap.CollectFull();

        Assert.Equal("field", result[0].AsString().ToString());
        Assert.True(metatable.IsAlive);
    }

    [Fact]
    public void RejectsCyclicIndexChainsWithAStableBudget()
    {
        var state = CreateStateWithSetMetatable();
        var module = Compile("local t = {}; local mt = { __index = t }; setmetatable(t, mt); return t.x");

        var exception = Assert.Throws<LuaRuntimeException>(() =>
            new LuaInterpreter().Execute(state, state.CreateMainClosure(module)));

        Assert.Contains("chain is too long", exception.Message, StringComparison.Ordinal);
    }

    private static LuaValue[] Execute(string source, LuaState? state = null)
    {
        state ??= CreateStateWithSetMetatable();
        var module = Compile(source);
        return new LuaInterpreter().Execute(state, state.CreateMainClosure(module)).Values.ToArray();
    }

    private static LuaState CreateStateWithSetMetatable()
    {
        var state = new LuaState(new LuaStateOptions
        {
            Heap = LuaHeapOptions.Default with { HashSeed = 1234 },
        });
        state.SetGlobal(
            "setmetatable",
            LuaValue.FromFunction(new LuaNativeFunction(
                "setmetatable",
                static (_, arguments) =>
                {
                    var table = arguments[0].AsTable();
                    table.SetMetatable(arguments[1].AsTable());
                    return [arguments[0]];
                })));
        return state;
    }

    private static Luac.IR.Canonical.LuaIrModule Compile(string source)
    {
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        return Assert.IsType<Luac.IR.Canonical.LuaIrModule>(lowering.Module);
    }

    private static LuaValue String(LuaState state, string value) =>
        LuaValue.FromString(state.Strings.GetOrCreate(System.Text.Encoding.UTF8.GetBytes(value)));
}
