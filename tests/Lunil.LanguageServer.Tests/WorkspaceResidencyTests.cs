using System.Text;
using Lunil.LanguageServer;

namespace Lunil.LanguageServer.Tests;

/// <summary>
/// Regression coverage for workspace residency behavior: byte-budgeted analysis
/// caching with open-document pinning, raw byte scanning of runtime class edges,
/// and BOM-safe disk loading.
/// </summary>
public sealed class WorkspaceResidencyTests
{
    [Fact]
    public async Task AnalysisCacheEvictsClosedDocumentsAndPinsOpenOnes()
    {
        var root = Path.Combine(Path.GetTempPath(), "lunil-residency-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var filler = new StringBuilder();
            for (var index = 0; index < 400; index++)
            {
                filler.Append("-- filler ").Append(index).Append(' ').AppendLine(new string('x', 60));
            }

            for (var index = 0; index < 4; index++)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(root, $"mod{index}.lua"),
                    filler.ToString() + $"return {{ index = {index} }}\n");
            }

            var folder = new Uri(root + Path.DirectorySeparatorChar);
            using var workspace = new LanguageServerWorkspace();
            workspace.Initialize([folder]);
            workspace.MaximumCachedAnalysisBytes = 16 * 1024;
            await WaitForAsync(() => workspace.GetDocuments().Length == 4);

            var openUri = new Uri(folder, "mod0.lua");
            workspace.Open(openUri, 1, await File.ReadAllTextAsync(Path.Combine(root, "mod0.lua")));

            // Opening schedules a debounced reindex that re-publishes open
            // documents; let it settle so the pinned-instance check below observes
            // a stable cache rather than a racing background replacement.
            await Task.Delay(500);
            var openFirst = await workspace.GetAnalysisAsync(openUri, CancellationToken.None);
            var closedUri = new Uri(folder, "mod1.lua");
            var closedFirst = await workspace.GetAnalysisAsync(closedUri, CancellationToken.None);

            // Churn through the remaining documents with a tiny budget: closed
            // analyses must evict while the open document's analysis stays pinned.
            _ = await workspace.GetAnalysisAsync(new Uri(folder, "mod2.lua"), CancellationToken.None);
            _ = await workspace.GetAnalysisAsync(new Uri(folder, "mod3.lua"), CancellationToken.None);

            var openSecond = await workspace.GetAnalysisAsync(openUri, CancellationToken.None);
            var closedSecond = await workspace.GetAnalysisAsync(closedUri, CancellationToken.None);
            Assert.Same(openFirst, openSecond);
            Assert.NotSame(closedFirst, closedSecond);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RuntimeClassEdgesScanMatchesWrittenIdioms()
    {
        var root = Path.Combine(Path.GetTempPath(), "lunil-residency-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "edges.lua"), string.Join("\n",
                "local Character = Base.extend(",
                "local Movable=Thing:extend (",
                "local Spaced = Aliased .extend(",
                "mylocal Fake = Real.extend(",
                "local NotACall = Other.extend",
                "local q = require('x')"));
            File.WriteAllText(Path.Combine(root, "mixins.lua"), string.Join("\n",
                "Class.mixin(Character, Movable)",
                "obj:install(1)",
                "notmixin(A, B)",
                "x.y.mixin(Dog, Cat)"));

            var folder = new Uri(root + Path.DirectorySeparatorChar);
            using var workspace = new LanguageServerWorkspace();
            workspace.Initialize([folder]);
            _ = WaitForSync(() => workspace.GetDocuments().Length == 2);

            var bases = workspace.GetRuntimeClassBases();
            Assert.Equal("Base", bases["Character"]);
            Assert.Equal("Thing", bases["Movable"]);
            // Whitespace before the dot/colon is legal in the idiom; a keyword
            // embedded in a longer identifier and calls without a parenthesis are
            // rejected.
            Assert.Equal("Aliased", bases["Spaced"]);
            Assert.False(bases.ContainsKey("Fake"));
            Assert.False(bases.ContainsKey("NotACall"));

