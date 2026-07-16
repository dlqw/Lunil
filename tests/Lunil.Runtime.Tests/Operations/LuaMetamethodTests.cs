using Lunil.Core.Text;
using Lunil.IR.Lua54;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Operations;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.Runtime.Tests.Operations;

public sealed class LuaMetamethodTests
{
    [Fact]
    public void ExistingTableEntryUpdateBypassesNewIndexAndPreservesMutationVersions()
    {
        var state = CreateStateWithSetMetatable();
        var table = state.CreateTable();
        var key = String(state, "existing");
        table.Set(key, LuaValue.FromInteger(1));
        var newIndex = LuaValue.FromFunction(new LuaNativeFunction(
            "__newindex",
            static (_, _) => throw new InvalidOperationException("Existing keys must not call __newindex.")));
        var metatable = state.CreateTable();
        metatable.Set(String(state, "__newindex"), newIndex);
        table.SetMetatable(metatable);
        var shape = table.ShapeVersion;
        var content = table.ContentVersion;

        var resolved = LuaRuntimeOperations.SetIndex(
            state,
            LuaValue.FromTable(table),
            key,
            LuaValue.FromInteger(2));

        Assert.False(resolved.RequiresCall);
        Assert.Equal(LuaValue.FromInteger(2), table.Get(key));
        Assert.Equal(shape, table.ShapeVersion);
        Assert.True(table.ContentVersion > content);

        var absent = LuaRuntimeOperations.SetIndex(
            state,
            LuaValue.FromTable(table),
            String(state, "absent"),
            LuaValue.FromInteger(3));
        Assert.True(absent.RequiresCall);
        Assert.Equal(newIndex, absent.Callable);
    }

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

    [Fact]
    public void LuaClosureOperationResultPreservesHigherLiveRegisters()
    {
        const string source = """
            local mt = {}
            function mt.__sub() return 123 end
            local low = setmetatable({}, mt)
            local keep = "alive"
            low = low - 3
            return low, keep
            """;
        var state = CreateStateWithSetMetatable();

        var values = ExecuteRoundTripped(source, state);

        Assert.Equal(LuaValue.FromInteger(123), values[0]);
        Assert.Equal("alive", values[1].AsString().ToString());
    }

    [Fact]
    public void NativeOperationResultPreservesHigherLiveRegisters()
    {
        const string source = """
            local low = object
            local keep = "alive"
            low = low - 3
            return low, keep
            """;
        var state = CreateStateWithSetMetatable();
        var value = state.CreateTable();
        var metatable = state.CreateTable();
        metatable.Set(
            String(state, "__sub"),
            LuaValue.FromFunction(new LuaNativeFunction(
                "native.__sub",
                static (_, _) => [LuaValue.FromInteger(321)])));
        value.SetMetatable(metatable);
        state.SetGlobal("object", LuaValue.FromTable(value));

        var values = ExecuteRoundTripped(source, state);

        Assert.Equal(LuaValue.FromInteger(321), values[0]);
        Assert.Equal("alive", values[1].AsString().ToString());
    }

    [Fact]
    public void ResumableNativeEqualityMetamethodPreservesLogicalNotTransform()
    {
        var state = CreateStateWithSetMetatable();
        var callback = state.CreateMainClosure(Compile("return true"));
        var equality = state.CreateNativeClosure(
            new LuaNativeFunction("resumable.__eq", ResumableEqualityStep),
            [LuaValue.FromFunction(callback)]);
        var metatable = state.CreateTable();
        metatable.Set(String(state, "__eq"), LuaValue.FromFunction(equality));
        var left = state.CreateTable();
        var right = state.CreateTable();
        left.SetMetatable(metatable);
        right.SetMetatable(metatable);
        state.SetGlobal("left", LuaValue.FromTable(left));
        state.SetGlobal("right", LuaValue.FromTable(right));

        var values = ExecuteRoundTripped(
            "local high='alive'; return left ~= right,high",
            state);

        Assert.False(values[0].AsBoolean());
        Assert.Equal("alive", values[1].AsString().ToString());
    }

    [Fact]
    public void YieldingNativeArithmeticMetamethodResumesOperationAndPreservesLiveSlots()
    {
        var state = CreateStateWithSetMetatable();
        var addition = state.CreateNativeClosure(
            new LuaNativeFunction("yielding.__add", YieldingAdditionStep));
        var metatable = state.CreateTable();
        metatable.Set(String(state, "__add"), LuaValue.FromFunction(addition));
        var left = state.CreateTable();
        var right = state.CreateTable();
        left.SetMetatable(metatable);
        right.SetMetatable(metatable);
        state.SetGlobal("left", LuaValue.FromTable(left));
        state.SetGlobal("right", LuaValue.FromTable(right));
        var module = CompileRoundTripped(
            "local value=left; local high='alive'; value=value+right; return value,high");
        var thread = state.CreateThread(LuaValue.FromFunction(state.CreateMainClosure(module)));
        var interpreter = new LuaInterpreter();

        var yielded = interpreter.Start(state, thread);
        var completed = interpreter.Resume(state, thread, [LuaValue.FromInteger(42)]);

        Assert.Equal(LuaVmSignal.Yielded, yielded.Signal);
        Assert.Equal(41, Assert.Single(yielded.Values).AsInteger());
        Assert.Equal(LuaVmSignal.Completed, completed.Signal);
        Assert.Equal(42, completed.Values[0].AsInteger());
        Assert.Equal("alive", completed.Values[1].AsString().ToString());
    }

    private static LuaValue[] Execute(string source, LuaState? state = null)
    {
        state ??= CreateStateWithSetMetatable();
        var module = Compile(source);
        return new LuaInterpreter().Execute(state, state.CreateMainClosure(module)).Values.ToArray();
    }

    private static LuaValue[] ExecuteRoundTripped(string source, LuaState state)
    {
        var module = CompileRoundTripped(source);
        return new LuaInterpreter().Execute(state, state.CreateMainClosure(module)).Values.ToArray();
    }

    private static Lunil.IR.Canonical.LuaIrModule CompileRoundTripped(string source)
    {
        var original = Compile(source);
        var bytes = Lua54CanonicalPrototypeWriter.Write(original, original.MainFunctionId);
        return Lua54PrototypeConverter.Convert(bytes);
    }

    private static LuaNativeStep ResumableEqualityStep(
        LuaNativeCallContext context,
        int continuation,
        ReadOnlySpan<LuaValue> values) => continuation == 0
            ? LuaNativeStep.CallLua(context.Captures[0], [], continuationId: 1)
            : LuaNativeStep.Completed(values[0]);

    private static LuaNativeStep YieldingAdditionStep(
        LuaNativeCallContext context,
        int continuation,
        ReadOnlySpan<LuaValue> values) => continuation == 0
            ? LuaNativeStep.Yielded([LuaValue.FromInteger(41)], continuationId: 1)
            : LuaNativeStep.Completed(values[0]);

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

    private static Lunil.IR.Canonical.LuaIrModule Compile(string source)
    {
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        return Assert.IsType<Lunil.IR.Canonical.LuaIrModule>(lowering.Module);
    }

    private static LuaValue String(LuaState state, string value) =>
        LuaValue.FromString(state.Strings.GetOrCreate(System.Text.Encoding.UTF8.GetBytes(value)));
}
