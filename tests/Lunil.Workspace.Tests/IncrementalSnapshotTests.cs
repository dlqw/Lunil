using Lunil.Compiler;
using Lunil.Workspace;

namespace Lunil.Workspace.Tests;

/// <summary>
/// Coverage for incremental compact snapshots: modules whose content and dependency
/// export hashes are unchanged reuse their previous projection contributions instead
/// of re-parsing and re-analyzing, and the reused snapshot is equivalent to one built
/// from scratch.
/// </summary>
public sealed class IncrementalSnapshotTests
{
    [Fact]
    public async Task UnchangedCorpusReusesContributionsWithoutReanalysis()
    {
        using var workspace = new LuaWorkspace();
        var documents = Corpus();
        var first = await workspace.AnalyzeCompactAsync(documents);
        Assert.True(first.Metrics.CacheMissCount > 0);

        // The unchanged corpus hits the retained caches and the reusable projections:
        // no module is parsed or analyzed at all.
        var second = await workspace.AnalyzeCompactAsync(documents);
        Assert.Equal(0, second.Metrics.CacheMissCount);
        Assert.True(second.Metrics.CacheHitCount >= first.Modules.Length);

        // Equivalence against a from-scratch build of the same corpus.
        using var fresh = new LuaWorkspace();
        var freshSnapshot = await fresh.AnalyzeCompactAsync(documents);
        AssertSnapshotsEquivalent(freshSnapshot, second);
    }

    [Fact]
    public async Task EditedModuleLosesReuseAndDependentsFollow()
    {
        using var workspace = new LuaWorkspace();
        var documents = Corpus();
        _ = await workspace.AnalyzeCompactAsync(documents);
        _ = await workspace.AnalyzeCompactAsync(documents);

        // Changing "a" invalidates its own contribution and "app"'s (the dependency's
        // export hash is part of the key); the island module keeps its projection.
        var edited = Corpus();
        edited[0] = Document("a", "return { value = 2 }");
        var third = await workspace.AnalyzeCompactAsync(edited);
        Assert.Equal(2, third.Metrics.CacheMissCount);

        // The rebuilt snapshot still resolves references through the re-merged universe.
        using var fresh = new LuaWorkspace();
        var freshSnapshot = await fresh.AnalyzeCompactAsync(edited);
        AssertSnapshotsEquivalent(freshSnapshot, third);
    }

    private static LuaWorkspaceDocument[] Corpus() =>
    [
        Document("a", "local value = 1\nreturn { value = value }"),
        Document("app", "local a = require('a')\nlocal function go() return a.value end\nreturn { go = go }"),
        Document("island", "local t = {}\nt.field = 7\nreturn t"),
    ];

    private static void AssertSnapshotsEquivalent(
        LuaWorkspaceCompactSnapshot expected,
        LuaWorkspaceCompactSnapshot actual)
    {
        Assert.Equal(
            expected.Modules.Select(static module => (module.Identity.Name, module.ContentHash)),
            actual.Modules.Select(static module => (module.Identity.Name, module.ContentHash)));
        Assert.Equal(expected.Metrics.IndexedReferenceCount, actual.Metrics.IndexedReferenceCount);
        Assert.Equal(expected.Metrics.IndexedCallCount, actual.Metrics.IndexedCallCount);
        Assert.Equal(
            expected.ExportGraph.Symbols.Select(static symbol => (symbol.Key, symbol.Kind)).OrderBy(static pair => pair.Key),
            actual.ExportGraph.Symbols.Select(static symbol => (symbol.Key, symbol.Kind)).OrderBy(static pair => pair.Key));
        Assert.Equal(expected.CallBindings.Edges.Length, actual.CallBindings.Edges.Length);
        Assert.Equal(
            expected.FindMemberReferences("value").Select(static reference => reference.Module.Name),
            actual.FindMemberReferences("value").Select(static reference => reference.Module.Name));
        Assert.Equal(
            expected.FindMemberReferences("field").Select(static reference => reference.Span.Start),
            actual.FindMemberReferences("field").Select(static reference => reference.Span.Start));
        Assert.Equal(expected.Diagnostics.Length, actual.Diagnostics.Length);
    }

    [Fact]
    public async Task ParallelCorpusWithUnresolvedRequiresRebuildsAcrossThreeRuns()
    {
        var modules = new List<LuaWorkspaceDocument>();
        for (var index = 0; index < 40; index++)
        {
            var source = index == 0
                ? "return { value = 1 }\n"
                : $"local prev = require('chain{index - 1}')\nlocal missing = require('src.mod')\nreturn {{ value = prev.value + 1 }}\n";
            modules.Add(Document($"chain{index}", source));
        }

        using var workspace = new LuaWorkspace(new LuaWorkspaceOptions { MaximumParallelism = 4 });
        var first = await workspace.AnalyzeCompactAsync([.. modules]);
        var second = await workspace.AnalyzeCompactAsync([.. modules]);
        Assert.Equal(0, second.Metrics.CacheMissCount);
        var third = await workspace.AnalyzeCompactAsync([.. modules]);
        Assert.Equal(0, third.Metrics.CacheMissCount);
        Assert.Equal(first.Metrics.IndexedReferenceCount, third.Metrics.IndexedReferenceCount);
    }

    private static LuaWorkspaceDocument Document(string name, string source) =>
        LuaWorkspaceDocument.FromUtf8(name, source);
}
