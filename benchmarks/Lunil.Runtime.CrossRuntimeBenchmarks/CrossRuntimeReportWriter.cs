using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lunil.Runtime.CrossRuntimeBenchmarks;

internal static class CrossRuntimeReportWriter
{
    private const int BootstrapIterations = 4_000;
    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
    };

    public static CrossRuntimeBenchmarkReport BuildReport(
        BenchmarkSuite suite,
        BenchmarkEnvironment environment,
        BenchmarkSettings settings,
        IReadOnlyList<BenchmarkEngineDescriptor> engines,
        IReadOnlyList<BenchmarkWorkload> workloads,
        IReadOnlyList<BenchmarkSample> samples)
    {
        var summaries = new List<EngineWorkloadSummary>(workloads.Count * engines.Count);
        foreach (var workload in workloads)
        {
            var baseline = samples.Where(sample =>
                    sample.Workload == workload.Name &&
                    sample.Engine == settings.BaselineEngine)
                .OrderBy(static sample => sample.Round)
                .ToArray();
            var comparison = samples.Where(sample =>
                    sample.Workload == workload.Name &&
                    sample.Engine == suite.Comparison.ReferenceEngine)
                .OrderBy(static sample => sample.Round)
                .ToArray();
            foreach (var engine in engines)
            {
                var selected = samples.Where(sample =>
                        sample.Workload == workload.Name &&
                        sample.Engine == engine.Id)
                    .OrderBy(static sample => sample.Round)
                    .ToArray();
                if (selected.Length == 0 || baseline.Length == 0)
                {
                    continue;
                }

                var timings = selected
                    .Select(static sample => sample.CpuNanosecondsPerOperation)
                    .ToArray();
                var baselineRatios = PairedSpeedups(baseline, selected);
                var speedup = Median(baselineRatios);
                var interval = engine.Id == settings.BaselineEngine
                    ? new ConfidenceInterval(1d, 1d)
                    : BootstrapMedianInterval(
                        baselineRatios,
                        StableSeed(workload.Name + "\0" + engine.Id));
                var comparisonRatios = PairedSpeedups(comparison, selected);
                var comparisonSpeedup = comparisonRatios.Length == 0
                    ? (double?)null
                    : Median(comparisonRatios);
                var comparisonInterval = comparisonRatios.Length == 0
                    ? null
                    : engine.Id == suite.Comparison.ReferenceEngine
                        ? new ConfidenceInterval(1d, 1d)
                        : BootstrapMedianInterval(
                            comparisonRatios,
                            StableSeed(
                                workload.Name + "\0" +
                                suite.Comparison.ReferenceEngine + "\0" + engine.Id));
                var median = Median(timings);
                summaries.Add(new EngineWorkloadSummary(
                    workload.Name,
                    workload.Category,
                    engine.Id,
                    selected[0].Operations,
                    selected.Length,
                    median,
                    Percentile(timings, 0.95d),
                    Median(timings.Select(value => Math.Abs(value - median)).ToArray()),
                    Median(selected.Select(static sample => sample.SetupCpuMilliseconds).ToArray()),
                    speedup,
                    interval.Lower,
                    interval.Upper,
                    comparisonSpeedup,
                    comparisonInterval?.Lower,
                    comparisonInterval?.Upper,
                    selected.GroupBy(static sample => sample.Route, StringComparer.Ordinal)
                        .OrderBy(static group => group.Key, StringComparer.Ordinal)
                        .ToDictionary(
                            static group => group.Key,
                            static group => group.Count(),
                            StringComparer.Ordinal),
                    selected.All(static sample => sample.Valid)));
            }
        }

        var overall = engines.Select(engine =>
        {
            var engineSummaries = summaries
                .Where(summary => summary.Engine == engine.Id && summary.Valid)
                .ToArray();
            var values = engineSummaries
                .Select(static summary => summary.SpeedupVsNativeLua)
                .ToArray();
            var comparisonValues = engineSummaries
                .Where(static summary => summary.SpeedupVsComparison.HasValue)
                .Select(static summary => summary.SpeedupVsComparison!.Value)
                .ToArray();
            return new OverallEngineSummary(
                engine.Id,
                values.Length,
                values.Length == 0 ? 0d : GeometricMean(values),
                values.Length == 0 ? 0d : values.Min(),
                values.Length == 0 ? 0d : values.Max(),
                comparisonValues.Length == 0 ? null : GeometricMean(comparisonValues),
                comparisonValues.Length == 0 ? null : comparisonValues.Min(),
                comparisonValues.Length == 0 ? null : comparisonValues.Max());
        }).ToArray();

        var performanceGate = BuildPerformanceGate(
            suite,
            workloads,
            samples,
            summaries);

        var engineIds = engines.Select(static engine => engine.Id).ToHashSet(StringComparer.Ordinal);
        var missingEngines = suite.RequiredEngines
            .Where(required => !engineIds.Contains(required))
            .ToArray();
        var missingMeasurements = (
            from workload in workloads
            from engine in engines
            where !samples.Any(sample =>
                sample.Workload == workload.Name &&
                sample.Engine == engine.Id)
            select $"{workload.Name}/{engine.Id}").ToArray();
        var invalidMeasurements = samples
            .Where(static sample => !sample.Valid)
            .Select(static sample => $"{sample.Workload}/{sample.Engine}/round-{sample.Round}")
            .ToArray();
        var completeness = new BenchmarkCompleteness(
            suite.RequiredEngines,
            missingEngines,
            missingMeasurements,
            invalidMeasurements,
            missingEngines.Length == 0 &&
            missingMeasurements.Length == 0 &&
            invalidMeasurements.Length == 0 &&
            summaries.All(summary => summary.Samples == settings.Rounds));

        return new CrossRuntimeBenchmarkReport(
            2,
            environment,
            settings,
            engines,
            workloads,
            samples,
            summaries,
            overall,
            performanceGate,
            completeness);
    }

    public static void Write(
        CrossRuntimeBenchmarkReport report,
        string outputDirectory,
        string suiteRoot)
    {
        Directory.CreateDirectory(outputDirectory);
        var json = JsonSerializer.Serialize(report, BenchmarkJsonContext.Default.CrossRuntimeBenchmarkReport);
        File.WriteAllText(Path.Combine(outputDirectory, "report.json"), json + Environment.NewLine);
        File.WriteAllText(Path.Combine(outputDirectory, "summary.csv"), CreateSummaryCsv(report));
        File.WriteAllText(Path.Combine(outputDirectory, "samples.csv"), CreateSamplesCsv(report));
        File.WriteAllText(Path.Combine(outputDirectory, "report.md"), CreateMarkdown(report));
        WriteProvenance(report, outputDirectory, suiteRoot);
    }

    public static void RunSelfTest()
    {
        var suite = new BenchmarkSuite(
            1,
            "lua54",
            ["lua54", "moonsharp", "candidate"],
            [new BenchmarkWorkload("synthetic", "test", "synthetic.lua", 1d, "Synthetic")],
            new BenchmarkComparisonPolicy("moonsharp", ["candidate"], 1.1d, 1d));
        var environment = new BenchmarkEnvironment(
            "self-test",
            "test",
            "test",
            "test",
            "test",
            1,
            "self-test",
            DateTimeOffset.UnixEpoch);
        var settings = new BenchmarkSettings(4, 1d, 1, 100, "lua54");
        BenchmarkEngineDescriptor[] engines =
        [
            new("lua54", "Lua", "PUC Lua", "test", "test", null),
            new("moonsharp", "MoonSharp", "MoonSharp", "test", "test", null),
            new("candidate", "Candidate", "Test", "test", "test", null),
        ];
        var samples = new List<BenchmarkSample>();
        for (var round = 1; round <= 4; round++)
        {
            samples.Add(Synthetic("lua54", round, 100d + round));
            samples.Add(Synthetic("moonsharp", round, 80d + round));
            samples.Add(Synthetic("candidate", round, 50d + round / 2d));
        }

        var report = BuildReport(
            suite,
            environment,
            settings,
            engines,
            suite.Workloads,
            samples);
        var candidate = report.Results.Single(static result => result.Engine == "candidate");
        if (!report.Completeness.Complete || !report.PerformanceGate.Passed ||
            Math.Abs(candidate.SpeedupVsNativeLua - 2d) > 1e-9 ||
            candidate.SpeedupVsComparison is not { } comparisonSpeedup ||
            comparisonSpeedup <= 1.5d)
        {
            throw new InvalidOperationException("Cross-runtime report self-test failed.");
        }

        var markdown = CreateMarkdown(report);
        if (!markdown.Contains("Candidate", StringComparison.Ordinal) ||
            !CreateSummaryCsv(report).Contains("candidate", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Cross-runtime report rendering self-test failed.");
        }

        var missingTelemetry = samples
            .Select(sample => sample.Engine == "candidate" && sample.Round == 1
                ? sample with { Telemetry = new Dictionary<string, string>() }
                : sample)
            .ToArray();
        var rejected = BuildReport(
            suite,
            environment,
            settings,
            engines,
            suite.Workloads,
            missingTelemetry);
        if (rejected.PerformanceGate.Passed ||
            rejected.PerformanceGate.Measurements.Single().CleanTelemetry)
        {
            throw new InvalidOperationException(
                "Cross-runtime report self-test admitted missing candidate telemetry.");
        }

        Console.WriteLine("Cross-runtime report self-test passed.");
    }

    private static BenchmarkSample Synthetic(string engine, int round, double nanoseconds) =>
        new(
            "self-test",
            "synthetic",
            engine,
            round,
            1,
            nanoseconds,
            1d,
            1d,
            1d,
            true,
            engine,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["interpreterFallbacks"] = "0",
                ["deoptimizations"] = "0",
                ["unexpectedDeoptimizations"] = "0",
                ["tier2UnsupportedExits"] = "0",
            });

    private static string CreateSummaryCsv(CrossRuntimeBenchmarkReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "rid,workload,category,engine,operations,samples,median_cpu_ns_op,p95_cpu_ns_op," +
            "mad_cpu_ns,median_setup_cpu_ms,speedup_vs_native_lua,speedup_ci95_lower," +
            "speedup_ci95_upper,speedup_vs_moonsharp,moonsharp_speedup_ci95_lower," +
            "moonsharp_speedup_ci95_upper,routes,valid");
        foreach (var result in report.Results)
        {
            AppendCsvRow(builder,
            [
                report.Environment.Rid,
                result.Workload,
                result.Category,
                result.Engine,
                Invariant(result.Operations),
                Invariant(result.Samples),
                Invariant(result.MedianCpuNanosecondsPerOperation),
                Invariant(result.P95CpuNanosecondsPerOperation),
                Invariant(result.MedianAbsoluteDeviationCpuNanoseconds),
                Invariant(result.MedianSetupCpuMilliseconds),
                Invariant(result.SpeedupVsNativeLua),
                Invariant(result.SpeedupCi95Lower),
                Invariant(result.SpeedupCi95Upper),
                Invariant(result.SpeedupVsComparison),
                Invariant(result.ComparisonSpeedupCi95Lower),
                Invariant(result.ComparisonSpeedupCi95Upper),
                string.Join(';', result.Routes.Select(static pair => $"{pair.Key}:{pair.Value}")),
                result.Valid.ToString(CultureInfo.InvariantCulture),
            ]);
        }

        return builder.ToString();
    }

    private static BenchmarkPerformanceGateSummary BuildPerformanceGate(
        BenchmarkSuite suite,
        IReadOnlyList<BenchmarkWorkload> workloads,
        IReadOnlyList<BenchmarkSample> samples,
        IReadOnlyList<EngineWorkloadSummary> summaries)
    {
        var measurements = new List<BenchmarkPerformanceGateMeasurement>(
            workloads.Count * suite.Comparison.CandidateEngines.Count);
        foreach (var workload in workloads)
        {
            foreach (var candidate in suite.Comparison.CandidateEngines)
            {
                var summary = summaries.FirstOrDefault(item =>
                    item.Workload == workload.Name && item.Engine == candidate);
                if (summary?.SpeedupVsComparison is not { } speedup ||
                    summary.ComparisonSpeedupCi95Lower is not { } lower ||
                    summary.ComparisonSpeedupCi95Upper is not { } upper)
                {
                    continue;
                }

                var candidateSamples = samples.Where(sample =>
                        sample.Workload == workload.Name && sample.Engine == candidate)
                    .ToArray();
                var routes = candidateSamples
                    .Select(static sample => sample.Route)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var stableRoute = routes.Length == 1 &&
                    !routes[0].Contains("fallback", StringComparison.Ordinal);
                var cleanTelemetry = candidateSamples.All(static sample =>
                    IsZero(sample.Telemetry, "interpreterFallbacks") &&
                    IsZero(sample.Telemetry, "deoptimizations") &&
                    IsZero(sample.Telemetry, "unexpectedDeoptimizations") &&
                    IsZero(sample.Telemetry, "tier2UnsupportedExits"));
                var failures = new List<string>(4);
                if (speedup < suite.Comparison.MinimumMedianSpeedup)
                {
                    failures.Add("median speedup");
                }

                if (lower < suite.Comparison.MinimumCi95Lower)
                {
                    failures.Add("CI95 lower bound");
                }

                if (!stableRoute)
                {
                    failures.Add("route");
                }

                if (!cleanTelemetry)
                {
                    failures.Add("telemetry");
                }

                measurements.Add(new BenchmarkPerformanceGateMeasurement(
                    workload.Name,
                    candidate,
                    speedup,
                    lower,
                    upper,
                    stableRoute,
                    cleanTelemetry,
                    failures.Count == 0,
                    failures.Count == 0 ? null : string.Join(", ", failures)));
            }
        }

        var expectedMeasurements = workloads.Count * suite.Comparison.CandidateEngines.Count;
        var complete = measurements.Count == expectedMeasurements;
        return new BenchmarkPerformanceGateSummary(
            suite.Comparison.ReferenceEngine,
            suite.Comparison.CandidateEngines,
            suite.Comparison.MinimumMedianSpeedup,
            suite.Comparison.MinimumCi95Lower,
            measurements,
            complete,
            complete && measurements.All(static measurement => measurement.Passed));

        static bool IsZero(
            IReadOnlyDictionary<string, string> telemetry,
            string key) =>
            telemetry.TryGetValue(key, out var value) &&
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed == 0;
    }

    private static string CreateSamplesCsv(CrossRuntimeBenchmarkReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "rid,workload,engine,round,operations,cpu_ns_op,setup_cpu_ms,result,expected," +
            "valid,route,telemetry_json");
        foreach (var sample in report.Samples)
        {
            AppendCsvRow(builder,
            [
                sample.Rid,
                sample.Workload,
                sample.Engine,
                Invariant(sample.Round),
                Invariant(sample.Operations),
                Invariant(sample.CpuNanosecondsPerOperation),
                Invariant(sample.SetupCpuMilliseconds),
                Invariant(sample.Result),
                Invariant(sample.ExpectedResult),
                sample.Valid.ToString(CultureInfo.InvariantCulture),
                sample.Route,
                JsonSerializer.Serialize(sample.Telemetry),
            ]);
        }

        return builder.ToString();
    }

    private static string CreateMarkdown(CrossRuntimeBenchmarkReport report)
    {
        var names = report.Engines.ToDictionary(
            static engine => engine.Id,
            static engine => engine.DisplayName,
            StringComparer.Ordinal);
        var builder = new StringBuilder();
        builder.AppendLine("# Cross-runtime Lua performance report");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- RID: `{report.Environment.Rid}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Commit: `{report.Environment.Commit}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- OS: {report.Environment.OperatingSystem}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Architecture: `{report.Environment.ProcessArchitecture}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- .NET: {report.Environment.Framework}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- CPU: {report.Environment.Processor}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Samples: {report.Settings.Rounds} balanced rounds per engine/workload");
        builder.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- Calibration floor: {report.Settings.TargetCpuMilliseconds:F0} ms CPU time; " +
            $"measured batches use a 4× safety factor"));
        builder.AppendLine("- Baseline: native PUC Lua (`lua54`), normalized to `1.000x`");
        builder.AppendLine();
        builder.AppendLine("## Method");
        builder.AppendLine();
        builder.AppendLine(
            "Every engine runs the same Lua source from `benchmarks/cross-runtime/workloads`. " +
            "Source loading, runtime creation, artifact creation/loading, and warmup are outside " +
            "the primary interval and are reported separately as setup CPU time. The primary " +
            "metric is process CPU nanoseconds per logical workload operation. Operation counts " +
            "are calibrated per engine/workload, engine order rotates by round, every result is " +
            "checked against the manifest, and speedups are the median of matched balanced-round " +
            "ratios with deterministic paired bootstrap confidence intervals. Values above " +
            "`1.000x` are faster than native Lua.");
        builder.AppendLine();
        builder.AppendLine("## Overall");
        builder.AppendLine();
        builder.AppendLine("| Engine | Workloads | Geomean vs native Lua | Native range | Geomean vs MoonSharp |");
        builder.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var overall in report.Overall.OrderByDescending(
                     static value => value.GeometricMeanSpeedupVsNativeLua))
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {Escape(names[overall.Engine])} | {overall.Workloads} | " +
                $"{overall.GeometricMeanSpeedupVsNativeLua:F3}x | " +
                $"{overall.MinimumSpeedupVsNativeLua:F3}x–" +
                $"{overall.MaximumSpeedupVsNativeLua:F3}x | " +
                $"{FormatOptionalRatio(overall.GeometricMeanSpeedupVsComparison)} |");
        }

        foreach (var workload in report.Workloads)
        {
            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"## {Escape(workload.Name)}");
            builder.AppendLine();
            builder.AppendLine(workload.Description);
            builder.AppendLine();
            builder.AppendLine(
                "| Engine | Median CPU | p95 CPU | Setup CPU | vs native Lua (95% CI) | " +
                "vs MoonSharp (95% CI) | " +
                "Operations | Observed route |");
            builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|");
            foreach (var result in report.Results
                         .Where(value => value.Workload == workload.Name)
                         .OrderBy(static value => value.MedianCpuNanosecondsPerOperation))
            {
                var routes = string.Join(
                    ", ",
                    result.Routes.Select(static pair => $"`{pair.Key}` ×{pair.Value}"));
                builder.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"| {Escape(names[result.Engine])} | " +
                    $"{FormatDuration(result.MedianCpuNanosecondsPerOperation)} | " +
                    $"{FormatDuration(result.P95CpuNanosecondsPerOperation)} | " +
                    $"{result.MedianSetupCpuMilliseconds:F3} ms | " +
                    $"{result.SpeedupVsNativeLua:F3}x " +
                    $"({result.SpeedupCi95Lower:F3}–{result.SpeedupCi95Upper:F3}) | " +
                    $"{FormatOptionalInterval(result.SpeedupVsComparison, result.ComparisonSpeedupCi95Lower, result.ComparisonSpeedupCi95Upper)} | " +
                    $"{result.Operations} | {routes} |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Lunil versus MoonSharp stability gate");
        builder.AppendLine();
        builder.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "Candidates must reach at least {0:F2}x paired median speedup and a paired bootstrap CI95 lower " +
            "bound of {1:F2}x on every workload, with one stable compiled route and clean " +
            "fallback/deoptimization telemetry.",
            report.PerformanceGate.MinimumMedianSpeedup,
            report.PerformanceGate.MinimumCi95Lower));
        builder.AppendLine();
        builder.AppendLine("| Workload | Candidate | Median speedup | CI95 | Route | Telemetry | Result |");
        builder.AppendLine("|---|---|---:|---:|---|---|---|");
        foreach (var gate in report.PerformanceGate.Measurements)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {Escape(gate.Workload)} | {Escape(names[gate.CandidateEngine])} | " +
                $"{gate.MedianSpeedup:F3}x | {gate.SpeedupCi95Lower:F3}–" +
                $"{gate.SpeedupCi95Upper:F3} | " +
                $"{(gate.StableRoute ? "stable" : "unstable")} | " +
                $"{(gate.CleanTelemetry ? "clean" : "fallback/deopt")} | " +
                $"{(gate.Passed ? "PASS" : "FAIL")} |");
        }
        builder.AppendLine();
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"Gate: **{(report.PerformanceGate.Passed ? "PASS" : report.PerformanceGate.Complete ? "FAIL" : "INCOMPLETE")}**.");

        builder.AppendLine();
        builder.AppendLine("## Runtime inventory");
        builder.AppendLine();
        builder.AppendLine("| Engine | Family | Version | Configuration |");
        builder.AppendLine("|---|---|---|---|");
        foreach (var engine in report.Engines)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {Escape(engine.DisplayName)} | {Escape(engine.Family)} | " +
                $"{Escape(engine.Version)} | {Escape(engine.Configuration)} |");
        }

        builder.AppendLine();
        builder.AppendLine(
            CultureInfo.InvariantCulture,
            $"Completeness: **{(report.Completeness.Complete ? "complete" : "incomplete")}**.");
        return builder.ToString();
    }

    private static void WriteProvenance(
        CrossRuntimeBenchmarkReport report,
        string outputDirectory,
        string suiteRoot)
    {
        var files = Directory.EnumerateFiles(suiteRoot, "*", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToDictionary(
                path => Path.GetRelativePath(suiteRoot, path).Replace('\\', '/'),
                static path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                StringComparer.Ordinal);
        var executables = report.Engines
            .Where(static engine => engine.Executable is not null)
            .ToDictionary(
                static engine => engine.Id,
                static engine => Convert.ToHexString(SHA256.HashData(
                    File.ReadAllBytes(engine.Executable!))).ToLowerInvariant(),
                StringComparer.Ordinal);
        var provenance = new
        {
            schemaVersion = 1,
            report.Environment.Commit,
            report.Environment.Rid,
            sourceFiles = files,
            runtimeExecutables = executables,
        };
        File.WriteAllText(
            Path.Combine(outputDirectory, "provenance.json"),
            JsonSerializer.Serialize(provenance, IndentedJsonOptions) +
            Environment.NewLine);
    }

    private static double[] PairedSpeedups(
        IReadOnlyList<BenchmarkSample> reference,
        IReadOnlyList<BenchmarkSample> candidate)
    {
        var candidateByRound = candidate.ToDictionary(static sample => sample.Round);
        return reference
            .Where(sample => candidateByRound.ContainsKey(sample.Round))
            .Select(sample =>
                sample.CpuNanosecondsPerOperation /
                candidateByRound[sample.Round].CpuNanosecondsPerOperation)
            .ToArray();
    }

    private static ConfidenceInterval BootstrapMedianInterval(
        double[] values,
        int seed)
    {
        if (values.Length == 0)
        {
            throw new ArgumentException("At least one paired ratio is required.", nameof(values));
        }

        var random = new Random(seed);
        var ratios = new double[BootstrapIterations];
        var sample = new double[values.Length];
        for (var iteration = 0; iteration < ratios.Length; iteration++)
        {
            for (var index = 0; index < sample.Length; index++)
            {
                sample[index] = values[random.Next(values.Length)];
            }

            ratios[iteration] = Median(sample);
        }

        return new ConfidenceInterval(Percentile(ratios, 0.025d), Percentile(ratios, 0.975d));
    }

    private static double Median(IReadOnlyCollection<double> values) => Percentile(values, 0.5d);

    private static double Percentile(IReadOnlyCollection<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            throw new ArgumentException("At least one value is required.", nameof(values));
        }

        var ordered = values.Order().ToArray();
        var rank = percentile * (ordered.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return ordered[lower];
        }

        return ordered[lower] + (ordered[upper] - ordered[lower]) * (rank - lower);
    }

    private static double GeometricMean(IEnumerable<double> values) =>
        Math.Exp(values.Average(Math.Log));

    private static int StableSeed(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToInt32(hash, 0);
    }

    private static string FormatDuration(double nanoseconds) => nanoseconds >= 1_000_000d
        ? $"{nanoseconds / 1_000_000d:F3} ms"
        : nanoseconds >= 1_000d
            ? $"{nanoseconds / 1_000d:F3} µs"
            : $"{nanoseconds:F3} ns";

    private static string FormatOptionalRatio(double? ratio) => ratio.HasValue
        ? string.Create(CultureInfo.InvariantCulture, $"{ratio.Value:F3}x")
        : "—";

    private static string FormatOptionalInterval(
        double? ratio,
        double? lower,
        double? upper) => ratio.HasValue && lower.HasValue && upper.HasValue
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"{ratio.Value:F3}x ({lower.Value:F3}–{upper.Value:F3})")
        : "—";

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);

    private static void AppendCsvRow(StringBuilder builder, IEnumerable<string> values) =>
        builder.AppendLine(string.Join(',', values.Select(EscapeCsv)));

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static string Invariant(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Invariant(double? value) => value.HasValue
        ? Invariant(value.Value)
        : string.Empty;

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
