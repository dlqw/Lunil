using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.Runtime.Tests.Execution;

public sealed class LuaFramePoolTests
{
    [Fact]
    public void ReusedFrameResetsContinuationDebugAndVarargState()
    {
        var state = new LuaState();
        var thread = state.MainThread;
        var closure = CreateClosure(state, isVarArg: true);
        var frame = thread.RentFrame(
            closure,
            @base: 0,
            top: 2,
            returnBase: 3,
            expectedResults: 4,
            [LuaValue.FromInteger(1), LuaValue.FromInteger(2)],
            LuaProtectedCallKind.ProtectedCall,
            LuaValue.FromInteger(9),
            isCloseHandler: true,
            isDebugHook: true,
            isHidden: true);
        frame.ProgramCounter = 1;
        frame.PendingDebugHookEvent = "call";
        frame.IsTailCall = true;
        frame.DebugFunctionName = "stale";
        frame.DebugFunctionNameWhat = "field";
        frame.DebugFunctionOverride = LuaValue.FromInteger(8);
        frame.LastDebugHookLine = 20;
        frame.LastDebugHookProgramCounter = 2;
        frame.LastLineHookProgramCounter = 2;
        frame.DebugHookCheckedProgramCounter = 2;
        frame.DispatchedDebugHookEvent = "line";
        frame.ReturnHookProgramCounter = 2;
        frame.NativeCallHookProgramCounter = 2;
        frame.NativeCallSourceLine = 20;
        frame.ToBeClosedSlots.Add(1);
        frame.Continuation.Kind = LuaContinuationKind.NativeCallLua;
        frame.Continuation.Value = LuaValue.FromInteger(7);
        frame.Continuation.Values = [LuaValue.FromInteger(6)];
        frame.Continuation.ProtectionFunction = LuaValue.FromInteger(5);
        frame.Continuation.IsYieldBarrier = true;
        thread.PushFrame(frame);

        Assert.Same(frame, thread.PopFrame());
        Assert.Equal(1, thread.RetiredFrameCount);
        thread.AdvanceFramePoolEpoch();
        Assert.Equal(1, thread.PooledFrameCount);

        var reused = thread.RentFrame(
            closure,
            @base: 4,
            top: 4,
            returnBase: 0,
            expectedResults: 0,
            ReadOnlySpan<LuaValue>.Empty);

        Assert.Same(frame, reused);
        Assert.Equal(4, reused.Base);
        Assert.Equal(0, reused.ProgramCounter);
        Assert.Empty(reused.VarArgs);
        Assert.False(reused.IsDebugHook);
        Assert.False(reused.IsHidden);
        Assert.False(reused.IsTailCall);
        Assert.Null(reused.PendingDebugHookEvent);
        Assert.Null(reused.DebugFunctionName);
        Assert.Null(reused.DebugFunctionNameWhat);
        Assert.True(reused.DebugFunctionOverride.IsNil);
        Assert.Equal(-1, reused.LastDebugHookLine);
        Assert.Equal(-1, reused.LastDebugHookProgramCounter);
        Assert.Equal(-1, reused.LastLineHookProgramCounter);
        Assert.Equal(-1, reused.DebugHookCheckedProgramCounter);
        Assert.Null(reused.DispatchedDebugHookEvent);
        Assert.Equal(-1, reused.ReturnHookProgramCounter);
        Assert.Equal(-1, reused.NativeCallHookProgramCounter);
        Assert.Equal(-1, reused.NativeCallSourceLine);
        Assert.Empty(reused.ToBeClosedSlots);
        Assert.True(reused.Continuation.IsEmpty);
        Assert.Equal(LuaProtectedCallKind.None, reused.Continuation.ProtectionKind);
        Assert.True(reused.Continuation.ProtectionFunction.IsNil);
        Assert.True(reused.Continuation.ErrorHandler.IsNil);
        Assert.False(reused.Continuation.IsCloseHandler);
        Assert.False(reused.Continuation.IsYieldBarrier);
    }

