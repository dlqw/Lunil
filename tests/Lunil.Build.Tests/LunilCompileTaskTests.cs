using System.Collections;
using Lunil.Build.Tasks;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Lunil.Build.Tests;

public sealed class LunilCompileTaskTests
{
    [Fact]
    public void AcceptsTheDocumentedItemMetadata()
    {
        using var fixture = BuildFixture.Create();
        var item = new TaskItem(fixture.SourcePath);
        item.SetMetadata("ModuleName", "sample.math");
        item.SetMetadata("Optimization", "Release");
        item.SetMetadata("DebugSymbols", "true");
        item.SetMetadata("Sandbox", "Restricted");
        var engine = new RecordingBuildEngine();
        var task = fixture.CreateTask(engine, item);

        Assert.True(task.Execute());
        Assert.Empty(engine.Errors);
        Assert.Single(task.GeneratedSources);
        Assert.Single(task.GeneratedReferences);
        Assert.Contains(task.GeneratedArtifacts, static item => item.ItemSpec.EndsWith(".lunil.json", StringComparison.Ordinal));
        Assert.Contains("sample.math", File.ReadAllText(task.GeneratedSources[0].ItemSpec), StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsStableCodesForInvalidMetadata()
    {
        using var fixture = BuildFixture.Create();
        var item = new TaskItem(fixture.SourcePath);
        item.SetMetadata("ModuleName", "not a module");
        var engine = new RecordingBuildEngine();
        var task = fixture.CreateTask(engine, item);

        Assert.False(task.Execute());
        Assert.Contains(engine.Errors, static error =>
            error.Code == LunilBuildDiagnosticCodes.InvalidModuleName);
    }

    [Fact]
    public void RejectsDuplicateModuleNames()
    {
        using var fixture = BuildFixture.Create();
        var first = new TaskItem(fixture.SourcePath);
        var secondPath = Path.Combine(fixture.Root, "other.lua");
        File.WriteAllText(secondPath, "return 2");
        var second = new TaskItem(secondPath);
        first.SetMetadata("ModuleName", "duplicate");
        second.SetMetadata("ModuleName", "duplicate");
        var engine = new RecordingBuildEngine();
        var task = fixture.CreateTask(engine, first, second);

        Assert.False(task.Execute());
        Assert.Contains(engine.Errors, static error =>
            error.Code == LunilBuildDiagnosticCodes.DuplicateModuleName);
    }

    [Fact]
    public void ReportsLuaSourceLocations()
    {
        using var fixture = BuildFixture.Create("local x = 1\nlocal = 2");
        var engine = new RecordingBuildEngine();
        var task = fixture.CreateTask(engine, new TaskItem(fixture.SourcePath));

        Assert.False(task.Execute());
        Assert.Contains(engine.Errors, static error =>
            error.Code == LunilBuildDiagnosticCodes.CompilationFailed &&
            error.LineNumber == 2 && error.ColumnNumber > 0);
    }

    [Fact]
    public void RejectsMalformedBinaryChunksWithAStableCode()
    {
        using var fixture = BuildFixture.CreateBytes([0x1b, (byte)'L', (byte)'u', (byte)'a']);
        var item = new TaskItem(fixture.SourcePath);
        item.SetMetadata("InputKind", "BinaryChunk");
        var engine = new RecordingBuildEngine();
        var task = fixture.CreateTask(engine, item);

        Assert.False(task.Execute());
        Assert.Contains(engine.Errors, static error =>
            error.Code == LunilBuildDiagnosticCodes.InvalidBinaryChunk);
    }

    [Fact]
    public void ReusesUnchangedArtifactsWithoutTouchingThem()
    {
        using var fixture = BuildFixture.Create();
        var first = fixture.CreateTask(new RecordingBuildEngine(), new TaskItem(fixture.SourcePath));
        Assert.True(first.Execute());
        var pePath = Assert.Single(first.GeneratedReferences).ItemSpec;
        var originalWriteTime = File.GetLastWriteTimeUtc(pePath);

        var second = fixture.CreateTask(new RecordingBuildEngine(), new TaskItem(fixture.SourcePath));
        Assert.True(second.Execute());

        Assert.Equal(pePath, Assert.Single(second.GeneratedReferences).ItemSpec);
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(pePath));
    }

    [Fact]
    public void DesignTimeBuildDoesNotEmitOrReferencePeArtifacts()
    {
        using var fixture = BuildFixture.Create();
        var task = fixture.CreateTask(new RecordingBuildEngine(), new TaskItem(fixture.SourcePath));
        task.DesignTimeBuild = "true";

        Assert.True(task.Execute());
        Assert.Empty(task.GeneratedReferences);
        Assert.Single(task.GeneratedSources);
        Assert.DoesNotContain(
            task.GeneratedArtifacts,
            static item => item.ItemSpec.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParallelTasksProduceTheSameCompleteArtifactSet()
    {
        using var fixture = BuildFixture.Create();
        var tasks = Enumerable.Range(0, 4)
            .Select(_ => fixture.CreateTask(
                new RecordingBuildEngine(),
                new TaskItem(fixture.SourcePath)))
            .ToArray();

        Parallel.ForEach(tasks, static task => Assert.True(task.Execute()));

        var references = tasks.Select(static task => Assert.Single(task.GeneratedReferences).ItemSpec);
        Assert.Single(references.Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.All(tasks, static task => Assert.All(
            task.GeneratedArtifacts,
            static artifact => Assert.True(File.Exists(artifact.ItemSpec))));
    }

    private sealed class BuildFixture : IDisposable
    {
        private BuildFixture(string root, string sourcePath)
        {
            Root = root;
            SourcePath = sourcePath;
        }

        public string Root { get; }

        public string SourcePath { get; }

        public static BuildFixture Create(string source = "return 1")
        {
            var root = Path.Combine(Path.GetTempPath(), "lunil-build-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sourcePath = Path.Combine(root, "sample.lua");
            File.WriteAllText(sourcePath, source);
            return new BuildFixture(root, sourcePath);
        }

        public static BuildFixture CreateBytes(ReadOnlySpan<byte> content)
        {
            var fixture = Create();
            File.WriteAllBytes(fixture.SourcePath, content.ToArray());
            return fixture;
        }

        public LunilCompileTask CreateTask(IBuildEngine engine, params ITaskItem[] sources) =>
            new()
            {
                BuildEngine = engine,
                Sources = sources,
                ProjectDirectory = Root,
                IntermediateOutputPath = Path.Combine(Root, "obj", "lunil"),
                TargetFramework = "net10.0",
            };

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class RecordingBuildEngine : IBuildEngine
    {
        public List<BuildErrorEventArgs> Errors { get; } = [];

        public bool ContinueOnError => false;

        public int LineNumberOfTaskNode => 0;

        public int ColumnNumberOfTaskNode => 0;

        public string ProjectFileOfTaskNode => "test.proj";

        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);

        public void LogWarningEvent(BuildWarningEventArgs e)
        {
        }

        public void LogMessageEvent(BuildMessageEventArgs e)
        {
        }

        public void LogCustomEvent(CustomBuildEventArgs e)
        {
        }

        public bool BuildProjectFile(
            string projectFileName,
            string[] targetNames,
            IDictionary globalProperties,
            IDictionary targetOutputs) => false;
    }
}
