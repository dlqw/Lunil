using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Lunil.CodeGen.Cil;
using Lunil.CodeGen.Cil.Jit;
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
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
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

const string LoopOsrCandidate = """
    local total = 0
    local first = 0
    local second = 1
    local index = 0
    while index < 20000 do
        local next = first + second
        first = second
        second = next
        total = total + (next & 1023)
        index = index + 1
    end
    return total
    """;
Run(
    "interpreter_warm_loop_osr_candidate",
    Scaled(iterations),
    CreateWarmRunner(LoopOsrCandidate));
Run(
    "jit_experimental_loop_osr_candidate",
    Scaled(iterations),
    CreateLoopOsrRunner(LoopOsrCandidate));

Run(
    "interpreter_warm_lua_fixed_call",
    Scaled(iterations),
    CreateWarmRunner("""
        local function add(a, b) return a + b end
        local total = 0
        for i = 1, 2000 do total = total + add(i, i + 1) end
        return total
        """));

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

const string BackendEvidenceSource = """
    local total = 0
    local first = 0
    local second = 1
    local index = 0
    while index < 5000 do
        local next = first + second
        first = second
        second = next
        total = total + (next & 1023)
        index = index + 1
    end
    return total
    """;
var backendEvidenceModule = Compile(BackendEvidenceSource);
var backendEvidenceOperations = Math.Max(5, Scaled(iterations));
var persistedArtifact = LuaAotCompiler.Compile(backendEvidenceModule).Artifact ??
    throw new InvalidOperationException("Backend evidence AOT artifact did not compile.");
Console.WriteLine(
    $"backend_artifact persisted_pe_bytes={persistedArtifact.PeImage.Length}, " +
    $"portable_pdb_bytes={persistedArtifact.PortablePdbImage.Length}, " +
    $"total_bytes={persistedArtifact.PeImage.Length + persistedArtifact.PortablePdbImage.Length}");
RunBackendEvidence(
    "interpreter",
    backendEvidenceModule,
    backendEvidenceOperations,
    BackendEvidenceRunner.CreateInterpreter);
RunBackendEvidence(
    "tier1",
    backendEvidenceModule,
    backendEvidenceOperations,
    module => BackendEvidenceRunner.CreateJit(module, LuaJitExecutorOptions.Default with
    {
        Policy = LuaJitPolicy.PreferJit,
        SynchronousCompilation = true,
        EnableTier2 = false,
    }));
RunBackendEvidence(
    "tier2",
    backendEvidenceModule,
    backendEvidenceOperations,
    module => BackendEvidenceRunner.CreateJit(module, LuaJitExecutorOptions.Default with
    {
        Policy = LuaJitPolicy.PreferJit,
        SynchronousCompilation = true,
        EnableTier2 = true,
        Tier2InvocationThreshold = 1,
        Tier2BackedgeThreshold = 1,
    }));
RunBackendEvidence(
    "loop_osr",
    backendEvidenceModule,
    backendEvidenceOperations,
    module => BackendEvidenceRunner.CreateJit(module, LuaJitExecutorOptions.Default with
    {
        Policy = LuaJitPolicy.Auto,
        FunctionEntryThreshold = int.MaxValue,
        BackedgeThreshold = int.MaxValue,
        SynchronousCompilation = true,
        EnableTier2 = false,
        EnableLoopOsr = true,
        LoopOsrBackedgeThreshold = 1,
    }));

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

