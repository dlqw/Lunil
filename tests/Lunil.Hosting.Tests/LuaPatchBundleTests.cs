using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Lunil.Core;

namespace Lunil.Hosting.Tests;

public sealed class LuaPatchBundleTests
{
    [Fact]
    public void SignedBundleRoundTripsDeterministically()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signer = new LuaPatchEcdsaSigner("release-1", key);
        var trust = new LuaPatchEcdsaTrustStore([
            new LuaPatchTrustedEcdsaKey("release-1", key.ExportSubjectPublicKeyInfo()),
        ]);
        var bundle = CreateBundle(signer);

        var first = Write(bundle);
        var second = Write(bundle);
        Assert.Equal(first, second);

        using var stream = new MemoryStream(first);
        var restored = LuaPatchBundle.Read(stream, trust);

        Assert.Equal("hotfix-42", restored.Manifest.PatchId);
        Assert.Equal(2, restored.Entries.Length);
        Assert.Equal("game.main", restored.Entries[0].ModuleName);
        Assert.Equal("game.shared", restored.Entries[1].ModuleName);
        Assert.Equal("return require('game.shared')", Encoding.UTF8.GetString(restored.Entries[0].Content.Span));
        Assert.Equal("return 42", Encoding.UTF8.GetString(restored.Entries[1].Content.Span));
    }

    [Fact]
    public void TamperedPayloadIsRejectedBeforeUse()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = new LuaPatchEcdsaTrustStore([
            new LuaPatchTrustedEcdsaKey("release-1", key.ExportSubjectPublicKeyInfo()),
        ]);
        var bytes = Write(CreateBundle(new LuaPatchEcdsaSigner("release-1", key)));
        bytes[^1] ^= 0x01;

        using var stream = new MemoryStream(bytes);
        var exception = Assert.Throws<LuaPatchFormatException>(() => LuaPatchBundle.Read(stream, trust));
        Assert.Equal(LuaPatchErrorCode.ContentHashMismatch, exception.Code);
    }

    [Fact]
    public void ReaderEnforcesEntryAndTotalSizeBudgets()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = new LuaPatchEcdsaTrustStore([
            new LuaPatchTrustedEcdsaKey("release-1", key.ExportSubjectPublicKeyInfo()),
        ]);
        var bytes = Write(CreateBundle(new LuaPatchEcdsaSigner("release-1", key)));

        using var stream = new MemoryStream(bytes);
        var exception = Assert.Throws<LuaPatchFormatException>(() => LuaPatchBundle.Read(
            stream,
            trust,
            new LuaPatchBundleReadOptions { MaximumEntryBytes = 8 }));
        Assert.Equal(LuaPatchErrorCode.ResourceLimitExceeded, exception.Code);
    }

    [Fact]
    public void BuilderRejectsUnsafeOrDuplicateEntryNames()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signer = new LuaPatchEcdsaSigner("release-1", key);
        var manifest = CreateManifest();

        var unsafeEntry = new LuaPatchEntry(
            "../escape.lua",
            "game.main",
            LuaPatchEntryKind.Source,
            "return 1"u8.ToArray());
        var unsafeException = Assert.Throws<LuaPatchFormatException>(
            () => LuaPatchBundle.Create(manifest, [unsafeEntry], signer));
        Assert.Equal(LuaPatchErrorCode.UnsafeEntryName, unsafeException.Code);

        var duplicate = new LuaPatchEntry(
            "modules/main.lua",
            "game.main",
            LuaPatchEntryKind.Source,
            "return 2"u8.ToArray());
        var duplicateException = Assert.Throws<LuaPatchFormatException>(() => LuaPatchBundle.Create(
            manifest,
            [duplicate, duplicate with { Content = "return 3"u8.ToArray() }],
            signer));
        Assert.Equal(LuaPatchErrorCode.DuplicateEntry, duplicateException.Code);
    }

    [Fact]
    public void DependencyPlanGroupsCyclesAndOrdersDependentsAfterDependencies()
    {
        var entries = ImmutableArray.Create(
            Entry("a", ["b"]),
            Entry("b", ["a"]),
            Entry("c", ["a"]));

        var plan = LuaPatchDependencyPlan.Create(entries);

        Assert.Equal(2, plan.Components.Length);
        Assert.True(plan.Components[0].IsCyclic);
        Assert.Equal<string>(["a", "b"], plan.Components[0].Modules);
        Assert.False(plan.Components[1].IsCyclic);
        Assert.Equal<string>(["c"], plan.Components[1].Modules);
    }

    [Fact]
    public void DependencyPlanRejectsMissingRequiredDependency()
    {
        var exception = Assert.Throws<LuaPatchFormatException>(() =>
            LuaPatchDependencyPlan.Create([Entry("a", ["missing"])]));

        Assert.Equal(LuaPatchErrorCode.MissingDependency, exception.Code);
    }

    [Fact]
    public void PreflightCompilesEveryModuleWithoutMutatingTheLiveHost()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var liveHost = new LuaHost();
        var bundle = CreateBundle(new LuaPatchEcdsaSigner("release-1", key));

        var result = LuaPatchPreflight.Analyze(bundle, liveHost.Options);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Modules.Length);
        Assert.All(result.Modules, static module => Assert.NotNull(module.Module));
        Assert.Empty(liveHost.State.GetLoadedModuleNames());
    }

    [Fact]
    public void PreflightReportsCompilationFailurePerModule()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bundle = LuaPatchBundle.Create(
            CreateManifest(),
            [new LuaPatchEntry(
                "modules/broken.lua",
                "broken",
                LuaPatchEntryKind.Source,
                "local ="u8.ToArray())],
            new LuaPatchEcdsaSigner("release-1", key));

        var result = LuaPatchPreflight.Analyze(bundle);

        Assert.False(result.Succeeded);
        Assert.Equal(LuaPatchPreflightStatus.CompilationFailed, Assert.Single(result.Modules).Status);
    }

    [Fact]
    public void AcceptancePolicyRejectsWrongBaseRevisionReplayAndExpiry()
    {
        var manifest = CreateManifest();
        var wrongRevision = new LuaPatchAcceptancePolicy
        {
            TargetBuild = "game-100",
            CurrentRevision = "build-98",
            RuntimeAbi = "lunil-0.12",
            AllowedChannels = ["production"],
        }.Evaluate(manifest, manifest.CreatedAt);
        Assert.Equal(LuaPatchAcceptanceStatus.BaseRevisionMismatch, wrongRevision.Status);

        var replay = new LuaPatchAcceptancePolicy
        {
            TargetBuild = "game-100",
            CurrentRevision = "build-99",
            RuntimeAbi = "lunil-0.12",
            AllowedChannels = ["production"],
            ReplayLookup = static (patchId, nonce) =>
                patchId == "hotfix-42" && nonce == "release-42",
        }.Evaluate(manifest, manifest.CreatedAt);
        Assert.Equal(LuaPatchAcceptanceStatus.ReplayDetected, replay.Status);

        var expired = new LuaPatchAcceptancePolicy
        {
            TargetBuild = "game-100",
            CurrentRevision = "build-99",
            RuntimeAbi = "lunil-0.12",
            AllowedChannels = ["production"],
        }.Evaluate(manifest, manifest.ExpiresAt);
        Assert.Equal(LuaPatchAcceptanceStatus.Expired, expired.Status);
    }

    [Fact]
    public void AcceptancePolicyResumesReservationUntilCommitThenRejectsReplay()
    {
        var manifest = CreateManifest();
        var policy = new LuaPatchAcceptancePolicy
        {
            TargetBuild = "game-100",
            CurrentRevision = "build-99",
            RuntimeAbi = "lunil-0.12",
            AllowedChannels = ["production"],
        };
        var store = new AtomicReplayStore();

        var first = policy.TryReserve(manifest, "state-a", store, manifest.CreatedAt);
        var resumed = policy.TryReserve(manifest, "state-a", store, manifest.CreatedAt);
        using (var lease = store.TryAcquireCommit(first.ReplayReservation!, manifest.CreatedAt)!)
        {
            lease.Complete(manifest.CreatedAt.AddSeconds(1));
        }
        var replay = policy.TryReserve(manifest, "state-a", store, manifest.CreatedAt);

        Assert.True(first.Accepted);
        Assert.True(resumed.Accepted);
        Assert.Equal(first.ReplayReservation, resumed.ReplayReservation);
        Assert.Equal(LuaPatchAcceptanceStatus.ReplayDetected, replay.Status);
        Assert.Equal(3, store.AttemptCount);
    }

    private static LuaPatchBundle CreateBundle(ILuaPatchSigner signer)
    {
        var main = new LuaPatchEntry(
            "modules/main.lua",
            "game.main",
            LuaPatchEntryKind.Source,
            Encoding.UTF8.GetBytes("return require('game.shared')"),
            [new LuaPatchDependency("game.shared", LuaPatchDependencyKind.Required)]);
        var shared = new LuaPatchEntry(
            "modules/shared.lua",
            "game.shared",
            LuaPatchEntryKind.Source,
            Encoding.UTF8.GetBytes("return 42"));
        return LuaPatchBundle.Create(CreateManifest(), [shared, main], signer);
    }

    private static LuaPatchManifest CreateManifest() => new()
    {
        PatchId = "hotfix-42",
        Channel = "production",
        TargetBuild = "game-100",
        BaseRevision = "build-99",
        TargetRevision = "build-100",
        LanguageVersion = LuaLanguageVersion.Lua54,
        RuntimeAbi = "lunil-0.12",
        CreatedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero),
        ExpiresAt = new DateTimeOffset(2026, 8, 22, 0, 0, 0, TimeSpan.Zero),
        Nonce = "release-42",
    };

    private static LuaPatchEntry Entry(string name, string[] dependencies) => new(
        $"modules/{name}.lua",
        name,
        LuaPatchEntryKind.Source,
        "return true"u8.ToArray(),
        dependencies.Select(static dependency =>
            new LuaPatchDependency(dependency, LuaPatchDependencyKind.Required)).ToImmutableArray());

    private static byte[] Write(LuaPatchBundle bundle)
    {
        using var stream = new MemoryStream();
        bundle.Write(stream);
        return stream.ToArray();
    }

    private sealed class AtomicReplayStore : ILuaPatchReplayStore
    {
        private readonly object _gate = new();
        private LuaPatchReplayReservation? _reservation;
        private bool _committed;
        private bool _leased;

        public int AttemptCount { get; private set; }

        public LuaPatchReplayReservationResult TryReserve(
            string scope,
            string patchId,
            string nonce,
            DateTimeOffset reservedAt)
        {
            lock (_gate)
            {
                AttemptCount++;
                _reservation ??= new LuaPatchReplayReservation(
                    scope, patchId, nonce, "reservation", reservedAt);
                if (_committed || _reservation.Scope != scope ||
                    _reservation.PatchId != patchId || _reservation.Nonce != nonce)
                {
                    return new LuaPatchReplayReservationResult(
                        LuaPatchReplayReservationStatus.ReplayDetected, null, "replay");
                }

                return new LuaPatchReplayReservationResult(
                    LuaPatchReplayReservationStatus.Reserved, _reservation, null);
            }
        }

        public ILuaPatchReplayCommitLease? TryAcquireCommit(
            LuaPatchReplayReservation reservation,
            DateTimeOffset acquiredAt)
        {
            lock (_gate)
            {
                if (_committed || _leased || reservation != _reservation)
                {
                    return null;
                }

                _leased = true;
                return new Lease(this, reservation);
            }
        }

        private sealed class Lease(
            AtomicReplayStore owner,
            LuaPatchReplayReservation reservation) : ILuaPatchReplayCommitLease
        {
            public LuaPatchReplayReservation Reservation => reservation;

            public bool IsCompleted { get; private set; }

            public void Complete(DateTimeOffset committedAt)
            {
                lock (owner._gate)
                {
                    owner._committed = true;
                    IsCompleted = true;
                }
            }

            public void Reopen(DateTimeOffset reopenedAt)
            {
                lock (owner._gate)
                {
                    owner._committed = false;
                    IsCompleted = false;
                }
            }

            public void Dispose()
            {
                lock (owner._gate)
                {
                    owner._leased = false;
                }
            }
        }
    }
}
