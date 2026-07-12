using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.StandardLibrary;
using Lunil.Syntax.Parsing;

var iterations = args.Length == 0
    ? 1_000_000
    : int.Parse(args[0], CultureInfo.InvariantCulture);
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

const string ArithmeticLoop =
    "local sum = 0; for i = 1, 10000 do sum = sum + i end; return sum";
var emptyLoopModule = Compile("for i = 1, 10000 do end");
Run("interpreter_empty_numeric_for", Scaled(iterations), count =>
{
    for (var index = 0; index < count; index++)
    {
        var state = new LuaState();
        _ = new LuaInterpreter().Execute(state, state.CreateMainClosure(emptyLoopModule));
    }
});

var arithmeticLoopModule = Compile(ArithmeticLoop);
Run("interpreter_arithmetic_numeric_for", Scaled(iterations), count =>
{
    for (var index = 0; index < count; index++)
    {
        var state = new LuaState();
        _ = new LuaInterpreter().Execute(state, state.CreateMainClosure(arithmeticLoopModule));
    }
});

Run("interpreter_cold_compile_execute_arithmetic", Scaled(iterations), count =>
{
    for (var index = 0; index < count; index++)
    {
        var module = Compile(ArithmeticLoop);
        var state = new LuaState();
        _ = new LuaInterpreter().Execute(state, state.CreateMainClosure(module));
    }
});

Run(
    "interpreter_warm_arithmetic_numeric_for",
    Scaled(iterations),
    CreateWarmRunner(ArithmeticLoop));

Run(
    "interpreter_warm_lua_call_vararg_multireturn",
    Scaled(iterations),
    CreateWarmRunner("""
        local function values(a, ...) return a, ... end
        local total = 0
        for i = 1, 2000 do
            local a, b, c = values(i, i + 1, i + 2)
            total = total + a + b + c
        end
        return total
        """));

Run(
    "interpreter_warm_table_array_hash",
    Scaled(iterations),
    CreateWarmRunner("""
        local values = {}
        local total = 0
        for i = 1, 5000 do
            values[i] = i
            values["field"] = i
            total = total + values[i] + values.field
        end
        return total
        """));

Run(
    "interpreter_warm_metamethod",
    Scaled(iterations),
    CreateWarmRunner("""
        local mt = { __add = function(left, right) return left.value + right.value end }
        local left = setmetatable({ value = 1 }, mt)
        local right = setmetatable({ value = 2 }, mt)
        local total = 0
        for i = 1, 2000 do total = total + (left + right) end
        return total
        """, installStandardLibrary: true));

Run(
    "interpreter_coroutine_yield_resume",
    Scaled(iterations),
    CreateCoroutineRunner("""
        local resumed = coroutine.yield(1)
        return resumed + 1
        """));

Run(
    "interpreter_warm_debug_count_hook",
    Scaled(iterations),
    CreateWarmRunner("""
        local count = 0
        debug.sethook(function() count = count + 1 end, "", 100)
        local total = 0
        for i = 1, 5000 do total = total + i end
        debug.sethook()
        return total, count
        """, installStandardLibrary: true));

Run("interpreter_every_allocation_gc_stress", Scaled(iterations), CreateWarmRunner(
    """
    local total = 0
    for i = 1, 500 do
        local value = { i }
        total = total + value[1]
    end
    return total
    """,
    stateOptions: new LuaStateOptions
    {
        Heap = LuaHeapOptions.Default with
        {
            StressEveryAllocation = true,
            HashSeed = 1,
        },
    }));

Run("full_gc_1000_tables", Scaled(iterations), static count =>
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

static int Scaled(int iterations) => Math.Max(1, iterations / 100_000);

static Action<int> CreateWarmRunner(
    string source,
    bool installStandardLibrary = false,
    LuaStateOptions? stateOptions = null)
{
    var module = Compile(source);
    var state = new LuaState(stateOptions);
    if (installStandardLibrary)
    {
        LuaStandardLibrary.InstallAll(state);
    }

    var closure = state.CreateMainClosure(module);
    var interpreter = new LuaInterpreter();
    return count =>
    {
        for (var index = 0; index < count; index++)
        {
            _ = interpreter.Execute(state, closure);
        }
    };
}

static Action<int> CreateCoroutineRunner(string source)
{
    var module = Compile(source);
    var state = new LuaState();
    LuaStandardLibrary.InstallAll(state);
    var closure = state.CreateMainClosure(module);
    var interpreter = new LuaInterpreter();
    return count =>
    {
        for (var index = 0; index < count; index++)
        {
            var thread = state.CreateThread(closure);
            _ = interpreter.Start(state, thread);
            _ = interpreter.Resume(state, thread, [LuaValue.FromInteger(41)]);
        }
    };
}

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

static LuaIrModule Compile(string source)
{
    var lowering = LuaLowerer.Lower(
        LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
    if (!lowering.Succeeded || lowering.Module is null)
    {
        throw new InvalidOperationException("Benchmark Lua source did not compile.");
    }

    return lowering.Module;
}
