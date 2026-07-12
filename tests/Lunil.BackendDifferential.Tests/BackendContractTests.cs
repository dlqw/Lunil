using Lunil.BackendDifferential.Tests.Infrastructure;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;

namespace Lunil.BackendDifferential.Tests;

public sealed class BackendContractTests
{
    [Fact]
    public void BackendsAgreeOnValuesClosuresVarargsAndTables()
    {
        const string source = """
            local function make(seed)
                return function(a, ...)
                    local values = { ... }
                    return seed + a, #values, values[1], values[2]
                end
            end
            return make(10)(5, 20, 30)
            """;

        LuaBackendAssert.AllAgree(backend =>
            LuaBackendSession.Create(backend, source).Execute());

        var observation = LuaBackendSession.Create(LuaBackendCatalog.All[0], source).Execute();
        Assert.Equal(LuaVmSignal.Completed, observation.Signal);
        AssertValues(
            observation,
            [
                new LuaObservedValue(LuaValueKind.Integer, "15"),
                new LuaObservedValue(LuaValueKind.Integer, "2"),
                new LuaObservedValue(LuaValueKind.Integer, "20"),
                new LuaObservedValue(LuaValueKind.Integer, "30"),
            ]);
    }

    [Fact]
    public void BackendsAgreeOnUnprotectedLuaErrors()
    {
        const string source = "local value = {}; return value + 1";

        LuaBackendAssert.AllAgree(backend =>
            LuaBackendSession.Create(backend, source).Execute());

        var observation = LuaBackendSession.Create(LuaBackendCatalog.All[0], source).Execute();
        Assert.Equal(LuaVmSignal.Error, observation.Signal);
        Assert.True(observation.ErrorValue is not null || observation.RuntimeError is not null);
    }

    [Fact]
    public void BackendsAgreeOnProtectedErrorsAndReverseCloseOrder()
    {
        const string source = """
            local closed = {}
            local mt = {
                __close = function(value)
                    closed[#closed + 1] = value.name
                end
            }
            local function run()
                local first <close> = setmetatable({ name = "first" }, mt)
                local second <close> = setmetatable({ name = "second" }, mt)
                error("stop")
            end
            local ok = pcall(run)
            return ok, table.concat(closed, ",")
            """;

        LuaBackendAssert.AllAgree(backend =>
            LuaBackendSession.Create(backend, source, installStandardLibrary: true).Execute());

        var observation = LuaBackendSession.Create(
            LuaBackendCatalog.All[0],
            source,
            installStandardLibrary: true).Execute();
        AssertValues(
            observation,
            new LuaObservedValue(LuaValueKind.Boolean, "false"),
            new LuaObservedValue(LuaValueKind.String, "7365636F6E642C6669727374"));
    }

    [Fact]
    public void BackendsAgreeOnInstructionBudgetExhaustion()
    {
        var backends = LuaBackendCatalog.CreateAll(new LuaBackendTestOptions
        {
            MaximumInstructionCount = 100,
        });

        foreach (var backend in backends)
        {
            var observation = LuaBackendSession.Create(backend, "while true do end").Execute();
            Assert.Equal(LuaVmSignal.Error, observation.Signal);
            Assert.Contains("instruction budget", observation.ErrorText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BackendsPreserveRootCoroutineYieldAndResume()
    {
        const string source = """
            local resumed = coroutine.yield(7, "pause")
            return resumed + 1
            """;
        foreach (var backend in LuaBackendCatalog.All)
        {
            var session = LuaBackendSession.Create(backend, source, installStandardLibrary: true);
            var thread = session.CreateThread();

            var yielded = session.Start(thread);
            var completed = session.Resume(thread, [LuaValue.FromInteger(41)]);

            Assert.Equal(LuaVmSignal.Yielded, yielded.Signal);
            AssertValues(
                yielded,
                [
                    new LuaObservedValue(LuaValueKind.Integer, "7"),
                    new LuaObservedValue(LuaValueKind.String, "7061757365"),
                ]);
            Assert.Equal(LuaVmSignal.Completed, completed.Signal);
            AssertValues(
                completed,
                [new LuaObservedValue(LuaValueKind.Integer, "42")]);
        }
    }

    [Fact]
    public void BackendsAgreeWithEveryAllocationGcStress()
    {
        const string source = """
            local total = 0
            for i = 1, 200 do
                local value = { number = i }
                local function read() return value.number end
                total = total + read()
            end
            return total
            """;
        var options = new Lunil.Runtime.LuaStateOptions
        {
            Heap = LuaHeapOptions.Default with
            {
                StressEveryAllocation = true,
                HashSeed = 1,
            },
        };

        LuaBackendAssert.AllAgree(backend =>
            LuaBackendSession.Create(backend, source, options).Execute());

        var observation = LuaBackendSession.Create(
            LuaBackendCatalog.All[0],
            source,
            options).Execute();
        AssertValues(
            observation,
            [new LuaObservedValue(LuaValueKind.Integer, "20100")]);
    }

    [Fact]
    public void BackendsAgreeOnExactHookVisibleExecution()
    {
        const string source = """
            local lines = 0
            debug.sethook(function(event)
                if event == "line" then lines = lines + 1 end
            end, "l")
            local value = 1
            value = value + 2
            debug.sethook()
            return value, lines > 0
            """;

        LuaBackendAssert.AllAgree(backend =>
            LuaBackendSession.Create(backend, source, installStandardLibrary: true).Execute());

        var observation = LuaBackendSession.Create(
            LuaBackendCatalog.All[0],
            source,
            installStandardLibrary: true).Execute();
        AssertValues(
            observation,
            [
                new LuaObservedValue(LuaValueKind.Integer, "3"),
                new LuaObservedValue(LuaValueKind.Boolean, "true"),
            ]);
    }

    private static void AssertValues(
        LuaBackendObservation observation,
        params LuaObservedValue[] expected) =>
        Assert.True(
            observation.Values.SequenceEqual(expected),
            $"Actual values [{string.Join(", ", observation.Values)}] did not match " +
            $"expected [{string.Join(", ", expected)}].");
}
