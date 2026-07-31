using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lunil.LanguageServer;

var profileName = args.FirstOrDefault(argument => argument.StartsWith("--profile=", StringComparison.Ordinal))?
    .Split('=', 2)[1] ?? "smoke";
var profile = profileName switch
{
    "smoke" => new Profile(100, 20, 150, 100, 500, 2_000),
    "L" => new Profile(20_000, 50, 150, 100, 500, 2_000),
    _ => throw new ArgumentException($"Unknown profile '{profileName}'."),
};

using var workspace = new LanguageServerWorkspace();
workspace.Initialize([]);
var documents = Enumerable.Range(0, profile.ModuleCount).Select(index =>
{
    var uri = new Uri("file:///module" + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) +
        ".lua");
    return new LspTextDocument(uri, 1, CreateSource(index, profile.LinesPerModule), isOpen: index == 0);
}).ToArray();
workspace.LoadDocumentsForScale(documents);
var service = new LuaLanguageService(workspace);
var interactiveUri = documents[0].Uri;
var parameters = JsonSerializer.SerializeToElement(new
{
    textDocument = new { uri = interactiveUri.AbsoluteUri },
    position = new { line = 2, character = 33 },
    context = new { includeDeclaration = true },
});
var renameParameters = JsonSerializer.SerializeToElement(new
{
    textDocument = new { uri = interactiveUri.AbsoluteUri },
    position = new { line = 2, character = 33 },
    newName = "capturedValue",
});

var total = Stopwatch.StartNew();
var background = workspace.ReindexNowAsync(CancellationToken.None);
var diagnosticTimer = Stopwatch.StartNew();
_ = await workspace.GetAnalysisAsync(interactiveUri, CancellationToken.None);
diagnosticTimer.Stop();
var interactive = new List<double>();
for (var iteration = 0; iteration < 30; iteration++)
{
    var timer = Stopwatch.StartNew();
    _ = await service.CompletionAsync(parameters, CancellationToken.None);
    _ = await service.HoverAsync(parameters, CancellationToken.None);
    timer.Stop();
    interactive.Add(timer.Elapsed.TotalMilliseconds);
}

var referencesTimer = Stopwatch.StartNew();
_ = await service.ReferencesAsync(parameters, CancellationToken.None);
referencesTimer.Stop();
var renameTimer = Stopwatch.StartNew();
_ = await service.RenameAsync(renameParameters, CancellationToken.None);
renameTimer.Stop();
await background;
total.Stop();

var diagnosticsMs = diagnosticTimer.Elapsed.TotalMilliseconds;
var interactiveP95 = Percentile(interactive, 0.95);
var referencesMs = referencesTimer.Elapsed.TotalMilliseconds;
var renameMs = renameTimer.Elapsed.TotalMilliseconds;
var snapshot = workspace.GetSnapshot() ?? throw new InvalidOperationException("Background index did not complete.");
Console.WriteLine(
    $"lsp_scale profile={profileName},modules={profile.ModuleCount},lines={profile.ModuleCount * profile.LinesPerModule}," +
    $"diagnostics_ms={diagnosticsMs:F3},interactive_p95_ms={interactiveP95:F3}," +
    $"references_first_batch_ms={referencesMs:F3},rename_plan_ms={renameMs:F3}," +
    $"indexed_references={snapshot.Metrics.IndexedReferenceCount},elapsed_ms={total.Elapsed.TotalMilliseconds:F3}," +
    $"managed_bytes={GC.GetTotalMemory(true)},working_set_bytes={Process.GetCurrentProcess().WorkingSet64}");

if (diagnosticsMs > profile.DiagnosticsLimitMs || interactiveP95 > profile.InteractiveLimitMs ||
    referencesMs > profile.ReferencesLimitMs || renameMs > profile.RenameLimitMs)
{
    throw new InvalidOperationException("One or more LSP latency gates were exceeded.");
}

static string CreateSource(int moduleIndex, int lines)
{
    var builder = new StringBuilder(lines * 48);
    builder.AppendLine("local captured = 1");
    builder.AppendLine("local M = {}");
    builder.AppendLine("function M.run(value) return value + captured end");
    if (moduleIndex > 0)
    {
        builder.Append("local previous = require('module").Append((moduleIndex - 1).ToString(
                "D5",
                System.Globalization.CultureInfo.InvariantCulture))
            .AppendLine("')");
        builder.AppendLine("M.previous = previous.run(captured)");
    }

    for (var line = moduleIndex > 0 ? 5 : 3; line < lines - 1; line++)
    {
        builder.Append("local value").Append(line).Append(" = captured + ").Append(line).AppendLine();
    }

    builder.AppendLine("return M");
    return builder.ToString();
}

static double Percentile(List<double> values, double percentile)
{
    values.Sort();
    return values[Math.Clamp((int)Math.Ceiling(values.Count * percentile) - 1, 0, values.Count - 1)];
}

internal sealed record Profile(
    int ModuleCount,
    int LinesPerModule,
    double DiagnosticsLimitMs,
    double InteractiveLimitMs,
    double ReferencesLimitMs,
    double RenameLimitMs);
