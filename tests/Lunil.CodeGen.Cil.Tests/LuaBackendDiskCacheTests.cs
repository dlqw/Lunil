using Lunil.CodeGen.Cil.Caching;

namespace Lunil.CodeGen.Cil.Tests;

public sealed class LuaBackendDiskCacheTests
{
    [Fact]
    public async Task WritesReadsAndReusesCommittedEntry()
    {
        using var directory = new TemporaryDirectory();
        var cache = CreateCache(directory.Path);
        var key = CreateKey(1);
        byte[] payload = [1, 2, 3, 4];

        var created = await cache.WriteAsync(key, payload);
        var reused = await cache.WriteAsync(key, payload);
        var read = await cache.TryReadAsync(key);

        Assert.Equal(LuaBackendCacheWriteStatus.Created, created.Status);
        Assert.Equal(LuaBackendCacheWriteStatus.AlreadyPresent, reused.Status);
        Assert.True(read.IsHit);
        Assert.Equal(payload, read.Payload.ToArray());
    }

    [Fact]
    public async Task ConcurrentWritersCommitExactlyOneEntry()
    {
        using var directory = new TemporaryDirectory();
        var firstCache = CreateCache(directory.Path);
        var secondCache = CreateCache(directory.Path);
        var key = CreateKey(2);
        byte[] payload = [5, 6, 7];

        var results = await Task.WhenAll(
            firstCache.WriteAsync(key, payload).AsTask(),
            secondCache.WriteAsync(key, payload).AsTask());

        Assert.Single(results, static result =>
            result.Status == LuaBackendCacheWriteStatus.Created);
        Assert.Single(results, static result =>
            result.Status == LuaBackendCacheWriteStatus.AlreadyPresent);
        Assert.True((await firstCache.TryReadAsync(key)).IsHit);
    }

