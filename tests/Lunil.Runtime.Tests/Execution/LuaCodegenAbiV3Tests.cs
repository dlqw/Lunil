using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Tests.Execution;

public sealed class LuaCodegenAbiV3Tests
{
    [Fact]
    public void RuntimeAbiV3KeepsEarlierContractsVersioned()
    {
        Assert.Equal(1, LuaCodegenAbiV1.RuntimeAbiVersion);
        Assert.Equal(2, LuaCodegenAbiV2.RuntimeAbiVersion);
        Assert.Equal(3, LuaCodegenAbiV3.RuntimeAbiVersion);
    }

    [Fact]
    public void DirectAllocatingWritesExtendLogicalGcRootWindow()
    {
        var (state, thread, frame, context) = CreateFrame();
        thread.PushFrame(frame);

        LuaCodegenAbiV3.ExecuteNewTable(
            context,
            thread,
            frame,
            destinationRegister: 3,
            hashCapacityBits: 0,
            arrayCapacity: 0);

        var table = thread.Stack[frame.Base + 3].AsTable();
        Assert.Equal(frame.Base + 4, frame.Top);
        state.Heap.CollectFull();
        Assert.True(table.IsAlive);
    }

    [Fact]
    public void TablePicGuardsMetatableIdentityAndContentMutation()
    {
        var (state, thread, frame, context) = CreateFrame();
        var table = state.CreateTable();
        var firstMetatable = state.CreateTable();
        var secondMetatable = state.CreateTable();
        firstMetatable.Set(String(state, "unrelated"), LuaValue.FromInteger(1));
        secondMetatable.Set(String(state, "__index"), LuaValue.FromInteger(1));
        Assert.Equal(firstMetatable.ContentVersion, secondMetatable.ContentVersion);
        table.SetMetatable(firstMetatable);
        thread.Stack[0] = LuaValue.FromTable(table);
        thread.Stack[1] = String(state, "missing");
        var cache = new LuaCodegenTableSiteCache();

        Assert.Equal(
            LuaCodegenPicExecutionResult.Executed,
            LuaCodegenAbiV3.TryExecuteTableGetPic(context, thread, frame, cache, 2, 0, 1));

        table.SetMetatable(secondMetatable);
        Assert.Equal(
            LuaCodegenPicExecutionResult.GuardFailure,
            LuaCodegenAbiV3.TryExecuteTableGetPic(context, thread, frame, cache, 2, 0, 1));

        table.SetMetatable(firstMetatable);
        firstMetatable.Set(String(state, "__index"), LuaValue.FromInteger(2));
        Assert.Equal(
            LuaCodegenPicExecutionResult.GuardFailure,
            LuaCodegenAbiV3.TryExecuteTableGetPic(context, thread, frame, cache, 2, 0, 1));
    }

    [Fact]
    public void SetPicOnlyRequiresAbsentNewIndexForMissingRawKeys()
    {
        var (state, thread, frame, context) = CreateFrame();
        var table = state.CreateTable();
        var metatable = state.CreateTable();
        metatable.Set(String(state, "__newindex"), LuaValue.FromInteger(1));
        table.SetMetatable(metatable);
        table.Set(String(state, "present"), LuaValue.FromInteger(1));
        thread.Stack[0] = LuaValue.FromTable(table);
        thread.Stack[1] = String(state, "present");
        thread.Stack[2] = LuaValue.FromInteger(2);
        var cache = new LuaCodegenTableSiteCache();

        Assert.Equal(
            LuaCodegenPicExecutionResult.Executed,
            LuaCodegenAbiV3.TryExecuteTableSetPic(context, thread, frame, cache, 0, 1, 2));
        Assert.Equal(LuaValue.FromInteger(2), table.Get(thread.Stack[1]));

        thread.Stack[1] = String(state, "missing");
        Assert.Equal(
            LuaCodegenPicExecutionResult.GuardFailure,
            LuaCodegenAbiV3.TryExecuteTableSetPic(context, thread, frame, cache, 0, 1, 2));
    }

