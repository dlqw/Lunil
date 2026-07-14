using Lunil.Core.Text;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.Runtime.Tests.Execution;

public sealed class LuaNestedNativeCallbackTests
{
    [Fact]
    public void ResumableNativeCallbackCrossesGeneratedLuaTrampolineAndCanYield()
    {
        var state = new LuaState();
        var callback = new LuaNativeFunction("callback", CallbackStep);
        var outerDescriptor = new LuaNativeFunction("outer", OuterStep);
        var outer = state.CreateNativeClosure(
            outerDescriptor,
            [LuaValue.FromFunction(callback)]);
        var thread = state.CreateThread(LuaValue.FromFunction(outer));
        var interpreter = new LuaInterpreter();

        var yielded = interpreter.Start(state, thread);
        var completed = interpreter.Resume(state, thread, [LuaValue.FromInteger(42)]);

        Assert.Equal(LuaVmSignal.Yielded, yielded.Signal);
        Assert.Equal([LuaValue.FromInteger(41)], yielded.Values.ToArray());
        Assert.Equal(LuaVmSignal.Completed, completed.Signal);
        Assert.Equal([LuaValue.FromInteger(42)], completed.Values.ToArray());
    }

    [Fact]
    public void ProtectedNativeCallbackConvertsLuaErrorsAtTheRootBoundary()
    {
        var state = new LuaState();
        var callback = Compile(state, "local value=nil; return value()");
        var outer = state.CreateNativeClosure(
            new LuaNativeFunction("protected outer", ProtectedOuterStep),
            [LuaValue.FromFunction(callback)]);
        var thread = state.CreateThread(LuaValue.FromFunction(outer));

        var completed = new LuaInterpreter().Start(state, thread);

        Assert.Equal(LuaVmSignal.Completed, completed.Signal);
        Assert.False(completed.Values[0].AsBoolean());
        Assert.Contains(
            "attempt to call a nil value",
            completed.Values[1].AsString().ToString());
    }

    private static LuaNativeStep CallbackStep(
        LuaNativeCallContext context,
        int continuation,
        ReadOnlySpan<LuaValue> values) => continuation == 0
            ? LuaNativeStep.Yielded(
                [values[0]],
                continuationId: 1,
                stateValues: [values[0]])
            : LuaNativeStep.Completed(values.Length == 0
                ? context.InvocationState[0]
                : values[0]);

    private static LuaNativeStep OuterStep(
        LuaNativeCallContext context,
        int continuation,
        ReadOnlySpan<LuaValue> values) => continuation == 0
            ? LuaNativeStep.CallLua(
                context.Captures[0],
                [LuaValue.FromInteger(41)],
                continuationId: 1)
            : LuaNativeStep.Completed(values.Length == 0 ? LuaValue.Nil : values[0]);

    private static LuaNativeStep ProtectedOuterStep(
        LuaNativeCallContext context,
        int continuation,
        ReadOnlySpan<LuaValue> values) => continuation == 0
            ? LuaNativeStep.CallLua(
                context.Captures[0],
                [],
                continuationId: 1,
                callIsProtected: true)
            : LuaNativeStep.Completed(values.ToArray());

    private static LuaClosure Compile(LuaState state, string source)
    {
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        return state.CreateMainClosure(lowering.Module!);
    }
}
