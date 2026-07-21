using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Lunil.CodeGen.Cil.Jit;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.StandardLibrary;
using Lunil.Syntax.Parsing;
using MoonSharp.Interpreter;

namespace Lunil.Runtime.CrossRuntimeBenchmarks;

internal interface IBenchmarkEngine
{
    BenchmarkEngineDescriptor Descriptor { get; }

    EngineMeasurement Measure(BenchmarkWorkload workload, int operations, int warmupCalls);
}

internal sealed class ExternalLuaEngine : IBenchmarkEngine
{
    private readonly string _executable;
    private readonly string _harnessPath;
    private readonly string _suiteRoot;

    public ExternalLuaEngine(
        string id,
        string displayName,
        string executable,
        string harnessPath,
        string suiteRoot,
        string? family = null)
    {
        _executable = Path.GetFullPath(executable);
        _harnessPath = Path.GetFullPath(harnessPath);
        _suiteRoot = Path.GetFullPath(suiteRoot);
        if (!File.Exists(_executable))
        {
            throw new FileNotFoundException($"{displayName} executable was not found.", _executable);
        }

        Descriptor = new BenchmarkEngineDescriptor(
            id,
            displayName,
            family ?? (id == "lua54" ? "PUC Lua" : id == "luajit" ? "LuaJIT" : displayName),
            ReadVersion(_executable),
            "source loaded once; CPU-timed common Lua harness",
            _executable);
    }

    public BenchmarkEngineDescriptor Descriptor { get; }

    public EngineMeasurement Measure(BenchmarkWorkload workload, int operations, int warmupCalls)
    {
        var workloadPath = Path.GetFullPath(Path.Combine(_suiteRoot, workload.File));
        var startInfo = new ProcessStartInfo
        {
            FileName = _executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(_harnessPath);
        startInfo.ArgumentList.Add(workloadPath);
        startInfo.ArgumentList.Add(operations.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(warmupCalls.ToString(CultureInfo.InvariantCulture));
        startInfo.Environment.Remove("LUA_INIT");
        startInfo.Environment.Remove("LUA_INIT_5_4");

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"Failed to start {Descriptor.DisplayName}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{Descriptor.DisplayName} timed out on {workload.Name}.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Descriptor.DisplayName} failed on {workload.Name}: {error.Trim()}");
        }

        var resultLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(static line => line.StartsWith(
                "cross_runtime_result\t",
                StringComparison.Ordinal)) ??
            throw new InvalidOperationException(
                $"{Descriptor.DisplayName} returned no benchmark result: {output.Trim()}");
        var values = resultLine.Split('\t', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(static value => value.Split('=', 2))
            .ToDictionary(
                static pair => pair[0],
                static pair => pair[1].Trim(),
                StringComparer.Ordinal);
        var jitEnabled = values["jit_enabled"] == "1";
        if (Descriptor.Id == "luajit" && !jitEnabled)
        {
            throw new InvalidOperationException("The pinned LuaJIT executable has JIT compilation disabled.");
        }

        return new EngineMeasurement(
            ParseDouble(values["elapsed"]),
            ParseDouble(values["elapsed"]),
            ParseDouble(values["setup"]),
            ParseDouble(values["result"]),
            Descriptor.Id,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["processExitCode"] = process.ExitCode.ToString(CultureInfo.InvariantCulture),
                ["jitEnabled"] = jitEnabled.ToString(CultureInfo.InvariantCulture),
            },
            ManagedAllocatedBytes: null);
    }

    private static string ReadVersion(string executable)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-v");
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"Failed to query {executable}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to query {executable}: {error.Trim()}");
        }

        return string.Join(' ', (output + " " + error)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static double ParseDouble(string value) =>
        double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
}

internal sealed class MoonSharpEngine : IBenchmarkEngine
{
    private readonly string _suiteRoot;

    public MoonSharpEngine(string suiteRoot)
    {
        _suiteRoot = Path.GetFullPath(suiteRoot);
        var assembly = typeof(Script).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            assembly.GetName().Version?.ToString() ??
            "unknown";
        Descriptor = new BenchmarkEngineDescriptor(
            "moonsharp",
            "MoonSharp",
            "MoonSharp",
            version,
            "CoreModules.Preset_Complete; source loaded once",
            null);
    }

