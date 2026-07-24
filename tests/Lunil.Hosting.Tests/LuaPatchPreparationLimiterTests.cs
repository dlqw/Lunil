using System.Security.Cryptography;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.StandardLibrary;

namespace Lunil.Hosting.Tests;

public sealed class LuaPatchPreparationLimiterTests
{
    [Fact]
    public async Task ZeroQueueBackpressureDefersSyncPreparationBeforePreflight()
    {
        using var first = CreateHost();
        using var second = CreateHost();
        Load(first);
        Load(second);
        var bundle = CreateBundle();
        using var decoder = BlockingDecoder(first);
        var limiter = new LuaPatchPreparationLimiter(1, 0);
        var options = Options(decoder, limiter, Timeout.InfiniteTimeSpan);
        var replayStore = new CountingReplayStore();

        var active = first.PreparePatchAsync(bundle, options);
        Assert.True(decoder.Entered.Wait(TimeSpan.FromSeconds(10)));

        var deferred = second.PreparePatch(bundle, options with
        {
            AcceptancePolicy = new LuaPatchAcceptancePolicy
            {
                TargetBuild = "build-2",
                CurrentRevision = "build-1",
                RuntimeAbi = "lunil-0.12",
                AllowedChannels = ["test"],
            },
            ReplayStore = replayStore,
            ReplayScope = "state-b",
        });

        Assert.Equal(LuaPatchPrepareStatus.Deferred, deferred.Status);
        Assert.Equal(
            LuaPatchPreparationAdmissionStatus.Saturated,
            deferred.AdmissionStatus);
        Assert.Null(deferred.PreparedPatch);
        Assert.All(deferred.Modules, static module =>
            Assert.Equal(LuaPatchPrepareStatus.Deferred, module.Status));
        Assert.All(deferred.Preflight.Modules, static module =>
            Assert.Equal(LuaPatchPreflightStatus.Deferred, module.Status));
        Assert.Equal(1, limiter.ActiveCount);
        Assert.Equal(0, limiter.QueuedCount);
        Assert.Equal(1, Value(first));
        Assert.Equal(1, Value(second));
        Assert.Equal(0, replayStore.ReservationCount);

        decoder.Release.Set();
        var prepared = await active.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(prepared.Succeeded, prepared.Message);
        Assert.Equal(
            LuaPatchPreparationAdmissionStatus.Acquired,
            prepared.AdmissionStatus);
        Assert.Equal(0, limiter.ActiveCount);
    }

    [Fact]
    public async Task BoundedQueueRejectsOverflowAndAdmitsTheOldestWaiter()
    {
        using var first = CreateHost();
        using var second = CreateHost();
        using var third = CreateHost();
        Load(first);
        Load(second);
        Load(third);
        var bundle = CreateBundle();
        using var decoder = BlockingDecoder(first);
        var limiter = new LuaPatchPreparationLimiter(1, 1);
        var queuedOptions = Options(decoder, limiter, Timeout.InfiniteTimeSpan);

        var active = first.PreparePatchAsync(bundle, queuedOptions);
        Assert.True(decoder.Entered.Wait(TimeSpan.FromSeconds(10)));
        var queued = second.PreparePatchAsync(bundle, queuedOptions);
        Assert.True(SpinWait.SpinUntil(
            () => limiter.QueuedCount == 1,
            TimeSpan.FromSeconds(10)));

        var overflow = await third.PreparePatchAsync(bundle, queuedOptions);
        Assert.Equal(LuaPatchPrepareStatus.Deferred, overflow.Status);
        Assert.Equal(
            LuaPatchPreparationAdmissionStatus.Saturated,
            overflow.AdmissionStatus);
        Assert.Contains("saturated", overflow.Message, StringComparison.Ordinal);
        Assert.Equal(1, limiter.ActiveCount);
        Assert.Equal(1, limiter.QueuedCount);

        decoder.Release.Set();
        var admitted = await Task.WhenAll(active, queued).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(admitted, static result => Assert.True(result.Succeeded, result.Message));
        Assert.Equal(0, limiter.ActiveCount);
        Assert.Equal(0, limiter.QueuedCount);
    }

