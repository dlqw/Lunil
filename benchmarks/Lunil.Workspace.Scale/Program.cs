using System.Diagnostics;
using System.Globalization;
using System.Text;
using Lunil.Workspace;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

var profileName = GetOption(args, "--profile=") ?? "smoke";
var snapshotCount = ParsePositive(GetOption(args, "--snapshots=") ?? "1", "snapshots", 100);
var profile = profileName.ToLowerInvariant() switch
{
    "l" => new ScaleProfile("L", 20_000, 50, 100L * 1024 * 1024, 5_000_000, 1_000_000,
        TimeSpan.FromMinutes(10), 8L * 1024 * 1024 * 1024),
    "xl" => new ScaleProfile("XL", 50_000, 100, 500L * 1024 * 1024, 20_000_000, 5_000_000,
        TimeSpan.FromMinutes(30), 16L * 1024 * 1024 * 1024),
    "m" => new ScaleProfile("M", 2_000, 50, 10L * 1024 * 1024, 500_000, 100_000,
        TimeSpan.FromMinutes(2), 2L * 1024 * 1024 * 1024),
    "m2" => new ScaleProfile("M2", 5_000, 50, 25L * 1024 * 1024, 1_250_000, 250_000,
        TimeSpan.FromMinutes(3), 4L * 1024 * 1024 * 1024),
    "smoke" => new ScaleProfile("smoke", 100, 20, 1024 * 1024, 10_000, 2_000,
        TimeSpan.FromMinutes(1), 512L * 1024 * 1024),
    _ => throw new ArgumentException($"Unknown scale profile '{profileName}'."),
};

var documents = CreateDocuments(profile);
var generatedLines = checked((long)profile.ModuleCount * profile.LinesPerModule);
var generatedBytes = documents.Sum(static document => (long)document.Source.Text.Length);
if (generatedLines != profile.ExpectedLines || generatedBytes != profile.SourceBytes)
{
    throw new InvalidOperationException(
        $"Generated corpus mismatch: lines={generatedLines}, bytes={generatedBytes}.");
}

using var workspace = new LuaWorkspace(new LuaWorkspaceOptions
{
    MaximumModuleCount = profile.ModuleCount + 1,
    MaximumDependencyCount = profile.ModuleCount * 2,
    MaximumSourceBytes = profile.SourceBytes + 1024,
    MaximumParallelism = Math.Max(1, Environment.ProcessorCount),
    MaximumCacheEntryCount = Math.Min(32_768, profile.ModuleCount * 2),
    MaximumCacheBytes = Math.Min(profile.MaximumManagedBytes / 4, 2L * 1024 * 1024 * 1024),
    MaximumPendingWorkItems = 2_048,
    Progress = new ConsoleProgress(),
});

using var process = Process.GetCurrentProcess();
var peakWorkingSet = process.WorkingSet64;
var elapsed = Stopwatch.StartNew();
Task<LuaWorkspaceCompactSnapshot>? analysis = workspace.AnalyzeCompactAsync(documents);
while (!analysis.IsCompleted)
{
    process.Refresh();
    peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
    await Task.WhenAny(analysis, Task.Delay(25)).ConfigureAwait(false);
}

var snapshot = await analysis.ConfigureAwait(false);
analysis = null;
elapsed.Stop();
var firstSnapshotElapsed = elapsed.Elapsed;
var expectedReferences = snapshot.Metrics.IndexedReferenceCount;
var expectedCalls = snapshot.Metrics.IndexedCallCount;
var maximumSnapshotElapsed = firstSnapshotElapsed;
var maximumManagedBytes = GC.GetTotalMemory(forceFullCollection: false);
var soakSource = Encoding.UTF8.GetString(documents[^1].Source.Text.AsSpan());
for (var snapshotIndex = 1; snapshotIndex < snapshotCount; snapshotIndex++)
{
    var changedIndex = documents.Length - 1;
    var changed = documents[changedIndex];
    var marker = $"-- snapshot={snapshotIndex}\n";
    documents[changedIndex] = LuaWorkspaceDocument.FromUtf8(
        changed.Module.Name,
        soakSource.TrimEnd('\r', '\n') + "\n" + marker,
        changed.SourceIdentity);

    var snapshotElapsed = Stopwatch.StartNew();
    snapshot = await workspace.AnalyzeCompactAsync(documents).ConfigureAwait(false);
    snapshotElapsed.Stop();
    process.Refresh();
    peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
    maximumManagedBytes = Math.Max(
        maximumManagedBytes,
        GC.GetTotalMemory(forceFullCollection: false));
    maximumSnapshotElapsed = snapshotElapsed.Elapsed > maximumSnapshotElapsed
        ? snapshotElapsed.Elapsed
        : maximumSnapshotElapsed;
    if (snapshot.Modules.Length != profile.ModuleCount ||
        snapshot.Metrics.IndexedReferenceCount != expectedReferences ||
        snapshot.Metrics.IndexedCallCount != expectedCalls ||
        snapshotElapsed.Elapsed > profile.MaximumElapsed)
    {
        throw new InvalidOperationException(
            $"Workspace snapshot soak failed at snapshot {snapshotIndex + 1}.");
    }

    Console.WriteLine(
        $"workspace_snapshot index={snapshotIndex + 1},elapsed_ms={snapshotElapsed.Elapsed.TotalMilliseconds:R}," +
        $"references={snapshot.Metrics.IndexedReferenceCount},calls={snapshot.Metrics.IndexedCallCount}");
}

