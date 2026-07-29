using System.Text;
using Lunil.Unity;
using UnityEngine;

namespace Lunil.EngineAdapters.Tests;

public sealed class UnityAdapterLogicTests
{
    [Fact]
    public void DispatcherBoundsOwnerThreadQueueAndClosure()
    {
        var dispatcher = new LuaUnityDispatcher();
        var trace = 0;
        dispatcher.Post(() => trace = trace * 10 + 1);
        dispatcher.Post(() => trace = trace * 10 + 2);

        Assert.True(dispatcher.CheckAccess());
        Assert.Equal(1, dispatcher.Drain(1));
        Assert.Equal(1, trace);
        Assert.Equal(1, dispatcher.Drain(4));
        Assert.Equal(12, trace);
        Assert.Throws<ArgumentOutOfRangeException>(() => dispatcher.Drain(0));

        Exception? wrongThread = null;
        var thread = new Thread(() =>
        {
            Assert.False(dispatcher.CheckAccess());
            wrongThread = Assert.Throws<InvalidOperationException>(() => dispatcher.Drain(1));
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.NotNull(wrongThread);

        dispatcher.Post(() => trace = -1);
        dispatcher.Close();
        Assert.Equal(0, dispatcher.Drain(4));
        Assert.Throws<ObjectDisposedException>(() => dispatcher.Post(() => { }));
        Assert.Throws<ArgumentNullException>(() => dispatcher.Post(null!));
    }

    [Fact]
    public void ClockAndConsoleUseExactEngineServices()
    {
        Time.realtimeSinceStartupAsDouble = 12.345678;
        var clock = new LuaUnityTimeProvider();
        var console = new LuaUnityConsole();

        console.Write("out"u8.ToArray());
        console.WriteError("err"u8.ToArray());
        console.WriteLine();

        Assert.Equal(1_000_000, clock.TimestampFrequency);
        Assert.Equal(12_345_678, clock.GetTimestamp());
        Assert.NotEqual(default, clock.GetUtcNow());
        Assert.Empty(console.ReadStandardInput());
        Assert.Contains(Debug.Messages, item => !item.Error && item.Text == "out");
        Assert.Contains(Debug.Messages, item => item.Error && item.Text == "err");
    }

    [Fact]
    public async Task AssetResolverPreservesIdentitiesAliasesCancellationAndMissingValues()
    {
        var main = Asset("return 1", "@Assets/Lua/main.lua", "game.main");
        var shared = Asset("return 2", "plain-shared", "game.shared");
        var resolver = new LuaUnityAssetResolver([main, null!, shared]);

        var found = await resolver.ResolveAsync(main.AssetId);
        var missing = await resolver.ResolveAsync("missing");
        var module = await resolver.ResolveAsync(new Lunil.Workspace.LuaModuleResolutionRequest(
            new Lunil.Workspace.LuaModuleIdentity("origin"), "game.shared", default));

        Assert.True(found.Found);
        Assert.Equal("return 1", Encoding.UTF8.GetString(found.Value.Span));
        Assert.False(missing.Found);
        Assert.Equal("game.shared", module!.Module.Name);
        Assert.True(resolver.FileExists("./game/main.lua"));
        Assert.True(resolver.FileExists("Assets\\Lua\\main.lua"));
        Assert.Equal("return 2", Encoding.UTF8.GetString(resolver.ReadAllBytes("game/shared.lua")));
        Assert.Throws<FileNotFoundException>(() => resolver.ReadAllBytes("missing.lua"));
        Assert.Throws<ArgumentNullException>(() => resolver.FileExists(null!));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await resolver.ResolveAsync("x", new CancellationToken(true)));
    }

    [Fact]
    public void AssetAndResolverRejectInvalidOrConflictingMetadata()
    {
        var first = Asset("a", "@same.lua", "same");
        var duplicateAsset = Asset("b", "@same.lua", "other");
        var duplicateModule = Asset("b", "@other.lua", "same");
        var duplicateFile = Asset("b", "@same.lua", "same.lua");

        Assert.Throws<ArgumentNullException>(() => new LuaUnityAssetResolver(null!));
        Assert.Throws<ArgumentException>(() => new LuaUnityAssetResolver([first, duplicateAsset]));
        Assert.Throws<ArgumentException>(() => new LuaUnityAssetResolver([first, duplicateModule]));
        Assert.Throws<ArgumentException>(() => new LuaUnityAssetResolver([first, duplicateFile]));
        Assert.Throws<ArgumentNullException>(() => first.SetImportedData(null!, "a", "m"));
        Assert.Throws<ArgumentException>(() => first.SetImportedData([], " ", "m"));
        Assert.Throws<ArgumentException>(() => first.SetImportedData([], "a", " "));
    }

    [Fact]
    public async Task PersistentStoreConfinesKeysAndReplacesValues()
    {
        var root = Path.Combine(Path.GetTempPath(), "lunil-unity-adapter-" + Guid.NewGuid().ToString("N"));
        Application.persistentDataPath = root;
        try
        {
            Assert.Throws<ArgumentException>(() => new LuaUnityPersistentStore(" "));
            Assert.Throws<ArgumentException>(() => new LuaUnityPersistentStore("bad/name"));
            var store = new LuaUnityPersistentStore("state");
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

    private static LuaScriptAsset Asset(string source, string assetId, string moduleName)
    {
        var asset = new LuaScriptAsset();
        asset.SetImportedData(Encoding.UTF8.GetBytes(source), assetId, moduleName);
        return asset;
    }
}
