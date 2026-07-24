using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Lunil.Core;
using Lunil.StandardLibrary;

namespace Lunil.Hosting.Tests;

public sealed class LuaPatchOperationalTests
{
    [Fact]
    public async Task ConcurrentTargetsReserveSamePatchInIndependentReplayScopes()
    {
        using var first = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        using var second = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        Load(first, "require('a')");
        Load(second, "require('a')");
        var bundle = CreateBundle(Entry("a", "return {value=2}"));
        var store = new AtomicReplayStore();
        var options = new LuaPatchPrepareOptions
        {
            AcceptancePolicy = new LuaPatchAcceptancePolicy
            {
                TargetBuild = "build-2",
                CurrentRevision = "build-1",
                RuntimeAbi = "lunil-0.12",
                AllowedChannels = ["test"],
            },
            ReplayStore = store,
            ReplayScope = "state-a",
            TimeProvider = new FixedTimeProvider(new DateTimeOffset(
                2026, 7, 23, 0, 0, 0, TimeSpan.Zero)),
        };

        var results = await Task.WhenAll(
            first.PreparePatchAsync(bundle, options),
            second.PreparePatchAsync(bundle, options with { ReplayScope = "state-b" }));

        Assert.All(results, static result => Assert.True(result.Succeeded));
        Assert.All(results, static result =>
            Assert.Equal(LuaPatchAcceptanceStatus.Accepted, result.Acceptance!.Status));
        Assert.Equal(2, store.AcceptedCount);
    }

    [Fact]
    public void PreparationRejectsPolicyMismatchWithoutRecordingReplay()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        Load(host, "require('a')");
        var bundle = CreateBundle(Entry("a", "return {value=2}"));
        var store = new AtomicReplayStore();
        var result = host.PreparePatch(bundle, new LuaPatchPrepareOptions
        {
            AcceptancePolicy = new LuaPatchAcceptancePolicy
            {
                TargetBuild = "different-build",
                CurrentRevision = "build-1",
                RuntimeAbi = "lunil-0.12",
            },
            ReplayStore = store,
            ReplayScope = "state-a",
            TimeProvider = new FixedTimeProvider(new DateTimeOffset(
                2026, 7, 23, 0, 0, 0, TimeSpan.Zero)),
        });

