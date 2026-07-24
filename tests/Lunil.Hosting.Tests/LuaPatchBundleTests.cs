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
    public void SignedManifestCanonicalizesIntentAndCapabilityRequests()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signer = new LuaPatchEcdsaSigner("rollback-1", key);
        var bundle = LuaPatchBundle.Create(
            CreateManifest() with
            {
                UpdateIntent = LuaPatchUpdateIntent.Rollback,
                RequiredCapabilities = ["game.world-write", "game.inventory-v2"],
            },
            [Entry("main", [])],
            signer);
        var trust = new LuaPatchEcdsaTrustStore([
            new LuaPatchTrustedEcdsaKey("rollback-1", key.ExportSubjectPublicKeyInfo()),
        ]);

        using var stream = new MemoryStream(Write(bundle));
        var restored = LuaPatchBundle.Read(
            stream,
            trust,
            new LuaPatchBundleReadOptions { UtcNow = bundle.Manifest.CreatedAt });
        using var limitedStream = new MemoryStream(Write(bundle));
        var limited = Assert.Throws<LuaPatchFormatException>(() => LuaPatchBundle.Read(
            limitedStream,
            trust,
            new LuaPatchBundleReadOptions
            {
                UtcNow = bundle.Manifest.CreatedAt,
                MaximumCapabilityCount = 1,
            }));

        Assert.Equal(LuaPatchUpdateIntent.Rollback, restored.Manifest.UpdateIntent);
        Assert.Equal<string>(
            ["game.inventory-v2", "game.world-write"],
            restored.Manifest.RequiredCapabilities);
        Assert.Equal(LuaPatchErrorCode.ResourceLimitExceeded, limited.Code);
    }

    [Fact]
    public void SignedManifestCanonicalizesAndBoundsRequiredTargetLabels()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signer = new LuaPatchEcdsaSigner("release-1", key);
        var bundle = LuaPatchBundle.Create(
            CreateManifest() with
            {
                RequiredTargetLabels =
                [
                    new("shard", "eu-2"),
                    new("environment", "production"),
                ],
            },
            [Entry("main", [])],
            signer);
        var trust = new LuaPatchEcdsaTrustStore([
            new LuaPatchTrustedEcdsaKey("release-1", key.ExportSubjectPublicKeyInfo()),
        ]);

        using var stream = new MemoryStream(Write(bundle));
        var restored = LuaPatchBundle.Read(
            stream,
            trust,
            new LuaPatchBundleReadOptions { UtcNow = bundle.Manifest.CreatedAt });
        using var limitedStream = new MemoryStream(Write(bundle));
        var limited = Assert.Throws<LuaPatchFormatException>(() => LuaPatchBundle.Read(
            limitedStream,
            trust,
            new LuaPatchBundleReadOptions
            {
                UtcNow = bundle.Manifest.CreatedAt,
                MaximumTargetLabelCount = 1,
            }));
        var duplicate = Assert.Throws<LuaPatchFormatException>(() => LuaPatchBundle.Create(
            CreateManifest() with
            {
                RequiredTargetLabels = [new("shard", "eu-1"), new("shard", "eu-2")],
            },
            [Entry("main", [])],
            signer));
        var invalid = Assert.Throws<LuaPatchFormatException>(() => LuaPatchBundle.Create(
            CreateManifest() with { RequiredTargetLabels = [new(" shard", "eu-2")] },
            [Entry("main", [])],
            signer));
        var oversized = Assert.Throws<LuaPatchFormatException>(() => LuaPatchBundle.Create(
            CreateManifest() with
            {
                RequiredTargetLabels = [new("shard", new string('x', 513))],
            },
            [Entry("main", [])],
            signer));

        Assert.Equal(
            ["environment", "shard"],
            restored.Manifest.RequiredTargetLabels.Select(static label => label.Name));
        Assert.Equal(LuaPatchErrorCode.ResourceLimitExceeded, limited.Code);
        Assert.Equal(LuaPatchErrorCode.InvalidManifest, duplicate.Code);
        Assert.Equal(LuaPatchErrorCode.InvalidManifest, invalid.Code);
        Assert.Equal(LuaPatchErrorCode.ResourceLimitExceeded, oversized.Code);
    }

    [Fact]
    public void BuilderRejectsDuplicateInvalidAndExcessiveCapabilityRequests()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signer = new LuaPatchEcdsaSigner("release-1", key);
        var entry = Entry("main", []);

        var duplicate = Assert.Throws<LuaPatchFormatException>(() => LuaPatchBundle.Create(
            CreateManifest() with { RequiredCapabilities = ["game.write", "game.write"] },
            [entry],
            signer));
        var invalid = Assert.Throws<LuaPatchFormatException>(() => LuaPatchBundle.Create(
            CreateManifest() with { RequiredCapabilities = [" game.write"] },
            [entry],
            signer));
        var excessive = Assert.Throws<LuaPatchFormatException>(() => LuaPatchBundle.Create(
            CreateManifest() with
            {
                RequiredCapabilities = Enumerable.Range(0, 129)
                    .Select(static index => $"game.capability-{index:D3}")
                    .ToImmutableArray(),
            },
            [entry],
            signer));
        var oversizedName = Assert.Throws<LuaPatchFormatException>(() => LuaPatchBundle.Create(
            CreateManifest() with { RequiredCapabilities = [new string('x', 257)] },
            [entry],
            signer));

        Assert.Equal(LuaPatchErrorCode.InvalidManifest, duplicate.Code);
        Assert.Equal(LuaPatchErrorCode.InvalidManifest, invalid.Code);
        Assert.Equal(LuaPatchErrorCode.ResourceLimitExceeded, excessive.Code);
        Assert.Equal(LuaPatchErrorCode.ResourceLimitExceeded, oversizedName.Code);
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
    public void ReaderEnforcesSigningKeyActivationExpiryAndRevocationAtOneInstant()
    {
        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = Write(CreateBundle(new LuaPatchEcdsaSigner("release-1", key)));

        AssertTrustFailure(
            bytes,
            new LuaPatchTrustedEcdsaKey("release-1", key.ExportSubjectPublicKeyInfo())
            {
                ValidFrom = now.AddSeconds(1),
            },
            now,
            LuaPatchErrorCode.SigningKeyNotYetValid);
        AssertTrustFailure(
            bytes,
            new LuaPatchTrustedEcdsaKey("release-1", key.ExportSubjectPublicKeyInfo())
            {
                ValidUntil = now,
            },
            now,
            LuaPatchErrorCode.SigningKeyExpired);
        AssertTrustFailure(
            bytes,
            new LuaPatchTrustedEcdsaKey("release-1", key.ExportSubjectPublicKeyInfo())
            {
                ValidFrom = now.AddDays(-1),
                ValidUntil = now.AddDays(1),
                RevokedAt = now,
            },
            now,
            LuaPatchErrorCode.SigningKeyRevoked);
    }

    [Fact]
    public void TrustStoreSupportsOverlappingRotationWindows()
    {
        var rotation = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        using var oldKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var nextKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trust = new LuaPatchEcdsaTrustStore([
            new LuaPatchTrustedEcdsaKey("release-old", oldKey.ExportSubjectPublicKeyInfo())
            {
                ValidUntil = rotation.AddMinutes(5),
            },
            new LuaPatchTrustedEcdsaKey("release-next", nextKey.ExportSubjectPublicKeyInfo())
            {
                ValidFrom = rotation.AddMinutes(-5),
            },
        ]);

        var before = trust.EvaluateTrust(
            LuaPatchEcdsaSigner.AlgorithmName,
            "release-old",
            rotation);
        var nextBefore = trust.EvaluateTrust(
            LuaPatchEcdsaSigner.AlgorithmName,
            "release-next",
            rotation);
        var oldAfter = trust.EvaluateTrust(
            LuaPatchEcdsaSigner.AlgorithmName,
            "release-old",
            rotation.AddMinutes(5));

        Assert.True(before.Trusted);
        Assert.True(nextBefore.Trusted);
        Assert.Equal(LuaPatchSignatureTrustStatus.Expired, oldAfter.Status);
    }

    [Fact]
    public void ReaderUsesTheSameConfiguredInstantForTrustAndVerification()
    {
        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stream = new MemoryStream(Write(
            CreateBundle(new LuaPatchEcdsaSigner("release-1", key))));
        var trust = new CapturingTrustPolicy();

        _ = LuaPatchBundle.Read(
            stream,
            trust,
            new LuaPatchBundleReadOptions { UtcNow = now });

        Assert.Equal(now, trust.EvaluationTime);
        Assert.Equal(now, trust.VerificationTime);
        Assert.Equal(0, trust.LegacyCallCount);
    }

    [Fact]
    public void TrustStoreRejectsInvalidKeysAndValidityWindowsAtConstruction()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var wrongCurve = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentException>(() => new LuaPatchEcdsaTrustStore([
            new LuaPatchTrustedEcdsaKey("bad", new byte[] { 1, 2, 3 }),
        ]));
        Assert.Throws<ArgumentException>(() => new LuaPatchEcdsaTrustStore([
            new LuaPatchTrustedEcdsaKey("wrong-curve", wrongCurve.ExportSubjectPublicKeyInfo()),
        ]));
        Assert.Throws<ArgumentException>(() => new LuaPatchEcdsaTrustStore([
            new LuaPatchTrustedEcdsaKey("release-1", key.ExportSubjectPublicKeyInfo())
            {
                ValidFrom = now,
                ValidUntil = now,
            },
        ]));
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
    public void AcceptancePolicyRequiresCapabilitiesAndExplicitRollbackAuthorization()
    {
        var manifest = CreateManifest() with
        {
            TargetRevision = "build-98",
            UpdateIntent = LuaPatchUpdateIntent.Rollback,
            RequiredCapabilities = ["game.inventory-v2"],
        };
        var signer = new LuaPatchSignerIdentity(
            LuaPatchEcdsaSigner.AlgorithmName,
            "rollback-1");
        var policy = new LuaPatchAcceptancePolicy
        {
            TargetBuild = "game-100",
            CurrentRevision = "build-99",
            RuntimeAbi = "lunil-0.12",
            AllowedChannels = ["production"],
            RevisionClassifier = static (_, _) => LuaPatchUpdateIntent.Rollback,
        };

        var unauthorized = policy.Evaluate(manifest, signer, manifest.CreatedAt);
        var unsignedRollback = (policy with
        {
            RollbackAuthorizer = static (_, _) => true,
        }).Evaluate(manifest, manifest.CreatedAt);
        var capabilityDenied = (policy with
        {
            RollbackAuthorizer = static (_, identity) => identity.KeyId == "rollback-1",
        }).Evaluate(manifest, signer, manifest.CreatedAt);
        var accepted = (policy with
        {
            GrantedCapabilities = ["game.inventory-v2", "game.world-write"],
            RollbackAuthorizer = static (patch, identity) =>
                identity.KeyId == "rollback-1" && patch.TargetRevision == "build-98",
        }).Evaluate(manifest, signer, manifest.CreatedAt);
        var mismatch = (policy with
        {
            GrantedCapabilities = ["game.inventory-v2"],
            RevisionClassifier = static (_, _) => LuaPatchUpdateIntent.Forward,
            RollbackAuthorizer = static (_, _) => true,
        }).Evaluate(manifest, signer, manifest.CreatedAt);

        Assert.Equal(LuaPatchAcceptanceStatus.RollbackNotAuthorized, unauthorized.Status);
        Assert.Equal(LuaPatchAcceptanceStatus.RollbackNotAuthorized, unsignedRollback.Status);
        Assert.Equal(LuaPatchAcceptanceStatus.CapabilityDenied, capabilityDenied.Status);
        Assert.True(accepted.Accepted);
        Assert.Equal(LuaPatchAcceptanceStatus.UpdateIntentMismatch, mismatch.Status);
    }

    [Fact]
    public void AcceptancePolicyRequiresExactSignedTargetLabels()
    {
        var manifest = CreateManifest() with
        {
            RequiredTargetLabels =
            [
                new("environment", "production"),
                new("shard", "eu-2"),
            ],
        };
        var policy = new LuaPatchAcceptancePolicy
        {
            TargetBuild = "game-100",
            CurrentRevision = "build-99",
            RuntimeAbi = "lunil-0.12",
            AllowedChannels = ["production"],
            TargetLabels =
            [
                new("shard", "eu-2"),
                new("environment", "production"),
                new("ring", "canary"),
            ],
        };

        var accepted = policy.Evaluate(manifest, manifest.CreatedAt);
        var wrongValue = (policy with
        {
            TargetLabels = [new("environment", "production"), new("shard", "eu-1")],
        }).Evaluate(manifest, manifest.CreatedAt);
        var wrongCase = (policy with
        {
            TargetLabels = [new("environment", "Production"), new("shard", "eu-2")],
        }).Evaluate(manifest, manifest.CreatedAt);

        Assert.True(accepted.Accepted);
        Assert.Equal(LuaPatchAcceptanceStatus.TargetSelectorMismatch, wrongValue.Status);
        Assert.Equal(LuaPatchAcceptanceStatus.TargetSelectorMismatch, wrongCase.Status);
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

    private static void AssertTrustFailure(
        byte[] bundleBytes,
        LuaPatchTrustedEcdsaKey trustedKey,
        DateTimeOffset verificationTime,
        LuaPatchErrorCode expectedCode)
    {
        using var stream = new MemoryStream(bundleBytes);
        var exception = Assert.Throws<LuaPatchFormatException>(() => LuaPatchBundle.Read(
            stream,
            new LuaPatchEcdsaTrustStore([trustedKey]),
            new LuaPatchBundleReadOptions { UtcNow = verificationTime }));
        Assert.Equal(expectedCode, exception.Code);
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

    private sealed class CapturingTrustPolicy : ILuaPatchSignatureTrustPolicy
    {
        public DateTimeOffset? EvaluationTime { get; private set; }

        public DateTimeOffset? VerificationTime { get; private set; }

        public int LegacyCallCount { get; private set; }

        public LuaPatchSignatureTrustResult EvaluateTrust(
            string algorithm,
            string keyId,
            DateTimeOffset verificationTime)
        {
            EvaluationTime = verificationTime;
            return new LuaPatchSignatureTrustResult(LuaPatchSignatureTrustStatus.Trusted, null);
        }

        public bool VerifyDigest(
            string algorithm,
            string keyId,
            ReadOnlySpan<byte> digest,
            ReadOnlySpan<byte> signature,
            DateTimeOffset verificationTime)
        {
            VerificationTime = verificationTime;
            return true;
        }

        public bool IsTrusted(string algorithm, string keyId)
        {
            LegacyCallCount++;
            return false;
        }

        public bool VerifyDigest(
            string algorithm,
            string keyId,
            ReadOnlySpan<byte> digest,
            ReadOnlySpan<byte> signature)
        {
            LegacyCallCount++;
            return false;
        }
    }
}
