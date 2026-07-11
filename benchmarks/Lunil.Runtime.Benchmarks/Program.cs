using System.Diagnostics;
using System.Runtime.InteropServices;
using Lunil.Core.Text;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

var iterations = args.Length == 0 ? 1_000_000 : int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);
Console.WriteLine(
    $"runtime={RuntimeInformation.FrameworkDescription}, os={RuntimeInformation.OSDescription}, " +
    $"arch={RuntimeInformation.ProcessArchitecture}, iterations={iterations}");

Run("table_integer_get_set", iterations, static count =>
{
    var state = new LuaState(new LuaStateOptions
    {
        Heap = LuaHeapOptions.Default with { HashSeed = 1 },
    });
    var table = state.CreateTable();
    for (var index = 0; index < count; index++)
    {
        var key = LuaValue.FromInteger(index & 1023);
        table.Set(key, LuaValue.FromInteger(index));
        _ = table.Get(key);
    }
});

var emptyLoopModule = Compile("for i = 1, 10000 do end");
Run("interpreter_empty_numeric_for", Math.Max(1, iterations / 100_000), count =>
{
    for (var index = 0; index < count; index++)
    {
        var state = new LuaState();
        _ = new LuaInterpreter().Execute(state, state.CreateMainClosure(emptyLoopModule));
    }
});

var arithmeticLoopModule = Compile("local sum = 0; for i = 1, 10000 do sum = sum + i end; return sum");
Run("interpreter_arithmetic_numeric_for", Math.Max(1, iterations / 100_000), count =>
{
    for (var index = 0; index < count; index++)
    {
        var state = new LuaState();
        _ = new LuaInterpreter().Execute(state, state.CreateMainClosure(arithmeticLoopModule));
    }
});

Run("full_gc_1000_tables", Math.Max(1, iterations / 100_000), static count =>
{
    for (var pass = 0; pass < count; pass++)
    {
        var state = new LuaState();
        for (var index = 0; index < 1_000; index++)
        {
            _ = state.CreateTable();
        }

        state.Heap.CollectFull();
    }
});

static void Run(string name, int operationCount, Action<int> action)
{
    action(Math.Min(operationCount, 10));
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var stopwatch = Stopwatch.StartNew();
    action(operationCount);
    stopwatch.Stop();
    var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    var nanoseconds = stopwatch.Elapsed.TotalNanoseconds / operationCount;
    var allocatedPerOperation = (double)allocated / operationCount;
    Console.WriteLine(
        $"{name}: operations={operationCount}, ns/op={nanoseconds:F2}, " +
        $"allocated={allocated}, allocated/op={allocatedPerOperation:F2}");
}

static Lunil.IR.Canonical.LuaIrModule Compile(string source)
{
    var lowering = LuaLowerer.Lower(
        LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
    if (!lowering.Succeeded || lowering.Module is null)
    {
        throw new InvalidOperationException("Benchmark Lua source did not compile.");
    }

    return lowering.Module;
}