    [Fact]
    public async Task QueueWaitTimeoutReturnsDeferredAndReleasesQueueCapacity()
    {
        using var first = CreateHost();
        using var second = CreateHost();
        Load(first);
        Load(second);
        var bundle = CreateBundle();
        using var decoder = BlockingDecoder(first);
        var limiter = new LuaPatchPreparationLimiter(1, 1);

        var active = first.PreparePatchAsync(
            bundle,
            Options(decoder, limiter, Timeout.InfiniteTimeSpan));
        Assert.True(decoder.Entered.Wait(TimeSpan.FromSeconds(10)));

        var deferred = await second.PreparePatchAsync(
            bundle,
            Options(decoder, limiter, TimeSpan.FromMilliseconds(50)));

        Assert.Equal(LuaPatchPrepareStatus.Deferred, deferred.Status);
        Assert.Equal(
            LuaPatchPreparationAdmissionStatus.TimedOut,
            deferred.AdmissionStatus);
        Assert.Contains("wait budget elapsed", deferred.Message, StringComparison.Ordinal);
        Assert.Equal(0, limiter.QueuedCount);
        Assert.Equal(1, limiter.ActiveCount);

        decoder.Release.Set();
        Assert.True((await active.WaitAsync(TimeSpan.FromSeconds(10))).Succeeded);
        Assert.Equal(0, limiter.ActiveCount);
    }

    [Fact]
    public async Task CallerCancellationRemovesAQueuedPreparationWithoutConsumingTheSlot()
    {
        using var first = CreateHost();
        using var second = CreateHost();
        Load(first);
        Load(second);
        var bundle = CreateBundle();
        using var decoder = BlockingDecoder(first);
        var limiter = new LuaPatchPreparationLimiter(1, 1);
        var options = Options(decoder, limiter, Timeout.InfiniteTimeSpan);

        var active = first.PreparePatchAsync(bundle, options);
        Assert.True(decoder.Entered.Wait(TimeSpan.FromSeconds(10)));
        using var cancellation = new CancellationTokenSource();
        var queued = second.PreparePatchAsync(bundle, options, cancellation.Token);
        Assert.True(SpinWait.SpinUntil(
            () => limiter.QueuedCount == 1,
            TimeSpan.FromSeconds(10)));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(0, limiter.QueuedCount);
        Assert.Equal(1, limiter.ActiveCount);

        decoder.Release.Set();
        Assert.True((await active.WaitAsync(TimeSpan.FromSeconds(10))).Succeeded);
        Assert.Equal(0, limiter.ActiveCount);
    }

    [Fact]
    public async Task CallerCancellationDuringAdmittedPreflightReleasesTheSlot()
    {
        using var host = CreateHost();
        Load(host);
        var bundle = CreateBundle();
        using var decoder = BlockingDecoder(host);
        var limiter = new LuaPatchPreparationLimiter(1, 0);
        var options = Options(decoder, limiter, Timeout.InfiniteTimeSpan);
        using var cancellation = new CancellationTokenSource();

        var active = host.PreparePatchAsync(bundle, options, cancellation.Token);
        Assert.True(decoder.Entered.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, limiter.ActiveCount);

        cancellation.Cancel();
        decoder.Release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        Assert.Equal(0, limiter.ActiveCount);

        var retry = await host.PreparePatchAsync(bundle, options)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(retry.Succeeded, retry.Message);
        Assert.Equal(0, limiter.ActiveCount);
    }

    [Fact]
    public void LimiterAndWaitTimeoutValidateTheirResourceBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LuaPatchPreparationLimiter(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LuaPatchPreparationLimiter(1, -1));

