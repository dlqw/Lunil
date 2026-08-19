using System.Text;
using Lunil.LanguageServer;

namespace Lunil.LanguageServer.Tests;

/// <summary>
/// Coverage for workspace file exclusion: glob patterns, generated-data auto-detection,
/// end-to-end loading, configuration changes, and force-analysis of opened files.
/// </summary>
public sealed class WorkspaceFileFilterTests
{
    [Fact]
    public void GlobPatternsMatchRelativePathsAndBasenames()
    {
        var filter = WorkspaceFileFilter.Create(
            ["data/**", "**/*.data.lua", "*.generated.lua", "assets/{tables,configs}/**"],
            autoDetect: false);
        Assert.NotNull(filter);
        Assert.True(filter!.IsExcludedByPattern("data/items.lua"));
        Assert.True(filter.IsExcludedByPattern("data/nested/deep/items.lua"));
        Assert.True(filter.IsExcludedByPattern("src/loot.data.lua"));
        // A pattern without a separator matches the file name in any directory.
        Assert.True(filter.IsExcludedByPattern("any/dir/parts.generated.lua"));
        Assert.True(filter.IsExcludedByPattern("assets/tables/t.lua"));
        Assert.True(filter.IsExcludedByPattern("Assets/Configs/T.lua"));
        Assert.False(filter.IsExcludedByPattern("src/main.lua"));
        Assert.False(filter.IsExcludedByPattern("database/init.lua"));
    }

    [Fact]
    public void GeneratedDataTablesAreDetectedAndCodeIsNot()
    {
        var data = BuildGeneratedDataTable(12_000);
        var code = BuildLargeCodeFile(12_000);
        var smallData = BuildGeneratedDataTable(120);

        Assert.True(WorkspaceFileFilter.LooksLikeDataFile(Encoding.UTF8.GetBytes(data)));
        Assert.False(WorkspaceFileFilter.LooksLikeDataFile(Encoding.UTF8.GetBytes(code)));
        // Below the size floor, auto-detection never fires even for pure tables.
        Assert.False(WorkspaceFileFilter.LooksLikeDataFile(Encoding.UTF8.GetBytes(smallData)));
    }

    [Fact]
    public void DataWithCodeMarkersStaysInCorpus()
    {
        var requiring = "local util = require('util')\n" + BuildGeneratedDataTable(12_000);
        Assert.False(WorkspaceFileFilter.LooksLikeDataFile(Encoding.UTF8.GetBytes(requiring)));

        var withFunctions = BuildGeneratedDataTable(12_000) + "\nlocal function helper() return 1 end\n";
        Assert.False(WorkspaceFileFilter.LooksLikeDataFile(Encoding.UTF8.GetBytes(withFunctions)));
    }