    [Fact]
    public void ReusedFrameRefreshesCachedSourceLineMetadataFromClosure()
    {
        var state = new LuaState();
        var thread = state.MainThread;
        var withoutLines = CreateClosure(state, isVarArg: false);
        var withLines = CreateClosure(state, isVarArg: false, sourceLine: 17);
        Assert.False(withoutLines.HasSourceLineInformation);
        Assert.True(withLines.HasSourceLineInformation);

        var frame = thread.RentFrame(
            withoutLines,
            @base: 0,
            top: 0,
            returnBase: 0,
            expectedResults: 0,
            ReadOnlySpan<LuaValue>.Empty);
        Assert.False(frame.HasSourceLineInformation);
        thread.PushFrame(frame);
        Assert.Same(frame, thread.PopFrame());
        thread.AdvanceFramePoolEpoch();

        var reused = thread.RentFrame(
            withLines,
            @base: 0,
            top: 0,
            returnBase: 0,
            expectedResults: 0,
            ReadOnlySpan<LuaValue>.Empty);
        Assert.Same(frame, reused);
        Assert.True(reused.HasSourceLineInformation);
    }

    [Fact]
    public void PooledFrameDoesNotRetainCollectableLuaValues()
    {
        var state = new LuaState();
        var thread = state.MainThread;
        var closure = CreateClosure(state, isVarArg: true);
        var collectable = state.CreateTable();
        var value = LuaValue.FromTable(collectable);
        var frame = thread.RentFrame(
            closure,
            @base: 0,
            top: 0,
            returnBase: 0,
            expectedResults: 0,
            [value]);
        frame.Continuation.Value = value;
        frame.Continuation.Values = [value];
        frame.Continuation.ErrorHandler = value;
        frame.Continuation.ProtectionFunction = value;
        thread.PushFrame(frame);
        thread.PopFrame();
        thread.AdvanceFramePoolEpoch();

        state.Heap.CollectFull();

        Assert.False(collectable.IsAlive);
    }

    [Fact]
    public void PoolIsBoundedAndTrimsLargeVarargBuffers()
    {
        var state = new LuaState();
        var thread = state.MainThread;
        var closure = CreateClosure(state, isVarArg: true);
        var largeVarargs = Enumerable.Range(0, 1024)
            .Select(static value => LuaValue.FromInteger(value))
            .ToArray();
        var large = thread.RentFrame(closure, 0, 0, 0, 0, largeVarargs);
        Assert.True(large.RetainedVarArgCapacity >= largeVarargs.Length);
        thread.PushFrame(large);
        thread.PopFrame();
        thread.AdvanceFramePoolEpoch();

        var trimmed = thread.RentFrame(closure, 0, 0, 0, 0, ReadOnlySpan<LuaValue>.Empty);
        Assert.Same(large, trimmed);
        Assert.InRange(trimmed.RetainedVarArgCapacity, 0, 64);

        thread.PushFrame(trimmed);
        for (var index = 0; index < 300; index++)
        {
            thread.PushFrame(thread.RentFrame(
                closure,
                index + 1,
                index + 1,
                0,
                0,
                ReadOnlySpan<LuaValue>.Empty));
        }

        while (thread.FrameCount > 0)
        {
            thread.PopFrame();
        }

        thread.AdvanceFramePoolEpoch();
        Assert.Equal(256, thread.PooledFrameCount);
    }

