using System.Collections;
using Lunil.Build.Tasks;
using Lunil.CodeGen.Cil.Caching;
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

    [Fact]
    public void RestoresArtifactsFromBackendCacheAfterClean()
    {
        using var fixture = BuildFixture.Create();
        var first = fixture.CreateTask(
            new RecordingBuildEngine(),
            new TaskItem(fixture.SourcePath));
        Assert.True(first.Execute());
        var expectedPe = File.ReadAllBytes(Assert.Single(first.GeneratedReferences).ItemSpec);
        Directory.Delete(fixture.IntermediateOutputPath, recursive: true);
        var engine = new RecordingBuildEngine();
        var second = fixture.CreateTask(engine, new TaskItem(fixture.SourcePath));

        Assert.True(second.Execute());

        Assert.Equal(
            expectedPe,
            File.ReadAllBytes(Assert.Single(second.GeneratedReferences).ItemSpec));
        Assert.Contains(engine.Messages, static message =>
            message.Message?.Contains("restored from backend cache", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void CorruptBackendCacheIsQuarantinedAndRebuilt()
    {
        using var fixture = BuildFixture.Create();
        var first = fixture.CreateTask(
            new RecordingBuildEngine(),
            new TaskItem(fixture.SourcePath));
        Assert.True(first.Execute());
        Directory.Delete(fixture.IntermediateOutputPath, recursive: true);
        var payload = Assert.Single(Directory.EnumerateFiles(
            fixture.CacheDirectory,
            "payload.bin",
            SearchOption.AllDirectories));
        File.WriteAllBytes(payload, [0xff]);
        var engine = new RecordingBuildEngine();
        var second = fixture.CreateTask(engine, new TaskItem(fixture.SourcePath));

        Assert.True(second.Execute());

        Assert.Empty(engine.Errors);
        Assert.Single(Directory.EnumerateDirectories(
            Path.Combine(fixture.CacheDirectory, "quarantine")));
        Assert.DoesNotContain(engine.Messages, static message =>
            message.Message?.Contains("restored from backend cache", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void InvalidLocalPeIsReplacedFromValidatedBackendCache()
    {
        using var fixture = BuildFixture.Create();
        var first = fixture.CreateTask(
            new RecordingBuildEngine(),
            new TaskItem(fixture.SourcePath));
        Assert.True(first.Execute());
        var pePath = Assert.Single(first.GeneratedReferences).ItemSpec;
        var expectedPe = File.ReadAllBytes(pePath);
        File.WriteAllBytes(pePath, [0]);
        var engine = new RecordingBuildEngine();
        var second = fixture.CreateTask(engine, new TaskItem(fixture.SourcePath));

        Assert.True(second.Execute());

        Assert.Equal(expectedPe, File.ReadAllBytes(pePath));
        Assert.Contains(engine.Messages, static message =>
            message.Message?.Contains("restored from backend cache", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void NativeAotAndPortableBuildsUseDifferentCacheKeys()
    {
        using var fixture = BuildFixture.Create();
        var portable = fixture.CreateTask(
            new RecordingBuildEngine(),
            new TaskItem(fixture.SourcePath));
        Assert.True(portable.Execute());
        Directory.Delete(fixture.IntermediateOutputPath, recursive: true);
        var engine = new RecordingBuildEngine();
        var nativeAot = fixture.CreateTask(engine, new TaskItem(fixture.SourcePath));
        nativeAot.PublishAot = "true";

        Assert.True(nativeAot.Execute());

        Assert.DoesNotContain(engine.Messages, static message =>
            message.Message?.Contains("restored from backend cache", StringComparison.Ordinal) == true);
        Assert.Equal(
            2,
            Directory.EnumerateFiles(
                fixture.CacheDirectory,
                "payload.bin",
                SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async System.Threading.Tasks.Task SemanticallyInvalidCachePayloadIsIsolatedBeforeRecompile()
    {
        using var fixture = BuildFixture.Create();
        var first = fixture.CreateTask(
            new RecordingBuildEngine(),
            new TaskItem(fixture.SourcePath));
        Assert.True(first.Execute());
        var descriptorPath = Assert.Single(Directory.EnumerateFiles(
            fixture.CacheDirectory,
            "descriptor.json",
            SearchOption.AllDirectories));
        var key = LuaBackendCacheKey.ParseCanonicalDescriptor(File.ReadAllBytes(descriptorPath));
        var cache = new LuaBackendDiskCache(new LuaBackendDiskCacheOptions
        {
            RootDirectory = fixture.CacheDirectory,
        });
        var cached = await cache.TryReadAsync(key);
        Assert.True(cached.IsHit);
        Assert.True(await cache.QuarantineAsync(key, "test replacement"));
        var invalidPayload = cached.Payload.ToArray();
        invalidPayload[^1] ^= 0xff;
        Assert.Equal(
            LuaBackendCacheWriteStatus.Created,
            (await cache.WriteAsync(key, invalidPayload)).Status);
        Directory.Delete(fixture.IntermediateOutputPath, recursive: true);
        var engine = new RecordingBuildEngine();
        var second = fixture.CreateTask(engine, new TaskItem(fixture.SourcePath));

        Assert.True(second.Execute());

        Assert.Empty(engine.Errors);
        Assert.Contains(engine.Messages, static message =>
            message.Message?.Contains("was rejected and will be rebuilt", StringComparison.Ordinal) == true);
        Assert.Equal(
            2,
            Directory.EnumerateDirectories(
                Path.Combine(fixture.CacheDirectory, "quarantine")).Count());
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

        public string IntermediateOutputPath => Path.Combine(Root, "obj", "lunil");

        public string CacheDirectory => Path.Combine(Root, "cache");

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
                IntermediateOutputPath = IntermediateOutputPath,
                TargetFramework = "net10.0",
                CacheDirectory = CacheDirectory,
            };

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class RecordingBuildEngine : IBuildEngine
    {
        public List<BuildErrorEventArgs> Errors { get; } = [];

        public List<BuildMessageEventArgs> Messages { get; } = [];

        public bool ContinueOnError => false;

        public int LineNumberOfTaskNode => 0;

        public int ColumnNumberOfTaskNode => 0;

        public string ProjectFileOfTaskNode => "test.proj";

        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);

        public void LogWarningEvent(BuildWarningEventArgs e)
        {
        }

        public void LogMessageEvent(BuildMessageEventArgs e) => Messages.Add(e);

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