            // Mixins only count when both arguments name declared classes; the
            // fixtures declare none, so the table stays empty and scanning simply
            // must not throw or loop.
            Assert.Empty(workspace.GetClassMixins());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DiskDocumentsWithUtf8BomScanCleanly()
    {
        var root = Path.Combine(Path.GetTempPath(), "lunil-residency-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bom = new byte[] { 0xEF, 0xBB, 0xBF };
            var stub = Encoding.UTF8.GetBytes("---@class BomClass\n---@field value number\nlocal BomClass = {}\n");
            await File.WriteAllBytesAsync(Path.Combine(root, "bom_stub.lua"), [.. bom, .. stub]);
            await File.WriteAllTextAsync(Path.Combine(root, "consumer.lua"), "return 1\n");

            var folder = new Uri(root + Path.DirectorySeparatorChar);
            using var workspace = new LanguageServerWorkspace();
            workspace.ConfigureLibraryFolders([root]);
            workspace.Initialize([folder]);
            await WaitForAsync(() => workspace.GetDocuments().Length == 2);

            // The declaration inside the BOM'd file must be reachable through the
            // byte-canonical load path exactly as it was through ReadAllText.
            Assert.True(workspace.TryGetTypeDeclarationLocation("BomClass", out var uri, out _));
            Assert.EndsWith("bom_stub.lua", uri.AbsoluteUri, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClosedDocumentBytesEvictOverBudgetAndReloadTransparently()
    {
        var root = Path.Combine(Path.GetTempPath(), "lunil-residency-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var filler = new StringBuilder();
            for (var index = 0; index < 400; index++)
            {
                filler.Append("-- filler ").Append(index).Append(' ').AppendLine(new string('x', 60));
            }

            for (var index = 0; index < 4; index++)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(root, $"mod{index}.lua"),
                    filler.ToString() + $"return {{ index = {index} }}\n");
            }

            var folder = new Uri(root + Path.DirectorySeparatorChar);
            using var workspace = new LanguageServerWorkspace();
            workspace.Initialize([folder]);
            await WaitForAsync(() => workspace.GetDocuments().Length == 4);

            workspace.MaximumDocumentResidencyBytes = 96 * 1024;
            workspace.TrimClosedDocumentsForTest();
            Assert.Contains(workspace.GetDocuments(), static document => document.IsTrimmed);

            // Analyzing a trimmed document reloads it from disk transparently.
            var trimmed = workspace.GetDocuments().First(static document => document.IsTrimmed);
            var analysis = await workspace.GetAnalysisAsync(trimmed.Uri, CancellationToken.None);
            Assert.NotNull(analysis);
            Assert.Contains(workspace.GetDocuments(),
                document => document.Uri == trimmed.Uri && !document.IsTrimmed);

            // Open documents stay pinned: their bytes survive a budget pass.
            var openUri = new Uri(folder, "mod0.lua");
            workspace.Open(openUri, 1, await File.ReadAllTextAsync(Path.Combine(root, "mod0.lua")));
            workspace.TrimClosedDocumentsForTest();
            Assert.All(
                workspace.GetDocuments().Where(document => document.Uri == openUri),
                static document => Assert.False(document.IsTrimmed));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnchangedCorpusRebuildsFromRetainedModuleCaches()
    {
        var root = Path.Combine(Path.GetTempPath(), "lunil-residency-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // A dependency chain deep enough that a full re-analysis would be visible
            // in the miss counter: chain<0> <- chain<1> <- ... Each module's cache key
            // includes its dependencies' export hashes, so unchanged content plus
            // unchanged exports must hit the retained cache on every rebuild.
            const int modules = 60;
            for (var index = 0; index < modules; index++)
            {
                var source = index == 0
                    ? "return { value = 1 }\n"
                    : $"local prev = require('chain{index - 1}')\nreturn {{ value = prev.value + 1 }}\n";
                await File.WriteAllTextAsync(Path.Combine(root, $"chain{index}.lua"), source);
            }

            var folder = new Uri(root + Path.DirectorySeparatorChar);
            using var workspace = new LanguageServerWorkspace();
            workspace.Initialize([folder]);
            await WaitForAsync(() => workspace.GetDocuments().Length == modules);
            await workspace.ReindexNowAsync(CancellationToken.None);
            var first = workspace.GetSnapshot();
            Assert.NotNull(first);
            // On slow runners the debounced load-time rebuild can complete the cold
            // analysis before this direct call, so either misses or hits cover every
            // module — but never neither.
            Assert.True(first!.Metrics.CacheMissCount + first.Metrics.CacheHitCount >= modules);

            // The debounced load-time rebuild already populated the retained caches;
            // a second full rebuild of the unchanged corpus analyzes nothing.
            await workspace.ReindexNowAsync(CancellationToken.None);
            var second = workspace.GetSnapshot();
            Assert.NotNull(second);
            Assert.Equal(0, second!.Metrics.CacheMissCount);
            Assert.True(second.Metrics.CacheHitCount >= modules);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    private static bool WaitForSync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            Thread.Sleep(50);
        }

        return condition();
    }
}
