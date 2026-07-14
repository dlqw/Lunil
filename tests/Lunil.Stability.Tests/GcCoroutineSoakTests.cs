using System.Globalization;
using Lunil.Hosting;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;

namespace Lunil.Stability.Tests;

public sealed class GcCoroutineSoakTests
{
    private const int Seed = 0x054F00D;
    private const int WorkerCount = 16;
    private const int RoundCount = 32;
    private const long InstructionBudget = 5_000_000;

    [Fact]
    public void FixedSeedGcCoroutineScheduleCompletesWithinBudget()
    {
        var expectedChecksum = CalculateExpectedChecksum();
        var expectedResumes = WorkerCount * (RoundCount + 1L);
        var expectedFinalizers = WorkerCount * RoundCount;

        for (var pass = 0; pass < 2; pass++)
        {
            try
            {
                using var host = new LuaHost(LuaHostOptions.Deterministic with
                {
                    State = LuaStateOptions.Default with
                    {
                        Heap = LuaHeapOptions.Default with
                        {
                            MaximumLogicalBytes = 64L * 1024 * 1024,
                            StressEveryAllocation = true,
                            HashSeed = Seed,
                        },
                    },
                    Execution = LuaInterpreterOptions.Default with
                    {
                        MaximumInstructionCount = InstructionBudget,
                        MaximumCallDepth = 2_048,
                        MaximumStackSlots = 250_000,
                    },
                });
                var result = host.RunUtf8(CreateSource(), "@stability/gc-coroutine-soak.lua");

                Assert.True(
                    result.Succeeded,
                    $"GC/coroutine soak did not complete: " +
                    string.Join("; ", result.Compilation.Diagnostics.Select(static item => item.Message)));
                Assert.Equal(LuaVmSignal.Completed, result.Execution!.Signal);
                Assert.Equal(expectedChecksum, result.Execution.Values[0].AsInteger());
                Assert.Equal(expectedResumes, result.Execution.Values[1].AsInteger());
                Assert.Equal(expectedFinalizers, result.Execution.Values[2].AsInteger());
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"Deterministic GC/coroutine soak failure; seed=0x{Seed:X8}; pass={pass}; workers={WorkerCount}; rounds={RoundCount}; instructionBudget={InstructionBudget}"),
                    exception);
            }
        }
    }

    private static long CalculateExpectedChecksum()
    {
        long checksum = 0;
        for (var worker = 1; worker <= WorkerCount; worker++)
        {
            long total = 0;
            for (var round = 1; round <= RoundCount; round++)
            {
                total += (worker * round) + 4;
                checksum += total;
            }

            checksum += total;
        }

        return checksum;
    }

    private static string CreateSource() => $$"""
        local workerCount = {{WorkerCount}}
        local roundCount = {{RoundCount}}
        local finalized = 0
        local payloadMetatable = {
            __gc = function()
                finalized = finalized + 1
            end,
        }
        local workers = {}

        for worker = 1, workerCount do
            workers[worker] = coroutine.create(function()
                local total = 0
                for round = 1, roundCount do
                    local payload = setmetatable({
                        worker = worker,
                        round = round,
                        values = { worker, round, worker + round, worker * round },
                    }, payloadMetatable)
                    total = total + payload.worker * payload.round + #payload.values
                    coroutine.yield(total)
                end
                return total
            end)
        end

        local checksum = 0
        local resumes = 0
        for round = 1, roundCount do
            for worker = 1, workerCount do
                local ok, value = coroutine.resume(workers[worker])
                assert(ok, value)
                checksum = checksum + value
                resumes = resumes + 1
            end
            if round % 4 == 0 then
                collectgarbage("collect")
            else
                collectgarbage("step", 32)
            end
        end

        for worker = 1, workerCount do
            local ok, value = coroutine.resume(workers[worker])
            assert(ok, value)
            assert(coroutine.status(workers[worker]) == "dead")
            assert(coroutine.close(workers[worker]))
            checksum = checksum + value
            resumes = resumes + 1
        end

        collectgarbage("collect")
        collectgarbage("collect")
        return checksum, resumes, finalized
        """;
}