workspace.ClearCache();
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
var managedBytes = GC.GetTotalMemory(forceFullCollection: false);
process.Refresh();
peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
Console.WriteLine(
    $"workspace_scale profile={profile.Name},modules={snapshot.Modules.Length},lines={generatedLines}," +
    $"source_bytes={generatedBytes},references={snapshot.Metrics.IndexedReferenceCount}," +
    $"calls={snapshot.Metrics.IndexedCallCount},snapshots={snapshotCount}," +
    $"elapsed_ms={firstSnapshotElapsed.TotalMilliseconds:R}," +
    $"maximum_snapshot_ms={maximumSnapshotElapsed.TotalMilliseconds:R}," +
    $"managed_bytes={managedBytes},maximum_managed_bytes={maximumManagedBytes}," +
    $"compact_bytes={snapshot.EstimatedResidentBytes}," +
    $"peak_working_set_bytes={peakWorkingSet},peak_parallelism={snapshot.Metrics.PeakParallelism}," +
    $"pending_high_watermark={snapshot.Metrics.PendingWorkItemHighWatermark}");

if (snapshot.Modules.Length != profile.ModuleCount ||
    snapshot.Metrics.IndexedReferenceCount < profile.MinimumReferences ||
    snapshot.Metrics.IndexedCallCount < profile.MinimumCalls ||
    firstSnapshotElapsed > profile.MaximumElapsed ||
    managedBytes > profile.MaximumManagedBytes ||
    maximumManagedBytes > profile.MaximumManagedBytes)
{
    throw new InvalidOperationException("Workspace scale gate failed.");
}

static LuaWorkspaceDocument[] CreateDocuments(ScaleProfile profile)
{
    var documents = new LuaWorkspaceDocument[profile.ModuleCount];
    var baseBytes = profile.SourceBytes / profile.ModuleCount;
    var remainder = profile.SourceBytes % profile.ModuleCount;
    for (var moduleIndex = 0; moduleIndex < documents.Length; moduleIndex++)
    {
        var targetBytes = checked((int)(baseBytes + (moduleIndex < remainder ? 1 : 0)));
        var name = moduleIndex == 0
            ? "foundation"
            : "module_" + moduleIndex.ToString("D5", CultureInfo.InvariantCulture);
        documents[moduleIndex] = LuaWorkspaceDocument.FromUtf8(
            name,
            CreateModuleSource(moduleIndex, profile.LinesPerModule, targetBytes));
    }

    return documents;
}

static string CreateModuleSource(int moduleIndex, int lineCount, int targetBytes)
{
    var lines = new string[lineCount];
    if (moduleIndex == 0)
    {
        lines[0] = "local total = 0; local function touch(value) total = total + value; return total end";
        for (var line = 1; line < lineCount - 1; line++)
        {
            lines[line] = "total = total + touch(total) + total + total";
        }

        lines[^1] = "return { touch = touch, value = total }";
    }
    else
    {
        lines[0] = "local dep = require('foundation'); local total = dep.touch(0)";
        for (var line = 1; line < lineCount - 1; line++)
        {
            lines[line] = "total = total + dep.value + dep.value; dep.touch(total)";
        }

        lines[^1] = "return { value = total, run = function() return dep.touch(total) end }";
    }

    var minimumBytes = lines.Sum(static line => line.Length + 1);
    if (minimumBytes > targetBytes)
    {
        throw new InvalidOperationException(
            $"Target module size {targetBytes} is below the generated minimum {minimumBytes}.");
    }

    var padding = targetBytes - minimumBytes;
    var perLine = padding / lineCount;
    var extra = padding % lineCount;
    var output = new StringBuilder(targetBytes);
    for (var line = 0; line < lines.Length; line++)
    {
        output.Append(lines[line]);
        var count = perLine + (line < extra ? 1 : 0);
        if (count >= 2)
        {
            output.Append("--").Append('x', count - 2);
        }
        else if (count == 1)
        {
            output.Append(' ');
        }

        output.Append('\n');
    }

    if (output.Length != targetBytes)
    {
        throw new InvalidOperationException("Generated module byte size is not exact.");
    }

    return output.ToString();
}

static string? GetOption(IEnumerable<string> arguments, string prefix) =>
    arguments.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.Ordinal))?
        [prefix.Length..];

static int ParsePositive(string text, string name, int maximum)
{
    if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
        value <= 0 || value > maximum)
    {
        throw new ArgumentOutOfRangeException(
            name,
            text,
            $"Expected a positive integer no greater than {maximum}.");
    }

    return value;
}

internal sealed record ScaleProfile(
    string Name,
    int ModuleCount,
    int LinesPerModule,
    long SourceBytes,
    int MinimumReferences,
    int MinimumCalls,
    TimeSpan MaximumElapsed,
    long MaximumManagedBytes)
{
    public long ExpectedLines => checked((long)ModuleCount * LinesPerModule);
}

internal sealed class ConsoleProgress : IProgress<LuaWorkspaceProgress>
{
    private LuaWorkspaceProgressPhase? _lastPhase;
    private int _lastCompleted;

    public void Report(LuaWorkspaceProgress value)
    {
        if (_lastPhase != value.Phase || value.CompletedWorkItems - _lastCompleted >= 1_000 ||
            value.CompletedWorkItems == value.TotalWorkItems)
        {
            Console.WriteLine(
                $"workspace_progress phase={value.Phase},completed={value.CompletedWorkItems}," +
                $"total={value.TotalWorkItems}");
            _lastPhase = value.Phase;
            _lastCompleted = value.CompletedWorkItems;
        }
    }
}
