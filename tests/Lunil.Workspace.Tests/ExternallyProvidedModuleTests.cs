using System.Collections.Immutable;
using Lunil.Compiler;

namespace Lunil.Workspace.Tests;

/// <summary>
/// Coverage for externally provided modules: names that exist at runtime but sit
/// outside the analyzed corpus (the language server's excluded data files).
/// Requires that name them must not report unresolved-module diagnostics.
/// </summary>
public sealed class ExternallyProvidedModuleTests
{
    [Fact]
    public async Task ExternallyProvidedRequireResolvesUntypedWithoutDiagnostics()
    {
        using var workspace = new LuaWorkspace();
        var externallyProvided = ImmutableHashSet<string>.Empty
            .WithComparer(StringComparer.Ordinal)
            .Add("data.items");
        var result = await workspace.AnalyzeAsync(
            [Document("main", "local items = require('data.items')\nreturn items")],
            externallyProvided);

        var edge = Assert.Single(result.Graph.Dependencies);
        Assert.Equal(LuaModuleDependencyKind.Static, edge.Kind);
        Assert.Null(edge.Target);
        Assert.Equal("data.items", edge.RequestedName);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "LUA7002");
    }

    [Fact]
    public async Task UnknownModuleStillReportsUnresolved()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync(
            [Document("main", "local missing = require('missing')\nreturn missing")],
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal).Add("data.items"));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "LUA7002");
    }

    [Fact]
    public async Task CorpusDocumentsWinOverExternallyProvidedNames()
    {
        using var workspace = new LuaWorkspace();
        var externallyProvided = ImmutableHashSet<string>.Empty
            .WithComparer(StringComparer.Ordinal)
            .Add("dep");
        var result = await workspace.AnalyzeAsync(
            [
                Document("app", "local dep = require('dep')\nreturn dep.value"),
                Document("dep", "return { value = 42 }"),
            ],
            externallyProvided);

        var edge = Assert.Single(result.Graph.Dependencies);
        Assert.NotNull(edge.Target);
        Assert.Equal("dep", edge.Target!.Name);
        Assert.Equal("42", result.GetModule("app")!.ExportedType.DisplayName);
    }

    [Fact]
    public async Task RetainedModuleCacheMakesRepeatAnalysisIncremental()
    {
        using var workspace = new LuaWorkspace(new LuaWorkspaceOptions
        {
            RetainFullAnalysisCacheResults = true,
        });
        var first = await workspace.AnalyzeAsync([
            Document("a", "return { value = 1 }"),
            Document("app", "local a = require('a')\nreturn a.value"),
        ]);
        Assert.True(first.Metrics.CacheMissCount > 0);

        // An unchanged corpus hits the retained caches: no module is re-analyzed.
        var second = await workspace.AnalyzeAsync([
            Document("a", "return { value = 1 }"),
            Document("app", "local a = require('a')\nreturn a.value"),
        ]);
        Assert.Equal(0, second.Metrics.CacheMissCount);
        Assert.True(second.Metrics.CacheHitCount > 0);

        // An unrelated new module misses alone; existing modules stay cached.
        var third = await workspace.AnalyzeAsync([
            Document("a", "return { value = 1 }"),
            Document("app", "local a = require('a')\nreturn a.value"),
            Document("island", "return 7"),
        ]);
        Assert.Equal(1, third.Metrics.CacheMissCount);
        Assert.True(third.Metrics.CacheHitCount > 0);

        // Editing one module invalidates it and its dependent (the dependency's
        // export hash is part of the cache key) while the island stays cached.
        var fourth = await workspace.AnalyzeAsync([
            Document("a", "return { value = 2 }"),
            Document("app", "local a = require('a')\nreturn a.value"),
            Document("island", "return 7"),
        ]);
        Assert.Equal(2, fourth.Metrics.CacheMissCount);
        Assert.Equal("2", fourth.GetModule("app")!.ExportedType.DisplayName);
    }

    private static LuaWorkspaceDocument Document(string name, string source) =>
        LuaWorkspaceDocument.FromUtf8(name, source);
}