    [Fact]
    public void ProtectedUnwindAndClosersRemainIsolatedAcrossPooledFramesUnderStressGc()
    {
        var state = CreateIntegrationState(installCoroutine: false);
        var hookCount = 0;
        LuaDebugApi.SetHook(
            state,
            state.MainThread,
            LuaValue.FromFunction(new LuaNativeFunction(
                "pool-hook",
                (_, _) =>
                {
                    hookCount++;
                    return [];
                })),
            LuaDebugHookMask.Call | LuaDebugHookMask.Return,
            count: 0);
        var closure = state.CreateMainClosure(Compile(
            """
            local closed = 0
            local mt = {}
            function mt.__close(self, err)
                if err ~= nil then closed = closed + self.value end
            end
            local function fail(value, ...)
                local first, second = ...
                local resource <close> = setmetatable({ value = value + first + second }, mt)
                raise("boom")
            end
            for i = 1, 200 do
                local ok = pcall(fail, i, 2, 3)
                if ok then return -1 end
            end
            return closed
            """));

        var result = new LuaInterpreter().Execute(state, closure);

        Assert.Equal(LuaValue.FromInteger(21_100), Assert.Single(result.Values));
        Assert.True(hookCount > 0);
        Assert.InRange(state.MainThread.PooledFrameCount, 1, 256);
    }

    [Fact]
    public void CoroutineYieldResumeAndCloseCyclesDoNotLeakFrameState()
    {
        var state = CreateIntegrationState(installCoroutine: true);
        var closure = state.CreateMainClosure(Compile(
            """
            local total = 0
            for i = 1, 150 do
                local closed = false
                local mt = {
                    __close = function(_, err) closed = err == nil end,
                }
                local co = coroutine.create(function(...)
                    local left, right = ...
                    local resource <close> = setmetatable({}, mt)
                    coroutine.yield(left + right)
                    return -1
                end)
                local ok, value = coroutine.resume(co, i, 1)
                if not ok or value ~= i + 1 then return -1 end
                local closeOk = coroutine.close(co)
                if not closeOk or not closed then return -2 end
                total = total + value
            end
            return total
            """));

        var result = new LuaInterpreter().Execute(state, closure);

        Assert.Equal(LuaValue.FromInteger(11_475), Assert.Single(result.Values));
    }

    [Fact]
    public void WarmFixedArityCallsAndReturnsDoNotAllocatePerInvocation()
    {
        var state = new LuaState();
        var closure = state.CreateMainClosure(Compile(
            """
            local function values(first, second, third) return first, second, third end
            local total = 0
            for i = 1, 5000 do
                local first, second, third = values(i, i + 1, i + 2)
                total = total + first + second + third
            end
            return total
            """));
        var interpreter = new LuaInterpreter();
        _ = interpreter.Execute(state, closure);
        _ = interpreter.Execute(state, closure);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var result = interpreter.Execute(state, closure);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(LuaValue.FromInteger(37_522_500), Assert.Single(result.Values));
        Assert.InRange(allocated, 0, 128 * 1024);
    }

    private static LuaClosure CreateClosure(
        LuaState state,
        bool isVarArg,
        int sourceLine = 0)
    {
        var instructions = new[]
        {
            new LuaIrInstruction(LuaIrOpcode.Return, 0, 0, sourceLine: sourceLine),
        };
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
                    IsVarArg = isVarArg,
                    Constants = [],
                    Instructions = [.. instructions],
                    BasicBlocks = LuaIrControlFlow.Build([.. instructions]),
                },
            ],
        };
        return state.CreateMainClosure(module);
    }

    private static LuaState CreateIntegrationState(bool installCoroutine)
    {
        var state = new LuaState(new LuaStateOptions
        {
            Heap = LuaHeapOptions.Default with { StressEveryAllocation = true },
        });
        state.InstallProtectedCallFunctions();
        if (installCoroutine)
        {
            state.InstallCoroutineModule();
        }

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
            "raise",
            LuaValue.FromFunction(new LuaNativeFunction(
                "raise",
                static (_, arguments) => throw new LuaRuntimeException(arguments[0]))));
        return state;
    }

    private static LuaIrModule Compile(string source)
    {
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        return Assert.IsType<LuaIrModule>(lowering.Module);
    }
}
