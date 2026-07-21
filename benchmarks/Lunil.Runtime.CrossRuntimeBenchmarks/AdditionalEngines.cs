using System.Diagnostics;
using System.Globalization;

namespace Lunil.Runtime.CrossRuntimeBenchmarks;

/// <summary>
/// NeoLua is executed out-of-process on net8.0 because NeoLua 1.3.x's type initializer is not
/// compatible with .NET 10. The harness speaks the same cross_runtime_result protocol as the
/// shared Lua harness.
/// </summary>
internal sealed class NeoLuaEngine : IBenchmarkEngine
{
    private readonly string _suiteRoot;
    private readonly string _harnessDll;

    public NeoLuaEngine(string suiteRoot, string harnessDll)
    {
        _suiteRoot = Path.GetFullPath(suiteRoot);
        _harnessDll = Path.GetFullPath(harnessDll);
        if (!File.Exists(_harnessDll))
        {
            throw new FileNotFoundException("NeoLua harness assembly was not found.", _harnessDll);
        }

        Descriptor = new BenchmarkEngineDescriptor(
            "neolua",
            "NeoLua",
            "NeoLua",
            "1.3.19",
            "out-of-process Neo.IronLua net8 harness; source compiled once",
            _harnessDll);
    }

    public BenchmarkEngineDescriptor Descriptor { get; }

    public EngineMeasurement Measure(BenchmarkWorkload workload, int operations, int warmupCalls) =>
        ProtocolProcessEngine.MeasureProcess(
            Descriptor,
            "dotnet",
            ["exec", _harnessDll],
            _suiteRoot,
            workload,
            operations,
            warmupCalls,
            route: "neolua_net8_harness",
            telemetry: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["package"] = "NeoLua",
                ["packageVersion"] = "1.3.19",
                ["harness"] = _harnessDll,
            });
}

/// <summary>
/// Optional engines that speak the NeoLua-style protocol:
/// <c>host [prefix-args...] workload.lua operations warmup</c> and print
/// <c>cross_runtime_result\t...</c>.
/// </summary>
internal sealed class ProtocolProcessEngine : IBenchmarkEngine
{
    private readonly string _suiteRoot;
    private readonly string _fileName;
    private readonly string[] _prefixArguments;
    private readonly string _route;
    private readonly IReadOnlyDictionary<string, string> _telemetry;