static Action<int> CreateLoopOsrRunner(string source)
{
    var module = Compile(source);
    var state = new LuaState();
    var closure = state.CreateMainClosure(module);
    var executor = new LuaJitExecutor(LuaJitExecutorOptions.Default with
    {
        Policy = LuaJitPolicy.Auto,
        FunctionEntryThreshold = int.MaxValue,
        BackedgeThreshold = int.MaxValue,
        EnableTier2 = false,
        EnableLoopOsr = true,
        LoopOsrBackedgeThreshold = 1,
        SynchronousCompilation = true,
    });
    return count =>
    {
        for (var index = 0; index < count; index++)
        {
            _ = executor.Execute(state, closure);
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

static void RunBackendEvidence(
    string name,
    LuaIrModule module,
    int operationCount,
    Func<LuaIrModule, BackendEvidenceRunner> factory)
{
    const int coldSamples = 9;
    var startupMilliseconds = new List<double>(coldSamples);
    var compilationMilliseconds = new List<double>();
    var tier1CompilationMilliseconds = new List<double>();
    var tier2CompilationMilliseconds = new List<double>();
    var loopOsrCompilationMilliseconds = new List<double>();
    long peakWorkingSetDelta = 0;
    long estimatedCodeBytes = 0;
    using var process = Process.GetCurrentProcess();

    for (var sample = 0; sample < coldSamples; sample++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        var workingSetBefore = process.WorkingSet64;
        var stopwatch = Stopwatch.StartNew();
        using var runner = factory(module);
        runner.ExecuteVerified();
        stopwatch.Stop();
        startupMilliseconds.Add(stopwatch.Elapsed.TotalMilliseconds);

        // Drive Tier 2 promotion after measuring the first-use path. Loop OSR normally compiles
        // during the first invocation, while Tier 1 remains unchanged by these extra executions.
        runner.ExecuteVerified();
        runner.ExecuteVerified();
        CollectCompilationDurations(
            runner.CompilationEvents,
            compilationMilliseconds,
            tier1CompilationMilliseconds,
            tier2CompilationMilliseconds,
            loopOsrCompilationMilliseconds);
        estimatedCodeBytes = Math.Max(estimatedCodeBytes, runner.EstimatedCodeBytes);
        process.Refresh();
        peakWorkingSetDelta = Math.Max(
            peakWorkingSetDelta,
            Math.Max(0, process.WorkingSet64 - workingSetBefore));
    }

    using var warmed = factory(module);
    for (var warmup = 0; warmup < 5; warmup++)
    {
        warmed.ExecuteVerified();
    }

    estimatedCodeBytes = Math.Max(estimatedCodeBytes, warmed.EstimatedCodeBytes);
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var throughputStopwatch = Stopwatch.StartNew();
    for (var operation = 0; operation < operationCount; operation++)
    {
        warmed.ExecuteVerified();
    }

    throughputStopwatch.Stop();
    var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    var nanosecondsPerOperation =
        throughputStopwatch.Elapsed.TotalNanoseconds / operationCount;
    var allocatedPerOperation = (double)allocated / operationCount;
    Console.WriteLine(
        $"backend_evidence name={name}, operations={operationCount}, " +
        $"startup_median_ms={Percentile(startupMilliseconds, 0.50):F3}, " +
        $"startup_p95_ms={Percentile(startupMilliseconds, 0.95):F3}, " +
        $"warm_ns_op={nanosecondsPerOperation:F2}, " +
        $"allocated_op={allocatedPerOperation:F2}, " +
        $"compilation_p95_ms={Percentile(compilationMilliseconds, 0.95):F3}, " +
        $"tier1_p95_ms={Percentile(tier1CompilationMilliseconds, 0.95):F3}, " +
        $"tier2_p95_ms={Percentile(tier2CompilationMilliseconds, 0.95):F3}, " +
        $"loop_osr_p95_ms={Percentile(loopOsrCompilationMilliseconds, 0.95):F3}, " +
        $"rss_peak_delta_bytes={peakWorkingSetDelta}, " +
        $"estimated_code_bytes={estimatedCodeBytes}");
}

static void CollectCompilationDurations(
    IEnumerable<LuaJitEvent> events,
    List<double> all,
    List<double> tier1,
    List<double> tier2,
    List<double> loopOsr)
{
    foreach (var jitEvent in events.Where(static item => item.Kind is
                 LuaJitEventKind.CompilationCompleted or
                 LuaJitEventKind.Tier2CompilationCompleted or
                 LuaJitEventKind.LoopOsrCompilationCompleted))
    {
        var milliseconds = jitEvent.Duration.TotalMilliseconds;
        all.Add(milliseconds);
        switch (jitEvent.Tier)
        {
            case LuaJitCompilationTier.Tier1:
                tier1.Add(milliseconds);
                break;
            case LuaJitCompilationTier.Tier2:
                tier2.Add(milliseconds);
                break;
            case LuaJitCompilationTier.LoopOsr:
                loopOsr.Add(milliseconds);
                break;
        }
    }
}

static double Percentile(IReadOnlyCollection<double> values, double percentile)
{
    if (values.Count == 0)
    {
        return 0;
    }

    var ordered = values.Order().ToArray();
    var index = Math.Clamp(
        (int)Math.Ceiling(percentile * ordered.Length) - 1,
        0,
        ordered.Length - 1);
    return ordered[index];
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

sealed class BackendEvidenceRunner : IDisposable
{
    private readonly LuaState _state;
    private readonly LuaClosure _closure;
    private readonly LuaInterpreter? _interpreter;
    private readonly LuaJitExecutor? _executor;
    private readonly List<LuaJitEvent> _compilationEvents = [];

    private BackendEvidenceRunner(
        LuaIrModule module,
        LuaInterpreter? interpreter,
        LuaJitExecutor? executor)
    {
        _state = new LuaState();
        _closure = _state.CreateMainClosure(module);
        _interpreter = interpreter;
        _executor = executor;
        if (_executor is not null)
        {
            _executor.EventOccurred += OnJitEvent;
        }
    }

    public IReadOnlyList<LuaJitEvent> CompilationEvents => _compilationEvents;

    public long EstimatedCodeBytes => _executor?.Statistics.EstimatedCodeBytes ?? 0;

    public static BackendEvidenceRunner CreateInterpreter(LuaIrModule module) =>
        new(module, new LuaInterpreter(), null);

    public static BackendEvidenceRunner CreateJit(
        LuaIrModule module,
        LuaJitExecutorOptions options) => new(module, null, new LuaJitExecutor(options));

    public void ExecuteVerified()
    {
        var result = _executor is null
            ? _interpreter!.Execute(_state, _closure)
            : _executor.Execute(_state, _closure);
        if (result.Signal != LuaVmSignal.Completed || result.Values.Length != 1)
        {
            throw new InvalidOperationException("Backend evidence workload did not complete.");
        }
    }

    public void Dispose()
    {
        if (_executor is not null)
        {
            _executor.EventOccurred -= OnJitEvent;
            _executor.Dispose();
        }
    }

    private void OnJitEvent(object? sender, LuaJitEvent jitEvent) =>
        _compilationEvents.Add(jitEvent);
}
