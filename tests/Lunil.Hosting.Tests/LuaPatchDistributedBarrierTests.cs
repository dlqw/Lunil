using System.Collections.Immutable;

namespace Lunil.Hosting.Tests;

public sealed class LuaPatchDistributedBarrierTests
{
    [Fact]
    public void FileStorePinsPreparedAndHealthyQuorumsAcrossInstances()
    {
        WithStore(directory =>
        {
            var first = new LuaPatchFileDistributedBarrierStore(directory);
            var second = new LuaPatchFileDistributedBarrierStore(directory);

            var waiting = first.Advance(Request("node-b", LuaPatchDistributedBarrierSignal.Prepared));
            Assert.Equal(LuaPatchDistributedBarrierDecision.Waiting, waiting.Decision);

            var apply = second.Advance(Request("node-a", LuaPatchDistributedBarrierSignal.Prepared));
            Assert.Equal(LuaPatchDistributedBarrierDecision.Apply, apply.Decision);
            Assert.Equal(["node-a", "node-b"], apply.SelectedParticipants.ToArray());

            var healthWaiting = first.Advance(Request("node-a", LuaPatchDistributedBarrierSignal.Healthy));
            Assert.Equal(LuaPatchDistributedBarrierDecision.Apply, healthWaiting.Decision);

            var committed = second.Advance(Request("node-b", LuaPatchDistributedBarrierSignal.Healthy));
            Assert.Equal(LuaPatchDistributedBarrierDecision.Commit, committed.Decision);
            Assert.Equal(["node-a", "node-b"], committed.HealthyParticipants.ToArray());

            var recovered = first.Advance(Request("node-a", LuaPatchDistributedBarrierSignal.Observe));
            Assert.Equal(committed.Decision, recovered.Decision);
            Assert.Equal(committed.HealthyParticipants.ToArray(), recovered.HealthyParticipants.ToArray());
        });
    }

    [Fact]
    public void FileStoreSelectsOnlyThePinnedPreparedQuorum()
    {
        WithStore(directory =>
        {
            var store = new LuaPatchFileDistributedBarrierStore(directory);
            var participants = ImmutableArray.Create("node-a", "node-b", "node-c");
            _ = store.Advance(Request(
                "node-c",
                LuaPatchDistributedBarrierSignal.Prepared,
                participants,
                required: 2));
            var apply = store.Advance(Request(
                "node-a",
                LuaPatchDistributedBarrierSignal.Prepared,
                participants,
                required: 2));

            Assert.Equal(["node-a", "node-c"], apply.SelectedParticipants.ToArray());
            var unselected = store.Advance(Request(
                "node-b",
                LuaPatchDistributedBarrierSignal.Prepared,
                participants,
                required: 2));
            Assert.False(unselected.IsSelected("node-b"));
            var conflict = Assert.Throws<LuaPatchDistributedBarrierException>(() => store.Advance(
                Request(
                    "node-b",
                    LuaPatchDistributedBarrierSignal.Healthy,
                    participants,
                    required: 2)));
            Assert.Equal(LuaPatchDistributedBarrierErrorCode.Conflict, conflict.Code);

            _ = store.Advance(Request(
                "node-a",
                LuaPatchDistributedBarrierSignal.Healthy,
                participants,
                required: 2));
            var committed = store.Advance(Request(
                "node-c",
                LuaPatchDistributedBarrierSignal.Healthy,
                participants,
                required: 2));
            Assert.Equal(LuaPatchDistributedBarrierDecision.Commit, committed.Decision);
        });
    }

    [Fact]
    public void FileStoreRollsBackWhenPreparationQuorumBecomesImpossible()
    {
        WithStore(directory =>
        {
            var store = new LuaPatchFileDistributedBarrierStore(directory);
            var rolledBack = store.Advance(Request(
                "node-a",
                LuaPatchDistributedBarrierSignal.PreparationFailed,
                message: "compile failed"));

            Assert.Equal(LuaPatchDistributedBarrierDecision.Rollback, rolledBack.Decision);
            Assert.Equal(["node-a"], rolledBack.FailedParticipants.ToArray());
            Assert.Equal("compile failed", rolledBack.Message);
            var terminal = store.Advance(Request("node-b", LuaPatchDistributedBarrierSignal.Prepared));
            Assert.Equal(rolledBack.Decision, terminal.Decision);
            Assert.Equal(rolledBack.Message, terminal.Message);
        });
    }

