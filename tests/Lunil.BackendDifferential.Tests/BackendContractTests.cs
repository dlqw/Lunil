using System.Collections.Immutable;
using Lunil.BackendDifferential.Tests.Infrastructure;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;

namespace Lunil.BackendDifferential.Tests;

public sealed class BackendContractTests
{
    [Fact]
    public void PersistedAotDifferentialCorpusCoversEveryCanonicalOpcode()
    {
        string[] sources =
        [
            """
            local captured = 1
            local function worker(a, ...)
                captured = captured + 1
                local missing
                local values = { ... }
                values[1] = a
                local copy = values[1]
                local negated = -copy
                local inverted = ~copy
                local truth = not missing
                local length = #values
                if truth then copy = negated + inverted end
                return copy, length, captured
            end
            return worker(3, 4, 5)
            """,
            """
            local mt = { __close = function() end }
            local function run()
                local value <close> = setmetatable({}, mt)
                local total = 0
                for i = 1, 3 do total = total + i end
                return total
            end
            return run()
            """,
            """
            local function sum(n, total)
                while n > 0 do
                    n = n - 1
                    total = total + n
                end
                if total > 0 then return sum(0, total) end
                return total
            end
            return sum(3, 0)
            """,
        ];
        var modules = sources
            .Select(LuaBackendSession.Compile)
            .ToList();
        var jumpIfTrue = RewriteFirstOpcode(
            LuaBackendSession.Compile(
                "local value = true; if value then return 1 else return 2 end"),
            LuaIrOpcode.JumpIfFalse,
            LuaIrOpcode.JumpIfTrue);
        var tailCall = RewriteFirstOpcode(
            LuaBackendSession.Compile(
                "local function identity(value) return value end; " +
                "local result = identity(7); return result"),
            LuaIrOpcode.Call,
            LuaIrOpcode.TailCall);
        modules.Add(jumpIfTrue);
        modules.Add(tailCall);
        LuaBackendAssert.AllAgree(backend =>
            LuaBackendSession.Create(backend, jumpIfTrue).Execute());
        LuaBackendAssert.AllAgree(backend =>
            LuaBackendSession.Create(backend, tailCall).Execute());

        var covered = modules
            .SelectMany(static module => module.Functions)
            .SelectMany(static function => function.Instructions)
            .Select(static instruction => instruction.Opcode)
            .ToHashSet();
        var missing = Enum.GetValues<LuaIrOpcode>()
            .Where(opcode => !covered.Contains(opcode))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"Differential corpus is missing canonical opcodes: {string.Join(", ", missing)}");
    }

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

    [Theory]
    [InlineData("return 9223372036854775807 + 1, 5 // 2, 5 % 2, ~5, 1 << 63")]
    [InlineData("local nan=0/0; local z=-0.0; return nan==nan, z==0.0, 1<1.5, '2'+3")]
    [InlineData("local n=0; for i=9223372036854775806,9223372036854775807 do n=n+1 end; return n")]
    [InlineData("local x=1; local function add() x=x+1 end; for i=1,4 do add() end; return x")]
    [InlineData("local mt={__add=function(a,b) return a.value+b.value end}; local a=setmetatable({value=2},mt); return a+a")]
    public void AbiV2PrimitiveNumericForUpvalueAndFallbackPathsAgree(string source)
    {
        LuaBackendAssert.AllAgree(backend =>
            LuaBackendSession.Create(
                backend,
                source,
                installStandardLibrary: source.Contains("setmetatable", StringComparison.Ordinal))
            .Execute());
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
    public void BackendsAgreeOnTailCallFrameReplacement()
    {
        const string source = """
            local function sum(remaining, total)
                if remaining == 0 then return total end
                return sum(remaining - 1, total + remaining)
            end
            return sum(100, 0)
            """;

        LuaBackendAssert.AllAgree(backend =>
            LuaBackendSession.Create(backend, source).Execute());

        var observation = LuaBackendSession.Create(LuaBackendCatalog.All[0], source).Execute();
        AssertValues(
            observation,
            new LuaObservedValue(LuaValueKind.Integer, "5050"));
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
    public async Task BackendsSurviveConcurrentCoroutineAndGcSoak()
    {
        const string source = """
            local total = 0
            for i = 1, 64 do
                local value = { number = i }
                total = total + value.number
                if i % 8 == 0 then
                    total = total + coroutine.yield(total)
                end
            end
            return total
            """;
        var options = new LuaStateOptions
        {
            Heap = LuaHeapOptions.Default with
            {
                StressEveryAllocation = true,
                HashSeed = 1,
            },
        };

        foreach (var backend in LuaBackendCatalog.All)
        {
            var tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
            {
                for (var pass = 0; pass < 4; pass++)
                {
                    var session = LuaBackendSession.Create(
                        backend,
                        source,
                        options,
                        installStandardLibrary: true);
                    var thread = session.CreateThread();
                    var observation = session.Start(thread);
                    long expectedTotal = 0;
                    for (var checkpoint = 1; checkpoint <= 8; checkpoint++)
                    {
                        expectedTotal += Enumerable.Range(
                            ((checkpoint - 1) * 8) + 1,
                            8).Sum();
                        Assert.Equal(LuaVmSignal.Yielded, observation.Signal);
                        AssertValues(
                            observation,
                            new LuaObservedValue(
                                LuaValueKind.Integer,
                                expectedTotal.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture)));
                        var resumed = worker + pass + checkpoint;
                        expectedTotal += resumed;
                        observation = session.Resume(
                            thread,
                            [LuaValue.FromInteger(resumed)]);
                    }

                    Assert.Equal(LuaVmSignal.Completed, observation.Signal);
                    AssertValues(
                        observation,
                        new LuaObservedValue(
                            LuaValueKind.Integer,
                            expectedTotal.ToString(
                                System.Globalization.CultureInfo.InvariantCulture)));
                }
            }));

            await Task.WhenAll(tasks);
        }
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

    private static LuaIrModule RewriteFirstOpcode(
        LuaIrModule module,
        LuaIrOpcode source,
        LuaIrOpcode replacement)
    {
        var functions = module.Functions.ToArray();
        for (var functionIndex = 0; functionIndex < functions.Length; functionIndex++)
        {
            var instructions = functions[functionIndex].Instructions.ToArray();
            var instructionIndex = Array.FindIndex(
                instructions,
                instruction => instruction.Opcode == source);
            if (instructionIndex < 0)
            {
                continue;
            }

            instructions[instructionIndex] = instructions[instructionIndex] with
            {
                Opcode = replacement,
            };
            var immutable = instructions.ToImmutableArray();
            functions[functionIndex] = functions[functionIndex] with
            {
                Instructions = immutable,
                BasicBlocks = LuaIrControlFlow.Build(immutable),
            };
            return module with { Functions = functions.ToImmutableArray() };
        }

        throw new InvalidOperationException($"Canonical opcode {source} was not found.");
    }
}
