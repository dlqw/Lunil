using Lunil.Runtime.CodeGen;

namespace Lunil.Runtime.Tests.CodeGen;

public sealed class LuaBackendGenerationTests
{
    [Fact]
    public async Task InvalidationRejectsStaleEntriesAndWaitsForActiveExecutions()
    {
        var generation = new LuaBackendGeneration();
        var original = generation.Current;
        Assert.True(generation.TryEnter(original));

        generation.BeginInvalidation();
        Assert.NotEqual(original, generation.Current);
        Assert.False(generation.TryEnter(original));
        Assert.False(generation.TryEnter(generation.Current));

        var invalidation = Task.Run(generation.CompleteInvalidation);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            Assert.False(invalidation.IsCompleted);
        }
        finally
        {
            generation.Exit();
        }

        await invalidation.WaitAsync(TimeSpan.FromSeconds(5));

        var current = generation.Current;
        Assert.True(generation.TryEnter(current));
        generation.Exit();
    }

    [Fact]
    public void ExitWithoutAnActiveLeaseDoesNotCorruptFutureAdmissions()
    {
        var generation = new LuaBackendGeneration();

        Assert.Throws<InvalidOperationException>(generation.Exit);

        var current = generation.Current;
        Assert.True(generation.TryEnter(current));
        generation.Exit();
    }
}
