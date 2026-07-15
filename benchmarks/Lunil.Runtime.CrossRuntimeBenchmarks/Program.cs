using System.Globalization;
using System.Text.Json;
using Lunil.Runtime.CrossRuntimeBenchmarks;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

if (args.Contains("--self-test", StringComparer.Ordinal))
{
    CrossRuntimeReportWriter.RunSelfTest();
    return;
}

var suiteRoot = Path.GetFullPath(
    GetOption(args, "--suite-root=") ?? Path.Combine(AppContext.BaseDirectory, "cross-runtime"));
var suitePath = Path.Combine(suiteRoot, "suite.json");
var suite = JsonSerializer.Deserialize(
    File.ReadAllText(suitePath),
    BenchmarkJsonContext.Default.BenchmarkSuite) ??
    throw new InvalidOperationException("Cross-runtime benchmark suite is empty.");
ValidateSuite(suite, suiteRoot);

var rounds = ParsePositiveInt(GetOption(args, "--rounds=") ?? "6", "rounds");
var targetMilliseconds = ParsePositiveDouble(
    GetOption(args, "--target-ms=") ?? "250",
    "target-ms");
var warmupCalls = ParsePositiveInt(
    GetOption(args, "--warmup-calls=") ?? "4",
    "warmup-calls");
var maximumOperations = ParsePositiveInt(
    GetOption(args, "--max-operations=") ?? "1000000",
    "max-operations");
var luaPath = RequireOption(args, "--lua=");
var luaJitPath = RequireOption(args, "--luajit=");
var outputDirectory = Path.GetFullPath(
    GetOption(args, "--output=") ?? Path.Combine("artifacts", "cross-runtime-performance"));
var rid = GetOption(args, "--rid=") ??
    $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}-" +
    System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
var commit = GetOption(args, "--commit=") ?? Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "unknown";
var workloadFilter = SplitFilter(GetOption(args, "--workloads="));
var engineFilter = SplitFilter(GetOption(args, "--engines="));

var workloads = suite.Workloads
    .Where(workload => workloadFilter.Count == 0 || workloadFilter.Contains(workload.Name))
    .ToArray();
if (workloads.Length == 0)
{
    throw new InvalidOperationException("No benchmark workloads matched --workloads.");
}

var harnessPath = Path.Combine(suiteRoot, "external-harness.lua");
IBenchmarkEngine[] allEngines =
[
    new ExternalLuaEngine("lua54", "Native Lua 5.4", luaPath, harnessPath, suiteRoot),
    new ExternalLuaEngine("luajit", "LuaJIT", luaJitPath, harnessPath, suiteRoot),
    new MoonSharpEngine(suiteRoot),
    new LunilBenchmarkEngine(suiteRoot, LunilBenchmarkConfiguration.Interpreter),
    new LunilBenchmarkEngine(suiteRoot, LunilBenchmarkConfiguration.Auto),
    new LunilBenchmarkEngine(suiteRoot, LunilBenchmarkConfiguration.Tier1),
    new LunilBenchmarkEngine(suiteRoot, LunilBenchmarkConfiguration.Tier2),
    new LunilBenchmarkEngine(suiteRoot, LunilBenchmarkConfiguration.LoopOsr),
    new LunilBenchmarkEngine(suiteRoot, LunilBenchmarkConfiguration.PersistedAot),
];
var engines = allEngines
    .Where(engine =>
        engineFilter.Count == 0 ||
        engineFilter.Contains(engine.Descriptor.Id) ||
        engine.Descriptor.Id == suite.BaselineEngine)
    .ToArray();
if (engines.All(engine => engine.Descriptor.Id != suite.BaselineEngine))
{
    throw new InvalidOperationException("The native Lua baseline engine is required.");
}

var settings = new BenchmarkSettings(
    rounds,
    targetMilliseconds,
    warmupCalls,
    maximumOperations,
    suite.BaselineEngine);
var runner = new CrossRuntimeBenchmarkRunner(
    suite,
    workloads,
    engines,
    settings,
    rid,
    commit);
var report = runner.Run();
CrossRuntimeReportWriter.Write(report, outputDirectory, suiteRoot);
if (!report.Completeness.Complete && engineFilter.Count == 0 && workloadFilter.Count == 0)
{
    throw new InvalidOperationException("The full cross-runtime report is incomplete.");
}

Console.WriteLine($"Cross-runtime performance report written to {outputDirectory}");

static void ValidateSuite(BenchmarkSuite suite, string suiteRoot)
{
    if (suite.SchemaVersion != 1)
    {
        throw new InvalidOperationException(
            $"Unsupported cross-runtime benchmark schema {suite.SchemaVersion}.");
    }

    if (suite.Workloads.Count == 0 || suite.RequiredEngines.Count == 0)
    {
        throw new InvalidOperationException("The cross-runtime suite must declare workloads and engines.");
    }

    if (!suite.RequiredEngines.Contains(suite.BaselineEngine, StringComparer.Ordinal))
    {
        throw new InvalidOperationException("The native Lua baseline must be a required engine.");
    }

    if (suite.Workloads.Select(static workload => workload.Name)
        .Distinct(StringComparer.Ordinal).Count() != suite.Workloads.Count)
    {
        throw new InvalidOperationException("Cross-runtime workload names must be unique.");
    }

    foreach (var workload in suite.Workloads)
    {
        if (!double.IsFinite(workload.ExpectedPerOperation) ||
            !File.Exists(Path.Combine(suiteRoot, workload.File)))
        {
            throw new InvalidOperationException($"Invalid workload definition {workload.Name}.");
        }
    }

    if (!File.Exists(Path.Combine(suiteRoot, "external-harness.lua")))
    {
        throw new InvalidOperationException("The external Lua harness is missing.");
    }
}

static HashSet<string> SplitFilter(string? value) => string.IsNullOrWhiteSpace(value)
    ? new HashSet<string>(StringComparer.Ordinal)
    : value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.Ordinal);

static string RequireOption(string[] arguments, string prefix) =>
    GetOption(arguments, prefix) ??
    throw new ArgumentException($"Required option {prefix}<path> was not provided.");

static string? GetOption(string[] arguments, string prefix) => arguments
    .FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

static int ParsePositiveInt(string value, string name)
{
    var result = int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(result, name);
    return result;
}

static double ParsePositiveDouble(string value, string name)
{
    var result = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    if (!double.IsFinite(result) || result <= 0)
    {
        throw new ArgumentOutOfRangeException(name);
    }

    return result;
}
