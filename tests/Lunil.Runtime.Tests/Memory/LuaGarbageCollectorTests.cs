using Lunil.Core.Text;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.Runtime.Tests.Memory;

public sealed class LuaGarbageCollectorTests
{
    [Fact]
    public void FullCollectionSweepsAnUnreachableObjectGraph()
    {
        var state = new LuaState();
        var baselineBytes = state.Heap.LogicalBytes;
        var table = state.CreateTable();
        var text = state.Strings.GetOrCreate("unreachable"u8);
        table.Set(LuaValue.FromInteger(1), LuaValue.FromString(text));

        state.Heap.CollectFull();

        Assert.False(table.IsAlive);
        Assert.False(text.IsAlive);
        Assert.Equal(2, state.Heap.ObjectCount);
        Assert.Equal(baselineBytes, state.Heap.LogicalBytes);
    }

    [Fact]
    public void PermanentAndHandleRootsKeepTheirReachableGraphsAlive()
    {
        var state = new LuaState();
        var globalTable = state.CreateTable();
        var globalValue = state.Strings.GetOrCreate("global-value"u8);
        globalTable.Set(LuaValue.FromInteger(1), LuaValue.FromString(globalValue));
        state.SetGlobal("root", LuaValue.FromTable(globalTable));

        var handled = state.CreateTable();
        using var handle = state.CreateHandle(LuaValue.FromTable(handled));
        state.Heap.CollectFull();

        Assert.True(globalTable.IsAlive);
        Assert.True(globalValue.IsAlive);
        Assert.True(handled.IsAlive);

        handle.Dispose();
        state.Heap.CollectFull();
        Assert.False(handled.IsAlive);
        Assert.True(globalTable.IsAlive);
    }

    [Fact]
    public void IncrementalBackwardBarrierPreservesAWhiteChild()
    {
        var options = new LuaStateOptions
        {
            Heap = LuaHeapOptions.Default with { StepObjectBudget = 1 },
        };
        var state = new LuaState(options);
        var parent = state.CreateTable();
        var child = state.CreateTable();
        state.SetGlobal("parent", LuaValue.FromTable(parent));

        state.Heap.Step(1);
        while (parent.Color != LuaGcColor.Black)
        {
            state.Heap.Step(1);
        }

        Assert.Equal(LuaGcColor.White, child.Color);
        parent.Set(LuaValue.FromInteger(1), LuaValue.FromTable(child));
        while (state.Heap.Phase != LuaGcPhase.Paused)
        {
            state.Heap.Step(1);
        }

        Assert.True(child.IsAlive);
        Assert.Equal(LuaValue.FromTable(child), parent.Get(LuaValue.FromInteger(1)));
    }

    [Fact]
    public void GenerationalCollectionRemembersOldToYoungWrites()
    {
        var state = new LuaState(new LuaStateOptions
        {
            Heap = LuaHeapOptions.Default with { InitialMode = LuaGcMode.Generational },
        });
        var parent = state.CreateTable();
        state.SetGlobal("parent", LuaValue.FromTable(parent));
        state.Heap.CollectFull();
        state.Heap.CollectFull();
        state.Heap.CollectFull();
        Assert.True(parent.Age >= LuaGcAge.Old0);

        var child = state.CreateTable();
        parent.Set(LuaValue.FromInteger(1), LuaValue.FromTable(child));
        Assert.True(state.Heap.RememberedObjectCount > 0);

        state.Heap.CollectMinor();
        Assert.True(child.IsAlive);

        parent.Set(LuaValue.FromInteger(1), LuaValue.Nil);
        state.Heap.CollectMinor();
        Assert.False(child.IsAlive);
    }

    [Fact]
    public void MinorCollectionDefersOldGarbageUntilMajorCycle()
    {
        var state = new LuaState();
        var table = state.CreateTable();
        var handle = state.CreateHandle(LuaValue.FromTable(table));
        state.Heap.CollectFull();
        state.Heap.CollectFull();
        state.Heap.CollectFull();
        handle.Dispose();

        state.Heap.CollectMinor();
        Assert.True(table.IsAlive);

        state.Heap.CollectFull();
        Assert.False(table.IsAlive);
    }

    [Fact]
    public void InterpreterSurvivesCollectionAtEveryAllocationSafePoint()
    {
        var state = new LuaState(new LuaStateOptions
        {
            Heap = LuaHeapOptions.Default with { StressEveryAllocation = true },
        });
        var module = Compile("local t = {10, 20}; return t[1] + t[2]");

        var result = new LuaInterpreter().Execute(state, state.CreateMainClosure(module));

        Assert.Equal([LuaValue.FromInteger(30)], result.Values.ToArray());
        Assert.True(state.Heap.CompletedCycleCount > 0);
    }

    private static Lunil.IR.Canonical.LuaIrModule Compile(string source)
    {
        var syntax = LuaParser.Parse(SourceText.FromUtf8(source));
        var lowering = LuaLowerer.Lower(LuaBinder.Bind(syntax));
        Assert.Empty(lowering.Diagnostics);
        return Assert.IsType<Lunil.IR.Canonical.LuaIrModule>(lowering.Module);
    }
}