    public BenchmarkEngineDescriptor Descriptor { get; }

    public EngineMeasurement Measure(BenchmarkWorkload workload, int operations, int warmupCalls)
    {
        var sourcePath = Path.GetFullPath(Path.Combine(_suiteRoot, workload.File));
        var setupStarted = Process.GetCurrentProcess().TotalProcessorTime;
        var source = File.ReadAllText(sourcePath);
        var script = new Script(CoreModules.Preset_Complete);
        var function = script.LoadString(source, null, workload.File);
        DynValue result = DynValue.Nil;
        for (var call = 0; call < warmupCalls; call++)
        {
            result = script.Call(function, DynValue.NewNumber(1));
        }

        PrepareManagedHeap();
        var setupSeconds = CpuSecondsSince(setupStarted);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Process.GetCurrentProcess().TotalProcessorTime;
        var stopwatch = Stopwatch.StartNew();
        result = script.Call(function, DynValue.NewNumber(operations));
        stopwatch.Stop();
        var elapsedSeconds = CpuSecondsSince(started);
        var managedAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (result.Type != DataType.Number)
        {
            throw new InvalidOperationException(
                $"MoonSharp workload {workload.Name} returned {result.Type}, not a number.");
        }

        return new EngineMeasurement(
            elapsedSeconds,
            Math.Min(elapsedSeconds, stopwatch.Elapsed.TotalSeconds),
            setupSeconds,
            result.Number,
            "moonsharp_interpreter",
            new Dictionary<string, string>(StringComparer.Ordinal),
            managedAllocatedBytes);
    }

    private static double CpuSecondsSince(TimeSpan started) =>
        (Process.GetCurrentProcess().TotalProcessorTime - started).TotalSeconds;

    private static void PrepareManagedHeap()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
    }
}

internal enum LunilBenchmarkConfiguration
{
    Interpreter,
    Auto,
    Tier1,
    Tier2,
    LoopOsr,
}

internal sealed class LunilBenchmarkEngine : IBenchmarkEngine
{
    private readonly LunilBenchmarkConfiguration _configuration;
    private readonly string _suiteRoot;

    public LunilBenchmarkEngine(string suiteRoot, LunilBenchmarkConfiguration configuration)
    {
        _suiteRoot = Path.GetFullPath(suiteRoot);
        _configuration = configuration;
        var assembly = typeof(LuaInterpreter).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            assembly.GetName().Version?.ToString() ??
            "unknown";
        Descriptor = new BenchmarkEngineDescriptor(
            ConfigurationId(configuration),
            ConfigurationDisplayName(configuration),
            "Lunil",
            version,
            ConfigurationDescription(configuration),
            null);
    }

    public BenchmarkEngineDescriptor Descriptor { get; }

    public EngineMeasurement Measure(BenchmarkWorkload workload, int operations, int warmupCalls)
    {
        var sourcePath = Path.GetFullPath(Path.Combine(_suiteRoot, workload.File));
        var setupStarted = Process.GetCurrentProcess().TotalProcessorTime;
        var source = File.ReadAllText(sourcePath);
        var module = Compile(source, workload.File);
        using var runner = LunilRunner.Create(module, _configuration);
        LuaValue result = LuaValue.Nil;
        for (var call = 0; call < warmupCalls; call++)
        {
            result = runner.Execute(1);
        }

        PrepareManagedHeap();
        var telemetryBeforeMeasurement = runner.Telemetry;
        var setupSeconds = CpuSecondsSince(setupStarted);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Process.GetCurrentProcess().TotalProcessorTime;
        var stopwatch = Stopwatch.StartNew();
        result = runner.Execute(operations);
        stopwatch.Stop();
        var elapsedSeconds = CpuSecondsSince(started);
        var managedAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (result.Kind is not (LuaValueKind.Integer or LuaValueKind.Float))
        {
            throw new InvalidOperationException(
                $"Lunil workload {workload.Name} returned {result.Kind}, not a number.");
        }

        return new EngineMeasurement(
            elapsedSeconds,
            Math.Min(elapsedSeconds, stopwatch.Elapsed.TotalSeconds),
            setupSeconds,
            result.AsFloat(),
            runner.Route,
            runner.TelemetrySince(telemetryBeforeMeasurement),
            managedAllocatedBytes);
    }

