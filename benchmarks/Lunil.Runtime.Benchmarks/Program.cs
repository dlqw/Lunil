using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Lunil.CodeGen.Cil;
using Lunil.CodeGen.Cil.Jit;
using Lunil.CodeGen.Cil.Planning;
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

var iterationArgument = args.FirstOrDefault(static argument =>
    !argument.StartsWith("--", StringComparison.Ordinal));
var iterations = iterationArgument is null
    ? 1_000_000
    : int.Parse(iterationArgument, CultureInfo.InvariantCulture);
var backendOnly = args.Contains("--backend-only", StringComparer.Ordinal);
var backendFilter = GetOption(args, "--backend=") ?? "all";
var workloadFilter = GetOption(args, "--workload=") ?? "all";
var coldSamples = int.Parse(
    GetOption(args, "--cold-samples=") ?? "9",
    CultureInfo.InvariantCulture);
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(coldSamples);
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
Console.WriteLine(
    $"runtime={RuntimeInformation.FrameworkDescription}, os={RuntimeInformation.OSDescription}, " +
    $"arch={RuntimeInformation.ProcessArchitecture}, iterations={iterations}");

if (!backendOnly)
{
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
}

var backendEvidenceOperations = Math.Max(5, Scaled(iterations));
BackendEvidenceWorkload[] backendWorkloads =
[
    new("arithmetic", ArithmeticSource(5_000)),
    new("control_flow", """
        local total = 0
        local index = 0
        while index < 5000 do
            if (index & 1) == 0 then
                total = total + index
            else
                total = total - 1
            end
            index = index + 1
        end
        repeat
            total = total + 1
        until total > 6247501
        return total
        """),
    new("lua_calls", """
        local function add(a, b) return a + b end
        local function values(a, ...) return a, ... end
        local total = 0
        for index = 1, 1000 do
            local a, b, c = values(index, index + 1, index + 2)
            total = total + add(a, b) + c
        end
        return total
        """),
    new("table_access", """
        local values = {}
        local total = 0
        for index = 1, 3000 do
            values[index] = index
            values.field = index
            total = total + values[index] + values.field
        end
        return total
        """),
    new("metamethod", """
        local mt = { __add = function(left, right) return left.value + right.value end }
        local left = setmetatable({ value = 1 }, mt)
        local right = setmetatable({ value = 2 }, mt)
        local total = 0
        for index = 1, 1000 do
            total = total + (left + right)
        end
        return total
        """, InstallStandardLibrary: true),
    new("coroutine_error_hook", """
        local hooks = 0
        debug.sethook(function() hooks = hooks + 1 end, "", 100)
        local total = 0
        for index = 1, 100 do
            local worker = coroutine.create(function(value)
                coroutine.yield(value)
                return value + 1
            end)
            local ok, first = coroutine.resume(worker, index)
            local resumed, second = coroutine.resume(worker)
            local protected = pcall(function() error("expected") end)
            if ok and resumed and not protected then
                total = total + first + second
            end
        end
        debug.sethook()
        return total + hooks
        """, InstallStandardLibrary: true),
];