    public ProtocolProcessEngine(
        string id,
        string displayName,
        string family,
        string version,
        string configuration,
        string suiteRoot,
        string fileName,
        string[] prefixArguments,
        string route,
        string? executableIdentity = null,
        IReadOnlyDictionary<string, string>? telemetry = null)
    {
        _suiteRoot = Path.GetFullPath(suiteRoot);
        _fileName = fileName;
        _prefixArguments = prefixArguments;
        _route = route;
        _telemetry = telemetry ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(fileName) && !string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fileName, "node", StringComparison.OrdinalIgnoreCase))
        {
            // node/dotnet may be on PATH; for absolute hosts require existence.
            if (Path.IsPathRooted(fileName) && !File.Exists(fileName))
            {
                throw new FileNotFoundException($"{displayName} host was not found.", fileName);
            }
        }

        Descriptor = new BenchmarkEngineDescriptor(
            id,
            displayName,
            family,
            version,
            configuration,
            executableIdentity ?? fileName);
    }

    public BenchmarkEngineDescriptor Descriptor { get; }

    public EngineMeasurement Measure(BenchmarkWorkload workload, int operations, int warmupCalls) =>
        MeasureProcess(
            Descriptor,
            _fileName,
            _prefixArguments,
            _suiteRoot,
            workload,
            operations,
            warmupCalls,
            _route,
            _telemetry);

    public static EngineMeasurement MeasureProcess(
        BenchmarkEngineDescriptor descriptor,
        string fileName,
        IReadOnlyList<string> prefixArguments,
        string suiteRoot,
        BenchmarkWorkload workload,
        int operations,
        int warmupCalls,
        string route,
        IReadOnlyDictionary<string, string>? telemetry = null)
    {
        var workloadPath = Path.GetFullPath(Path.Combine(suiteRoot, workload.File));
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in prefixArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(workloadPath);
        startInfo.ArgumentList.Add(operations.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(warmupCalls.ToString(CultureInfo.InvariantCulture));

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"Failed to start {descriptor.DisplayName}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{descriptor.DisplayName} timed out on {workload.Name}.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{descriptor.DisplayName} failed on {workload.Name}: {error.Trim()}{output.Trim()}");
        }

        var resultLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(static line => line.StartsWith(
                "cross_runtime_result\t",
                StringComparison.Ordinal)) ??
            throw new InvalidOperationException(
                $"{descriptor.DisplayName} returned no benchmark result: {output.Trim()}");
        var values = resultLine.Split('\t', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(static value => value.Split('=', 2))
            .ToDictionary(
                static pair => pair[0],
                static pair => pair[1].Trim(),
                StringComparer.Ordinal);

        var mergedTelemetry = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["processExitCode"] = process.ExitCode.ToString(CultureInfo.InvariantCulture),
        };
        if (telemetry is not null)
        {
            foreach (var pair in telemetry)
            {
                mergedTelemetry[pair.Key] = pair.Value;
            }
        }

        return new EngineMeasurement(
            ParseDouble(values["elapsed"]),
            ParseDouble(values["elapsed"]),
            ParseDouble(values["setup"]),
            ParseDouble(values["result"]),
            route,
            mergedTelemetry,
            ManagedAllocatedBytes: null);
    }

    private static double ParseDouble(string value) =>
        double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    public static bool TryCreateLuau(
        string suiteRoot,
        string? luauExecutable,
        string? hostScript,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ProtocolProcessEngine? engine)
    {
        engine = null;
        if (string.IsNullOrWhiteSpace(luauExecutable) || !File.Exists(luauExecutable) ||
            string.IsNullOrWhiteSpace(hostScript) || !File.Exists(hostScript))
        {
            return false;
        }

        engine = new ProtocolProcessEngine(
            "luau",
            "Luau",
            "Luau",
            "0.623",
            "node luau-host rewriting workload (load sandboxed); Luau CLI 0.623",
            suiteRoot,
            "node",
            [Path.GetFullPath(hostScript), Path.GetFullPath(luauExecutable)],
            route: "luau_host",
            executableIdentity: Path.GetFullPath(luauExecutable),
            telemetry: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["version"] = "0.623",
                ["host"] = Path.GetFullPath(hostScript),
            });
        return true;
    }

    public static bool TryCreateGopherLua(
        string suiteRoot,
        string? hostExecutable,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ProtocolProcessEngine? engine)
    {
        engine = null;
        if (string.IsNullOrWhiteSpace(hostExecutable) || !File.Exists(hostExecutable))
        {
            return false;
        }

        engine = new ProtocolProcessEngine(
            "gopherlua",
            "GopherLua",
            "GopherLua",
            "1.1.1",
            "gopher-lua v1.1.1 Go host; source loaded once",
            suiteRoot,
            Path.GetFullPath(hostExecutable),
            [],
            route: "gopherlua_host",
            telemetry: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["version"] = "1.1.1",
                ["module"] = "github.com/yuin/gopher-lua",
            });
        return true;
    }

    public static bool TryCreateWasmoon(
        string suiteRoot,
        string? hostScript,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ProtocolProcessEngine? engine)
    {
        engine = null;
        if (string.IsNullOrWhiteSpace(hostScript) || !File.Exists(hostScript))
        {
            return false;
        }

        engine = new ProtocolProcessEngine(
            "wasmoon",
            "Wasmoon",
            "Wasmoon",
            "1.16.0",
            "wasmoon 1.16.0 WASM Lua via Node host; source loaded once",
            suiteRoot,
            "node",
            [Path.GetFullPath(hostScript)],
            route: "wasmoon_host",
            executableIdentity: Path.GetFullPath(hostScript),
            telemetry: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["package"] = "wasmoon",
                ["packageVersion"] = "1.16.0",
            });
        return true;
    }

    public static bool TryCreateUniLua(
        string suiteRoot,
        string? harnessDll,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ProtocolProcessEngine? engine)
    {
        engine = null;
        if (string.IsNullOrWhiteSpace(harnessDll) || !File.Exists(harnessDll))
        {
            return false;
        }

        engine = new ProtocolProcessEngine(
            "unilua",
            "UniLua",
            "UniLua",
            "194eb311",
            "out-of-process UniLua net8 harness (Unity deps stripped); pin 194eb311",
            suiteRoot,
            "dotnet",
            ["exec", Path.GetFullPath(harnessDll)],
            route: "unilua_net8_harness",
            executableIdentity: Path.GetFullPath(harnessDll),
            telemetry: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["commit"] = "194eb311191111bfdbc77070de67c100235dc618",
                ["harness"] = Path.GetFullPath(harnessDll),
            });
        return true;
    }
}
