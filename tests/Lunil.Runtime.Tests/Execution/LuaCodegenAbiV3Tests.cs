using System.Runtime.CompilerServices;
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
    public void PicHotHitsAreAllocationFreeAndCallCacheUsesWeakModuleIdentity()
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
        Assert.All(closures, closure => Assert.True(callCache.TryMatchOrAdd(closure)));

        var expectedModule = frame.FunctionVersion.Module;
        var equivalentModule = CreateModule();
        var otherModule = CreateModule();
        var guardedCallCache = new LuaCodegenCallSiteCache(
            "expected",
            module => ReferenceEquals(module, expectedModule) ||
                ReferenceEquals(module, equivalentModule)
                    ? "expected"
                    : "other");
        Assert.All(closures, closure => Assert.True(guardedCallCache.TryMatchOrAdd(closure)));
        Assert.True(guardedCallCache.TryMatchOrAdd(
            state.CreateMainClosure(equivalentModule)));
        Assert.False(guardedCallCache.TryMatchOrAdd(state.CreateMainClosure(otherModule)));
    }

    [Fact]
    public void DenseIntegerTablePicReadsUpdatesAndAppendsWithGcAndOwnerChecks()
    {
        var (state, thread, frame, context) = CreateFrame();
        var table = state.CreateTable();
        var first = state.CreateTable();
        var second = state.CreateTable();
        table.Set(LuaValue.FromInteger(1), LuaValue.FromTable(first));
        thread.Stack[0] = LuaValue.FromTable(table);
        thread.Stack[1] = LuaValue.FromInteger(1);
        thread.Stack[2] = LuaValue.FromTable(second);
        var counters = new TestTablePicCounterSink();
        var cache = new LuaCodegenTableSiteCache(counters);

        Assert.Equal(
            LuaCodegenPicExecutionResult.Executed,
            LuaCodegenAbiV3.TryExecuteTableGetPic(context, thread, frame, cache, 3, 0, 1));
        Assert.Same(first, thread.Stack[3].AsTable());
        Assert.Equal(
            LuaCodegenPicExecutionResult.Executed,
            LuaCodegenAbiV3.TryExecuteTableSetPic(context, thread, frame, cache, 0, 1, 2));
        Assert.Same(second, table.Get(LuaValue.FromInteger(1)).AsTable());

        thread.Stack[1] = LuaValue.FromInteger(2);
        thread.Stack[2] = LuaValue.FromInteger(42);
        Assert.Equal(
            LuaCodegenPicExecutionResult.Executed,
            LuaCodegenAbiV3.TryExecuteTableSetPic(context, thread, frame, cache, 0, 1, 2));
        Assert.Equal(LuaValue.FromInteger(42), table.Get(LuaValue.FromInteger(2)));
        Assert.Equal(3, counters.Hits);

        state.SetGlobal("root", LuaValue.FromTable(table));
        state.Heap.CollectFull();
        Assert.True(second.IsAlive);

        var foreignState = new LuaState();
        thread.Stack[1] = LuaValue.FromInteger(3);
        Assert.Throws<LuaRuntimeException>(() =>
            thread.Stack[2] = LuaValue.FromTable(foreignState.CreateTable()));
        Assert.True(table.Get(LuaValue.FromInteger(3)).IsNil);
    }

    [Fact]
    public void StringFieldPicHitsAndInvalidatesAcrossTombstoneRehashAndMetatableMutation()
    {
        var (state, thread, frame, context) = CreateFrame();
        var table = state.CreateTable();
        var key = String(state, "field");
        table.Set(key, LuaValue.FromInteger(1));
        thread.Stack[0] = LuaValue.FromTable(table);
        thread.Stack[1] = key;
        var counters = new TestTablePicCounterSink();
        var cache = new LuaCodegenTableSiteCache(counters);

        LuaValue Read()
        {
            Assert.Equal(
                LuaCodegenPicExecutionResult.Executed,
                LuaCodegenAbiV3.TryExecuteTableGetPic(
                    context,
                    thread,
                    frame,
                    cache,
                    2,
                    0,
                    1));
            return thread.Stack[2];
        }

        Assert.Equal(LuaValue.FromInteger(1), Read());
        Assert.Equal(LuaValue.FromInteger(1), Read());
        Assert.Equal(1, counters.Misses);
        Assert.Equal(1, counters.Hits);

        table.Set(key, LuaValue.FromInteger(2));
        Assert.Equal(LuaValue.FromInteger(2), Read());
        Assert.Equal(2, counters.Hits);

        table.Set(key, LuaValue.Nil);
        Assert.True(Read().IsNil);
        Assert.Equal(1, counters.Invalidations);
        table.Set(key, LuaValue.FromInteger(3));
        Assert.Equal(LuaValue.FromInteger(3), Read());

        for (var index = 0; index < 512; index++)
        {
            table.Set(String(state, $"rehash-{index}"), LuaValue.FromInteger(index));
        }

        Assert.Equal(LuaValue.FromInteger(3), Read());
        Assert.True(counters.Invalidations >= 2);

        var metatable = state.CreateTable();
        table.SetMetatable(metatable);
        Assert.Equal(LuaValue.FromInteger(3), Read());
        table.SetMetatable(null);
        Assert.Equal(LuaValue.FromInteger(3), Read());
        Assert.True(counters.Invalidations >= 4);
    }

    [Fact]
    public void StringFieldPicIsBoundedAndDoesNotRetainTableOrKeyOwners()
    {
        var (state, thread, frame, context) = CreateFrame();
        var cache = new LuaCodegenTableSiteCache();
        for (var index = 0; index < 8; index++)
        {
            var table = state.CreateTable();
            var key = String(state, $"field-{index}");
            table.Set(key, LuaValue.FromInteger(index));
            thread.Stack[0] = LuaValue.FromTable(table);
            thread.Stack[1] = key;
            Assert.Equal(
                LuaCodegenPicExecutionResult.Executed,
                LuaCodegenAbiV3.TryExecuteTableGetPic(
                    context,
                    thread,
                    frame,
                    cache,
                    2,
                    0,
                    1));
        }

        Assert.Equal(4, cache.FieldEntryCount);

        var (weakCache, weakTable, weakKey) = CreateAndReleaseStringPicOwners();
        for (var attempt = 0;
             attempt < 10 && (weakTable.IsAlive || weakKey.IsAlive);
             attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(weakTable.IsAlive);
        Assert.False(weakKey.IsAlive);
        GC.KeepAlive(weakCache);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (LuaCodegenTableSiteCache Cache, WeakReference Table, WeakReference Key)
        CreateAndReleaseStringPicOwners()
    {
        var (state, thread, frame, context) = CreateFrame();
        var table = state.CreateTable();
        var key = state.Strings.GetOrCreate("ephemeral-field"u8);
        table.Set(LuaValue.FromString(key), LuaValue.FromInteger(1));
        thread.Stack[0] = LuaValue.FromTable(table);
        thread.Stack[1] = LuaValue.FromString(key);
        var cache = new LuaCodegenTableSiteCache();
        Assert.Equal(
            LuaCodegenPicExecutionResult.Executed,
            LuaCodegenAbiV3.TryExecuteTableGetPic(context, thread, frame, cache, 2, 0, 1));
        var weakTable = new WeakReference(table);
        var weakKey = new WeakReference(key);
        thread.Stack[0] = LuaValue.Nil;
        thread.Stack[1] = LuaValue.Nil;
        thread.Stack[2] = LuaValue.Nil;
        state.Heap.CollectFull();
        return (cache, weakTable, weakKey);
    }

    private static (
        LuaState State,
        LuaThread Thread,
        LuaFrame Frame,
        LuaExecutionContext Context) CreateFrame()
    {
        var module = CreateModule();
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

    private static LuaIrModule CreateModule()
    {
        var instructions = new[] { new LuaIrInstruction(LuaIrOpcode.Return, 0, 0) };
        return new LuaIrModule
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
    }

    private static LuaValue String(LuaState state, string value) =>
        LuaValue.FromString(state.Strings.GetOrCreate(System.Text.Encoding.UTF8.GetBytes(value)));

    private sealed class TestTablePicCounterSink : ILuaCodegenTablePicCounterSink
    {
        public long Hits { get; private set; }

        public long Misses { get; private set; }

        public long Invalidations { get; private set; }

        public void RecordHit() => Hits++;

        public void RecordMiss() => Misses++;

        public void RecordInvalidation() => Invalidations++;
    }
}