foreach (var workload in backendWorkloads.Where(item =>
             ShouldRunWorkload(workloadFilter, item.Name)))
{
    var backendEvidenceModule = Compile(workload.Source);
    var diagnosticPlan = LuaCilCodeGenerator.PlanFunction(
        backendEvidenceModule,
        0,
        limits: CilPlanLimits.Default,
        includeInstructionObservation: false).Plan ??
        throw new InvalidOperationException(
            $"Backend evidence CIL plan did not compile for {workload.Name}.");
    var function = backendEvidenceModule.Functions[0];
    var slowPaths = string.Join(
        ';',
        diagnosticPlan.SlowPathCanonicalProgramCounters.Select(programCounter =>
            $"{programCounter}:{function.Instructions[programCounter].Opcode}"));
    Console.WriteLine(
        $"backend_plan workload={workload.Name}, " +
        $"direct={diagnosticPlan.DirectCanonicalInstructionCount}, " +
        $"slow={diagnosticPlan.SlowPathCanonicalInstructionCount}, " +
        $"slow_paths={slowPaths}");
    var persistedArtifact = LuaAotCompiler.Compile(backendEvidenceModule).Artifact ??
        throw new InvalidOperationException(
            $"Backend evidence AOT artifact did not compile for {workload.Name}.");
    Console.WriteLine(
        $"backend_artifact workload={workload.Name}, " +
        $"persisted_pe_bytes={persistedArtifact.PeImage.Length}, " +
        $"portable_pdb_bytes={persistedArtifact.PortablePdbImage.Length}, " +
        $"total_bytes={persistedArtifact.PeImage.Length + persistedArtifact.PortablePdbImage.Length}");
    if (ShouldRunBackend(backendFilter, "interpreter"))
    {
        RunBackendEvidence(
            workload.Name,
            "interpreter",
            backendEvidenceModule,
            backendEvidenceOperations,
            coldSamples,
            module => BackendEvidenceRunner.CreateInterpreter(
                module,
                workload.InstallStandardLibrary));
    }

    if (ShouldRunBackend(backendFilter, "tier1"))
    {
        RunBackendEvidence(
            workload.Name,
            "tier1",
            backendEvidenceModule,
            backendEvidenceOperations,
            coldSamples,
            module => BackendEvidenceRunner.CreateJit(
                module,
                LuaJitExecutorOptions.Default with
                {
                    Policy = LuaJitPolicy.PreferJit,
                    SynchronousCompilation = true,
                    EnableTier2 = false,
                },
                workload.InstallStandardLibrary));
    }

    if (ShouldRunBackend(backendFilter, "tier2"))
    {
        RunBackendEvidence(
            workload.Name,
            "tier2",
            backendEvidenceModule,
            backendEvidenceOperations,
            coldSamples,
            module => BackendEvidenceRunner.CreateJit(
                module,
                LuaJitExecutorOptions.Default with
                {
                    Policy = LuaJitPolicy.PreferJit,
                    SynchronousCompilation = true,
                    EnableTier2 = true,
                    Tier2InvocationThreshold = 1,
                    Tier2BackedgeThreshold = 1,
                },
                workload.InstallStandardLibrary));
    }

    if (ShouldRunBackend(backendFilter, "loop_osr"))
    {
        RunBackendEvidence(
            workload.Name,
            "loop_osr",
            backendEvidenceModule,
            backendEvidenceOperations,
            coldSamples,
            module => BackendEvidenceRunner.CreateJit(
                module,
                LuaJitExecutorOptions.Default with
                {
                    Policy = LuaJitPolicy.Auto,
                    FunctionEntryThreshold = int.MaxValue,
                    BackedgeThreshold = int.MaxValue,
                    SynchronousCompilation = true,
                    EnableTier2 = false,
                    EnableLoopOsr = true,
                    LoopOsrBackedgeThreshold = 1,
                },
                workload.InstallStandardLibrary));
    }

    var eligibility = LuaJitExecutor.EvaluateFunctionEligibility(
        backendEvidenceModule,
        0,
        includeInstructionObservation: false);
    Console.WriteLine(
        $"backend_eligibility workload={workload.Name}, " +
        $"auto_eligible={eligibility.IsAutoEligible.ToString().ToLowerInvariant()}, " +
        $"compilable={eligibility.IsCompilable.ToString().ToLowerInvariant()}, " +
        $"reason={eligibility.Reason}, " +
        $"break_even={eligibility.BreakEvenClass}, " +
        $"direct_coverage={eligibility.DirectCoverage:F6}, " +
        $"slow_path_density={eligibility.SlowPathDensity:F6}, " +
        $"scheduler_boundary_density={eligibility.SchedulerBoundaryDensity:F6}, " +
        $"estimated_code_bytes={eligibility.EstimatedCodeBytes}");
}

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
    string workload,
    string name,
    LuaIrModule module,
    int operationCount,
    int coldSampleCount,
    Func<LuaIrModule, BackendEvidenceRunner> factory)
{
    var startupMilliseconds = new List<double>(coldSampleCount);
    var compilationMilliseconds = new List<double>();
    var tier1CompilationMilliseconds = new List<double>();
    var tier2CompilationMilliseconds = new List<double>();
    var loopOsrCompilationMilliseconds = new List<double>();
    var canonicalVerificationMilliseconds = new List<double>();
    var controlFlowAnalysisMilliseconds = new List<double>();
    var methodPlanBuildMilliseconds = new List<double>();
    var planVerificationMilliseconds = new List<double>();
    var reflectionEmitMilliseconds = new List<double>();
    var delegateCreationMilliseconds = new List<double>();
    var compileAllocatedBytes = new List<double>();
    var tier2IrVerificationMilliseconds = new List<double>();
    var tier2LivenessMilliseconds = new List<double>();
    var tier2LivenessCacheHits = new List<double>();
    var tier2OptimizationPlanningMilliseconds = new List<double>();
    var tier2CilEmissionMilliseconds = new List<double>();
    var tier2DelegateCreationMilliseconds = new List<double>();
    var tier2CompileAllocatedBytes = new List<double>();
    long peakWorkingSetDelta = 0;
    long estimatedCodeBytes = 0;
    using var process = Process.GetCurrentProcess();

    for (var sample = 0; sample < coldSampleCount; sample++)
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
            loopOsrCompilationMilliseconds,
            canonicalVerificationMilliseconds,
            controlFlowAnalysisMilliseconds,
            methodPlanBuildMilliseconds,
            planVerificationMilliseconds,
            reflectionEmitMilliseconds,
            delegateCreationMilliseconds,
            compileAllocatedBytes,
            tier2IrVerificationMilliseconds,
            tier2LivenessMilliseconds,
            tier2LivenessCacheHits,
            tier2OptimizationPlanningMilliseconds,
            tier2CilEmissionMilliseconds,
            tier2DelegateCreationMilliseconds,
            tier2CompileAllocatedBytes);
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
    var allocationSlopeBytesPerIteration = 0.0;
    if (string.Equals(workload, "arithmetic", StringComparison.Ordinal))
    {
        using var small = factory(Compile(ArithmeticSource(1_000)));
        using var large = factory(Compile(ArithmeticSource(10_000)));
        var smallAllocated = MeasureAllocatedPerExecution(small);
        var largeAllocated = MeasureAllocatedPerExecution(large);
        allocationSlopeBytesPerIteration = (largeAllocated - smallAllocated) / 9_000.0;
    }

    var statistics = warmed.Statistics;
    var schedulerExits = statistics?.SchedulerExits ?? 0;
    var compiledCanonicalInstructions = statistics?.CompiledCanonicalInstructions ?? 0;
    var canonicalInstructionsPerSchedulerExit = schedulerExits == 0
        ? 0
        : (double)compiledCanonicalInstructions / schedulerExits;
    Console.WriteLine(
        $"backend_compile_samples workload={workload}, name={name}, " +
        $"total_ms={JoinSamples(compilationMilliseconds)}, " +
        $"plan_verify_ms={JoinSamples(planVerificationMilliseconds)}, " +
        $"emit_ms={JoinSamples(reflectionEmitMilliseconds)}, " +
        $"allocated_bytes={JoinSamples(compileAllocatedBytes)}, " +
        $"tier2_ir_verify_ms={JoinSamples(tier2IrVerificationMilliseconds)}, " +
        $"tier2_liveness_ms={JoinSamples(tier2LivenessMilliseconds)}, " +
        $"tier2_optimization_plan_ms={JoinSamples(tier2OptimizationPlanningMilliseconds)}, " +
        $"tier2_cil_emit_ms={JoinSamples(tier2CilEmissionMilliseconds)}, " +
        $"tier2_delegate_create_ms={JoinSamples(tier2DelegateCreationMilliseconds)}, " +
        $"tier2_allocated_bytes={JoinSamples(tier2CompileAllocatedBytes)}");
    Console.WriteLine(
        $"backend_evidence workload={workload}, name={name}, operations={operationCount}, " +
        $"startup_median_ms={Percentile(startupMilliseconds, 0.50):F3}, " +
        $"startup_p95_ms={Percentile(startupMilliseconds, 0.95):F3}, " +
        $"warm_ns_op={nanosecondsPerOperation:F2}, " +
        $"allocated_op={allocatedPerOperation:F2}, " +
        $"allocation_slope_bytes_iteration={allocationSlopeBytesPerIteration:F6}, " +
        $"compilation_p95_ms={Percentile(compilationMilliseconds, 0.95):F3}, " +
        $"tier1_p95_ms={Percentile(tier1CompilationMilliseconds, 0.95):F3}, " +
        $"tier2_p95_ms={Percentile(tier2CompilationMilliseconds, 0.95):F3}, " +
        $"loop_osr_p95_ms={Percentile(loopOsrCompilationMilliseconds, 0.95):F3}, " +
        $"canonical_verify_p95_ms={Percentile(canonicalVerificationMilliseconds, 0.95):F3}, " +
        $"cfg_liveness_p95_ms={Percentile(controlFlowAnalysisMilliseconds, 0.95):F3}, " +
        $"method_plan_p95_ms={Percentile(methodPlanBuildMilliseconds, 0.95):F3}, " +
        $"plan_verify_p95_ms={Percentile(planVerificationMilliseconds, 0.95):F3}, " +
        $"reflection_emit_p95_ms={Percentile(reflectionEmitMilliseconds, 0.95):F3}, " +
        $"delegate_create_p95_ms={Percentile(delegateCreationMilliseconds, 0.95):F3}, " +
        $"compile_allocated_p95_bytes={Percentile(compileAllocatedBytes, 0.95):F0}, " +
        $"tier2_ir_verify_p95_ms={Percentile(tier2IrVerificationMilliseconds, 0.95):F3}, " +
        $"tier2_liveness_p95_ms={Percentile(tier2LivenessMilliseconds, 0.95):F3}, " +
        $"tier2_liveness_cache_hit_rate={Average(tier2LivenessCacheHits):F6}, " +
        $"tier2_optimization_plan_p95_ms={Percentile(tier2OptimizationPlanningMilliseconds, 0.95):F3}, " +
        $"tier2_cil_emit_p95_ms={Percentile(tier2CilEmissionMilliseconds, 0.95):F3}, " +
        $"tier2_delegate_create_p95_ms={Percentile(tier2DelegateCreationMilliseconds, 0.95):F3}, " +
        $"tier2_compile_allocated_p95_bytes={Percentile(tier2CompileAllocatedBytes, 0.95):F0}, " +
        $"tier2_code_kind={warmed.Tier2CodeKind}, " +
        $"tier2_optimization_count={warmed.Tier2OptimizationCount}, " +
        $"tier2_specialized_optimization_count={warmed.Tier2SpecializedOptimizationCount}, " +
        $"tier2_deopt_site_count={warmed.Tier2DeoptSiteCount}, " +
        $"compiled_invocations={statistics?.CompiledInvocations ?? 0}, " +
        $"compiled_instructions={compiledCanonicalInstructions}, " +
        $"scheduler_exits={schedulerExits}, " +
        $"instructions_per_scheduler_exit={canonicalInstructionsPerSchedulerExit:F3}, " +
        $"continue_exits={statistics?.ContinueExits ?? 0}, " +
        $"poll_exits={statistics?.PollExits ?? 0}, " +
        $"call_exits={statistics?.CallExits ?? 0}, " +
        $"tail_call_exits={statistics?.TailCallExits ?? 0}, " +
        $"return_exits={statistics?.ReturnExits ?? 0}, " +
        $"budget_polls={statistics?.InstructionBudgetPolls ?? 0}, " +
        $"gc_polls={statistics?.GarbageCollectionPolls ?? 0}, " +
        $"debug_deopts={statistics?.DebugModeDeoptimizations ?? 0}, " +
        $"plan_direct_instructions={statistics?.Tier1DirectCanonicalInstructions ?? 0}, " +
        $"plan_slow_path_instructions={statistics?.Tier1SlowPathCanonicalInstructions ?? 0}, " +
        $"plan_instructions={statistics?.Tier1PlanInstructions ?? 0}, " +
        $"rss_peak_delta_bytes={peakWorkingSetDelta}, " +
        $"estimated_code_bytes={estimatedCodeBytes}");
}