    [Fact]
    public void FileStoreExpiresPreparationAndHealthBarriers()
    {
        WithStore(directory =>
        {
            var preparationTime = new ManualTimeProvider();
            var preparationStore = Store(directory, preparationTime);
            _ = preparationStore.Advance(Request(
                "node-a",
                LuaPatchDistributedBarrierSignal.Prepared,
                preparationTimeout: TimeSpan.FromSeconds(2)));
            preparationTime.Advance(TimeSpan.FromSeconds(2));
            var preparationExpired = preparationStore.Advance(Request(
                "node-a",
                LuaPatchDistributedBarrierSignal.Observe,
                preparationTimeout: TimeSpan.FromSeconds(2)));
            Assert.Equal(LuaPatchDistributedBarrierDecision.Rollback, preparationExpired.Decision);
            Assert.Contains("preparation", preparationExpired.Message, StringComparison.OrdinalIgnoreCase);

            var healthDirectory = Path.Combine(directory, "health");
            var healthTime = new ManualTimeProvider();
            var healthStore = Store(healthDirectory, healthTime);
            _ = healthStore.Advance(Request(
                "node-a",
                LuaPatchDistributedBarrierSignal.Prepared,
                healthTimeout: TimeSpan.FromSeconds(3)));
            var apply = healthStore.Advance(Request(
                "node-b",
                LuaPatchDistributedBarrierSignal.Prepared,
                healthTimeout: TimeSpan.FromSeconds(3)));
            Assert.Equal(LuaPatchDistributedBarrierDecision.Apply, apply.Decision);
            _ = healthStore.Advance(Request(
                "node-a",
                LuaPatchDistributedBarrierSignal.Healthy,
                healthTimeout: TimeSpan.FromSeconds(3)));
            healthTime.Advance(TimeSpan.FromSeconds(3));
            var healthExpired = healthStore.Advance(Request(
                "node-b",
                LuaPatchDistributedBarrierSignal.Healthy,
                healthTimeout: TimeSpan.FromSeconds(3)));
            Assert.Equal(LuaPatchDistributedBarrierDecision.Rollback, healthExpired.Decision);
            Assert.Contains("health", healthExpired.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void FileStoreRejectsPinnedIdentityAndPolicyConflicts()
    {
        WithStore(directory =>
        {
            var store = new LuaPatchFileDistributedBarrierStore(directory);
            _ = store.Advance(Request("node-a", LuaPatchDistributedBarrierSignal.Prepared));

            var patch = Assert.Throws<LuaPatchDistributedBarrierException>(() => store.Advance(
                Request("node-b", LuaPatchDistributedBarrierSignal.Observe) with { PatchId = "patch-2" }));
            Assert.Equal(LuaPatchDistributedBarrierErrorCode.Conflict, patch.Code);

            var manifest = Assert.Throws<LuaPatchDistributedBarrierException>(() => store.Advance(
                Request("node-b", LuaPatchDistributedBarrierSignal.Observe) with
                {
                    PatchManifestIdentity = new string('B', 64),
                }));
            Assert.Equal(LuaPatchDistributedBarrierErrorCode.Conflict, manifest.Code);

            var membership = Assert.Throws<LuaPatchDistributedBarrierException>(() => store.Advance(
                Request(
                    "node-a",
                    LuaPatchDistributedBarrierSignal.Observe,
                    ["node-a"],
                    required: 1)));
            Assert.Equal(LuaPatchDistributedBarrierErrorCode.Conflict, membership.Code);

            var timeout = Assert.Throws<LuaPatchDistributedBarrierException>(() => store.Advance(
                Request("node-a", LuaPatchDistributedBarrierSignal.Observe) with
                {
                    HealthTimeout = TimeSpan.FromSeconds(31),
                }));
            Assert.Equal(LuaPatchDistributedBarrierErrorCode.Conflict, timeout.Code);
        });
    }

    [Fact]
    public void FileStoreValidatesAndCanonicalizesManifestIdentity()
    {
        WithStore(directory =>
        {
            var store = new LuaPatchFileDistributedBarrierStore(directory);
            var normalized = store.Advance(Request(
                "node-a",
                LuaPatchDistributedBarrierSignal.Observe) with
            {
                PatchManifestIdentity = new string('a', 64),
            });
            Assert.Equal(new string('A', 64), normalized.PatchManifestIdentity);

            foreach (var invalid in new string?[] { null, "AA", new string('G', 64) })
            {
                var exception = Assert.Throws<LuaPatchDistributedBarrierException>(() =>
                    store.Advance(Request(
                        "node-a",
                        LuaPatchDistributedBarrierSignal.Observe) with
                    {
                        RolloutId = Guid.NewGuid().ToString("N"),
                        PatchManifestIdentity = invalid!,
                    }));
                Assert.Equal(LuaPatchDistributedBarrierErrorCode.InvalidRequest, exception.Code);
            }
        });
    }

    [Fact]
    public void FileStoreRejectsTheFinalPreparationAtTheExactDeadline()
    {
        WithStore(directory =>
        {
            var time = new ManualTimeProvider();
            var store = Store(directory, time);
            _ = store.Advance(Request(
                "node-a",
                LuaPatchDistributedBarrierSignal.Prepared,
                preparationTimeout: TimeSpan.FromSeconds(2)));
            time.Advance(TimeSpan.FromSeconds(2));

            var expired = store.Advance(Request(
                "node-b",
                LuaPatchDistributedBarrierSignal.Prepared,
                preparationTimeout: TimeSpan.FromSeconds(2)));

            Assert.Equal(LuaPatchDistributedBarrierDecision.Rollback, expired.Decision);
            Assert.Equal(["node-a", "node-b"], expired.PreparedParticipants.ToArray());
        });
    }

    [Fact]
    public void FileStoreClampsClockRegressionToTheLastDurableUpdate()
    {
        WithStore(directory =>
        {
            var time = new ManualTimeProvider();
            var store = Store(directory, time);
            _ = store.Advance(Request("node-a", LuaPatchDistributedBarrierSignal.Prepared));
            time.Advance(TimeSpan.FromSeconds(1));
            var latest = store.Advance(Request(
                "node-a",
                LuaPatchDistributedBarrierSignal.Observe));
            time.Advance(TimeSpan.FromSeconds(-10));

            var regressed = store.Advance(Request(
                "node-a",
                LuaPatchDistributedBarrierSignal.Observe));

            Assert.Equal(latest.UpdatedAt, regressed.UpdatedAt);
            Assert.Equal(latest.Decision, regressed.Decision);
        });
    }

    [Fact]
    public void FileStoreDetectsTamperingAndEnforcesLimits()
    {
        WithStore(directory =>
        {
            var store = new LuaPatchFileDistributedBarrierStore(directory);
            _ = store.Advance(Request("node-a", LuaPatchDistributedBarrierSignal.Prepared));
            var statePath = Assert.Single(Directory.GetFiles(directory, "*.json"));
            var bytes = File.ReadAllBytes(statePath);
            bytes[bytes.Length / 2] ^= 1;
            File.WriteAllBytes(statePath, bytes);

            var corrupted = Assert.Throws<LuaPatchDistributedBarrierException>(() => store.Advance(
                Request("node-a", LuaPatchDistributedBarrierSignal.Observe)));
            Assert.Contains(
                corrupted.Code,
                new[]
                {
                    LuaPatchDistributedBarrierErrorCode.Corrupted,
                    LuaPatchDistributedBarrierErrorCode.HashMismatch,
                    LuaPatchDistributedBarrierErrorCode.IoFailure,
                });
        });

        WithStore(directory =>
        {
            var limited = new LuaPatchFileDistributedBarrierStore(
                directory,
                new LuaPatchFileDistributedBarrierStoreOptions
                {
                    MaximumParticipantCount = 1,
                });
            var exceeded = Assert.Throws<LuaPatchDistributedBarrierException>(() => limited.Advance(
                Request("node-a", LuaPatchDistributedBarrierSignal.Observe)));
            Assert.Equal(LuaPatchDistributedBarrierErrorCode.ResourceLimitExceeded, exceeded.Code);
        });

        WithStore(directory =>
        {
            var limited = new LuaPatchFileDistributedBarrierStore(
                directory,
                new LuaPatchFileDistributedBarrierStoreOptions
                {
                    MaximumIdentityBytes = 4,
                    MaximumMessageBytes = 4,
                });
            var identity = Assert.Throws<LuaPatchDistributedBarrierException>(() => limited.Advance(
                Request("node-a", LuaPatchDistributedBarrierSignal.Observe) with
                {
                    RolloutId = "rollout-too-long",
                }));
            Assert.Equal(LuaPatchDistributedBarrierErrorCode.ResourceLimitExceeded, identity.Code);

            var message = Assert.Throws<LuaPatchDistributedBarrierException>(() => limited.Advance(
                Request("node-a", LuaPatchDistributedBarrierSignal.Observe) with
                {
                    RolloutId = "r-1",
                    RingName = "ring",
                    PatchId = "p-1",
                    TargetRevision = "rev",
                    ParticipantId = "n-a",
                    Participants = ["n-a"],
                    Message = "large",
                }));
            Assert.Equal(LuaPatchDistributedBarrierErrorCode.ResourceLimitExceeded, message.Code);
        });

        WithStore(directory =>
        {
            var limited = new LuaPatchFileDistributedBarrierStore(
                directory,
                new LuaPatchFileDistributedBarrierStoreOptions
                {
                    MaximumStateBytes = 256,
                    MaximumMessageBytes = 64,
                });
            var state = Assert.Throws<LuaPatchDistributedBarrierException>(() => limited.Advance(
                Request("node-a", LuaPatchDistributedBarrierSignal.Observe)));
            Assert.Equal(LuaPatchDistributedBarrierErrorCode.ResourceLimitExceeded, state.Code);
        });
    }

    [Fact]
    public void FileStoreKeepsTerminalDecisionsImmutableForDuplicateSignals()
    {
        WithStore(directory =>
        {
            var store = new LuaPatchFileDistributedBarrierStore(directory);
            _ = store.Advance(Request("node-a", LuaPatchDistributedBarrierSignal.Prepared));
            _ = store.Advance(Request("node-b", LuaPatchDistributedBarrierSignal.Prepared));
            _ = store.Advance(Request("node-a", LuaPatchDistributedBarrierSignal.Healthy));
            var committed = store.Advance(Request(
                "node-b",
                LuaPatchDistributedBarrierSignal.Healthy));

            var duplicate = store.Advance(Request(
                "node-a",
                LuaPatchDistributedBarrierSignal.Unhealthy,
                message: "late failure"));

            Assert.Equal(committed.Decision, duplicate.Decision);
            Assert.Equal(committed.UpdatedAt, duplicate.UpdatedAt);
            Assert.Equal(
                committed.HealthyParticipants.ToArray(),
                duplicate.HealthyParticipants.ToArray());
            Assert.Equal(LuaPatchDistributedBarrierDecision.Commit, duplicate.Decision);
            Assert.Empty(duplicate.FailedParticipants);
        });
    }

    [Fact]
    public void FileStoreSerializesConcurrentParticipantUpdates()
    {
        WithStore(directory =>
        {
            var participants = Enumerable.Range(0, 32)
                .Select(static index => $"node-{index:D2}")
                .ToImmutableArray();
            Parallel.ForEach(participants, participant =>
            {
                var store = new LuaPatchFileDistributedBarrierStore(directory);
                _ = store.Advance(Request(
                    participant,
                    LuaPatchDistributedBarrierSignal.Prepared,
                    participants,
                    participants.Length));
            });

            var observer = new LuaPatchFileDistributedBarrierStore(directory);
            var apply = observer.Advance(Request(
                participants[0],
                LuaPatchDistributedBarrierSignal.Observe,
                participants,
                participants.Length));
            Assert.Equal(LuaPatchDistributedBarrierDecision.Apply, apply.Decision);
            Assert.Equal(participants.ToArray(), apply.SelectedParticipants.ToArray());

            Parallel.ForEach(participants, participant =>
            {
                var store = new LuaPatchFileDistributedBarrierStore(directory);
                _ = store.Advance(Request(
                    participant,
                    LuaPatchDistributedBarrierSignal.Healthy,
                    participants,
                    participants.Length));
            });
            var committed = observer.Advance(Request(
                participants[0],
                LuaPatchDistributedBarrierSignal.Observe,
                participants,
                participants.Length));
            Assert.Equal(LuaPatchDistributedBarrierDecision.Commit, committed.Decision);
        });
    }

    [Fact]
    public void FileStoreBoundsAndPrunesTerminalBarrierState()
    {
        WithStore(directory =>
        {
            var time = new ManualTimeProvider();
            var store = new LuaPatchFileDistributedBarrierStore(
                directory,
                new LuaPatchFileDistributedBarrierStoreOptions
                {
                    MaximumBarrierCount = 1,
                    TimeProvider = time,
                });
            _ = store.Advance(Request("node-a", LuaPatchDistributedBarrierSignal.Prepared));
            var incomplete = store.PruneCompleted(TimeSpan.Zero);
            Assert.Equal(1, incomplete.ScannedBarrierCount);
            Assert.Equal(0, incomplete.RemovedBarrierCount);

            var limited = Assert.Throws<LuaPatchDistributedBarrierException>(() => store.Advance(
                Request("node-a", LuaPatchDistributedBarrierSignal.Observe) with
                {
                    RolloutId = "rollout-2",
                }));
            Assert.Equal(LuaPatchDistributedBarrierErrorCode.ResourceLimitExceeded, limited.Code);

            _ = store.Advance(Request("node-b", LuaPatchDistributedBarrierSignal.Prepared));
            _ = store.Advance(Request("node-a", LuaPatchDistributedBarrierSignal.Healthy));
            _ = store.Advance(Request("node-b", LuaPatchDistributedBarrierSignal.Healthy));
            time.Advance(TimeSpan.FromMinutes(1));
            File.WriteAllText(Path.Combine(directory, "orphan.tmp"), "partial");
            File.WriteAllText(Path.Combine(directory, "orphan.json.lock"), "");
            var pruned = store.PruneCompleted(TimeSpan.FromSeconds(30));
            Assert.Equal(1, pruned.ScannedBarrierCount);
            Assert.Equal(1, pruned.RemovedBarrierCount);
            Assert.Equal(1, pruned.RemovedTemporaryFileCount);
            Assert.Equal(1, pruned.RemovedOrphanLockCount);
            Assert.Empty(Directory.GetFiles(directory, "*.json"));
            Assert.Empty(Directory.GetFiles(directory, "*.json.lock"));

            var recreated = store.Advance(Request(
                "node-a",
                LuaPatchDistributedBarrierSignal.Observe) with
            {
                RolloutId = "rollout-2",
            });
            Assert.Equal(LuaPatchDistributedBarrierDecision.Waiting, recreated.Decision);
        });
    }

    [Fact]
    public void FileStorePruneValidatesAgeAndCancellation()
    {
        WithStore(directory =>
        {
            var store = new LuaPatchFileDistributedBarrierStore(directory);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                store.PruneCompleted(TimeSpan.FromTicks(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                store.PruneCompleted(TimeSpan.FromDays(3651)));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                store.PruneCompleted(TimeSpan.Zero, cancellation.Token));
        });
    }

    private static LuaPatchDistributedBarrierRequest Request(
        string participantId,
        LuaPatchDistributedBarrierSignal signal,
        ImmutableArray<string> participants = default,
        int required = 0,
        TimeSpan? preparationTimeout = null,
        TimeSpan? healthTimeout = null,
        string? message = null) => new()
        {
            RolloutId = "rollout-1",
            RingName = "canary",
            PatchId = "patch-1",
            TargetRevision = "2",
            PatchManifestIdentity = new string('A', 64),
            ParticipantId = participantId,
            Participants = participants.IsDefault
                ? ["node-a", "node-b"]
                : participants,
            RequiredParticipantCount = required,
            PreparationTimeout = preparationTimeout ?? TimeSpan.FromSeconds(30),
            HealthTimeout = healthTimeout ?? TimeSpan.FromSeconds(30),
            Signal = signal,
            Message = message,
        };

    private static LuaPatchFileDistributedBarrierStore Store(
        string directory,
        TimeProvider timeProvider) => new(
            directory,
            new LuaPatchFileDistributedBarrierStoreOptions { TimeProvider = timeProvider });

    private static void WithStore(Action<string> action)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "lunil-distributed-barrier-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            action(directory);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now += value;
    }
}
