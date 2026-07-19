using Lunil.Runtime.Operations;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Tests.Operations;

public sealed class LuaOperationResolutionTests
{
    [Fact]
    public void StoresCommonMetamethodArgumentsInline()
    {
        var callable = LuaValue.FromFunction(new LuaNativeFunction("callable", static (_, _) => []));
        var resolution = LuaOperationResolution.Call(
            callable,
            LuaValue.FromInteger(1),
            LuaValue.FromInteger(2),
            LuaValue.FromInteger(3));

        Assert.True(resolution.RequiresCall);
        Assert.Equal(3, resolution.ArgumentCount);
        Assert.Equal(LuaValue.FromInteger(1), resolution.GetArgument(0));
        Assert.Equal(LuaValue.FromInteger(2), resolution.GetArgument(1));
        Assert.Equal(LuaValue.FromInteger(3), resolution.GetArgument(2));
        Assert.True(resolution.Arguments.SequenceEqual(
            [
                LuaValue.FromInteger(1),
                LuaValue.FromInteger(2),
                LuaValue.FromInteger(3),
            ]));
    }

    [Fact]
    public void DoesNotAllocateWhenCreatingCommonOperationResolutions()
    {
        var callable = LuaValue.FromFunction(new LuaNativeFunction("callable", static (_, _) => []));
        var left = LuaValue.FromInteger(1);
        var right = LuaValue.FromInteger(2);
        for (var index = 0; index < 100; index++)
        {
            _ = LuaOperationResolution.Call(callable, left, right);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var observedArguments = 0;
        for (var index = 0; index < 10_000; index++)
        {
            var resolution = LuaOperationResolution.Call(callable, left, right);
            observedArguments += resolution.ArgumentCount;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Equal(20_000, observedArguments);
        Assert.InRange(allocated, 0, 256);
    }

    [Fact]
    public void ReusesOwnedOverflowArgumentsAtRuntimeBoundaries()
    {
        var callable = LuaValue.FromFunction(new LuaNativeFunction("callable", static (_, _) => []));
        var source = new[]
        {
            LuaValue.FromInteger(1),
            LuaValue.FromInteger(2),
            LuaValue.FromInteger(3),
            LuaValue.FromInteger(4),
        };
        var resolution = LuaOperationResolution.Call(callable, source);

        var first = resolution.MaterializeArgumentsForRuntime();
        var second = resolution.MaterializeArgumentsForRuntime();

        Assert.NotSame(source, first);
        Assert.Same(first, second);
        Assert.Equal(source, first);
    }
}