static double MeasureAllocatedPerExecution(BackendEvidenceRunner runner)
{
    const int warmups = 3;
    const int samples = 5;
    for (var warmup = 0; warmup < warmups; warmup++)
    {
        runner.ExecuteVerified();
    }

    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    for (var sample = 0; sample < samples; sample++)
    {
        runner.ExecuteVerified();
    }

    return (double)(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / samples;
}

static void CollectCompilationDurations(
    IEnumerable<LuaJitEvent> events,
    List<double> all,
    List<double> tier1,
    List<double> tier2,
    List<double> loopOsr,
    List<double> canonicalVerification,
    List<double> controlFlowAnalysis,
    List<double> methodPlanBuild,
    List<double> planVerification,
    List<double> reflectionEmit,
    List<double> delegateCreation,
    List<double> compileAllocatedBytes,
    List<double> tier2IrVerification,
    List<double> tier2Liveness,
    List<double> tier2LivenessCacheHits,
    List<double> tier2OptimizationPlanning,
    List<double> tier2CilEmission,
    List<double> tier2DelegateCreation,
    List<double> tier2CompileAllocatedBytes)
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

        if (jitEvent.CompilationMetrics is { } metrics)
        {
            canonicalVerification.Add(metrics.CanonicalVerificationDuration.TotalMilliseconds);
            controlFlowAnalysis.Add(metrics.ControlFlowAnalysisDuration.TotalMilliseconds);
            methodPlanBuild.Add(metrics.MethodPlanBuildDuration.TotalMilliseconds);
            planVerification.Add(metrics.PlanVerificationDuration.TotalMilliseconds);
            reflectionEmit.Add(metrics.ReflectionEmitDuration.TotalMilliseconds);
            delegateCreation.Add(metrics.DelegateCreationDuration.TotalMilliseconds);
            compileAllocatedBytes.Add(metrics.AllocatedBytes);
        }

        if (jitEvent.Tier2CompilationMetrics is { } tier2Metrics)
        {
            tier2IrVerification.Add(
                tier2Metrics.CanonicalVerificationDuration.TotalMilliseconds);
            tier2Liveness.Add(tier2Metrics.LivenessAnalysisDuration.TotalMilliseconds);
            tier2LivenessCacheHits.Add(tier2Metrics.LivenessCacheHit ? 1.0 : 0.0);
            tier2OptimizationPlanning.Add(
                tier2Metrics.OptimizationPlanningDuration.TotalMilliseconds);
            tier2CilEmission.Add(tier2Metrics.CilEmissionDuration.TotalMilliseconds);
            tier2DelegateCreation.Add(
                tier2Metrics.DelegateCreationDuration.TotalMilliseconds);
            tier2CompileAllocatedBytes.Add(tier2Metrics.AllocatedBytes);
        }
    }
}