    private static LuaIrModule Compile(string source, string sourcePath)
    {
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        if (!lowering.Succeeded || lowering.Module is null)
        {
            throw new InvalidOperationException($"Benchmark Lua source {sourcePath} did not compile.");
        }

        return lowering.Module;
    }

    private static string ConfigurationId(LunilBenchmarkConfiguration configuration) =>
        configuration switch
        {
            LunilBenchmarkConfiguration.Interpreter => "lunil_interpreter",
            LunilBenchmarkConfiguration.Auto => "lunil_auto",
            LunilBenchmarkConfiguration.Tier1 => "lunil_tier1",
            LunilBenchmarkConfiguration.Tier2 => "lunil_tier2",
            LunilBenchmarkConfiguration.LoopOsr => "lunil_loop_osr",
            _ => throw new ArgumentOutOfRangeException(nameof(configuration)),
        };

    private static string ConfigurationDisplayName(LunilBenchmarkConfiguration configuration) =>
        configuration switch
        {
            LunilBenchmarkConfiguration.Interpreter => "Lunil interpreter",
            LunilBenchmarkConfiguration.Auto => "Lunil Auto JIT",
            LunilBenchmarkConfiguration.Tier1 => "Lunil Tier 1",
            LunilBenchmarkConfiguration.Tier2 => "Lunil Tier 2",
            LunilBenchmarkConfiguration.LoopOsr => "Lunil Loop OSR",
            _ => throw new ArgumentOutOfRangeException(nameof(configuration)),
        };

    private static string ConfigurationDescription(LunilBenchmarkConfiguration configuration) =>
        configuration switch
        {
            LunilBenchmarkConfiguration.Interpreter => "reference interpreter; unlimited instruction budget",
            LunilBenchmarkConfiguration.Auto => "release Auto policy; synchronous compilation for repeatability",
            LunilBenchmarkConfiguration.Tier1 => "Tier 1 thresholds=1; Tier 2 and Loop OSR disabled",
            LunilBenchmarkConfiguration.Tier2 => "Tier 1 and Tier 2 thresholds=1; Loop OSR disabled",
            LunilBenchmarkConfiguration.LoopOsr => "function JIT disabled; Loop OSR threshold=1",
            _ => throw new ArgumentOutOfRangeException(nameof(configuration)),
        };

    private static double CpuSecondsSince(TimeSpan started) =>
        (Process.GetCurrentProcess().TotalProcessorTime - started).TotalSeconds;

    private static void PrepareManagedHeap()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
    }
}

internal sealed class LunilRunner : IDisposable
{
    private static readonly LuaInterpreterOptions UnlimitedInterpreter =
        LuaInterpreterOptions.Default with { MaximumInstructionCount = long.MaxValue };

    private readonly LuaState _state;
    private readonly LuaClosure _closure;
    private readonly LuaInterpreter? _interpreter;
    private readonly LuaJitExecutor? _jit;

    private LunilRunner(
        LuaIrModule module,
        LuaInterpreter? interpreter,
        LuaJitExecutor? jit)
    {
        _state = new LuaState();
        LuaStandardLibrary.InstallAll(_state);
        _closure = _state.CreateMainClosure(module);
        _interpreter = interpreter;
        _jit = jit;
    }

    public string Route
    {
        get
        {
            if (_jit is null)
            {
                return "interpreter";
            }

            var statistics = _jit.Statistics;
            if (statistics.Tier2MethodEntries > 0)
            {
                return "tier2";
            }

            if (statistics.LoopOsrEntries > 0)
            {
                return "loop_osr";
            }

            return statistics.CompiledInvocations > 0 ? "tier1" : "interpreter_fallback";
        }
    }

