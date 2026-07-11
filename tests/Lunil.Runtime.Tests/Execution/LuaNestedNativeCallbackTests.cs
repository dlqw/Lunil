using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Tests.Execution;

public sealed class LuaNestedNativeCallbackTests
{
    [Fact]
    public void ResumableNativeCallbackCrossesGeneratedLuaTrampolineAndCanYield()
    {
        var state = new LuaState();
        var callback = new LuaNativeFunction(
            "callback",
            static (context, continuation, values) => continuation == 0
                ? LuaNativeStep.Yielded(
                    [values[0]],
                    continuationId: 1,
                    stateValues: [values[0]])
                : LuaNativeStep.Completed(values.Length == 0
                    ? context.InvocationState[0]
                    : values[0]));
        var outerDescriptor = new LuaNativeFunction(
            "outer",
            static (context, continuation, values) => continuation == 0
                ? LuaNativeStep.CallLua(
                    context.Captures[0],
                    [LuaValue.FromInteger(41)],
                    continuationId: 1)
                : LuaNativeStep.Completed(values.Length == 0 ? LuaValue.Nil : values[0]));
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
}
