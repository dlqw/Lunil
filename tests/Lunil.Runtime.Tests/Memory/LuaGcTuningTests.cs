using Lunil.Core.Text;
using Lunil.Runtime;
using Lunil.Runtime.Memory;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.Runtime.Tests.Memory;

public sealed class LuaGcTuningTests
{
    [Fact]
    public void SetPauseScalesTheAllocationDebtThatRestartsCollection()
    {
        var state = new LuaState(new LuaStateOptions
        {
            Heap = LuaHeapOptions.Default with { StepSizeBytes = 4096 },
        });
        var heap = state.Heap;

        CreateGarbage(state, objectCount: 200);
        Assert.True(heap.RequiresInterpreterSafePoint);

        // A large pause multiplies the threshold (4096 x 500 = 2,048,000), so the same
        // allocation debt no longer requests a collection.
        Assert.Equal(200, heap.SetPause(100_000));
        Assert.False(heap.RequiresInterpreterSafePoint);

        // Restoring the default pause restores the threshold.
        Assert.Equal(100_000, heap.SetPause(200));
        Assert.True(heap.RequiresInterpreterSafePoint);
    }

    [Fact]
    public void SetStepMultiplierScalesTheWorkDonePerSafePoint()
    {
        var state = new LuaState(new LuaStateOptions
        {
            Heap = LuaHeapOptions.Default with
            {
                StepSizeBytes = 1,
                StepObjectBudget = 1,
            },
        });
        var heap = state.Heap;

        CreateGarbage(state, objectCount: 120);
        var callsWithDefaultMultiplier = SafePointsToCompleteCycle(heap);
        Assert.True(callsWithDefaultMultiplier > 4);

        CreateGarbage(state, objectCount: 120);
        heap.SetStepMultiplier(10_000);
        var callsWithLargeMultiplier = SafePointsToCompleteCycle(heap);

        Assert.True(callsWithLargeMultiplier < callsWithDefaultMultiplier);
    }

    private static void CreateGarbage(LuaState state, int objectCount)
    {
        for (var index = 0; index < objectCount; index++)
        {
            _ = state.CreateTable();
        }
    }

    private static int SafePointsToCompleteCycle(LuaHeap heap)
    {
        var calls = 0;
        while (calls < 4096)
        {
            calls++;
            heap.SafePoint();
            if (heap.Phase == LuaGcPhase.Paused)
            {
                return calls;
            }
        }

        return calls;
    }
}
