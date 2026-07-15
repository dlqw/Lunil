using System.Text.Json.Serialization;

namespace Lunil.Runtime.CrossRuntimeBenchmarks;

internal sealed record BenchmarkSuite(
    int SchemaVersion,
    string BaselineEngine,
    IReadOnlyList<string> RequiredEngines,
    IReadOnlyList<BenchmarkWorkload> Workloads,
    BenchmarkComparisonPolicy Comparison);

internal sealed record BenchmarkComparisonPolicy(
    string ReferenceEngine,
    IReadOnlyList<string> CandidateEngines,
    double MinimumMedianSpeedup,
    double MinimumCi95Lower);

internal sealed record BenchmarkWorkload(
    string Name,
    string Category,
    string File,
    double ExpectedPerOperation,
    string Description);

internal sealed record BenchmarkSettings(
    int Rounds,
    double TargetCpuMilliseconds,
    int WarmupCalls,
    int MaximumOperations,
    string BaselineEngine);

internal sealed record BenchmarkEnvironment(
    string Rid,
    string OperatingSystem,
    string ProcessArchitecture,
    string Framework,
    string Processor,
    int ProcessorCount,
    string Commit,
    DateTimeOffset GeneratedAtUtc);

internal sealed record BenchmarkEngineDescriptor(
    string Id,
    string DisplayName,
    string Family,
    string Version,
    string Configuration,
    string? Executable);

internal sealed record EngineMeasurement(
    double CpuSeconds,
    double CalibrationSeconds,
    double SetupCpuSeconds,
    double Result,
    string Route,
    IReadOnlyDictionary<string, string> Telemetry);

internal sealed record BenchmarkSample(
    string Rid,
    string Workload,
    string Engine,
    int Round,
    int Operations,
    double CpuNanosecondsPerOperation,
    double SetupCpuMilliseconds,
    double Result,
    double ExpectedResult,
    bool Valid,
    string Route,
    IReadOnlyDictionary<string, string> Telemetry);

internal sealed record ConfidenceInterval(double Lower, double Upper);

internal sealed record EngineWorkloadSummary(
    string Workload,
    string Category,
    string Engine,
    int Operations,
    int Samples,
    double MedianCpuNanosecondsPerOperation,
    double P95CpuNanosecondsPerOperation,
    double MedianAbsoluteDeviationCpuNanoseconds,
    double MedianSetupCpuMilliseconds,
    double SpeedupVsNativeLua,
    double SpeedupCi95Lower,
    double SpeedupCi95Upper,
    double? SpeedupVsComparison,
    double? ComparisonSpeedupCi95Lower,
    double? ComparisonSpeedupCi95Upper,
    IReadOnlyDictionary<string, int> Routes,
    bool Valid);

internal sealed record OverallEngineSummary(
    string Engine,
    int Workloads,
    double GeometricMeanSpeedupVsNativeLua,
    double MinimumSpeedupVsNativeLua,
    double MaximumSpeedupVsNativeLua,
    double? GeometricMeanSpeedupVsComparison,
    double? MinimumSpeedupVsComparison,
    double? MaximumSpeedupVsComparison);

internal sealed record BenchmarkPerformanceGateMeasurement(
    string Workload,
    string CandidateEngine,
    double MedianSpeedup,
    double SpeedupCi95Lower,
    double SpeedupCi95Upper,
    bool StableRoute,
    bool CleanTelemetry,
    bool Passed,
    string? Failure);

internal sealed record BenchmarkPerformanceGateSummary(
    string ReferenceEngine,
    IReadOnlyList<string> CandidateEngines,
    double MinimumMedianSpeedup,
    double MinimumCi95Lower,
    IReadOnlyList<BenchmarkPerformanceGateMeasurement> Measurements,
    bool Complete,
    bool Passed);

internal sealed record BenchmarkCompleteness(
    IReadOnlyList<string> RequiredEngines,
    IReadOnlyList<string> MissingEngines,
    IReadOnlyList<string> MissingMeasurements,
    IReadOnlyList<string> InvalidMeasurements,
    bool Complete);

internal sealed record CrossRuntimeBenchmarkReport(
    int SchemaVersion,
    BenchmarkEnvironment Environment,
    BenchmarkSettings Settings,
    IReadOnlyList<BenchmarkEngineDescriptor> Engines,
    IReadOnlyList<BenchmarkWorkload> Workloads,
    IReadOnlyList<BenchmarkSample> Samples,
    IReadOnlyList<EngineWorkloadSummary> Results,
    IReadOnlyList<OverallEngineSummary> Overall,
    BenchmarkPerformanceGateSummary PerformanceGate,
    BenchmarkCompleteness Completeness);

[JsonSerializable(typeof(BenchmarkSuite))]
[JsonSerializable(typeof(CrossRuntimeBenchmarkReport))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext;
