using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Lunil.Analysis;
using Lunil.Compiler;
using Lunil.IR.Canonical;
using Lunil.Core.Text;
using Lunil.Syntax.Parsing;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

var sizes = ParseSizes(GetOption(args, "--sizes=") ?? "30720,307200,1048576,2097152");
var session = new LuaFrontEndSession(new LuaCompilerOptions
{
    Analysis = LuaAnalysisOptions.Default with
    {
        MaximumTypeCount = 1_000_000,
        MaximumConstraintCount = 1_000_000,
        MaximumControlFlowBlockCount = 1_000_000,
        MaximumGenericInstantiationCount = 1_000_000,
    },
    Verifier = LuaIrVerifierOptions.Default with
    {
        MaximumFunctions = 100_000,
        MaximumInstructionsPerFunction = 1_000_000,
        MaximumRegistersPerFunction = 1_000_000,
    },
});

_ = session.Process(
    LuaSourceDocument.FromUtf8(CreateSource(Math.Min(sizes[0], 30 * 1024))),
    LuaFrontEndStage.Verification);

Console.WriteLine(
    $"frontend_perf_environment runtime={Environment.Version},process_arch={RuntimeInformation.ProcessArchitecture}," +
    $"sizes={string.Join(';', sizes)}");
foreach (var size in sizes)
{
    var document = LuaSourceDocument.FromUtf8(CreateSource(size), $"=frontend-{size}");
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var managedBefore = GC.GetTotalMemory(forceFullCollection: false);
    using var process = Process.GetCurrentProcess();
    process.Refresh();
    var peakWorkingSet = process.WorkingSet64;
    LuaFrontEndSnapshot? snapshot = null;
    var total = Stopwatch.StartNew();
    var worker = Task.Run(() =>
        snapshot = session.Process(document, LuaFrontEndStage.Verification));
    while (!worker.IsCompleted)
    {
        process.Refresh();
        peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        Thread.Yield();
    }

    await worker.ConfigureAwait(false);
    total.Stop();
    process.Refresh();
    peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
    var managedAfter = GC.GetTotalMemory(forceFullCollection: false);
    var result = snapshot ?? throw new InvalidOperationException("The benchmark produced no snapshot.");
    if (result.Metrics.Length != Enum.GetValues<LuaFrontEndOperation>().Length)
    {
        throw new InvalidOperationException("The benchmark did not reach every front-end operation.");
    }

    Console.WriteLine(
        $"frontend_perf size_bytes={size},elapsed_ms={total.Elapsed.TotalMilliseconds:R}," +
        $"managed_growth_bytes={Math.Max(0, managedAfter - managedBefore)}," +
        $"peak_working_set_bytes={peakWorkingSet},succeeded={result.Module is not null && !result.HasErrors}");
    foreach (var metric in result.Metrics)
    {
        Console.WriteLine(
            $"frontend_stage size_bytes={size},stage={metric.Operation.ToString().ToLowerInvariant()}," +
            $"elapsed_ms={metric.Elapsed.TotalMilliseconds:R},allocated_bytes={metric.AllocatedBytes}");
    }

    var editOffset = size / 2;
    var sourceBytes = document.Text.AsSpan();
    while (editOffset < sourceBytes.Length && sourceBytes[editOffset] != (byte)'1')
    {
        editOffset++;
    }

    if (editOffset == sourceBytes.Length)
    {
        throw new InvalidOperationException("The benchmark source has no incremental edit target.");
    }

    var change = LuaTextChange.FromUtf8(new TextSpan(editOffset, 1), "2");
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    var incrementalTime = Stopwatch.StartNew();
    var incremental = LuaParser.ParseIncremental(result.Syntax, change);
    incrementalTime.Stop();
    var incrementalAllocated = Math.Max(
        0,
        GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    Console.WriteLine(
        $"frontend_incremental size_bytes={size},elapsed_ms={incrementalTime.Elapsed.TotalMilliseconds:R}," +
        $"allocated_bytes={incrementalAllocated},reparsed_bytes={incremental.IncrementalMetrics!.ReparsedNewSpan.Length}," +
        $"reused_nodes={incremental.IncrementalMetrics.ReusedNodeCount}," +
        $"reused_tokens={incremental.IncrementalMetrics.ReusedTokenCount}");
}

static int[] ParseSizes(string value)
{
    var sizes = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(item => int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            ? size
            : throw new ArgumentException($"Invalid source size '{item}'."))
        .ToArray();
    if (sizes.Length == 0 || sizes.Any(static size => size < 128))
    {
        throw new ArgumentOutOfRangeException(nameof(value), "Every source size must be at least 128 bytes.");
    }

    return sizes;
}

static string CreateSource(int targetBytes)
{
    const string Prefix = "local total = 0\n";
    const string Suffix = "return total\n";
    var builder = new StringBuilder(targetBytes);
    builder.Append(Prefix);
    var functionIndex = 0;
    while (builder.Length + Suffix.Length + 80 <= targetBytes)
    {
        var name = functionIndex.ToString("D6", CultureInfo.InvariantCulture);
        builder.Append("function f").Append(name).Append("(value) return value + 1 end -- ");
        var filler = Math.Min(128, targetBytes - builder.Length - Suffix.Length - 1);
        builder.Append('x', filler).Append('\n');
        functionIndex++;
    }

    builder.Append(Suffix);
    var remaining = targetBytes - builder.Length;
    if (remaining == 1)
    {
        builder.Append(' ');
    }
    else if (remaining >= 2)
    {
        builder.Append("--");
        builder.Append('x', remaining - 2);
    }

    if (Encoding.UTF8.GetByteCount(builder.ToString()) != targetBytes)
    {
        throw new InvalidOperationException("The generated ASCII source size is not exact.");
    }

    return builder.ToString();
}

static string? GetOption(IEnumerable<string> arguments, string prefix) =>
    arguments.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.Ordinal))?
        [prefix.Length..];