    public IReadOnlyDictionary<string, string> Telemetry
    {
        get
        {
            if (_jit is null)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var jit = _jit.Statistics;
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["compilationCompleted"] = Invariant(jit.CompilationCompleted),
                ["compiledInvocations"] = Invariant(jit.CompiledInvocations),
                ["interpreterFallbacks"] = Invariant(jit.InterpreterFallbacks),
                ["deoptimizations"] = Invariant(jit.Deoptimizations),
                // Any deoptimization inside this hook-free timed workload is unexpected. Keep
                // the explicit field so the gate can reject a missing counter rather than
                // silently treating it as zero.
                ["unexpectedDeoptimizations"] = Invariant(jit.Deoptimizations),
                ["tier2CompilationCompleted"] = Invariant(jit.Tier2CompilationCompleted),
                ["tier2MethodEntries"] = Invariant(jit.Tier2MethodEntries),
                ["tier2UnsupportedExits"] = Invariant(jit.Tier2UnsupportedExits),
                ["directCallEntries"] = Invariant(jit.DirectCallEntries),
                ["directCallCompletions"] = Invariant(jit.DirectCallCompletions),
                ["directCallFallbacks"] = Invariant(jit.DirectCallFallbacks),
                ["directCallInvalidations"] = Invariant(jit.DirectCallInvalidations),
                ["schedulerExitsAvoided"] = Invariant(jit.SchedulerExitsAvoided),
                ["tablePicHits"] = Invariant(jit.TablePicHits),
                ["tablePicMisses"] = Invariant(jit.TablePicMisses),
                ["tablePicInvalidations"] = Invariant(jit.TablePicInvalidations),
                ["loopOsrCompilationCompleted"] = Invariant(jit.LoopOsrCompilationCompleted),
                ["loopOsrEntries"] = Invariant(jit.LoopOsrEntries),
                ["loopOsrGuardFailures"] = Invariant(jit.LoopOsrGuardFailures),
            };
        }
    }

    public IReadOnlyDictionary<string, string> TelemetrySince(
        IReadOnlyDictionary<string, string> baseline)
    {
        var current = Telemetry;
        return current.ToDictionary(
            static pair => pair.Key,
            pair =>
            {
                var currentValue = long.Parse(pair.Value, CultureInfo.InvariantCulture);
                var baselineValue = baseline.TryGetValue(pair.Key, out var value)
                    ? long.Parse(value, CultureInfo.InvariantCulture)
                    : 0;
                return Invariant(checked(currentValue - baselineValue));
            },
            StringComparer.Ordinal);
    }

    public static LunilRunner Create(LuaIrModule module, LunilBenchmarkConfiguration configuration)
    {
        if (configuration == LunilBenchmarkConfiguration.Interpreter)
        {
            return new LunilRunner(
                module,
                new LuaInterpreter(UnlimitedInterpreter),
                null);
        }

        var options = CreateJitOptions(configuration);
        return new LunilRunner(module, null, new LuaJitExecutor(options));
    }

    public LuaValue Execute(int operations)
    {
        LuaValue[] arguments = [LuaValue.FromInteger(operations)];
        var result = _jit is not null
            ? _jit.Execute(_state, _closure, arguments)
            : _interpreter!.Execute(_state, _closure, arguments);
        if (result.Signal != LuaVmSignal.Completed || result.Values.Length != 1)
        {
            throw new InvalidOperationException(
                $"Lunil benchmark did not complete: signal={result.Signal}, values={result.Values.Length}.");
        }

        return result.Values[0];
    }

    public void Dispose()
    {
        _jit?.Dispose();
    }

    private static LuaJitExecutorOptions CreateJitOptions(
        LunilBenchmarkConfiguration configuration)
    {
        var common = LuaJitExecutorOptions.Default with
        {
            SynchronousCompilation = true,
            Interpreter = UnlimitedInterpreter,
        };
        return configuration switch
        {
            LunilBenchmarkConfiguration.Auto => common,
            LunilBenchmarkConfiguration.Tier1 => common with
            {
                FunctionEntryThreshold = 1,
                BackedgeThreshold = 1,
                EnableTier2 = false,
                EnableLoopOsr = false,
            },
            LunilBenchmarkConfiguration.Tier2 => common with
            {
                FunctionEntryThreshold = 1,
                BackedgeThreshold = 1,
                Tier2InvocationThreshold = 1,
                Tier2BackedgeThreshold = 1,
                EnableLoopOsr = false,
            },
            LunilBenchmarkConfiguration.LoopOsr => common with
            {
                FunctionEntryThreshold = int.MaxValue,
                BackedgeThreshold = int.MaxValue,
                EnableTier2 = false,
                LoopOsrBackedgeThreshold = 1,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(configuration)),
        };
    }

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
