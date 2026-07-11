using Luac.Runtime.Memory;
using Luac.Runtime.Values;

namespace Luac.Runtime.Tests.Fuzz;

public sealed class LuaRuntimeFuzzTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(65537)]
    public void RandomizedTableOperationsMatchAReferenceMap(int seed)
    {
        var state = new LuaState(new LuaStateOptions
        {
            Heap = LuaHeapOptions.Default with { HashSeed = seed },
        });
        var table = state.CreateTable();
        var random = new Random(seed);
        var stringKeys = Enumerable.Range(0, 100)
            .Select(index => LuaValue.FromString(state.Strings.GetOrCreate(
                System.Text.Encoding.UTF8.GetBytes($"key-{index}"))))
            .ToArray();
        var reference = new Dictionary<LuaValue, LuaValue>();

        for (var operation = 0; operation < 20_000; operation++)
        {
            var key = random.Next(2) == 0
                ? LuaValue.FromInteger(random.Next(1, 200))
                : stringKeys[random.Next(stringKeys.Length)];
            if (random.Next(4) == 0)
            {
                table.Set(key, LuaValue.Nil);
                reference.Remove(key);
            }
            else
            {
                var value = LuaValue.FromInteger(random.NextInt64());
                table.Set(key, value);
                reference[key] = value;
            }

            if (operation % 257 == 0)
            {
                Assert.Equal(reference.GetValueOrDefault(key), table.Get(key));
            }
        }

        var actual = new Dictionary<LuaValue, LuaValue>();
        var current = LuaValue.Nil;
        while (table.Next(current, out var key, out var value))
        {
            actual.Add(key, value);
            current = key;
        }

        Assert.Equal(reference.Count, actual.Count);
        Assert.All(reference, pair => Assert.Equal(pair.Value, actual[pair.Key]));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(29)]
    public void RandomObjectGraphsMatchLogicalReachability(int seed)
    {
        const int objectCount = 200;
        var state = new LuaState();
        var random = new Random(seed);
        var objects = Enumerable.Range(0, objectCount)
            .Select(_ => state.CreateTable())
            .ToArray();
        var edges = new List<int>[objectCount];
        for (var index = 0; index < objectCount; index++)
        {
            edges[index] = [];
            var edgeCount = random.Next(0, 5);
            for (var edge = 0; edge < edgeCount; edge++)
            {
                var target = random.Next(objectCount);
                edges[index].Add(target);
                objects[index].Set(
                    LuaValue.FromInteger(edge + 1L),
                    LuaValue.FromTable(objects[target]));
            }
        }

        var roots = Enumerable.Range(0, 10).Select(_ => random.Next(objectCount)).Distinct().ToArray();
        var handles = roots.Select(index =>
            state.CreateHandle(LuaValue.FromTable(objects[index]))).ToArray();
        var reachable = new bool[objectCount];
        var pending = new Stack<int>(roots);
        while (pending.TryPop(out var index))
        {
            if (reachable[index])
            {
                continue;
            }

            reachable[index] = true;
            foreach (var target in edges[index])
            {
                pending.Push(target);
            }
        }

        state.Heap.CollectFull();

        for (var index = 0; index < objectCount; index++)
        {
            Assert.Equal(reachable[index], objects[index].IsAlive);
        }

        foreach (var handle in handles)
        {
            handle.Dispose();
        }
    }
}
