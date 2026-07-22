using System.Globalization;
using Lunil.EmmyLua;
using Lunil.StaticAnalysis.Embedding;

namespace Lunil.Workspace.Tests;

public sealed class StaticAnalysisEmbeddingSampleTests
{
    [Fact]
    public async Task EndToEndSampleCompilesAndExercisesIncrementalWorkspaceContracts()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var run = await EmbeddingScenario.RunAsync(output);

        Assert.True(run.SingleFile.Succeeded);
        Assert.Contains(
            run.SingleFile.Annotations.Annotations,
            static annotation => annotation is LuaClassAnnotationSyntax);
        Assert.Contains(
            run.SingleFile.Annotations.Annotations,
            static annotation => annotation is LuaAliasAnnotationSyntax);
        Assert.Contains(
            run.SingleFile.Annotations.Annotations,
            static annotation => annotation is LuaEnumAnnotationSyntax);
        Assert.NotEmpty(run.SingleFile.SemanticModel.Symbols);
        Assert.NotEmpty(run.SingleFile.SemanticModel.References);
        Assert.NotEmpty(run.SingleFile.Analysis.Functions);
        Assert.NotEmpty(run.SingleFile.Analysis.CallGraph.Edges);

        var component = Assert.Single(run.InitialWorkspace.Graph.Components);
        Assert.True(component.IsCyclic);
        Assert.Equal(2, component.Modules.Length);
        Assert.True(run.InitialWorkspace.Metrics.FixedPointIterationCount >= 2);
        Assert.True(run.CachedWorkspace.Metrics.CacheHitCount >= 2);
        Assert.Equal(0, run.CachedWorkspace.Metrics.CacheMissCount);
        Assert.Equal(2, run.ChangedWorkspace.Metrics.InvalidatedModuleCount);
        Assert.Equal("'changed'", run.ChangedWorkspace.GetModule("game.service")!.ExportedType.DisplayName);

        var text = output.ToString();
        Assert.Contains("== single-file compilation ==", text, StringComparison.Ordinal);
        Assert.Contains("utf16[", text, StringComparison.Ordinal);
        Assert.Contains("-- symbols, references, and types --", text, StringComparison.Ordinal);
        Assert.Contains("-- functions, CFG, and calls --", text, StringComparison.Ordinal);
        Assert.Contains("== workspace snapshot: cached ==", text, StringComparison.Ordinal);
        Assert.Contains("workspace-index functions=", text, StringComparison.Ordinal);
    }
}
