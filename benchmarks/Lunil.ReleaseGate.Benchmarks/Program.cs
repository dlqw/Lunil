using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using Lunil.Hosting;
using Lunil.Runtime.Values;
#if NET10_0 && !LUNIL_RELEASE_BASELINE
using Lunil.Godot;
using Lunil.Unity;
#endif

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

var mode = GetOption(args, "--mode=") ?? "interpreter";
var operations = ParsePositive(GetOption(args, "--operations=") ?? "24", "operations");
var warmup = ParsePositive(GetOption(args, "--warmup=") ?? "160", "warmup");
var samples = ParsePositive(GetOption(args, "--samples=") ?? "5", "samples");
var reverseOrder = args.Contains("--reverse-order", StringComparer.Ordinal);

if (string.Equals(mode, "adapter", StringComparison.Ordinal))
{
#if NET10_0 && !LUNIL_RELEASE_BASELINE
    RunAdapterBenchmarks(operations, warmup, samples, reverseOrder);
    return;
#else
    throw new PlatformNotSupportedException(
        "Adapter measurements require the current net10.0 fixture.");
#endif
}

var backend = mode switch
{
    "interpreter" => LuaHostExecutionBackend.Interpreter,
    "auto" => LuaHostExecutionBackend.Auto,
    _ => throw new ArgumentException($"Unknown benchmark mode '{mode}'."),
};
#if !NET10_0
if (backend != LuaHostExecutionBackend.Interpreter)
{
    throw new PlatformNotSupportedException("The portable fixture only measures the interpreter.");
}
#endif

var assemblyFramework = typeof(LuaHost).Assembly
    .GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "unknown";
Console.WriteLine(
    $"release_perf_environment mode={mode},framework={assemblyFramework.Replace(',', ';')}," +
    $"runtime={Environment.Version},operations={operations},warmup={warmup},samples={samples}");

BenchmarkWorkload[] workloads =
[
    new("arithmetic", """
        local total = 0
        for index = 1, 5000 do total = total + index * 3 - 1 end
        return total
        """),
    new("control_flow", """
        local total = 0
        local index = 0
        while index < 5000 do
            if (index & 1) == 0 then total = total + index else total = total - 1 end
            index = index + 1
        end
        return total
        """),
    new("function_calls", """
        local function add(left, right) return left + right end
        local total = 0
        for index = 1, 1500 do total = add(total, index) end
        return total
        """),
    new("table_access", """
        local values = {}
        local total = 0
        for index = 1, 2000 do
            values[index] = index
            values.field = index
            total = total + values[index] + values.field
        end
        return total
        """),
];
if (reverseOrder)
{
    Array.Reverse(workloads);
}

foreach (var workload in workloads)
{
    var options = new LuaHostOptions
    {
        ExecutionBackend = backend,
        InstallStandardLibrary = false,
    };
    using var host = new LuaHost(options);
    var compilation = host.CompileUtf8(workload.Source, "=release-performance-" + workload.Name);
    if (!compilation.Succeeded)
    {
        throw new InvalidOperationException($"Compilation failed for {workload.Name}.");
    }

    long expected = 0;
    for (var iteration = 0; iteration < warmup; iteration++)
    {
        expected = Execute(host, compilation, expected, iteration != 0);
    }

    var measurements = new double[samples];
    for (var sample = 0; sample < measurements.Length; sample++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var stopwatch = Stopwatch.StartNew();
        for (var operation = 0; operation < operations; operation++)
        {
            _ = Execute(host, compilation, expected, verify: true);
        }

        stopwatch.Stop();
        measurements[sample] = stopwatch.Elapsed.TotalNanoseconds / operations;
    }

    Array.Sort(measurements);
    var median = Median(measurements);
    Console.WriteLine(
        $"release_perf workload={workload.Name},ns_op={median:R},checksum={expected}," +
        $"selected_backend={host.SelectedExecutionBackend.ToString().ToLowerInvariant()}");
}

