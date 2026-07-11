using Lunil.Core.Text;
using System.Collections.Immutable;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.Runtime.Tests.Execution;

public sealed class LuaCoroutineTests
{
    [Fact]
    public void HostResumeInjectsInitialAndYieldResults()
    {
        var state = new LuaState();
        state.InstallCoroutineModule();
        var entry = Compile(state, "local a, b = ...; local x, y = coroutine.yield(a, nil, b); return x, y");
        var thread = state.CreateThread(entry);
        var interpreter = new LuaInterpreter();

        var yielded = interpreter.Resume(
            state,
            thread,
            [LuaValue.FromInteger(1), LuaValue.FromInteger(2)]);
        var completed = interpreter.Resume(
            state,
            thread,
            [LuaValue.FromInteger(3), LuaValue.FromInteger(4)]);

        Assert.Equal(LuaVmSignal.Yielded, yielded.Signal);
        Assert.Equal(
            [LuaValue.FromInteger(1), LuaValue.Nil, LuaValue.FromInteger(2)],
            yielded.Values.ToArray());
        Assert.Equal(LuaVmSignal.Completed, completed.Signal);
        Assert.Equal([LuaValue.FromInteger(3), LuaValue.FromInteger(4)], completed.Values.ToArray());
        Assert.Equal(LuaThreadStatus.Dead, thread.Status);
    }

    [Fact]
    public void LuaResumeAndWrapUseSchedulerWithoutRecursiveExecute()
    {
        var values = Execute(
            """
            local co = coroutine.create(function(a)
                local b = coroutine.yield(a + 1)
                return b + 1
            end)
            local ok1, first = coroutine.resume(co, 10)
            local ok2, second = coroutine.resume(co, 20)
            local wrapped = coroutine.wrap(function(v) return v * 2 end)
            return ok1, first, ok2, second, wrapped(7), coroutine.status(co)
            """);

        Assert.Equal(
            [
                LuaValue.FromBoolean(true),
                LuaValue.FromInteger(11),
                LuaValue.FromBoolean(true),
                LuaValue.FromInteger(21),
                LuaValue.FromInteger(14),
                values[5],
            ],
            values.ToArray());
        Assert.Equal("dead", values[5].AsString().ToString());
    }

    [Fact]
    public void ProtectedCallCanYield()
    {
        var state = new LuaState();
        state.InstallProtectedCallFunctions();
        state.InstallCoroutineModule();
        var entry = Compile(
            state,
            "local ok, value = pcall(function() return coroutine.yield(4) end); return ok, value");
        var thread = state.CreateThread(entry);
        var interpreter = new LuaInterpreter();

        var yielded = interpreter.Resume(state, thread);
        var completed = interpreter.Resume(state, thread, [LuaValue.FromInteger(9)]);

        Assert.Equal([LuaValue.FromInteger(4)], yielded.Values.ToArray());
        Assert.Equal(
            [LuaValue.FromBoolean(true), LuaValue.FromInteger(9)],
            completed.Values.ToArray());
    }

    [Fact]
    public void ResumableNativeCanCallLuaThenYieldAndContinue()
    {
        var state = new LuaState();
        state.InstallCoroutineModule();
        state.SetGlobal(
            "bridge",
            LuaValue.FromFunction(new LuaNativeFunction(
                "bridge",
                BridgeStep)));
        var entry = Compile(state, "return bridge(function(v) return v * 3 end, 4)");
        var thread = state.CreateThread(entry);
        var interpreter = new LuaInterpreter();

        var yielded = interpreter.Resume(state, thread);
        var completed = interpreter.Resume(state, thread, [LuaValue.FromInteger(9)]);

        Assert.Equal([LuaValue.FromInteger(12)], yielded.Values.ToArray());
        Assert.Equal([LuaValue.FromInteger(9)], completed.Values.ToArray());
    }

    [Fact]
    public void ResumableNativePreservesPerInvocationStateAcrossCallYieldAndGc()
    {
        var state = new LuaState(new LuaStateOptions
        {
            Heap = LuaHeapOptions.Default with { StressEveryAllocation = true },
        });
        state.SetGlobal(
            "statebridge",
            LuaValue.FromFunction(new LuaNativeFunction("statebridge", StatefulBridgeStep)));
        var entry = Compile(
            state,
            "local marker = { answer = 42 }; return statebridge(function() return 7 end, marker)");
        var thread = state.CreateThread(entry);
        using var threadRoot = state.CreateHandle(LuaValue.FromThread(thread));
        var interpreter = new LuaInterpreter();

        var yielded = interpreter.Resume(state, thread);
        state.Heap.CollectFull();
        var completed = interpreter.Resume(state, thread, [LuaValue.FromInteger(9)]);

        Assert.Equal([LuaValue.FromInteger(7)], yielded.Values.ToArray());
        Assert.Equal(LuaValueKind.Table, completed.Values[0].Kind);
        Assert.Equal(
            LuaValue.FromInteger(42),
            completed.Values[0].AsTable().Get(LuaValue.FromString(
                state.Strings.GetOrCreate("answer"u8))));
        Assert.Equal(LuaValue.FromInteger(9), completed.Values[1]);
    }

