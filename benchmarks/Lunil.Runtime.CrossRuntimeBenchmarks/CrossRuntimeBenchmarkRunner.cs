using System.Globalization;
using System.Runtime.InteropServices;

namespace Lunil.Runtime.CrossRuntimeBenchmarks;

internal sealed class CrossRuntimeBenchmarkRunner
{
    private const int CalibrationSafetyFactor = 4;
    private readonly BenchmarkSuite _suite;
    private readonly IReadOnlyList<BenchmarkWorkload> _workloads;
    private readonly IReadOnlyList<IBenchmarkEngine> _engines;
    private readonly BenchmarkSettings _settings;
    private readonly BenchmarkEnvironment _environment;

    public CrossRuntimeBenchmarkRunner(
        BenchmarkSuite suite,
        IReadOnlyList<BenchmarkWorkload> workloads,
        IReadOnlyList<IBenchmarkEngine> engines,
        BenchmarkSettings settings,
        string rid,
        string commit)
    {
        _suite = suite;
        _workloads = workloads;
        _engines = engines;
        _settings = settings;
        _environment = new BenchmarkEnvironment(
            rid,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            GetProcessorDescription(),
            Environment.ProcessorCount,
            commit,
            DateTimeOffset.UtcNow);
    }

    public CrossRuntimeBenchmarkReport Run()
    {
        var operations = CalibrateOperations();
        var samples = new List<BenchmarkSample>(
            checked(_workloads.Count * _engines.Count * _settings.Rounds));

        for (var workloadIndex = 0; workloadIndex < _workloads.Count; workloadIndex++)
        {
            var workload = _workloads[workloadIndex];
            for (var round = 1; round <= _settings.Rounds; round++)
            {
                foreach (var engine in RotateEngines(round + workloadIndex))
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    var operationCount = operations[(workload.Name, engine.Descriptor.Id)];
                    var measurement = engine.Measure(
                        workload,
                        operationCount,
                        _settings.WarmupCalls);
                    var expected = workload.ExpectedPerOperation * operationCount;
                    var valid = IsExpectedResult(measurement.Result, expected);
                    if (!valid)
                    {
                        throw new InvalidOperationException(
                            $"{engine.Descriptor.DisplayName}/{workload.Name} returned " +
                            $"{measurement.Result.ToString("R", CultureInfo.InvariantCulture)}; " +
                            $"expected {expected.ToString("R", CultureInfo.InvariantCulture)}.");
                    }

                    if (measurement.CpuSeconds <= 0)
                    {
                        throw new InvalidOperationException(
                            $"{engine.Descriptor.DisplayName}/{workload.Name} produced a zero CPU-time " +
                            "sample after calibration.");
                    }

                    var sample = new BenchmarkSample(
                        _environment.Rid,
                        workload.Name,
                        engine.Descriptor.Id,
                        round,
                        operationCount,
                        measurement.CpuSeconds * 1_000_000_000d / operationCount,
                        measurement.SetupCpuSeconds * 1_000d,
                        measurement.Result,
                        expected,
                        valid,
                        measurement.Route,
                        measurement.Telemetry,
                        measurement.ManagedAllocatedBytes is { } allocatedBytes
                            ? allocatedBytes / (double)operationCount
                            : null);
                    samples.Add(sample);
                    Console.WriteLine(
                        $"sample workload={workload.Name}, engine={engine.Descriptor.Id}, " +
                        $"round={round}/{_settings.Rounds}, operations={operationCount}, " +
                        $"cpu_ns_op={sample.CpuNanosecondsPerOperation:F3}, " +
                        $"setup_cpu_ms={sample.SetupCpuMilliseconds:F3}, route={sample.Route}");
                }
            }
        }

