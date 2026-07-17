using Lunil.Core.Text;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.Runtime.Tests.Execution;

public sealed class LuaDebugHookAllocationTests
{
    [Theory]
    [InlineData("call")]
    [InlineData("tail call")]
    [InlineData("return")]
    [InlineData("line")]
    [InlineData("count")]
    public void EventStringsAreStablePermanentPerStateRoots(string hookEvent)
    {
        var state = new LuaState();
        var value = state.GetDebugHookEventString(hookEvent);

        state.Heap.CollectFull();

        Assert.True(value.IsAlive);
        Assert.Same(value, state.GetDebugHookEventString(hookEvent));
    }

    [Fact]
    public void NativeCountHookDispatchDoesNotAllocatePerEvent()
    {
        var hookCount = 0;
        var state = new LuaState();
        var hook = LuaValue.FromFunction(new LuaNativeFunction(
            "allocation-hook",
            (_, _) =>
            {
                hookCount++;
                return [];
            }));
        LuaDebugApi.SetHook(
            state,
            state.MainThread,
            hook,
            LuaDebugHookMask.Count,
            count: 1);
        var module = Compile("for i = 1, 10000 do local value = i + 1 end");
        var closure = state.CreateMainClosure(module);
        var interpreter = new LuaInterpreter();
        _ = interpreter.Execute(state, closure);
        hookCount = 0;

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        _ = interpreter.Execute(state, closure);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.True(hookCount > 10_000);
        Assert.InRange(allocated, 0, 16_384);
    }

    private static Lunil.IR.Canonical.LuaIrModule Compile(string source)
    {
        var syntax = LuaParser.Parse(SourceText.FromUtf8(source));
        var semanticModel = LuaBinder.Bind(syntax);
        var lowering = LuaLowerer.Lower(semanticModel);
        Assert.Empty(lowering.Diagnostics);
        return Assert.IsType<Lunil.IR.Canonical.LuaIrModule>(lowering.Module);
    }
}