static double Average(IReadOnlyCollection<double> values) =>
    values.Count == 0 ? 0.0 : values.Average();

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

static string? GetOption(string[] arguments, string prefix) => arguments
    .FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.Ordinal))?
    [prefix.Length..];

static string JoinSamples(IEnumerable<double> values) => string.Join(
    ';',
    values.Select(value => value.ToString("F3", CultureInfo.InvariantCulture)));

static string ArithmeticSource(int loopIterations) => $$"""
    local total = 0
    local first = 0
    local second = 1
    local index = 0
    while index < {{loopIterations}} do
        local next = first + second
        first = second
        second = next
        total = total + (next & 1023)
        index = index + 1
    end
    return total
    """;

static bool ShouldRunBackend(string filter, string backend) =>
    string.Equals(filter, "all", StringComparison.Ordinal) ||
    string.Equals(filter, backend, StringComparison.Ordinal);

static bool ShouldRunWorkload(string filter, string workload) =>
    string.Equals(filter, "all", StringComparison.Ordinal) ||
    string.Equals(filter, workload, StringComparison.Ordinal);

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
        LuaJitExecutor? executor,
        bool installStandardLibrary)
    {
        _state = new LuaState();
        if (installStandardLibrary)
        {
            LuaStandardLibrary.InstallAll(_state);
        }

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

    public LuaJitStatistics? Statistics => _executor?.Statistics;

    public LuaJitTier2CodeKind? Tier2CodeKind =>
        _executor?.GetTier2Plan(_closure.Module, _closure.Function.Id)?.CodeKind;

    public int Tier2OptimizationCount =>
        _executor?.GetTier2Plan(_closure.Module, _closure.Function.Id)?.Optimizations.Length ?? 0;

    public int Tier2SpecializedOptimizationCount => _compilationEvents
        .LastOrDefault(jitEvent =>
            jitEvent.Kind == LuaJitEventKind.Tier2CompilationCompleted &&
            jitEvent.FunctionId == _closure.Function.Id)?
        .Tier2CompilationMetrics?.SpecializedOptimizationCount ?? 0;

    public int Tier2DeoptSiteCount =>
        _executor?.GetTier2Plan(_closure.Module, _closure.Function.Id)?.DeoptMap.Length ?? 0;

    public static BackendEvidenceRunner CreateInterpreter(
        LuaIrModule module,
        bool installStandardLibrary) =>
        new(module, new LuaInterpreter(), null, installStandardLibrary);

    public static BackendEvidenceRunner CreateJit(
        LuaIrModule module,
        LuaJitExecutorOptions options,
        bool installStandardLibrary) =>
        new(module, null, new LuaJitExecutor(options), installStandardLibrary);

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

sealed record BackendEvidenceWorkload(
    string Name,
    string Source,
    bool InstallStandardLibrary = false);