        Assert.Equal(LuaPatchPrepareStatus.AcceptanceRejected, result.Status);
        Assert.Null(result.PreparedPatch);
        Assert.Equal(LuaPatchAcceptanceStatus.TargetBuildMismatch, result.Acceptance!.Status);
        Assert.Equal(0, store.AcceptedCount);
    }

    [Fact]
    public void PreparationAuthorizesRollbackFromTheVerifiedBundleSigner()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=2}",
        });
        Load(host, "require('a')");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bundle = LuaPatchBundle.Create(
            new LuaPatchManifest
            {
                PatchId = "rollback-patch",
                Channel = "production",
                TargetBuild = "build-2",
                BaseRevision = "build-2",
                TargetRevision = "build-1",
                UpdateIntent = LuaPatchUpdateIntent.Rollback,
                RequiredCapabilities = ["game.state-write"],
                RequiredTargetLabels = [new("environment", "production"), new("shard", "eu-2")],
                LanguageVersion = LuaLanguageVersion.Lua54,
                RuntimeAbi = "lunil-0.12",
                CreatedAt = new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero),
                ExpiresAt = new DateTimeOffset(2099, 8, 22, 0, 0, 0, TimeSpan.Zero),
                Nonce = "rollback-operational",
            },
            [Entry("a", "return {value=1}")],
            new LuaPatchEcdsaSigner("rollback-key", key));
        var store = new AtomicReplayStore();
        var options = new LuaPatchPrepareOptions
        {
            AcceptancePolicy = new LuaPatchAcceptancePolicy
            {
                TargetBuild = "build-2",
                CurrentRevision = "build-2",
                RuntimeAbi = "lunil-0.12",
                AllowedChannels = ["production"],
                GrantedCapabilities = ["game.state-write"],
                TargetLabels = [new("environment", "production"), new("shard", "eu-2")],
                RevisionClassifier = static (current, target) =>
                    current == "build-2" && target == "build-1"
                        ? LuaPatchUpdateIntent.Rollback
                        : LuaPatchUpdateIntent.Forward,
                RollbackAuthorizer = static (patch, signer) =>
                    signer.Algorithm == LuaPatchEcdsaSigner.AlgorithmName &&
                    signer.KeyId == "rollback-key" && patch.TargetRevision == "build-1",
            },
            ReplayStore = store,
            ReplayScope = "state-a",
            TimeProvider = new FixedTimeProvider(new DateTimeOffset(
                2026, 7, 23, 1, 0, 0, TimeSpan.Zero)),
        };

        var accepted = host.PreparePatch(bundle, options);
        var deniedStore = new AtomicReplayStore();
        var denied = host.PreparePatch(bundle, options with
        {
            AcceptancePolicy = options.AcceptancePolicy! with
            {
                RollbackAuthorizer = static (_, signer) => signer.KeyId == "another-key",
            },
            ReplayStore = deniedStore,
        });
        var wrongTargetStore = new AtomicReplayStore();
        var wrongTarget = host.PreparePatch(bundle, options with
        {
            AcceptancePolicy = options.AcceptancePolicy! with
            {
                TargetLabels = [new("environment", "production"), new("shard", "us-1")],
            },
            ReplayStore = wrongTargetStore,
        });

        Assert.True(accepted.Succeeded, accepted.Message);
        Assert.Equal(LuaPatchAcceptanceStatus.Accepted, accepted.Acceptance!.Status);
        Assert.Equal(1, store.AcceptedCount);
        Assert.Equal(LuaPatchPrepareStatus.AcceptanceRejected, denied.Status);
        Assert.Equal(
            LuaPatchAcceptanceStatus.RollbackNotAuthorized,
            denied.Acceptance!.Status);
        Assert.Equal(0, deniedStore.AcceptedCount);
        Assert.Equal(LuaPatchPrepareStatus.AcceptanceRejected, wrongTarget.Status);
        Assert.Equal(
            LuaPatchAcceptanceStatus.TargetSelectorMismatch,
            wrongTarget.Acceptance!.Status);
        Assert.Equal(0, wrongTargetStore.AcceptedCount);
    }

    [Fact]
    public void PreparationRequiresAcceptancePolicyAndReplayStoreTogether()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        var bundle = CreateBundle(Entry("a", "return {value=2}"));
        var policy = new LuaPatchAcceptancePolicy
        {
            TargetBuild = "build-2",
            CurrentRevision = "build-1",
            RuntimeAbi = "lunil-0.12",
        };

        Assert.Throws<ArgumentException>(() => host.PreparePatch(
            bundle,
            new LuaPatchPrepareOptions { AcceptancePolicy = policy }));
        Assert.Throws<ArgumentException>(() => host.PreparePatch(
            bundle,
            new LuaPatchPrepareOptions { ReplayStore = new AtomicReplayStore() }));
        Assert.Throws<ArgumentException>(() => host.PreparePatch(
            bundle,
            new LuaPatchPrepareOptions
            {
                AcceptancePolicy = policy,
                ReplayStore = new AtomicReplayStore(),
            }));
    }

    [Fact]
    public void FailedLiveBindingDoesNotConsumeReplayIdentity()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        var store = new AtomicReplayStore();
        var result = host.PreparePatch(
            CreateBundle(Entry("a", "return {value=2}")),
            new LuaPatchPrepareOptions
            {
                AcceptancePolicy = new LuaPatchAcceptancePolicy
                {
                    TargetBuild = "build-2",
                    CurrentRevision = "build-1",
                    RuntimeAbi = "lunil-0.12",
                    AllowedChannels = ["test"],
                },
                ReplayStore = store,
                ReplayScope = "state-a",
                TimeProvider = new FixedTimeProvider(new DateTimeOffset(
                    2026, 7, 23, 0, 0, 0, TimeSpan.Zero)),
            });

        Assert.Equal(LuaPatchPrepareStatus.ModuleNotLoaded, result.Status);
        Assert.Null(result.Acceptance);
        Assert.Equal(0, store.AcceptedCount);
    }

    [Fact]
    public void PreparedReservationResumesAfterRestartAndBecomesTerminalAtCommit()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        Load(host, "require('a')");
        var bundle = CreateBundle(Entry("a", "return {value=2}"));
        var store = new AtomicReplayStore();
        var options = AcceptanceOptions(store, "state-a");

        var prepared = host.PreparePatch(bundle, options);
        var resumed = host.PreparePatch(bundle, options);

        Assert.True(prepared.Succeeded);
        Assert.True(resumed.Succeeded);
        Assert.Equal(
            prepared.Acceptance!.ReplayReservation,
            resumed.Acceptance!.ReplayReservation);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded);
        LuaPatchCommitResult committed;
        using (opened.Window!)
        {
            committed = host.CommitPatch(resumed.PreparedPatch!, opened.Window!);
        }

        Assert.True(committed.Succeeded);
        var replay = host.PreparePatch(bundle, options);
        Assert.Equal(LuaPatchPrepareStatus.AcceptanceRejected, replay.Status);
        Assert.Equal(LuaPatchAcceptanceStatus.ReplayDetected, replay.Acceptance!.Status);
    }

    [Fact]
    public void FailedCommitReleasesReservationForSafeRetry()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        Load(host, "require('a')");
        var bundle = CreateBundle(Entry("a", "error('boom')"));
        var store = new AtomicReplayStore();
        var options = AcceptanceOptions(store, "state-a");
        var prepared = host.PreparePatch(bundle, options);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded);
        LuaPatchCommitResult failed;
        using (opened.Window!)
        {
            failed = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.Equal(LuaPatchCommitStatus.ExecutionFailed, failed.Status);
        var retry = host.PreparePatch(bundle, options);
        Assert.True(retry.Succeeded);
        Assert.Equal(
            prepared.Acceptance!.ReplayReservation,
            retry.Acceptance!.ReplayReservation);
    }

    [Fact]
    public void PrepareAndCoordinatorEnforceHardResourceLimitsBeforeExecution()
    {
        using var first = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
            ["mods/b.lua"] = "return {value=1}",
        });
        using var second = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
            ["mods/b.lua"] = "return {value=1}",
        });
        Load(first, "require('a'); require('b')");
        Load(second, "require('a'); require('b')");
        var bundle = CreateBundle(
            Entry("a", "return {value=2}"),
            Entry("b", "return {value=2}"));
        var oneModule = LuaPatchResourceLimits.Default with { MaximumPatchModules = 1 };

        var moduleLimit = Assert.Throws<LuaPatchResourceLimitException>(() =>
            first.PreparePatch(bundle, new LuaPatchPrepareOptions
            {
                ResourceLimits = oneModule,
            }));
        Assert.Equal(nameof(LuaPatchResourceLimits.MaximumPatchModules), moduleLimit.LimitName);

        var firstPrepared = first.PreparePatch(bundle);
        var secondPrepared = second.PreparePatch(bundle);
        Assert.True(firstPrepared.Succeeded, firstPrepared.Message);
        Assert.True(secondPrepared.Succeeded, secondPrepared.Message);
        var ring = new LuaPatchRolloutRing
        {
            Name = "production",
            Targets =
            [
                new("state-a", first, firstPrepared.PreparedPatch!),
                new("state-b", second, secondPrepared.PreparedPatch!),
            ],
        };
        var oneTarget = LuaPatchResourceLimits.Default with
        {
            MaximumTargetsPerRing = 1,
        };

        var targetLimit = Assert.Throws<LuaPatchResourceLimitException>(() =>
            new LuaPatchCoordinator().CommitRing(
                "limited",
                ring,
                new LuaPatchCoordinatorOptions { ResourceLimits = oneTarget }));
        Assert.Equal(nameof(LuaPatchResourceLimits.MaximumTargetsPerRing), targetLimit.LimitName);
        Assert.Equal(1, Value(first, "a"));
        Assert.Equal(1, Value(second, "a"));
    }

    [Fact]
    public void MigrationSchemaSerializationEnforcesByteAndRuleLimits()
    {
        var schema = new LuaPatchMigrationSchema
        {
            SchemaId = "game-state",
            BaseVersion = "1",
            TargetVersion = "2",
            Modules =
            [
                new LuaPatchModuleMigrationSchema
                {
                    ModuleName = "a",
                    State =
                    [
                        new LuaPatchStateRule
                        {
                            TargetPath = "/a",
                            Kind = LuaPatchStateRuleKind.Preserve,
                        },
                        new LuaPatchStateRule
                        {
                            TargetPath = "/b",
                            Kind = LuaPatchStateRuleKind.Preserve,
                        },
                    ],
                },
            ],
        };

        var ruleLimit = Assert.Throws<LuaPatchResourceLimitException>(() =>
            LuaPatchMigrationSchemaSerializer.Serialize(
                schema,
                LuaPatchResourceLimits.Default with { MaximumStateMigrationRules = 1 }));
        Assert.Equal(
            nameof(LuaPatchResourceLimits.MaximumStateMigrationRules),
            ruleLimit.LimitName);

        var byteLimit = Assert.Throws<LuaPatchResourceLimitException>(() =>
            LuaPatchMigrationSchemaSerializer.Serialize(
                schema,
                LuaPatchResourceLimits.Default with { MaximumMigrationSchemaBytes = 32 }));
        Assert.Equal(
            nameof(LuaPatchResourceLimits.MaximumMigrationSchemaBytes),
            byteLimit.LimitName);
    }

    [Fact]
    public void HotUpdatePublishesStableActivitiesAndLowCardinalityMetrics()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = static source =>
                source.Name == LuaPatchTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(activityListener);

        var measurements = new ConcurrentQueue<(string Name, string? Status)>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == LuaPatchTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, Status(tags))));
        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, Status(tags))));
        meterListener.Start();

        using var direct = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        Load(direct, "require('a')");
        var directPrepared = direct.PreparePatch(CreateBundle(Entry("a", "return {value=2}")));
        Assert.True(directPrepared.Succeeded, directPrepared.Message);
        var opened = direct.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);
        using (var window = opened.Window!)
        {
            Assert.True(direct.CommitPatch(directPrepared.PreparedPatch!, window).Succeeded);
        }

        using var coordinated = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        Load(coordinated, "require('a')");
        var coordinatedPrepared = coordinated.PreparePatch(
            CreateBundle(Entry("a", "return {value=2}")));
        Assert.True(coordinatedPrepared.Succeeded, coordinatedPrepared.Message);
        var rollout = new LuaPatchCoordinator().Deploy(new LuaPatchRolloutPlan
        {
            RolloutId = "telemetry-rollout",
            Rings =
            [
                new LuaPatchRolloutRing
                {
                    Name = "canary",
                    Targets =
                    [
                        new(
                            "state-a",
                            coordinated,
                            coordinatedPrepared.PreparedPatch!),
                    ],
                },
            ],
        });
        Assert.True(rollout.Succeeded);

        Assert.Contains(stopped, static activity => activity.OperationName == "lunil.patch.prepare" &&
            Equals(activity.GetTagItem("lunil.patch.status"), "Ready") &&
            Equals(activity.GetTagItem("lunil.patch.id"), "operational-patch"));
        Assert.Contains(stopped, static activity => activity.OperationName == "lunil.patch.commit" &&
            activity.Status == ActivityStatusCode.Ok);
        Assert.Contains(stopped, static activity => activity.OperationName == "lunil.patch.ring" &&
            Equals(activity.GetTagItem("lunil.ring.name"), "canary"));
        Assert.Contains(stopped, static activity => activity.OperationName == "lunil.patch.rollout" &&
            Equals(activity.GetTagItem("lunil.rollout.id"), "telemetry-rollout"));
        Assert.Contains(measurements, static measurement =>
            measurement is ("lunil.patch.preparations", "Ready"));
        Assert.Contains(measurements, static measurement =>
            measurement is ("lunil.patch.commits", "Committed"));
        Assert.Contains(measurements, static measurement =>
            measurement is ("lunil.patch.rings", "Committed"));
    }

    private static string? Status(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == "lunil.patch.status")
            {
                return tag.Value?.ToString();
            }
        }

        return null;
    }

    private static LuaPatchEntry Entry(string moduleName, string source) => new(
        $"modules/{moduleName}.lua",
        moduleName,
        LuaPatchEntryKind.Source,
        Encoding.UTF8.GetBytes(source));

    private static LuaPatchBundle CreateBundle(params LuaPatchEntry[] entries)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return LuaPatchBundle.Create(
            new LuaPatchManifest
            {
                PatchId = "operational-patch",
                Channel = "test",
                TargetBuild = "build-2",
                BaseRevision = "build-1",
                TargetRevision = "build-2",
                LanguageVersion = LuaLanguageVersion.Lua54,
                RuntimeAbi = "lunil-0.12",
                CreatedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero),
                ExpiresAt = new DateTimeOffset(2099, 8, 22, 0, 0, 0, TimeSpan.Zero),
                Nonce = "operational-test",
            },
            entries.ToImmutableArray(),
            new LuaPatchEcdsaSigner("test", key));
    }

    private static LuaHost CreateHost(IReadOnlyDictionary<string, string> files) => new(
        LuaHostOptions.Default with
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            StandardLibrary = LuaHostCapabilityProfiles.Create(LuaHostProfile.Restricted) with
            {
                FileSystem = new DictionaryFileSystem(files),
            },
        });

    private static void Load(LuaHost host, string source)
    {
        var result = host.RunUtf8("package.path='mods/?.lua'; " + source);
        Assert.True(result.Succeeded);
    }

    private static long Value(LuaHost host, string moduleName) => host.RunUtf8(
        $"return require('{moduleName}').value").Execution!.Values[0].AsInteger();

    private sealed class DictionaryFileSystem(IReadOnlyDictionary<string, string> files)
        : ILuaFileSystem
    {
        public byte[] ReadAllBytes(string path) => files.TryGetValue(path, out var source)
            ? Encoding.UTF8.GetBytes(source)
            : throw new FileNotFoundException(path);

        public bool FileExists(string path) => files.ContainsKey(path);
    }

    private sealed class AtomicReplayStore : ILuaPatchReplayStore
    {
        private readonly ConcurrentDictionary<string, LuaPatchReplayReservation> _accepted =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _committed = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _leased = new(StringComparer.Ordinal);

        public int AcceptedCount => _accepted.Count;

        public LuaPatchReplayReservationResult TryReserve(
            string scope,
            string patchId,
            string nonce,
            DateTimeOffset reservedAt)
        {
            var key = scope + "\0" + patchId + "\0" + nonce;
            if (_committed.ContainsKey(key))
            {
                return new LuaPatchReplayReservationResult(
                    LuaPatchReplayReservationStatus.ReplayDetected, null, "replay");
            }

            var reservation = _accepted.GetOrAdd(key, _ => new LuaPatchReplayReservation(
                scope, patchId, nonce, Guid.NewGuid().ToString("N"), reservedAt));
            return new LuaPatchReplayReservationResult(
                LuaPatchReplayReservationStatus.Reserved, reservation, null);
        }

        public ILuaPatchReplayCommitLease? TryAcquireCommit(
            LuaPatchReplayReservation reservation,
            DateTimeOffset acquiredAt)
        {
            var key = reservation.Scope + "\0" + reservation.PatchId + "\0" + reservation.Nonce;
            return _committed.ContainsKey(key) || !_leased.TryAdd(key, 0)
                ? null
                : new Lease(this, reservation, key);
        }

        private sealed class Lease(
            AtomicReplayStore owner,
            LuaPatchReplayReservation reservation,
            string key) : ILuaPatchReplayCommitLease
        {
            public LuaPatchReplayReservation Reservation => reservation;

            public bool IsCompleted { get; private set; }

            public void Complete(DateTimeOffset committedAt)
            {
                owner._committed[key] = 0;
                IsCompleted = true;
            }

            public void Reopen(DateTimeOffset reopenedAt)
            {
                owner._committed.TryRemove(key, out _);
                IsCompleted = false;
            }

            public void Dispose() => owner._leased.TryRemove(key, out _);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private static LuaPatchPrepareOptions AcceptanceOptions(
        ILuaPatchReplayStore store,
        string scope) => new()
        {
            AcceptancePolicy = new LuaPatchAcceptancePolicy
            {
                TargetBuild = "build-2",
                CurrentRevision = "build-1",
                RuntimeAbi = "lunil-0.12",
                AllowedChannels = ["test"],
            },
            ReplayStore = store,
            ReplayScope = scope,
            TimeProvider = new FixedTimeProvider(new DateTimeOffset(
                2026, 7, 23, 0, 0, 0, TimeSpan.Zero)),
        };
}