static long Execute(
    LuaHost host,
    Lunil.Compiler.LuaCompilationResult compilation,
    long expected,
    bool verify)
{
    var result = host.Execute(compilation);
    if (result.Signal != Lunil.Runtime.Execution.LuaVmSignal.Completed ||
        result.Values.Length != 1)
    {
        throw new InvalidOperationException("The performance workload did not complete exactly once.");
    }

    var value = result.Values[0].Kind == LuaValueKind.Integer
        ? result.Values[0].AsInteger()
        : checked((long)result.Values[0].AsFloat());
    if (verify && value != expected)
    {
        throw new InvalidOperationException(
            $"The performance workload result changed from {expected} to {value}.");
    }

    return value;
}

#if NET10_0 && !LUNIL_RELEASE_BASELINE
static void RunAdapterBenchmarks(int operations, int warmup, int samples, bool reverseOrder)
{
    const int CallbacksPerFrame = 64;
    var operationCount = checked(operations * 100);
    var warmupCount = checked(warmup * 10);
    using var neutralHost = CreateGameLoopHost();
    using var unityHost = CreateGameLoopHost();
    using var godotHost = CreateGameLoopHost();
    var unity = new LuaUnityDispatcher();
    using var godot = new LuaGodotDispatcher();
    var callbackCount = 0L;
    Action callback = () => callbackCount++;

    Action neutralFrame = () =>
    {
        for (var index = 0; index < CallbacksPerFrame; index++)
            neutralHost.PublishAtFrameBoundary(_ => callback());
        _ = neutralHost.Tick();
    };
    Action unityFrame = () =>
    {
        for (var index = 0; index < CallbacksPerFrame; index++)
            unityHost.PublishAtFrameBoundary(_ => callback());
        if (unity.Drain(CallbacksPerFrame) != 0)
            throw new InvalidOperationException("Unity dispatcher contained unexpected work.");
        _ = unityHost.Tick();
    };
    Action godotFrame = () =>
    {
        for (var index = 0; index < CallbacksPerFrame; index++)
            godotHost.PublishAtFrameBoundary(_ => callback());
        if (godot.Drain(CallbacksPerFrame) != 0)
            throw new InvalidOperationException("Godot dispatcher contained unexpected work.");
        _ = godotHost.Tick();
    };

    AdapterBenchmark[] benchmarks =
    {
        new AdapterBenchmark("neutral", neutralFrame),
        new AdapterBenchmark("unity", unityFrame),
        new AdapterBenchmark("godot", godotFrame),
    };
    if (reverseOrder)
    {
        Array.Reverse(benchmarks);
    }

    foreach (var benchmark in benchmarks)
    {
        for (var index = 0; index < warmupCount; index++) benchmark.Frame();
        var measurements = new double[samples];
        for (var sample = 0; sample < samples; sample++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var stopwatch = Stopwatch.StartNew();
            for (var operation = 0; operation < operationCount; operation++) benchmark.Frame();
            stopwatch.Stop();
            measurements[sample] = stopwatch.Elapsed.TotalNanoseconds / operationCount;
        }

        Array.Sort(measurements);
        Console.WriteLine(
            $"adapter_perf name={benchmark.Name},ns_frame={Median(measurements):R}," +
            $"callbacks_per_frame={CallbacksPerFrame},checksum={callbackCount}");
    }

    unity.Close();
}

static LuaGameLoopHost CreateGameLoopHost() => new(new LuaGameLoopHostOptions
{
    HostOptions = new LuaHostOptions
    {
        ExecutionBackend = LuaHostExecutionBackend.Interpreter,
        InstallStandardLibrary = false,
    },
});
#endif

static double Median(double[] ordered) => ordered.Length % 2 == 0
    ? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2d
    : ordered[ordered.Length / 2];

static int ParsePositive(string value, string name)
{
    var result = int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(result, name);
    return result;
}

static string? GetOption(string[] arguments, string prefix) => arguments
    .FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

internal sealed record BenchmarkWorkload(string Name, string Source);

#if NET10_0 && !LUNIL_RELEASE_BASELINE
internal sealed record AdapterBenchmark(string Name, Action Frame);
#endif
