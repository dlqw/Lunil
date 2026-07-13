using Lunil.Core.Diagnostics;

namespace Lunil.Workspace.Tests;

public sealed class LuaWorkspaceTests
{
    [Fact]
    public async Task ResolvesStaticRequiresAndPropagatesExportTypes()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document("app", "local dep = require('dep')\nreturn dep.value + 1"),
            Document("dep", "return { value = 42 }"),
        ]);

        Assert.Equal(2, result.Graph.Nodes.Length);
        var edge = Assert.Single(result.Graph.Dependencies);
        Assert.Equal(LuaModuleDependencyKind.Static, edge.Kind);
        Assert.Equal("dep", edge.Target?.Name);
        Assert.DoesNotContain(result.GetModule("app")!.Compilation.Diagnostics, static diagnostic =>
            diagnostic.Code is "LUA6003" or "LUA6007");
        Assert.Equal("integer", result.GetModule("app")!.ExportedType.DisplayName);
    }

    [Fact]
    public async Task AnyModuleExportRemainsConservativeAcrossRequire()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document("app", "return require('dep')"),
            Document("dep", "return unknown_factory()"),
        ]);

        Assert.Equal("any", result.GetModule("dep")!.ExportedType.DisplayName);
        Assert.Equal("any", result.GetModule("app")!.ExportedType.DisplayName);
    }

    [Fact]
    public async Task ShadowedRequireIsNotTreatedAsAModuleDependencyOrBuiltin()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document(
                "app",
                "local function require(name) return { value = name } end\n" +
                "local dep = require('not-a-module')\nreturn dep.value"),
        ]);

        Assert.Empty(result.Graph.Dependencies);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "LUA7002");
    }

    [Fact]
    public async Task DynamicAndUnresolvedRequiresRemainConservativeAndDiagnosable()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document(
                "app",
                "local name = 'dynamic'\nlocal a = require(name)\nlocal b = require('missing')\nreturn a or b"),
        ]);

        Assert.Contains(result.Graph.Dependencies, static dependency =>
            dependency.Kind == LuaModuleDependencyKind.Dynamic);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "LUA7002");
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "LUA7003");
    }

    [Fact]
    public async Task CyclicModulesReachADeterministicFixedPoint()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document("a", "local peer = require('b')\nreturn 'a'"),
            Document("b", "local peer = require('a')\nreturn 42"),
        ]);

        var component = Assert.Single(result.Graph.Components);
        Assert.True(component.IsCyclic);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "LUA7005");
        Assert.True(result.Metrics.FixedPointIterationCount >= 2);
        Assert.Equal("'a'", result.GetModule("a")!.ExportedType.DisplayName);
        Assert.Equal("42", result.GetModule("b")!.ExportedType.DisplayName);
    }

    [Fact]
    public async Task NonConvergingCycleUsesBoundedWidening()
    {
        using var workspace = new LuaWorkspace(new LuaWorkspaceOptions
        {
            MaximumFixedPointIterations = 1,
        });
        var result = await workspace.AnalyzeAsync([
            Document("a", "local peer = require('b')\nreturn { peer = peer, kind = 'a' }"),
            Document("b", "local peer = require('a')\nreturn { peer = peer, kind = 'b' }"),
        ]);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "LUA7005");
        Assert.All(result.Modules, static module => Assert.True(module.WasWidened));
        Assert.Equal(1, result.Metrics.FixedPointIterationCount);
    }

    [Fact]
    public async Task RepeatedSnapshotUsesContentAddressedAnalysisCache()
    {
        using var workspace = new LuaWorkspace();
        LuaWorkspaceDocument[] documents = [
            Document("app", "local dep = require('dep')\nreturn dep.value"),
            Document("dep", "return { value = 42 }"),
        ];

        var first = await workspace.AnalyzeAsync(documents);
        var second = await workspace.AnalyzeAsync(documents.Reverse());

        Assert.Equal(2, first.Metrics.CacheMissCount);
        Assert.Equal(0, first.Metrics.InvalidatedModuleCount);
        Assert.Equal(2, second.Metrics.CacheHitCount);
        Assert.Equal(0, second.Metrics.CacheMissCount);
        Assert.Equal(0, second.Metrics.InvalidatedModuleCount);
        Assert.All(second.Modules, static module => Assert.True(module.WasCacheHit));
    }

    [Fact]
    public async Task NonExportingLeafChangeInvalidatesOnlyThatModule()
    {
        using var workspace = new LuaWorkspace();
        var app = Document("app", "local dep = require('dep')\nreturn dep.value + 1");
        var first = await workspace.AnalyzeAsync([
            app,
            WidenedDependency(42),
        ]);
        var second = await workspace.AnalyzeAsync([
            app,
            WidenedDependency(43),
        ]);

        Assert.Equal(first.GetModule("dep")!.ExportHash, second.GetModule("dep")!.ExportHash);
        Assert.Equal(1, second.Metrics.CacheHitCount);
        Assert.Equal(1, second.Metrics.CacheMissCount);
        Assert.Equal(1, second.Metrics.InvalidatedModuleCount);
        Assert.True(second.GetModule("app")!.WasCacheHit);
        Assert.False(second.GetModule("dep")!.WasCacheHit);
    }

    [Fact]
    public async Task ExportChangeInvalidatesDependentAndPublishesNewDiagnostics()
    {
        using var workspace = new LuaWorkspace();
        var app = Document("app", "local dep = require('dep')\nreturn dep.value + 1");
        _ = await workspace.AnalyzeAsync([
            app,
            Document("dep", "return { value = 42 }"),
        ]);
        var changed = await workspace.AnalyzeAsync([
            app,
            Document("dep", "return { value = 'text' }"),
        ]);

        Assert.Equal(0, changed.Metrics.CacheHitCount);
        Assert.Equal(2, changed.Metrics.CacheMissCount);
        Assert.Equal(2, changed.Metrics.InvalidatedModuleCount);
        Assert.Contains(changed.GetModule("app")!.Compilation.Diagnostics, static diagnostic =>
            diagnostic.Code == "LUA6003");
    }

    [Fact]
    public async Task FileResolverUsesRootConfinedLuaPathPatterns()
    {
        var root = Path.Combine(Path.GetTempPath(), "lunil-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "pkg"));
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "pkg", "value.lua"),
                "return { answer = 42 }");
            var resolver = new LuaFileSystemModuleResolver(new LuaFileSystemModuleResolverOptions
            {
                RootDirectories = [root],
            });
            using var workspace = new LuaWorkspace(resolver: resolver);
            var result = await workspace.AnalyzeAsync([
                Document("app", "local value = require('pkg.value')\nreturn value.answer"),
            ]);

            Assert.NotNull(result.GetModule("pkg.value"));
            Assert.Equal("@pkg/value.lua", result.GetModule("pkg.value")!.SourceIdentity);
            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "LUA7002");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ModuleAndSourceBudgetsStopGraphExpansion()
    {
        var resolver = new LuaInMemoryModuleResolver([
            Document("dep", "return 42"),
        ]);
        using var workspace = new LuaWorkspace(new LuaWorkspaceOptions
        {
            MaximumModuleCount = 1,
            MaximumSourceBytes = 1_024,
        }, resolver);
        var result = await workspace.AnalyzeAsync([
            Document("app", "return require('dep')"),
        ]);

        Assert.Single(result.Graph.Nodes);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "LUA7004" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ResolverFailuresBecomeStableWorkspaceDiagnostics()
    {
        using var workspace = new LuaWorkspace(resolver: new FailingResolver());
        var result = await workspace.AnalyzeAsync([
            Document("app", "return require('dep')"),
        ]);

        var diagnostic = Assert.Single(result.Diagnostics.Where(static diagnostic =>
            diagnostic.Code == "LUA7006"));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("InvalidOperationException", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParallelResultMergingIsDeterministicAndGloballyBounded()
    {
        var documents = Enumerable.Range(0, 12)
            .Select(index => Document("m" + index, $"return {index}"))
            .ToArray();
        var options = new LuaWorkspaceOptions { MaximumParallelism = 3 };
        using var firstWorkspace = new LuaWorkspace(options);
        using var secondWorkspace = new LuaWorkspace(options);

        var first = await firstWorkspace.AnalyzeAsync(documents);
        var second = await secondWorkspace.AnalyzeAsync(documents.Reverse());

        Assert.InRange(first.Metrics.PeakParallelism, 2, 3);
        Assert.InRange(second.Metrics.PeakParallelism, 2, 3);
        Assert.Equal(
            first.Modules.Select(static module =>
                (module.Identity.Name, module.ContentHash, module.ExportHash)),
            second.Modules.Select(static module =>
                (module.Identity.Name, module.ContentHash, module.ExportHash)));
        Assert.Equal(first.Diagnostics, second.Diagnostics);
    }

    [Fact]
    public async Task CancellationAndDiagnosticSuppressionAreHonored()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var workspace = new LuaWorkspace(new LuaWorkspaceOptions
        {
            SuppressedDiagnosticCodes = ["LUA7002", "LUA7003"],
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.AnalyzeAsync([
            Document("app", "return 42"),
        ], cancelled.Token));
        var result = await workspace.AnalyzeAsync([
            Document("app", "local name = 'x'\nrequire(name)\nreturn require('missing')"),
        ]);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code is "LUA7002" or "LUA7003");
    }

    [Fact]
    public async Task DisposalLetsAnActiveSnapshotFinishAndRejectsNewOperations()
    {
        var resolver = new BlockingResolver(Document("dep", "return 42"));
        var workspace = new LuaWorkspace(resolver: resolver);
        var analysis = workspace.AnalyzeAsync([
            Document("app", "return require('dep')"),
        ]);
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        workspace.Dispose();
        resolver.Release.SetResult();

        var result = await analysis;
        Assert.True(result.Succeeded);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => workspace.AnalyzeAsync([
            Document("other", "return 1"),
        ]));
        Assert.Throws<ObjectDisposedException>(workspace.ClearCache);
    }

    private static LuaWorkspaceDocument Document(string name, string source) =>
        LuaWorkspaceDocument.FromUtf8(name, source);

    private static LuaWorkspaceDocument WidenedDependency(int value) =>
        Document(
            "dep",
            $"local exports = {{ value = {value} }}\n" +
            "---@cast exports { value: integer }\n" +
            "return exports");

    private sealed class FailingResolver : ILuaModuleResolver
    {
        public ValueTask<LuaWorkspaceDocument?> ResolveAsync(
            LuaModuleResolutionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("resolver failure");
    }

    private sealed class BlockingResolver(LuaWorkspaceDocument document) : ILuaModuleResolver
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<LuaWorkspaceDocument?> ResolveAsync(
            LuaModuleResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return document;
        }
    }
}