    [Fact]
    public async Task LoadFoldersSkipsExcludedFilesAndReportsThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "lunil-file-filter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "data"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "main.lua"),
                "local items = require('data.items')\nreturn items\n");
            await File.WriteAllTextAsync(
                Path.Combine(root, "data", "items.lua"),
                BuildGeneratedDataTable(12_000));
            await File.WriteAllTextAsync(
                Path.Combine(root, "notes.data.lua"),
                "return { note = 'small' }\n");

            var folder = new Uri(root + Path.DirectorySeparatorChar);
            using var workspace = new LanguageServerWorkspace();
            workspace.ConfigureAnalysisExclusions(["**/*.data.lua"], autoDetect: true);
            workspace.Initialize([folder]);
            await WaitForAsync(() => workspace.GetDocuments().Length == 1);

            var status = workspace.GetIndexStatus();
            Assert.Equal(2, (int)status["excluded"]!);
            var excludedFiles = (System.Text.Json.Nodes.JsonArray)status["excludedFiles"]!;
            var reasons = excludedFiles
                .Select(static node => node!["uri"]!.GetValue<string>() + ":" + node!["reason"]!.GetValue<string>())
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();
            Assert.Contains(reasons, static reason =>
                reason.EndsWith("data/items.lua:data", StringComparison.Ordinal));
            Assert.Contains(reasons, static reason =>
                reason.EndsWith("notes.data.lua:pattern", StringComparison.Ordinal));

            // Excluded modules stay out of the navigation index.
            Assert.Null(workspace.GetUri("notes.data"));
            // Opened files analyze anyway, even while excluded from indexing.
            var opened = new Uri(folder, "notes.data.lua");
            workspace.Open(opened, 1, "return { note = 'small' }\n");
            var analysis = await workspace.GetAnalysisAsync(opened, CancellationToken.None);
            Assert.NotNull(analysis);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExclusionConfigurationChangesReloadTheCorpus()
    {
        var root = Path.Combine(Path.GetTempPath(), "lunil-file-filter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "main.lua"),
                "return 1\n");
            await File.WriteAllTextAsync(
                Path.Combine(root, "bigdata.lua"),
                BuildGeneratedDataTable(12_000));

            var folder = new Uri(root + Path.DirectorySeparatorChar);
            using var workspace = new LanguageServerWorkspace();
            workspace.Initialize([folder]);
            await WaitForAsync(() => workspace.GetDocuments().Length == 1);
            Assert.Null(workspace.GetUri("bigdata"));

            // Disabling auto-detection re-includes the data file.
            workspace.ConfigureAnalysisExclusions([], autoDetect: false);
            await WaitForAsync(() => workspace.GetDocuments().Length == 2);
            Assert.NotNull(workspace.GetUri("bigdata"));

            // Re-enabling it excludes the file again.
            workspace.ConfigureAnalysisExclusions([], autoDetect: true);
            await WaitForAsync(() => workspace.GetDocuments().Length == 1);
            Assert.Null(workspace.GetUri("bigdata"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WatchedChangesOnExcludedFilesDoNotReloadContents()
    {
        var root = Path.Combine(Path.GetTempPath(), "lunil-file-filter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "main.lua"), "return 1\n");
            var dataPath = Path.Combine(root, "bigdata.lua");
            await File.WriteAllTextAsync(dataPath, BuildGeneratedDataTable(12_000));

            var folder = new Uri(root + Path.DirectorySeparatorChar);
            using var workspace = new LanguageServerWorkspace();
            workspace.Initialize([folder]);
            await WaitForAsync(() => workspace.GetDocuments().Length == 1);

            var dataUri = new Uri(folder, "bigdata.lua");
            workspace.WatchedFileChanged(dataUri, changeType: 2);
            await Task.Delay(150);
            // Still excluded, and not resident: the watched change must not load it.
            Assert.Single(workspace.GetDocuments());
            var status = workspace.GetIndexStatus();
            Assert.Equal(1, (int)status["excluded"]!);

            // Replacing the data file with code re-includes it on the next change event.
            await File.WriteAllTextAsync(dataPath, "local M = {}\nfunction M.go() return 1 end\nreturn M\n");
            workspace.WatchedFileChanged(dataUri, changeType: 2);
            await WaitForAsync(() => workspace.GetDocuments().Length == 2);
            Assert.Equal(0, (int)workspace.GetIndexStatus()["excluded"]!);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Entries look like generated item tables: bracket keys, nested values, trailing commas.</summary>
    private static string BuildGeneratedDataTable(int entries)
    {
        var builder = new StringBuilder("return {\n");
        for (var index = 0; index < entries; index++)
        {
            builder.Append("    [\"item_").Append(index).Append("\"] = { id = ")
                .Append(index)
                .Append(", name = \"item\", weight = 1.5, tags = { \"a\", \"b\" } },\n");
        }

        builder.Append("}\n");
        return builder.ToString();
    }

    private static string BuildLargeCodeFile(int functions)
    {
        var builder = new StringBuilder("local M = {}\n");
        for (var index = 0; index < functions; index++)
        {
            builder.Append("function M.fn").Append(index).Append("(a, b)\n")
                .Append("    if a > b then return a - b end\n")
                .Append("    local total = 0\n")
                .Append("    for step = 1, 8 do total = total + a * step end\n")
                .Append("    return total\n")
                .Append("end\n");
        }

        builder.Append("return M\n");
        return builder.ToString();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }
}