    [Fact]
    public void FusedTablePicReservesOnlyAfterItsGuardSucceeds()
    {
        var (state, thread, frame, _) = CreateFrame();
        var table = state.CreateTable();
        var metatable = state.CreateTable();
        metatable.Set(String(state, "__index"), LuaValue.FromInteger(1));
        table.SetMetatable(metatable);
        thread.Stack[0] = LuaValue.FromTable(table);
        thread.Stack[1] = String(state, "missing");
        var cache = new LuaCodegenTableSiteCache();
        var guarded = new LuaExecutionContext(state, thread, 1);

        Assert.Equal(
            LuaCodegenPicExecutionResult.GuardFailure,
            LuaCodegenAbiV3.TryExecuteTableGetPic(guarded, thread, frame, cache, 2, 0, 1));
        Assert.Equal(0, guarded.InstructionsConsumed);
        Assert.Equal(0, frame.ProgramCounter);

        table.SetMetatable(null);
        var exhausted = new LuaExecutionContext(state, thread, 0);
        Assert.Equal(
            LuaCodegenPicExecutionResult.InstructionBudget,
            LuaCodegenAbiV3.TryExecuteTableGetPic(exhausted, thread, frame, cache, 2, 0, 1));
        Assert.Equal(0, exhausted.InstructionsConsumed);
        Assert.Equal(0, frame.ProgramCounter);

        var executable = new LuaExecutionContext(state, thread, 1);
        Assert.Equal(
            LuaCodegenPicExecutionResult.Executed,
            LuaCodegenAbiV3.TryExecuteTableGetPic(executable, thread, frame, cache, 2, 0, 1));
        Assert.Equal(1, executable.InstructionsConsumed);
        Assert.Equal(1, frame.ProgramCounter);
        Assert.True(thread.Stack[2].IsNil);
    }

    [Fact]
    public void PicHotHitsAreAllocationFreeAndCallCacheIsFourWayPolymorphic()
    {
        var (state, thread, frame, _) = CreateFrame();
        var table = state.CreateTable();
        var metatable = state.CreateTable();
        table.SetMetatable(metatable);
        thread.Stack[0] = LuaValue.FromTable(table);
        thread.Stack[1] = String(state, "missing");
        var tableCache = new LuaCodegenTableSiteCache();
        var context = new LuaExecutionContext(state, thread, 20_001);
        Assert.Equal(
            LuaCodegenPicExecutionResult.Executed,
            LuaCodegenAbiV3.TryExecuteTableGetPic(context, thread, frame, tableCache, 2, 0, 1));

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var lastResult = LuaCodegenPicExecutionResult.GuardFailure;
        for (var iteration = 0; iteration < 20_000; iteration++)
        {
            lastResult = LuaCodegenAbiV3.TryExecuteTableGetPic(
                context,
                thread,
                frame,
                tableCache,
                2,
                0,
                1);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Equal(LuaCodegenPicExecutionResult.Executed, lastResult);
        Assert.InRange(allocated, 0, 512);

        var callCache = new LuaCodegenCallSiteCache();
        var closures = Enumerable.Range(0, 5)
            .Select(_ => state.CreateMainClosure(frame.FunctionVersion.Module))
            .ToArray();
        Assert.All(closures[..4], closure => Assert.True(callCache.TryMatchOrAdd(closure)));
        Assert.False(callCache.TryMatchOrAdd(closures[4]));
        Assert.True(callCache.TryMatchOrAdd(closures[0]));
    }

    private static (
        LuaState State,
        LuaThread Thread,
        LuaFrame Frame,
        LuaExecutionContext Context) CreateFrame()
    {
        var instructions = new[] { new LuaIrInstruction(LuaIrOpcode.Return, 0, 0) };
        var module = new LuaIrModule
        {
            MainFunctionId = 0,
            Functions =
            [
                new LuaIrFunction
                {
                    Id = 0,
                    Span = new TextSpan(0, 0),
                    RegisterCount = 4,
                    Constants = [],
                    Instructions = [.. instructions],
                    BasicBlocks = LuaIrControlFlow.Build([.. instructions]),
                },
            ],
        };
        var state = new LuaState();
        var thread = state.MainThread;
        var frame = new LuaFrame(
            state.CreateMainClosure(module),
            @base: 0,
            top: 0,
            returnBase: 0,
            expectedResults: 0,
            varArgs: []);
        return (state, thread, frame, new LuaExecutionContext(state, thread, 100));
    }

    private static LuaValue String(LuaState state, string value) =>
        LuaValue.FromString(state.Strings.GetOrCreate(System.Text.Encoding.UTF8.GetBytes(value)));
}