        using var host = CreateHost();
        Load(host);
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => host.PreparePatch(
            CreateBundle(),
            new LuaPatchPrepareOptions
            {
                PreparationLimiter = new LuaPatchPreparationLimiter(1, 1),
                PreparationWaitTimeout = TimeSpan.FromMilliseconds(-2),
            }));
        Assert.Contains("wait timeout", error.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentOutOfRangeException>(() => host.PreparePatch(
            CreateBundle(),
            new LuaPatchPrepareOptions
            {
                PreparationLimiter = new LuaPatchPreparationLimiter(1, 1),
                PreparationWaitTimeout = TimeSpan.FromMilliseconds((double)int.MaxValue + 1),
            }));
    }

    private static LuaPatchPrepareOptions Options(
        ILuaPatchCanonicalIrDecoder decoder,
        LuaPatchPreparationLimiter limiter,
        TimeSpan waitTimeout) => new()
        {
            CanonicalIrDecoder = decoder,
            PreparationLimiter = limiter,
            PreparationWaitTimeout = waitTimeout,
        };

    private static BlockingCanonicalIrDecoder BlockingDecoder(LuaHost host)
    {
        var compilation = host.CompileUtf8("return {value=2}", "@patch/a.lua");
        Assert.True(compilation.Succeeded);
        return new BlockingCanonicalIrDecoder(compilation.Module!);
    }

    private static LuaHost CreateHost() => new(
        LuaHostOptions.Default with
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            StandardLibrary = LuaHostCapabilityProfiles.Create(LuaHostProfile.Restricted) with
            {
                FileSystem = new SingleFileSystem(),
            },
        });

    private static void Load(LuaHost host)
    {
        var result = host.RunUtf8("package.path='mods/?.lua'; require('a')");
        Assert.True(result.Succeeded);
    }

    private static long Value(LuaHost host) => host.RunUtf8(
        "return require('a').value").Execution!.Values[0].AsInteger();

    private static LuaPatchBundle CreateBundle()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return LuaPatchBundle.Create(
            new LuaPatchManifest
            {
                PatchId = "prepare-backpressure",
                Channel = "test",
                TargetBuild = "build-2",
                BaseRevision = "build-1",
                TargetRevision = "build-2",
                LanguageVersion = LuaLanguageVersion.Lua54,
                RuntimeAbi = "lunil-0.12",
                CreatedAt = new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.Zero),
                ExpiresAt = new DateTimeOffset(2099, 7, 24, 0, 0, 0, TimeSpan.Zero),
                Nonce = "prepare-backpressure",
            },
            [new LuaPatchEntry(
                "modules/a.ir",
                "a",
                LuaPatchEntryKind.CanonicalIr,
                new byte[] { 1 })],
            new LuaPatchEcdsaSigner("test", key));
    }

    private sealed class BlockingCanonicalIrDecoder(
        LuaIrModule module) : ILuaPatchCanonicalIrDecoder, IDisposable
    {
        private int _calls;

        public ManualResetEventSlim Entered { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public LuaIrModule Decode(string moduleName, ReadOnlySpan<byte> payload)
        {
            Assert.Equal("a", moduleName);
            Assert.Equal([1], payload.ToArray());
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.Set();
                Assert.True(Release.Wait(TimeSpan.FromSeconds(10)));
            }

            return module;
        }

        public void Dispose()
        {
            Release.Set();
            Entered.Dispose();
            Release.Dispose();
        }
    }

    private sealed class SingleFileSystem : ILuaFileSystem
    {
        private static readonly byte[] Source = "return {value=1}"u8.ToArray();

        public byte[] ReadAllBytes(string path) => path == "mods/a.lua"
            ? Source.ToArray()
            : throw new FileNotFoundException(path);

        public bool FileExists(string path) => path == "mods/a.lua";
    }

    private sealed class CountingReplayStore : ILuaPatchReplayStore
    {
        public int ReservationCount { get; private set; }

        public LuaPatchReplayReservationResult TryReserve(
            string scope,
            string patchId,
            string nonce,
            DateTimeOffset reservedAt)
        {
            ReservationCount++;
            return new LuaPatchReplayReservationResult(
                LuaPatchReplayReservationStatus.Reserved,
                new LuaPatchReplayReservation(
                    scope,
                    patchId,
                    nonce,
                    "reservation",
                    reservedAt),
                null);
        }

        public ILuaPatchReplayCommitLease? TryAcquireCommit(
            LuaPatchReplayReservation reservation,
            DateTimeOffset acquiredAt) => null;
    }
}