    [Fact]
    public void CloseSuspendedCoroutinePassesNilAndRunsClosers()
    {
        var values = Execute(
            """
            local seen = false
            local mt = { __close = function(self, err) seen = err == nil end }
            local co = coroutine.create(function()
                local value <close> = setmetatable({}, mt)
                coroutine.yield()
            end)
            coroutine.resume(co)
            local ok, err = coroutine.close(co)
            return ok, err, seen, coroutine.status(co)
            """,
            installSetMetatable: true);

        Assert.True(values[0].AsBoolean());
        Assert.True(values[1].IsNil);
        Assert.True(values[2].AsBoolean());
        Assert.Equal("dead", values[3].AsString().ToString());
    }

    [Fact]
    public void ErroredCoroutineDefersCloseAndPassesOriginalError()
    {
        var values = Execute(
            """
            local seen = false
            local mt = { __close = function(self, err) seen = err ~= nil end }
            local co = coroutine.create(function()
                local value <close> = setmetatable({}, mt)
                return nil + 1
            end)
            local resumed, original = coroutine.resume(co)
            local before = seen
            local closed, closeError = coroutine.close(co)
            return resumed, original ~= nil, before, closed, closeError ~= nil, seen
            """,
            installSetMetatable: true);

        Assert.Equal(
            [
                LuaValue.FromBoolean(false),
                LuaValue.FromBoolean(true),
                LuaValue.FromBoolean(false),
                LuaValue.FromBoolean(false),
                LuaValue.FromBoolean(true),
                LuaValue.FromBoolean(true),
            ],
            values.ToArray());
    }

    [Fact]
    public void NormalReturnCloserMayYieldButExplicitCloseMayNot()
    {
        var normal = Execute(
            """
            local mt = { __close = function() coroutine.yield(5) end }
            local co = coroutine.create(function()
                local value <close> = setmetatable({}, mt)
                return 7
            end)
            local firstOk, first = coroutine.resume(co)
            local secondOk, second = coroutine.resume(co)
            return firstOk, first, secondOk, second
            """,
            installSetMetatable: true);
        var explicitClose = Execute(
            """
            local mt = { __close = function() coroutine.yield(5) end }
            local co = coroutine.create(function()
                local value <close> = setmetatable({}, mt)
                coroutine.yield(1)
            end)
            coroutine.resume(co)
            local ok, err = coroutine.close(co)
            return ok, err ~= nil, coroutine.status(co)
            """,
            installSetMetatable: true);

        Assert.Equal(
            [
                LuaValue.FromBoolean(true),
                LuaValue.FromInteger(5),
                LuaValue.FromBoolean(true),
                LuaValue.FromInteger(7),
            ],
            normal.ToArray());
        Assert.False(explicitClose[0].AsBoolean());
        Assert.True(explicitClose[1].AsBoolean());
        Assert.Equal("dead", explicitClose[2].AsString().ToString());
    }

    [Fact]
    public void ThousandsOfNestedResumesDoNotGrowClrCallStack()
    {
        var values = Execute(
            """
            local threads = {}
            for i = 1, 3000 do
                threads[i] = coroutine.create(function()
                    if threads[i + 1] then
                        local ok = coroutine.resume(threads[i + 1])
                        return ok
                    end
                    return true
                end)
            end
            return coroutine.resume(threads[1])
            """);

        Assert.Equal(
            [LuaValue.FromBoolean(true), LuaValue.FromBoolean(true)],
            values.ToArray());
    }

    [Fact]
    public void GcStressTracesSuspendedFramesNativeCapturesAndErroredClosers()
    {
        var state = new LuaState(new LuaStateOptions
        {
            Heap = LuaHeapOptions.Default with { StressEveryAllocation = true },
        });
        state.InstallCoroutineModule();
        state.SetGlobal(
            "collect",
            LuaValue.FromFunction(new LuaNativeFunction(
                "collect",
                static (runtime, _) =>
                {
                    runtime.Heap.CollectFull();
                    return [];
                })));
        state.SetGlobal(
            "setmetatable",
            LuaValue.FromFunction(new LuaNativeFunction(
                "setmetatable",
                static (_, arguments) =>
                {
                    arguments[0].AsTable().SetMetatable(arguments[1].AsTable());
                    return [arguments[0]];
                })));
        var closure = Compile(
            state,
            """
            local wrapped = coroutine.wrap(function()
                local held = { value = 40 }
                coroutine.yield(held.value)
                return held.value + 2
            end)
            local first = wrapped()
            collect()
            local second = wrapped()

            local closed = false
            local mt = { __close = function(self, err) closed = self.value == 9 and err ~= nil end }
            local failed = coroutine.create(function()
                local held <close> = setmetatable({ value = 9 }, mt)
                return nil + 1
            end)
            coroutine.resume(failed)
            collect()
            coroutine.close(failed)
            return first, second, closed
            """);

        var result = new LuaInterpreter().Execute(state, closure);

        Assert.Equal(
            [LuaValue.FromInteger(40), LuaValue.FromInteger(42), LuaValue.FromBoolean(true)],
            result.Values.ToArray());
        Assert.True(state.Heap.CompletedCycleCount > 0);
    }