        return CrossRuntimeReportWriter.BuildReport(
            _suite,
            _environment,
            _settings,
            _engines.Select(static engine => engine.Descriptor).ToArray(),
            _workloads,
            samples);
    }

    private Dictionary<(string Workload, string Engine), int> CalibrateOperations()
    {
        var result = new Dictionary<(string Workload, string Engine), int>();
        foreach (var workload in _workloads)
        {
            foreach (var engine in _engines)
            {
                var operations = 1;
                while (true)
                {
                    var measurement = engine.Measure(workload, operations, _settings.WarmupCalls);
                    var expected = workload.ExpectedPerOperation * operations;
                    if (!IsExpectedResult(measurement.Result, expected))
                    {
                        throw new InvalidOperationException(
                            $"Calibration failed result validation for " +
                            $"{engine.Descriptor.DisplayName}/{workload.Name}.");
                    }

                    var elapsedMilliseconds = measurement.CalibrationSeconds * 1_000d;
                    if (elapsedMilliseconds >= _settings.TargetCpuMilliseconds * 0.8d)
                    {
                        var verification = engine.Measure(
                            workload,
                            operations,
                            _settings.WarmupCalls);
                        if (!IsExpectedResult(verification.Result, expected))
                        {
                            throw new InvalidOperationException(
                                $"Calibration verification failed result validation for " +
                                $"{engine.Descriptor.DisplayName}/{workload.Name}.");
                        }

                        var verificationMilliseconds = verification.CalibrationSeconds * 1_000d;
                        var minimumMilliseconds = Math.Min(
                            elapsedMilliseconds,
                            verificationMilliseconds);
                        if (minimumMilliseconds >= _settings.TargetCpuMilliseconds * 0.8d)
                        {
                            var measuredOperations = (int)Math.Min(
                                _settings.MaximumOperations,
                                checked((long)operations * CalibrationSafetyFactor));
                            result[(workload.Name, engine.Descriptor.Id)] = measuredOperations;
                            Console.WriteLine(
                                $"calibration workload={workload.Name}, " +
                                $"engine={engine.Descriptor.Id}, operations={operations}, " +
                                $"measured_operations={measuredOperations}, " +
                                $"cpu_ms={elapsedMilliseconds:F3}, " +
                                $"verification_cpu_ms={verificationMilliseconds:F3}, " +
                                $"route={verification.Route}");
                            break;
                        }

                        elapsedMilliseconds = minimumMilliseconds;
                    }

                    if (operations == _settings.MaximumOperations)
                    {
                        throw new InvalidOperationException(
                            $"Unable to resolve stable CPU time for " +
                            $"{engine.Descriptor.DisplayName}/{workload.Name} at " +
                            $"{operations} operations.");
                    }

                    var factor = elapsedMilliseconds <= 0
                        ? 10d
                        : Math.Clamp(
                            _settings.TargetCpuMilliseconds * 1.25d / elapsedMilliseconds,
                            2d,
                            10d);
                    var proposed = checked((long)Math.Ceiling(operations * factor));
                    operations = (int)Math.Min(_settings.MaximumOperations, proposed);
                }
            }
        }

        return result;
    }

    private IEnumerable<IBenchmarkEngine> RotateEngines(int offset)
    {
        for (var index = 0; index < _engines.Count; index++)
        {
            yield return _engines[(index + offset) % _engines.Count];
        }
    }

    private static bool IsExpectedResult(double actual, double expected)
    {
        var tolerance = Math.Max(1e-9, Math.Abs(expected) * 1e-12);
        return double.IsFinite(actual) && Math.Abs(actual - expected) <= tolerance;
    }

    private static string GetProcessorDescription()
    {
        var environmentValue = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue.Trim();
        }

        const string CpuInfoPath = "/proc/cpuinfo";
        if (File.Exists(CpuInfoPath))
        {
            var model = File.ReadLines(CpuInfoPath)
                .FirstOrDefault(static line => line.StartsWith("model name", StringComparison.Ordinal));
            if (model is not null)
            {
                return model[(model.IndexOf(':') + 1)..].Trim();
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sysctl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("machdep.cpu.brand_string");
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is not null)
            {
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    return output;
                }
            }
        }

        return RuntimeInformation.OSArchitecture.ToString();
    }
}