    [Fact]
    public async Task CorruptPayloadIsQuarantinedAndBecomesSafeMiss()
    {
        using var directory = new TemporaryDirectory();
        var cache = CreateCache(directory.Path);
        var key = CreateKey(3);
        Assert.Equal(
            LuaBackendCacheWriteStatus.Created,
            (await cache.WriteAsync(key, new byte[] { 8, 9, 10 })).Status);
        File.WriteAllBytes(GetEntryFile(directory.Path, key, "payload.bin"), [0xff]);

        var corrupt = await cache.TryReadAsync(key);
        var miss = await cache.TryReadAsync(key);

        Assert.Equal(LuaBackendCacheReadStatus.CorruptMiss, corrupt.Status);
        Assert.Equal(LuaBackendCacheDiagnosticCodes.CorruptEntry, corrupt.DiagnosticCode);
        Assert.Equal(LuaBackendCacheReadStatus.Miss, miss.Status);
        Assert.False(Directory.Exists(GetEntryPath(directory.Path, key)));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(directory.Path, "quarantine")));
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("descriptor.json")]
    [InlineData("access")]
    public async Task TruncatedMetadataIsQuarantined(string fileName)
    {
        using var directory = new TemporaryDirectory();
        var cache = CreateCache(directory.Path);
        var key = CreateKey(30 + fileName.Length);
        await cache.WriteAsync(key, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(GetEntryFile(directory.Path, key, fileName), [0]);

        var result = await cache.TryReadAsync(key);

        Assert.Equal(LuaBackendCacheReadStatus.CorruptMiss, result.Status);
        Assert.False(Directory.Exists(GetEntryPath(directory.Path, key)));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(directory.Path, "quarantine")));
    }

    [Fact]
    public async Task ConflictingPayloadIsIsolatedBeforeReplacement()
    {
        using var directory = new TemporaryDirectory();
        var cache = CreateCache(directory.Path);
        var key = CreateKey(4);
        await cache.WriteAsync(key, new byte[] { 1 });

        var replaced = await cache.WriteAsync(key, new byte[] { 2 });
        var read = await cache.TryReadAsync(key);

        Assert.Equal(LuaBackendCacheWriteStatus.Created, replaced.Status);
        Assert.Equal([2], read.Payload.ToArray());
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(directory.Path, "quarantine")));
    }

    [Fact]
    public async Task EntryLockTimeoutFailsSoftly()
    {
        using var directory = new TemporaryDirectory();
        var key = CreateKey(5);
        var cache = CreateCache(directory.Path);
        await cache.WriteAsync(key, new byte[] { 1, 2 });
        var lockPath = Path.Combine(directory.Path, "locks", $"{key.CacheId}.lock");
        await using var heldLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        var impatientCache = CreateCache(
            directory.Path,
            lockTimeout: TimeSpan.FromMilliseconds(60));

        var result = await impatientCache.TryReadAsync(key);

        Assert.Equal(LuaBackendCacheReadStatus.Unavailable, result.Status);
        Assert.Equal(LuaBackendCacheDiagnosticCodes.Unavailable, result.DiagnosticCode);
    }

    [Fact]
    public async Task TrimEvictsLeastRecentlyUsedEntriesAndCleansOrphans()
    {
        using var directory = new TemporaryDirectory();
        var cache = CreateCache(directory.Path);
        var oldest = CreateKey(6);
        var middle = CreateKey(7);
        var newest = CreateKey(8);
        foreach (var key in new[] { oldest, middle, newest })
        {
            await cache.WriteAsync(key, new byte[512]);
        }

        SetAccessTime(directory.Path, oldest, DateTime.UtcNow.AddHours(-3));
        SetAccessTime(directory.Path, middle, DateTime.UtcNow.AddHours(-2));
        SetAccessTime(directory.Path, newest, DateTime.UtcNow.AddHours(-1));
        var middleSize = GetDirectorySize(GetEntryPath(directory.Path, middle));
        var newestSize = GetDirectorySize(GetEntryPath(directory.Path, newest));
        var orphan = Path.Combine(directory.Path, "tmp", "orphan");
        Directory.CreateDirectory(orphan);
        Directory.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-1));
        var quotaCache = CreateCache(
            directory.Path,
            maximumBytes: middleSize + newestSize,
            orphanAge: TimeSpan.Zero);

        var trim = await quotaCache.TrimAsync();

        Assert.True(trim.Succeeded);
        Assert.Equal(1, trim.RemovedEntries);
        Assert.True(trim.RemovedBytes > 0);
        Assert.False(Directory.Exists(GetEntryPath(directory.Path, oldest)));
        Assert.True(Directory.Exists(GetEntryPath(directory.Path, middle)));
        Assert.True(Directory.Exists(GetEntryPath(directory.Path, newest)));
        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public async Task RejectsEntryThatCannotFitQuotaWithoutCreatingFiles()
    {
        using var directory = new TemporaryDirectory();
        var cache = CreateCache(
            directory.Path,
            maximumBytes: 1024,
            maximumEntryBytes: 1024);
        var key = CreateKey(9);

        var result = await cache.WriteAsync(key, new byte[512]);

        Assert.Equal(LuaBackendCacheWriteStatus.RejectedTooLarge, result.Status);
        Assert.Equal(LuaBackendCacheDiagnosticCodes.EntryTooLarge, result.DiagnosticCode);
        Assert.False(Directory.Exists(GetEntryPath(directory.Path, key)));
    }

    [Fact]
    public async Task QuarantineUsesIndependentSizeQuota()
    {
        using var directory = new TemporaryDirectory();
        var cache = CreateCache(directory.Path, maximumQuarantineBytes: 0);
        var key = CreateKey(10);
        await cache.WriteAsync(key, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(GetEntryFile(directory.Path, key, "payload.bin"), [9]);
        Assert.Equal(
            LuaBackendCacheReadStatus.CorruptMiss,
            (await cache.TryReadAsync(key)).Status);

        var trim = await cache.TrimAsync();

        Assert.True(trim.Succeeded);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(directory.Path, "quarantine")));
    }

    private static LuaBackendDiskCache CreateCache(
        string root,
        long maximumBytes = 8 * 1024 * 1024,
        long maximumEntryBytes = 1024 * 1024,
        long maximumQuarantineBytes = 1024 * 1024,
        TimeSpan? lockTimeout = null,
        TimeSpan? orphanAge = null) => new(new LuaBackendDiskCacheOptions
        {
            RootDirectory = root,
            MaximumBytes = maximumBytes,
            MaximumEntryBytes = maximumEntryBytes,
            MaximumQuarantineBytes = maximumQuarantineBytes,
            LockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5),
            LockRetryDelay = TimeSpan.FromMilliseconds(10),
            OrphanTemporaryEntryAge = orphanAge ?? TimeSpan.FromHours(1),
        });

    private static LuaBackendCacheKey CreateKey(int id) => LuaBackendCacheKey.Create(new()
    {
        ArtifactKind = LuaBackendCacheArtifactKind.PersistedCil,
        SourceContentHash = LuaBackendCacheKey.ComputeContentHash([(byte)id]),
        CanonicalModuleHash = LuaBackendCacheKey.ComputeContentHash([0xb]),
        SourceBindingId = $"module:{id}",
        CompilerVersion = "0.6.0-alpha.5",
        Optimization = LuaBackendOptimizationMode.Release,
        DebugSymbols = false,
        HookMode = LuaBackendHookMode.Disabled,
        SandboxMode = LuaBackendSandboxMode.Restricted,
        TargetFramework = "net10.0",
        RuntimeIdentifier = "win-x64",
        DeploymentMode = LuaBackendDeploymentMode.CoreClr,
        TrimmingMode = LuaBackendTrimmingMode.Disabled,
    });

    private static string GetEntryPath(string root, LuaBackendCacheKey key) => Path.Combine(
        root,
        "entries-v1",
        key.CacheId[..2],
        key.CacheId);

    private static string GetEntryFile(
        string root,
        LuaBackendCacheKey key,
        string fileName) => Path.Combine(GetEntryPath(root, key), fileName);

    private static void SetAccessTime(
        string root,
        LuaBackendCacheKey key,
        DateTime value) => File.SetLastWriteTimeUtc(
            GetEntryFile(root, key, "access"),
            value);

    private static long GetDirectorySize(string path) => Directory
        .EnumerateFiles(path, "*", SearchOption.AllDirectories)
        .Sum(static file => new FileInfo(file).Length);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "lunil-cache-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