    [Fact]
    public void CoroutineEntriesCapturesAndResumeRejectCrossStateValues()
    {
        var first = new LuaState();
        var second = new LuaState();
        var closure = Compile(first, "return 1");
        var thread = first.CreateThread(closure);
        var descriptor = new LuaNativeFunction("capture", BridgeStep);

        Assert.Throws<LuaRuntimeException>(() => second.CreateThread(closure));
        Assert.Throws<LuaRuntimeException>(() => second.CreateNativeClosure(
            descriptor,
            [LuaValue.FromThread(thread)]));
        Assert.Throws<LuaRuntimeException>(() => new LuaInterpreter().Resume(second, thread));
    }

    [Fact]
    public void NativeFunctionsCanBeCoroutineEntries()
    {
        var state = new LuaState();
        var module = state.InstallCoroutineModule();
        var yield = module.Get(LuaValue.FromString(state.Strings.GetOrCreate("yield"u8)));
        var yieldingThread = state.CreateThread(yield);
        var completedThread = state.CreateThread(LuaValue.FromFunction(new LuaNativeFunction(
            "entry",
            static (_, arguments) => arguments.ToArray())));
        var interpreter = new LuaInterpreter();

        var yielded = interpreter.Resume(state, yieldingThread, [LuaValue.FromInteger(1)]);
        var resumed = interpreter.Resume(state, yieldingThread, [LuaValue.FromInteger(2)]);
        var completed = interpreter.Resume(state, completedThread, [LuaValue.FromInteger(3)]);

        Assert.Equal([LuaValue.FromInteger(1)], yielded.Values.ToArray());
        Assert.Equal([LuaValue.FromInteger(2)], resumed.Values.ToArray());
        Assert.Equal([LuaValue.FromInteger(3)], completed.Values.ToArray());
    }

    [Fact]
    public void InternalPingPongHasNoPerSwitchManagedAllocation()
    {
        var state = new LuaState();
        state.InstallCoroutineModule();
        var closure = Compile(
            state,
            """
            local co = coroutine.create(function()
                for i = 1, 20000 do coroutine.yield(i) end
            end)
            for i = 1, 20001 do coroutine.resume(co) end
            """);
        var interpreter = new LuaInterpreter();
        _ = interpreter.Execute(state, closure);

        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = interpreter.Execute(state, closure);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, 16_384);
    }

    [Fact]
    public void ResumableNativeDescriptorsRejectClrCaptureClosures()
    {
        _ = new LuaNativeFunction(
            "static",
            static (_, _, _) => LuaNativeStep.Completed());
        var captured = LuaValue.FromInteger(1);

        Assert.Throws<ArgumentException>(() => new LuaNativeFunction(
            "captured",
            (_, _, _) => LuaNativeStep.Completed(captured)));
    }

    private static ImmutableArray<LuaValue> Execute(
        string source,
        bool installSetMetatable = false)
    {
        var state = new LuaState();
        state.InstallCoroutineModule();
        if (installSetMetatable)
        {
            state.SetGlobal(
                "setmetatable",
                LuaValue.FromFunction(new LuaNativeFunction(
                    "setmetatable",
                    static (_, arguments) =>
                    {
                        arguments[0].AsTable().SetMetatable(arguments[1].AsTable());
                        return [arguments[0]];
                    })));
        }

        return new LuaInterpreter().Execute(state, Compile(state, source)).Values;
    }

    private static LuaNativeStep BridgeStep(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        _ = context;
        return continuationId switch
        {
            0 => LuaNativeStep.CallLua(values[0], [values[1]], continuationId: 1),
            1 => LuaNativeStep.Yielded([values[0]], continuationId: 2),
            2 => LuaNativeStep.Completed(values.ToArray()),
            _ => throw new InvalidOperationException(),
        };
    }

    private static LuaNativeStep StatefulBridgeStep(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values) => continuationId switch
        {
            0 => LuaNativeStep.CallLua(
                values[0],
                [],
                continuationId: 1,
                stateValues: [values[1]]),
            1 => LuaNativeStep.Yielded(
                values.ToArray(),
                continuationId: 2,
                stateValues: context.InvocationState.ToArray()),
            2 => LuaNativeStep.Completed([.. context.InvocationState, .. values]),
            _ => throw new InvalidOperationException(),
        };

    private static LuaClosure Compile(LuaState state, string source)
    {
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        return state.CreateMainClosure(lowering.Module!);
    }
}
