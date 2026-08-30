using System.Text;
using Godot;
using Lunil.Godot;

namespace Lunil.EngineAdapters.Tests;

public sealed class GodotAdapterLogicTests
{
    [Fact]
    public void DispatcherBoundsOwnerThreadQueueAndDisposal()
    {
        var dispatcher = new LuaGodotDispatcher();
        var trace = 0;
        dispatcher.Post(() => trace = trace * 10 + 1);
        dispatcher.Post(() => trace = trace * 10 + 2);
        Assert.Equal(1, dispatcher.Drain(1));
        Assert.Equal(1, dispatcher.Drain(8));
        Assert.Equal(12, trace);
        Assert.Throws<ArgumentOutOfRangeException>(() => dispatcher.Drain(0));

        Exception? wrongThread = null;
        var thread = new Thread(() =>
            wrongThread = Assert.Throws<InvalidOperationException>(() => dispatcher.Drain(1)));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.NotNull(wrongThread);

        dispatcher.Post(() => trace = -1);
        dispatcher.Dispose();
        dispatcher.Dispose();
        Assert.Equal(0, dispatcher.Drain(8));
        Assert.Throws<ObjectDisposedException>(() => dispatcher.Post(() => { }));
        Assert.Throws<ArgumentNullException>(() => dispatcher.Post(null!));
    }

    [Fact]
    public void ClockConsoleAndResourceFallbackUseExactGodotValues()
    {
        global::Godot.Time.TicksUsec = 42;
        var clock = new LuaGodotTimeProvider();
        var console = new LuaGodotConsole();
        console.Write(ReadOnlyMemory<byte>.Empty);
        console.WriteLine();
        console.Write("out"u8.ToArray());
        console.WriteError("err"u8.ToArray());
        console.WriteLine();

        var explicitResource = Resource("x", "@explicit", "module");
        var pathResource = Resource("x", "", "module");
        pathResource.ResourcePath = "res://fallback.lua";

        Assert.Equal(1_000_000, clock.TimestampFrequency);
        Assert.Equal(42, clock.GetTimestamp());
        Assert.NotEqual(default, clock.GetUtcNow());
        Assert.Empty(console.ReadStandardInput());
        Assert.Equal("@explicit", explicitResource.GetEffectiveAssetId());
        Assert.Equal("@res://fallback.lua", pathResource.GetEffectiveAssetId());
        Assert.Contains(GD.Messages, item => !item.Error && item.Text == "out");
        Assert.Contains(GD.Messages, item => item.Error && item.Text == "err");
        Assert.Throws<InvalidOperationException>(() => Resource("x", "", "module").GetEffectiveAssetId());
    }

    [Fact]
    public async Task ResolverPreservesIdentitiesAliasesLoadsAndCancellation()
    {
        ResourceLoader.Clear();
        var main = Resource("return 1", "@res://main.lua", "game.main");
        var shared = Resource("return 2", "plain", "game.shared");
        ResourceLoader.Register("res://main.tres", main);
        var loaded = LuaGodotAssetResolver.Load(["res://main.tres"]);
        var resolver = new LuaGodotAssetResolver([main, null!, shared]);

        Assert.True((await loaded.ResolveAsync(main.AssetId)).Found);
        Assert.False((await resolver.ResolveAsync("missing")).Found);
        var document = await resolver.ResolveAsync(new Lunil.Workspace.LuaModuleResolutionRequest(
            new Lunil.Workspace.LuaModuleIdentity("origin"), "game.shared", default));
        Assert.Equal("game.shared", document!.Module.Name);
        Assert.True(resolver.FileExists("./game/main.lua"));
        Assert.True(resolver.FileExists("res://main.lua"));
        Assert.True(resolver.FileExists("game\\shared.lua"));
        Assert.Equal("return 2", Encoding.UTF8.GetString(resolver.ReadAllBytes("game/shared.lua")));
        Assert.Throws<FileNotFoundException>(() => resolver.ReadAllBytes("missing"));
        Assert.Throws<ArgumentNullException>(() => resolver.FileExists(null!));
        Assert.Throws<ArgumentNullException>(() => LuaGodotAssetResolver.Load(null!));
        Assert.Throws<ArgumentException>(() => LuaGodotAssetResolver.Load([" "]));
        Assert.Throws<FileNotFoundException>(() => LuaGodotAssetResolver.Load(["res://missing.tres"]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await resolver.ResolveAsync("x", new CancellationToken(true)));
    }

    [Fact]
    public void ResolverRejectsInvalidOrConflictingMetadata()
    {
        var first = Resource("a", "@same.lua", "same");
        var duplicateAsset = Resource("b", "@same.lua", "other");
        var duplicateModule = Resource("b", "@other.lua", "same");
        var duplicateFile = Resource("b", "@same.lua", "same.lua");

        Assert.Throws<ArgumentNullException>(() => new LuaGodotAssetResolver(null!));
        Assert.Throws<ArgumentException>(() =>
            new LuaGodotAssetResolver([Resource("x", "@x", " ")]));
        Assert.Throws<ArgumentException>(() => new LuaGodotAssetResolver([first, duplicateAsset]));
        Assert.Throws<ArgumentException>(() => new LuaGodotAssetResolver([first, duplicateModule]));
        Assert.Throws<ArgumentException>(() => new LuaGodotAssetResolver([first, duplicateFile]));
    }

    [Fact]
    public async Task PersistentStoreConfinesKeysAndReplacesValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "lunil-godot-adapter-" + Guid.NewGuid().ToString("N"));
        ProjectSettings.UserRoot = root;
        try
        {
            Assert.Throws<ArgumentException>(() => new LuaGodotPersistentStore(" "));
            Assert.Throws<ArgumentException>(() => new LuaGodotPersistentStore("bad/name"));
            var store = new LuaGodotPersistentStore("state");
            Assert.False((await store.ReadAsync("slot")).Found);
            await store.WriteAsync("slot", "one"u8.ToArray());
            await store.WriteAsync("slot", "two"u8.ToArray());
            Assert.Equal("two", Encoding.UTF8.GetString((await store.ReadAsync("slot")).Value.Span));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await store.ReadAsync("slot", new CancellationToken(true)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await store.WriteAsync("slot", ReadOnlyMemory<byte>.Empty, new CancellationToken(true)));
            await Assert.ThrowsAsync<ArgumentException>(async () => await store.ReadAsync(" "));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static LuaGodotScriptResource Resource(
        string source,
        string assetId,
        string moduleName) => new()
        {
            Source = source,
            AssetId = assetId,
            ModuleName = moduleName,
        };

    [Fact]
    public void DefaultGodotCapabilitiesDenyProcessAndEnvironmentAccess()
    {
        var resolver = new LuaGodotAssetResolver(Array.Empty<LuaGodotScriptResource>());
        var options = LuaGodotServices.CreateDefaultStandardLibrary(resolver);

        Assert.Same(resolver, options.FileSystem);
        Assert.Throws<UnauthorizedAccessException>(() => options.OperatingSystem.Execute("dir"));
        Assert.Throws<UnauthorizedAccessException>(() => options.OperatingSystem.Terminate(1, false));
        Assert.Null(options.Environment.GetEnvironmentVariable("PATH"));
    }
}
