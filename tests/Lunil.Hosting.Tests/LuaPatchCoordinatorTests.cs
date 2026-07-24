using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Lunil.Core;
using Lunil.Runtime.Values;
using Lunil.StandardLibrary;

namespace Lunil.Hosting.Tests;

public sealed class LuaPatchCoordinatorTests
{
    [Fact]
    public void BarrierCommitPublishesAllStatesAndWritesDurablePhases()
    {
        using var first = CreateHost("return {value=1}");
        using var second = CreateHost("return {value=1}");
        Load(first);
        Load(second);
        var bundle = CreateBundle("return {value=2}");
        var journal = new MemoryJournal();
        var ring = Ring(
            Target("state-b", second, bundle),
            Target("state-a", first, bundle));

        var result = new LuaPatchCoordinator().CommitRing(
            "rollout-1",
            ring,
            new LuaPatchCoordinatorOptions { Journal = journal });

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal<string>(["state-a", "state-b"], result.Targets.Select(
            static target => target.TargetId));
        Assert.All(result.Targets, static target => Assert.True(target.Commit.Succeeded));
        Assert.Equal(2, Value(first));
        Assert.Equal(2, Value(second));
        Assert.Equal<LuaPatchJournalPhase>(
            [
                LuaPatchJournalPhase.Started,
                LuaPatchJournalPhase.Prepared,
                LuaPatchJournalPhase.Publishing,
                LuaPatchJournalPhase.Committed,
            ],
            journal.Entries.Select(static entry => entry.Phase));
    }

    [Fact]
    public void TargetLifecycleIsolatesQuiescesAndRestoresAroundTheCommitBarrier()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var events = new List<string>();
        var lifecycle = new TestTargetLifecycle("state-a", events);
        var target = Target("state-a", host, CreateBundle("return {value=2}")) with
        {
            Lifecycle = lifecycle,
        };
        var journal = new MemoryJournal();

        var result = new LuaPatchCoordinator().CommitRing(
            "isolated-rollout",
            Ring(target),
            new LuaPatchCoordinatorOptions
            {
                RequireTargetIsolation = true,
                Journal = journal,
                HealthCheck = _ =>
                {
                    events.Add($"health:{Value(host)}");
                    return LuaPatchRingHealthDecision.Accept;
                },
            });

        Assert.True(result.Succeeded, result.Message);
        var targetResult = Assert.Single(result.Targets);
        Assert.Equal(LuaPatchTargetLifecycleStatus.Restored, targetResult.Lifecycle.Status);
        Assert.Equal(
            ["state-a:isolate", "state-a:quiesce", "health:2", "state-a:restore:Committed"],
            events);
        Assert.Equal<LuaPatchJournalPhase>(
            [
                LuaPatchJournalPhase.Started,
                LuaPatchJournalPhase.Prepared,
                LuaPatchJournalPhase.Publishing,
                LuaPatchJournalPhase.Restoring,
                LuaPatchJournalPhase.Committed,
            ],
            journal.Entries.Select(static entry => entry.Phase));
        Assert.Equal("isolated-rollout", lifecycle.Context!.RolloutId);
        Assert.Equal("state-a", lifecycle.Context.TargetId);
        Assert.Equal(TimeSpan.FromSeconds(30), lifecycle.Context.Timeout);
    }

    [Fact]
    public void IsolationDeferralRestoresEarlierTargetsAndNeverEntersTheHostBarrier()
    {
        using var first = CreateHost("return {value=1}");
        using var second = CreateHost("return {value=1}");
        Load(first);
        Load(second);
        var events = new List<string>();
        var firstLifecycle = new TestTargetLifecycle("state-a", events);
        var secondLifecycle = new TestTargetLifecycle("state-b", events)
        {
            IsolationStatus = LuaPatchTargetIsolationStatus.Deferred,
        };
        var bundle = CreateBundle("return {value=2}");

        var result = new LuaPatchCoordinator().CommitRing(
            "deferred-isolation",
            Ring(
                Target("state-a", first, bundle) with { Lifecycle = firstLifecycle },
                Target("state-b", second, bundle) with { Lifecycle = secondLifecycle }));

        Assert.Equal(LuaPatchRingCommitStatus.Deferred, result.Status);
        Assert.Equal(1, Value(first));
        Assert.Equal(1, Value(second));
        Assert.Equal(
            [
                "state-a:isolate",
                "state-b:isolate",
                "state-a:restore:RolledBack",
            ],
            events);
        Assert.Equal(
            LuaPatchTargetLifecycleStatus.Restored,
            result.Targets[0].Lifecycle.Status);
        Assert.Equal(
            LuaPatchTargetLifecycleStatus.IsolationDeferred,
            result.Targets[1].Lifecycle.Status);
        Assert.All(result.Targets, static target => Assert.False(target.Commit.Succeeded));
    }

    [Fact]
    public void QuiescenceFailureRestoresTheIsolatedTargetWithoutExecutingCandidates()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var events = new List<string>();
        var lifecycle = new TestTargetLifecycle("state-a", events)
        {
            QuiescenceStatus = LuaPatchTargetQuiescenceStatus.Failed,
        };

        var result = new LuaPatchCoordinator().CommitRing(
            "failed-drain",
            Ring(Target("state-a", host, CreateBundle(
                "candidate_ran=true; return {value=2}")) with
            {
                Lifecycle = lifecycle,
            }));

        Assert.Equal(LuaPatchRingCommitStatus.QuiescenceFailed, result.Status);
        Assert.Equal(LuaPatchTargetLifecycleStatus.Restored, result.Targets[0].Lifecycle.Status);
        Assert.Equal(
            LuaPatchTargetLifecycleStatus.QuiescenceFailed,
            result.Targets[0].Lifecycle.Failure);
        Assert.Equal(
            ["state-a:isolate", "state-a:quiesce", "state-a:restore:RolledBack"],
            events);
        Assert.Equal(1, Value(host));
        Assert.True(host.State.GetGlobal("candidate_ran").IsNil);
    }

    [Fact]
    public void RestoreFailureLeavesCommittedCodeObservableAndJournalRecoverable()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var lifecycle = new TestTargetLifecycle("state-a", [])
        {
            RestoreStatus = LuaPatchTargetRestoreStatus.Failed,
        };
        var journal = new MemoryJournal();

        var result = new LuaPatchCoordinator().CommitRing(
            "restore-failure",
            Ring(Target("state-a", host, CreateBundle("return {value=2}")) with
            {
                Lifecycle = lifecycle,
            }),
            new LuaPatchCoordinatorOptions { Journal = journal });

        Assert.Equal(LuaPatchRingCommitStatus.RestoreFailed, result.Status);
        Assert.True(result.Targets[0].Commit.Succeeded);
        Assert.Equal(
            LuaPatchTargetLifecycleStatus.RestoreFailed,
            result.Targets[0].Lifecycle.Status);
        Assert.Equal(2, Value(host));
        Assert.Equal(LuaPatchJournalPhase.Restoring, journal.Entries[^1].Phase);
        Assert.DoesNotContain(journal.Entries, static entry =>
            entry.Phase == LuaPatchJournalPhase.Committed);
    }

    [Fact]
    public void RestoringJournalFailureRollsBackBeforeTrafficIsRestored()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var events = new List<string>();
        var lifecycle = new TestTargetLifecycle("state-a", events);

        var result = new LuaPatchCoordinator().CommitRing(
            "restoring-journal-failure",
            Ring(Target("state-a", host, CreateBundle("return {value=2}")) with
            {
                Lifecycle = lifecycle,
            }),
            new LuaPatchCoordinatorOptions
            {
                Journal = new MemoryJournal { ThrowOn = LuaPatchJournalPhase.Restoring },
            });

        Assert.Equal(LuaPatchRingCommitStatus.JournalFailed, result.Status);
        Assert.False(result.Targets[0].Commit.Succeeded);
        Assert.Equal(1, Value(host));
        Assert.Equal("state-a:restore:RolledBack", events[^1]);
    }

    [Fact]
    public void TerminalJournalFailureAfterTrafficRestoreDoesNotReportAFalseRollback()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var events = new List<string>();
        var lifecycle = new TestTargetLifecycle("state-a", events);
        var journal = new MemoryJournal { ThrowOn = LuaPatchJournalPhase.Committed };

        var result = new LuaPatchCoordinator().CommitRing(
            "terminal-journal-failure",
            Ring(Target("state-a", host, CreateBundle("return {value=2}")) with
            {
                Lifecycle = lifecycle,
            }),
            new LuaPatchCoordinatorOptions { Journal = journal });

        Assert.Equal(LuaPatchRingCommitStatus.JournalFailed, result.Status);
        Assert.True(result.Targets[0].Commit.Succeeded);
        Assert.Equal(2, Value(host));
        Assert.Equal("state-a:restore:Committed", events[^1]);
        Assert.Equal(LuaPatchJournalPhase.Restoring, journal.Entries[^1].Phase);
    }

    [Fact]
    public void RequiredTargetIsolationRejectsMissingAdaptersBeforeStartingAJournal()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var journal = new MemoryJournal();

        var error = Assert.Throws<ArgumentException>(() =>
            new LuaPatchCoordinator().CommitRing(
                "required-isolation",
                Ring(Target("state-a", host, CreateBundle("return {value=2}"))),
                new LuaPatchCoordinatorOptions
                {
                    RequireTargetIsolation = true,
                    Journal = journal,
                }));

        Assert.Contains("requires a lifecycle adapter", error.Message, StringComparison.Ordinal);
        Assert.Empty(journal.Entries);
        Assert.Equal(1, Value(host));
    }

    [Fact]
    public void MalformedIsolationResultIsRejectedAndItsSessionIsRestored()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var events = new List<string>();
        var lifecycle = new TestTargetLifecycle("state-a", events)
        {
            IsolationStatus = LuaPatchTargetIsolationStatus.Deferred,
            ReturnIsolationOnFailure = true,
        };

        var result = new LuaPatchCoordinator().CommitRing(
            "malformed-isolation",
            Ring(Target("state-a", host, CreateBundle("return {value=2}")) with
            {
                Lifecycle = lifecycle,
            }));

        Assert.Equal(LuaPatchRingCommitStatus.IsolationFailed, result.Status);
        Assert.Contains("invalid isolation result", result.Message, StringComparison.Ordinal);
        Assert.Equal(
            ["state-a:isolate", "state-a:restore:RolledBack"],
            events);
        Assert.Equal(1, Value(host));
    }

    [Fact]
    public void TargetLifecycleRejectsNegativeTimeoutsBeforeStartingAJournal()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var journal = new MemoryJournal();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LuaPatchCoordinator().CommitRing(
                "invalid-lifecycle-timeout",
                Ring(Target("state-a", host, CreateBundle("return {value=2}"))),
                new LuaPatchCoordinatorOptions
                {
                    Journal = journal,
                    TargetLifecycle = new LuaPatchTargetLifecycleOptions
                    {
                        QuiescenceTimeout = TimeSpan.FromMilliseconds(-2),
                    },
                }));

        Assert.Empty(journal.Entries);
        Assert.Equal(1, Value(host));
    }

    [Fact]
    public void BarrierCommitUsesIndependentTargetScopesAndSealsEveryReplayReservation()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lunil-coordinator-replay-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var first = CreateHost("return {value=1}");
            using var second = CreateHost("return {value=1}");
            Load(first);
            Load(second);
            var bundle = CreateBundle("return {value=2}");
            var store = new LuaPatchFileReplayStore(System.IO.Path.Combine(
                directory,
                "replay.ndjson"));
            var firstPreparation = first.PreparePatch(
                bundle,
                AcceptanceOptions(store, "state-a"));
            var secondPreparation = second.PreparePatch(
                bundle,
                AcceptanceOptions(store, "state-b"));
            Assert.True(firstPreparation.Succeeded, firstPreparation.Message);
            Assert.True(secondPreparation.Succeeded, secondPreparation.Message);
            Assert.Throws<ArgumentException>(() => new LuaPatchCoordinator().CommitRing(
                "rollout-wrong-scope",
                Ring(new LuaPatchDeploymentTarget(
                    "state-z",
                    first,
                    firstPreparation.PreparedPatch!))));
            var ring = Ring(
                new LuaPatchDeploymentTarget(
                    "state-a", first, firstPreparation.PreparedPatch!),
                new LuaPatchDeploymentTarget(
                    "state-b", second, secondPreparation.PreparedPatch!));

            var result = new LuaPatchCoordinator().CommitRing(
                "rollout-scoped-replay",
                ring,
                new LuaPatchCoordinatorOptions
                {
                    Journal = new MemoryJournal(),
                    TimeProvider = new FixedTimeProvider(new DateTimeOffset(
                        2026, 7, 23, 0, 0, 0, TimeSpan.Zero)),
                });

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(
                [
                    LuaPatchReplayRecordState.Reserved,
                    LuaPatchReplayRecordState.Reserved,
                    LuaPatchReplayRecordState.Committed,
                    LuaPatchReplayRecordState.Committed,
                ],
                store.ReadAll().Select(static record => record.State).ToArray());
            Assert.Equal(
                LuaPatchPrepareStatus.AcceptanceRejected,
                first.PreparePatch(bundle, AcceptanceOptions(store, "state-a")).Status);
            Assert.Equal(
                LuaPatchPrepareStatus.AcceptanceRejected,
                second.PreparePatch(bundle, AcceptanceOptions(store, "state-b")).Status);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void CandidateFailureOnOneStateRollsBackEveryPreparedParticipant()
    {
        using var first = CreateHost("return {value=1}");
        using var second = CreateHost("return {value=1}");
        Load(first);
        Load(second);
        first.State.SetGlobal("fail_patch", LuaValue.FromBoolean(false));
        second.State.SetGlobal("fail_patch", LuaValue.FromBoolean(true));
        var bundle = CreateBundle(
            "if fail_patch then error('state rejected candidate') end; return {value=2}");
        var ring = Ring(
            Target("state-a", first, bundle),
            Target("state-b", second, bundle));

        var result = new LuaPatchCoordinator().CommitRing("rollout-2", ring);

        Assert.Equal(LuaPatchRingCommitStatus.PrepareFailed, result.Status);
        Assert.Equal(2, result.Targets.Length);
        Assert.Equal(1, Value(first));
        Assert.Equal(1, Value(second));
        Assert.Contains(result.Targets, static target =>
            target.Commit.Status == LuaPatchCommitStatus.BarrierAborted);
        Assert.Contains(result.Targets, static target =>
            target.Commit.Status == LuaPatchCommitStatus.ExecutionFailed);
    }

    [Fact]
    public void ExpiredPatchStopsAtBarrierPreparationUsingCoordinatorClock()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var expiresAt = new DateTimeOffset(2099, 8, 22, 0, 0, 0, TimeSpan.Zero);
        var bundle = CreateBundle(
            "patch_candidate_ran=true; return {value=2}",
            expiresAt);

        var result = new LuaPatchCoordinator().CommitRing(
            "expired-rollout",
            Ring(Target("state-a", host, bundle)),
            new LuaPatchCoordinatorOptions
            {
                TimeProvider = new FixedTimeProvider(expiresAt),
            });

        Assert.Equal(LuaPatchRingCommitStatus.PrepareFailed, result.Status);
        var target = Assert.Single(result.Targets);
        Assert.Equal(LuaPatchCommitStatus.Expired, target.Commit.Status);
        Assert.False(target.Commit.SideEffectsMayHaveOccurred);
        Assert.Equal(1, Value(host));
        Assert.True(host.State.GetGlobal("patch_candidate_ran").IsNil);
    }

    [Fact]
    public void HealthRejectionRollsBackAlreadyPublishedCachesRecordsAndGenerations()
    {
        const string initial =
            "local n=0; local function next() n=n+1; return n end; return {next=next,value=1}";
        using var first = CreateHost(initial);
        using var second = CreateHost(initial);
        Load(first, "alias=require('value').next");
        Load(second, "alias=require('value').next");
        var firstAlias = first.State.GetGlobal("alias").TryGetClosure()!;
        var secondAlias = second.State.GetGlobal("alias").TryGetClosure()!;
        var firstVersion = firstAlias.FunctionVersion;
        var secondVersion = secondAlias.FunctionVersion;
        Assert.True(first.State.TryGetModule("value", out var firstRecord));
        Assert.True(second.State.TryGetModule("value", out var secondRecord));
        var bundle = CreateBundle(
            "local n=0; local function next() n=n+10; return n end; " +
            "return {next=next,value=2}");
        var observedNewValues = false;
        var ring = Ring(
            Target("state-a", first, bundle),
            Target("state-b", second, bundle));

        var result = new LuaPatchCoordinator().CommitRing(
            "rollout-health",
            ring,
            new LuaPatchCoordinatorOptions
            {
                HealthCheck = _ =>
                {
                    observedNewValues = Value(first) == 2 && Value(second) == 2;
                    return LuaPatchRingHealthDecision.Rollback;
                },
            });

        Assert.Equal(LuaPatchRingCommitStatus.HealthRejected, result.Status);
        Assert.True(observedNewValues);
        Assert.Equal(1, Value(first));
        Assert.Equal(1, Value(second));
        Assert.Same(firstVersion, firstAlias.FunctionVersion);
        Assert.Same(secondVersion, secondAlias.FunctionVersion);
        Assert.True(first.State.TryGetModule("value", out var firstAfter));
        Assert.True(second.State.TryGetModule("value", out var secondAfter));
        Assert.Equal(firstRecord!.Revision, firstAfter!.Revision);
        Assert.Equal(secondRecord!.Revision, secondAfter!.Revision);
    }

    [Fact]
    public void JournalCommitFailureRollsBackBarrierBeforeReturning()
    {
        using var first = CreateHost("return {value=1}");
        using var second = CreateHost("return {value=1}");
        Load(first);
        Load(second);
        var bundle = CreateBundle("return {value=2}");
        var journal = new MemoryJournal { ThrowOn = LuaPatchJournalPhase.Committed };

        var result = new LuaPatchCoordinator().CommitRing(
            "rollout-journal",
            Ring(
                Target("state-a", first, bundle),
                Target("state-b", second, bundle)),
            new LuaPatchCoordinatorOptions { Journal = journal });

        Assert.Equal(LuaPatchRingCommitStatus.JournalFailed, result.Status);
        Assert.Equal(1, Value(first));
        Assert.Equal(1, Value(second));
        Assert.All(result.Targets, static target =>
            Assert.Equal(LuaPatchCommitStatus.BarrierAborted, target.Commit.Status));
    }

    [Fact]
    public void JournalCommitFailureReopensCompletedReplayReservation()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lunil-coordinator-replay-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var host = CreateHost("return {value=1}");
            Load(host);
            var bundle = CreateBundle("return {value=2}");
            var store = new LuaPatchFileReplayStore(System.IO.Path.Combine(
                directory,
                "replay.ndjson"));
            var preparation = host.PreparePatch(
                bundle,
                AcceptanceOptions(store, "state-a"));
            Assert.True(preparation.Succeeded, preparation.Message);

            var result = new LuaPatchCoordinator().CommitRing(
                "rollout-replay-rollback",
                Ring(new LuaPatchDeploymentTarget(
                    "state-a",
                    host,
                    preparation.PreparedPatch!)),
                new LuaPatchCoordinatorOptions
                {
                    Journal = new MemoryJournal
                    {
                        ThrowOn = LuaPatchJournalPhase.Committed,
                    },
                    TimeProvider = new FixedTimeProvider(new DateTimeOffset(
                        2026, 7, 23, 0, 0, 0, TimeSpan.Zero)),
                });

            Assert.Equal(LuaPatchRingCommitStatus.JournalFailed, result.Status);
            Assert.Equal(
                [
                    LuaPatchReplayRecordState.Reserved,
                    LuaPatchReplayRecordState.Committed,
                    LuaPatchReplayRecordState.Reopened,
                ],
                store.ReadAll().Select(static record => record.State).ToArray());
            Assert.True(host.PreparePatch(
                bundle,
                AcceptanceOptions(store, "state-a")).Succeeded);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(LuaPatchJournalPhase.Started)]
    [InlineData(LuaPatchJournalPhase.Prepared)]
    [InlineData(LuaPatchJournalPhase.Publishing)]
    [InlineData(LuaPatchJournalPhase.Committed)]
    public void JournalFaultAtEveryDurabilityGateNeverLeaksRingState(
        LuaPatchJournalPhase faultPhase)
    {
        using var first = CreateHost("return {value=1}");
        using var second = CreateHost("return {value=1}");
        Load(first);
        Load(second);
        var bundle = CreateBundle("return {value=2}");

        var result = new LuaPatchCoordinator().CommitRing(
            "fault-injection",
            Ring(
                Target("state-a", first, bundle),
                Target("state-b", second, bundle)),
            new LuaPatchCoordinatorOptions
            {
                Journal = new MemoryJournal { ThrowOn = faultPhase },
            });

        Assert.Equal(LuaPatchRingCommitStatus.JournalFailed, result.Status);
        Assert.Equal(2, result.Targets.Length);
        Assert.Equal(1, Value(first));
        Assert.Equal(1, Value(second));
    }

    [Fact]
    public void RolloutStopsAfterFailedRingAndKeepsAcceptedCanary()
    {
        using var canary = CreateHost("return {value=1}");
        using var production = CreateHost("return {value=1}");
        Load(canary);
        Load(production);
        canary.State.SetGlobal("fail_patch", LuaValue.FromBoolean(false));
        production.State.SetGlobal("fail_patch", LuaValue.FromBoolean(true));
        var bundle = CreateBundle(
            "if fail_patch then error('production failure') end; return {value=2}");
        var plan = new LuaPatchRolloutPlan
        {
            RolloutId = "ring-rollout",
            Rings =
            [
                new LuaPatchRolloutRing
                {
                    Name = "canary",
                    Targets = [Target("canary-1", canary, bundle)],
                },
                new LuaPatchRolloutRing
                {
                    Name = "production",
                    Targets = [Target("production-1", production, bundle)],
                },
            ],
        };

        var result = new LuaPatchCoordinator().Deploy(plan);

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Rings.Length);
        Assert.Equal(LuaPatchRingCommitStatus.Committed, result.Rings[0].Status);
        Assert.Equal(LuaPatchRingCommitStatus.PrepareFailed, result.Rings[1].Status);
        Assert.Equal(2, Value(canary));
        Assert.Equal(1, Value(production));
    }

    [Fact]
    public void BarrierRejectsTargetsPreparedFromDifferentCanonicalManifests()
    {
        using var first = CreateHost("return {value=1}");
        using var second = CreateHost("return {value=1}");
        Load(first);
        Load(second);

        var error = Assert.Throws<ArgumentException>(() =>
            new LuaPatchCoordinator().CommitRing(
                "rollout-mismatch",
                Ring(
                    Target("state-a", first, CreateBundle("return {value=2}")),
                    Target("state-b", second, CreateBundle("return {value=3}")))));

        Assert.Contains("canonical patch manifest", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, Value(first));
        Assert.Equal(1, Value(second));
    }

    [Fact]
    public void RolloutRejectsAHostAssignedToMoreThanOneRing()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var target = Target("state-a", host, CreateBundle("return {value=2}"));
        var plan = new LuaPatchRolloutPlan
        {
            RolloutId = "duplicate-host",
            Rings =
            [
                new LuaPatchRolloutRing { Name = "canary", Targets = [target] },
                new LuaPatchRolloutRing
                {
                    Name = "production",
                    Targets = [target with { TargetId = "state-b" }],
                },
            ],
        };

        var error = Assert.Throws<ArgumentException>(() =>
            new LuaPatchCoordinator().Deploy(plan));

        Assert.Contains("unique across a rollout", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, Value(host));
    }

    [Fact]
    public void InvalidOrReentrantHealthDecisionRollsBackThePublishedRing()
    {
        using var first = CreateHost("return {value=1}");
        using var second = CreateHost("return {value=1}");
        Load(first);
        Load(second);
        var bundle = CreateBundle("return {value=2}");
        var innerRing = Ring(Target("inner", second, bundle));

        var reentrant = new LuaPatchCoordinator().CommitRing(
            "outer",
            Ring(Target("outer", first, bundle)),
            new LuaPatchCoordinatorOptions
            {
                HealthCheck = context =>
                {
                    Assert.Equal("outer", context.RolloutId);
                    new LuaPatchCoordinator().CommitRing("inner", innerRing);
                    return LuaPatchRingHealthDecision.Accept;
                },
            });

        Assert.Equal(LuaPatchRingCommitStatus.HealthRejected, reentrant.Status);
        Assert.Equal(1, Value(first));
        Assert.Equal(1, Value(second));

        var invalid = new LuaPatchCoordinator().CommitRing(
            "invalid-health",
            Ring(Target("outer", first, bundle)),
            new LuaPatchCoordinatorOptions
            {
                HealthCheck = _ => (LuaPatchRingHealthDecision)byte.MaxValue,
            });
        Assert.Equal(LuaPatchRingCommitStatus.HealthRejected, invalid.Status);
        Assert.Equal(1, Value(first));
    }

    [Fact]
    public async Task ConcurrentCoordinatorInstancesSerializeProcessWideWithoutDeadlock()
    {
        using var first = CreateHost("return {value=1}");
        using var second = CreateHost("return {value=1}");
        Load(first);
        Load(second);
        var bundle = CreateBundle("return {value=2}");
        var firstEnteredHealth = new ManualResetEventSlim();
        var releaseFirst = new ManualResetEventSlim();
        var firstRing = Ring(Target("state-a", first, bundle));
        var secondRing = Ring(Target("state-b", second, bundle));

        var firstDeployment = Task.Run(() => new LuaPatchCoordinator().CommitRing(
            "first",
            firstRing,
            new LuaPatchCoordinatorOptions
            {
                HealthCheck = _ =>
                {
                    firstEnteredHealth.Set();
                    Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(10)));
                    return LuaPatchRingHealthDecision.Accept;
                },
            }));
        Assert.True(firstEnteredHealth.Wait(TimeSpan.FromSeconds(10)));

        var secondDeployment = Task.Run(() =>
            new LuaPatchCoordinator().CommitRing("second", secondRing));
        await Task.Delay(100);
        Assert.False(secondDeployment.IsCompleted);
        releaseFirst.Set();

        var results = await Task.WhenAll(firstDeployment, secondDeployment)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, static result => Assert.True(result.Succeeded, result.Message));
        Assert.Equal(2, Value(first));
        Assert.Equal(2, Value(second));
    }

    [Fact]
    public void RepeatedBarrierRollbackPreservesRevisionForAHealthyRetry()
    {
        using var first = CreateHost("return {value=1}");
        using var second = CreateHost("return {value=1}");
        Load(first);
        Load(second);
        var bundle = CreateBundle("return {value=2}");
        var ring = Ring(
            Target("state-a", first, bundle),
            Target("state-b", second, bundle));
        var coordinator = new LuaPatchCoordinator();

        for (var attempt = 0; attempt < 64; attempt++)
        {
            var rejected = coordinator.CommitRing(
                $"rollback-{attempt}",
                ring,
                new LuaPatchCoordinatorOptions
                {
                    HealthCheck = _ => LuaPatchRingHealthDecision.Rollback,
                });
            Assert.Equal(LuaPatchRingCommitStatus.HealthRejected, rejected.Status);
            Assert.Equal(1, Value(first));
            Assert.Equal(1, Value(second));
        }

        var accepted = coordinator.CommitRing("healthy-retry", ring);
        Assert.True(accepted.Succeeded, accepted.Message);
        Assert.Equal(2, Value(first));
        Assert.Equal(2, Value(second));
    }

    [Fact]
    public void FileJournalVerifiesHashChainAndRecoversIncompleteTransaction()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lunil-journal-tests",
            Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "deploy.ndjson");
        try
        {
            using var journal = new LuaPatchFileJournal(path);
            journal.Append(JournalEntry(LuaPatchJournalPhase.Started));
            journal.Append(JournalEntry(LuaPatchJournalPhase.Prepared));
            journal.Append(JournalEntry(LuaPatchJournalPhase.Publishing));
            journal.Dispose();

            using var restored = new LuaPatchFileJournal(path);
            var entries = restored.ReadAll();
            Assert.Equal(3, entries.Length);
            Assert.Equal(1, entries[0].Sequence);
            Assert.Equal(entries[0].Hash, entries[1].PreviousHash);
            var incomplete = Assert.Single(restored.GetIncompleteTransactions());
            Assert.Equal(LuaPatchJournalPhase.Publishing, incomplete.LastPhase);

            var recovery = Assert.Single(restored.RecoverIncomplete(
                new RollbackRecoveryHandler(),
                new FixedTimeProvider(new DateTimeOffset(
                    2026, 7, 22, 1, 0, 0, TimeSpan.Zero))));
            Assert.Equal(LuaPatchRecoveryResolution.RolledBack, recovery.Resolution);
            Assert.Empty(restored.GetIncompleteTransactions());
            Assert.Equal(
                LuaPatchJournalPhase.RecoveredRolledBack,
                restored.ReadAll()[^1].Phase);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void FileJournalRecoversTrafficRestorationAfterPublicationCommitted()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lunil-journal-tests",
            Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "deploy.ndjson");
        try
        {
            using var journal = new LuaPatchFileJournal(path);
            journal.Append(JournalEntry(LuaPatchJournalPhase.Started));
            journal.Append(JournalEntry(LuaPatchJournalPhase.Prepared));
            journal.Append(JournalEntry(LuaPatchJournalPhase.Publishing));
            journal.Append(JournalEntry(LuaPatchJournalPhase.Restoring));

            var incomplete = Assert.Single(journal.GetIncompleteTransactions());
            Assert.Equal(LuaPatchJournalPhase.Restoring, incomplete.LastPhase);
            var recovery = Assert.Single(journal.RecoverIncomplete(
                new CommitRecoveryHandler(),
                new FixedTimeProvider(new DateTimeOffset(
                    2026, 7, 22, 2, 0, 0, TimeSpan.Zero))));

            Assert.Equal(LuaPatchRecoveryResolution.Committed, recovery.Resolution);
            Assert.Equal(
                LuaPatchJournalPhase.RecoveredCommitted,
                journal.ReadAll()[^1].Phase);
            Assert.Empty(journal.GetIncompleteTransactions());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void FileJournalRejectsTamperingAndTruncatedRecords()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lunil-journal-tests",
            Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "deploy.ndjson");
        try
        {
            using var journal = new LuaPatchFileJournal(path);
            journal.Append(JournalEntry(LuaPatchJournalPhase.Started));
            journal.Dispose();
            var text = File.ReadAllText(path, Encoding.UTF8);
            File.WriteAllText(
                path,
                text.Replace("patch-1", "patch-2", StringComparison.Ordinal),
                new UTF8Encoding(false));
            var tampered = Assert.Throws<LuaPatchJournalException>(() =>
                new LuaPatchFileJournal(path).ReadAll());
            Assert.Equal(LuaPatchJournalErrorCode.HashMismatch, tampered.Code);

            File.WriteAllText(path, text.TrimEnd('\n'), new UTF8Encoding(false));
            var truncated = Assert.Throws<LuaPatchJournalException>(() =>
                new LuaPatchFileJournal(path).ReadAll());
            Assert.Equal(LuaPatchJournalErrorCode.Corrupted, truncated.Code);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void FileJournalRejectsInvalidTransitionsAndCallerOwnedChainFields()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lunil-journal-tests",
            Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "deploy.ndjson");
        try
        {
            using var journal = new LuaPatchFileJournal(path);
            var first = Assert.Throws<LuaPatchJournalException>(() =>
                journal.Append(JournalEntry(LuaPatchJournalPhase.Prepared)));
            Assert.Equal(LuaPatchJournalErrorCode.InvalidTransition, first.Code);

            var storedFields = Assert.Throws<LuaPatchJournalException>(() =>
                journal.Append(JournalEntry(LuaPatchJournalPhase.Started) with
                {
                    Sequence = 1,
                    Hash = new string('A', 64),
                }));
            Assert.Equal(LuaPatchJournalErrorCode.InvalidEntry, storedFields.Code);

            journal.Append(JournalEntry(LuaPatchJournalPhase.Started));
            var skipped = Assert.Throws<LuaPatchJournalException>(() =>
                journal.Append(JournalEntry(LuaPatchJournalPhase.Publishing)));
            Assert.Equal(LuaPatchJournalErrorCode.InvalidTransition, skipped.Code);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void FileJournalEnforcesExclusiveWriterOwnershipButAllowsReaders()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lunil-journal-tests",
            Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "deploy.ndjson");
        try
        {
            using var first = new LuaPatchFileJournal(path);
            first.Append(JournalEntry(LuaPatchJournalPhase.Started, "transaction-1"));

            using var second = new LuaPatchFileJournal(path);
            Assert.Single(second.ReadAll());
            var unavailable = Assert.Throws<LuaPatchJournalException>(() =>
                second.Append(JournalEntry(LuaPatchJournalPhase.Started, "transaction-2")));
            Assert.Equal(LuaPatchJournalErrorCode.WriterUnavailable, unavailable.Code);

            first.Dispose();
            second.Append(JournalEntry(LuaPatchJournalPhase.Started, "transaction-2"));
            Assert.Equal(2, second.ReadAll().Length);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void FileJournalCompactionRetainsIncompleteAndRecentCompletedTransactions()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lunil-journal-tests",
            Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "deploy.ndjson");
        try
        {
            using var journal = new LuaPatchFileJournal(path);
            AppendCompletedTransaction(journal, "transaction-1");
            journal.Append(JournalEntry(LuaPatchJournalPhase.Started, "transaction-2"));
            journal.Append(JournalEntry(LuaPatchJournalPhase.Prepared, "transaction-2"));
            journal.Append(JournalEntry(LuaPatchJournalPhase.Started, "transaction-3"));
            journal.Append(JournalEntry(LuaPatchJournalPhase.Failed, "transaction-3"));
            File.WriteAllText(path + ".compact.tmp", "stale", Encoding.UTF8);

            var compacted = journal.Compact(new LuaPatchJournalCompactionOptions
            {
                RetainCompletedTransactions = 1,
            });

            Assert.Equal(8, compacted.OriginalEntryCount);
            Assert.Equal(4, compacted.RetainedEntryCount);
            Assert.Equal(4, compacted.RemovedEntryCount);
            Assert.True(compacted.Changed);
            Assert.NotEqual(compacted.OriginalTailHash, compacted.RetainedTailHash);
            Assert.False(File.Exists(path + ".compact.tmp"));
            var entries = journal.ReadAll();
            Assert.Equal(Enumerable.Range(1, 4).Select(static value => (long)value),
                entries.Select(static entry => entry.Sequence));
            Assert.Null(entries[0].PreviousHash);
            Assert.Equal(entries[0].Hash, entries[1].PreviousHash);
            Assert.DoesNotContain(entries, static entry =>
                entry.TransactionId == "transaction-1");
            Assert.Single(journal.GetIncompleteTransactions());

            journal.Append(JournalEntry(LuaPatchJournalPhase.Publishing, "transaction-2"));
            Assert.Equal(5, journal.ReadAll().Length);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void FileJournalAutomaticallyCompactsBeforeItsEntryLimit()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lunil-journal-tests",
            Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "deploy.ndjson");
        try
        {
            using var journal = new LuaPatchFileJournal(path, new LuaPatchFileJournalOptions
            {
                MaximumEntries = 5,
                AutomaticCompaction = new LuaPatchJournalCompactionOptions
                {
                    RetainCompletedTransactions = 0,
                },
            });
            AppendCompletedTransaction(journal, "transaction-1");
            journal.Append(JournalEntry(LuaPatchJournalPhase.Started, "transaction-2"));

            journal.Append(JournalEntry(LuaPatchJournalPhase.Prepared, "transaction-2"));

            var entries = journal.ReadAll();
            Assert.Equal(2, entries.Length);
            Assert.All(entries, static entry =>
                Assert.Equal("transaction-2", entry.TransactionId));
            Assert.Equal([1L, 2L], entries.Select(static entry => entry.Sequence));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void FileJournalKeepsTheVerifiedSourceWhenAtomicReplacementFails()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lunil-journal-tests",
            Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "deploy.ndjson");
        try
        {
            using var journal = new LuaPatchFileJournal(path);
            AppendCompletedTransaction(journal, "transaction-1");
            using (var replacementBlocker = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite))
            {
                var failure = Assert.Throws<LuaPatchJournalException>(() =>
                    journal.Compact(new LuaPatchJournalCompactionOptions
                    {
                        RetainCompletedTransactions = 0,
                    }));
                Assert.Equal(LuaPatchJournalErrorCode.IoFailure, failure.Code);
            }

            Assert.Equal(4, journal.ReadAll().Length);
            var faulted = Assert.Throws<LuaPatchJournalException>(() =>
                journal.Append(JournalEntry(LuaPatchJournalPhase.Started, "transaction-2")));
            Assert.Equal(LuaPatchJournalErrorCode.IoFailure, faulted.Code);
            journal.Dispose();

            using var reopened = new LuaPatchFileJournal(path);
            Assert.Equal(4, reopened.ReadAll().Length);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FileJournalReadersObserveValidSnapshotsDuringAppendAndCompaction()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lunil-journal-tests",
            Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "deploy.ndjson");
        try
        {
            using var writer = new LuaPatchFileJournal(path, new LuaPatchFileJournalOptions
            {
                MaximumEntries = 12,
                AutomaticCompaction = new LuaPatchJournalCompactionOptions
                {
                    RetainCompletedTransactions = 1,
                },
            });
            using var reader = new LuaPatchFileJournal(path);
            using var finished = new CancellationTokenSource();
            var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            var readTask = Task.Run(() =>
            {
                while (!finished.IsCancellationRequested)
                {
                    try
                    {
                        _ = reader.ReadAll();
                    }
                    catch (Exception exception)
                    {
                        errors.Enqueue(exception);
                    }
                }
            });

            for (var index = 0; index < 32; index++)
            {
                AppendCompletedTransaction(writer, $"transaction-{index}");
            }

            finished.Cancel();
            await readTask;
            Assert.Empty(errors);
            Assert.NotEmpty(reader.ReadAll());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void DistributedBarrierPublishesOnlyAfterApplyAndCompletesAfterGlobalHealth()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var store = new ScriptedDistributedStore(request => request.Signal switch
        {
            LuaPatchDistributedBarrierSignal.Prepared => Snapshot(
                request,
                LuaPatchDistributedBarrierDecision.Apply,
                ["process-a"]),
            LuaPatchDistributedBarrierSignal.Healthy => Snapshot(
                request,
                LuaPatchDistributedBarrierDecision.Commit,
                ["process-a"],
                ["process-a"]),
            _ => throw new InvalidOperationException("unexpected distributed signal"),
        });

        var result = new LuaPatchCoordinator().CommitRing(
            "distributed-rollout",
            Ring(Target("state-a", host, CreateBundle("return {value=2}"))),
            DistributedOptions(store));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, Value(host));
        Assert.Equal(
            [LuaPatchDistributedBarrierSignal.Prepared, LuaPatchDistributedBarrierSignal.Healthy],
            store.Requests.Select(static request => request.Signal));
        Assert.Equal(LuaPatchDistributedBarrierDecision.Commit, result.DistributedBarrier!.Decision);
    }

    [Fact]
    public void DistributedBarrierLeavesUnselectedParticipantOnTheOldGeneration()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var store = new ScriptedDistributedStore(request => Snapshot(
            request,
            LuaPatchDistributedBarrierDecision.Apply,
            ["process-b"]));

        var result = new LuaPatchCoordinator().CommitRing(
            "distributed-rollout",
            Ring(Target("state-a", host, CreateBundle("return {value=2}"))),
            DistributedOptions(store));

        Assert.Equal(LuaPatchRingCommitStatus.Deferred, result.Status);
        Assert.Equal(1, Value(host));
        Assert.Equal(
            [LuaPatchDistributedBarrierSignal.Prepared],
            store.Requests.Select(static request => request.Signal));
        Assert.False(result.Targets[0].Commit.SideEffectsMayHaveOccurred);
    }

    [Fact]
    public void DistributedHealthRollbackRestoresTheOldGeneration()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var store = new ScriptedDistributedStore(request => request.Signal switch
        {
            LuaPatchDistributedBarrierSignal.Prepared => Snapshot(
                request,
                LuaPatchDistributedBarrierDecision.Apply,
                ["process-a"]),
            LuaPatchDistributedBarrierSignal.Healthy => Snapshot(
                request,
                LuaPatchDistributedBarrierDecision.Rollback,
                ["process-a"],
                message: "peer health failed"),
            _ => throw new InvalidOperationException("unexpected distributed signal"),
        });

        var result = new LuaPatchCoordinator().CommitRing(
            "distributed-rollout",
            Ring(Target("state-a", host, CreateBundle("return {value=2}"))),
            DistributedOptions(store));

        Assert.Equal(LuaPatchRingCommitStatus.HealthRejected, result.Status);
        Assert.Equal(1, Value(host));
        Assert.True(result.Targets[0].Commit.SideEffectsMayHaveOccurred);
        Assert.Equal("peer health failed", result.Message);
    }

    [Fact]
    public void DistributedHealthRollbackReopensReplayAcceptance()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "lunil-distributed-replay-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var host = CreateHost("return {value=1}");
            Load(host);
            var bundle = CreateBundle("return {value=2}");
            var replay = new LuaPatchFileReplayStore(Path.Combine(directory, "replay.ndjson"));
            var preparation = host.PreparePatch(bundle, AcceptanceOptions(replay, "state-a"));
            Assert.True(preparation.Succeeded, preparation.Message);
            var store = new ScriptedDistributedStore(request => request.Signal switch
            {
                LuaPatchDistributedBarrierSignal.Prepared => Snapshot(
                    request,
                    LuaPatchDistributedBarrierDecision.Apply,
                    ["process-a"]),
                LuaPatchDistributedBarrierSignal.Healthy => Snapshot(
                    request,
                    LuaPatchDistributedBarrierDecision.Rollback,
                    ["process-a"],
                    message: "peer health failed"),
                _ => throw new InvalidOperationException("unexpected distributed signal"),
            });

            var result = new LuaPatchCoordinator().CommitRing(
                "distributed-rollout",
                Ring(new LuaPatchDeploymentTarget("state-a", host, preparation.PreparedPatch!)),
                DistributedOptions(store) with
                {
                    TimeProvider = new FixedTimeProvider(new DateTimeOffset(
                        2026, 7, 23, 0, 0, 0, TimeSpan.Zero)),
                });

            Assert.Equal(LuaPatchRingCommitStatus.HealthRejected, result.Status);
            Assert.Equal(
                [
                    LuaPatchReplayRecordState.Reserved,
                    LuaPatchReplayRecordState.Committed,
                    LuaPatchReplayRecordState.Reopened,
                ],
                replay.ReadAll().Select(static record => record.State).ToArray());
            Assert.True(host.PreparePatch(
                bundle,
                AcceptanceOptions(replay, "state-a")).Succeeded);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void LocalHealthFailureAcknowledgesUnhealthyBeforeReturning()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var store = new ScriptedDistributedStore(request => request.Signal switch
        {
            LuaPatchDistributedBarrierSignal.Prepared => Snapshot(
                request,
                LuaPatchDistributedBarrierDecision.Apply,
                ["process-a"]),
            LuaPatchDistributedBarrierSignal.Unhealthy => Snapshot(
                request,
                LuaPatchDistributedBarrierDecision.Rollback,
                ["process-a"],
                message: request.Message),
            _ => throw new InvalidOperationException("unexpected distributed signal"),
        });

        var result = new LuaPatchCoordinator().CommitRing(
            "distributed-rollout",
            Ring(Target("state-a", host, CreateBundle("return {value=2}"))),
            DistributedOptions(store) with
            {
                HealthCheck = _ => LuaPatchRingHealthDecision.Rollback,
            });

        Assert.Equal(LuaPatchRingCommitStatus.HealthRejected, result.Status);
        Assert.Equal(1, Value(host));
        Assert.Equal(
            [LuaPatchDistributedBarrierSignal.Prepared, LuaPatchDistributedBarrierSignal.Unhealthy],
            store.Requests.Select(static request => request.Signal));
    }

    [Fact]
    public void DistributedStoreFailureFailsClosedBeforePublication()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var store = new ScriptedDistributedStore(_ => throw new IOException("store unavailable"));

        var result = new LuaPatchCoordinator().CommitRing(
            "distributed-rollout",
            Ring(Target("state-a", host, CreateBundle("return {value=2}"))),
            DistributedOptions(store));

        Assert.Equal(LuaPatchRingCommitStatus.CoordinationFailed, result.Status);
        Assert.Equal(1, Value(host));
        Assert.False(result.Targets[0].Commit.SideEffectsMayHaveOccurred);
    }

    [Fact]
    public void DistributedStoreUnexpectedCancellationFailsClosedBeforePublication()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var store = new ScriptedDistributedStore(
            _ => throw new OperationCanceledException("store cancelled internally"));

        var result = new LuaPatchCoordinator().CommitRing(
            "distributed-rollout",
            Ring(Target("state-a", host, CreateBundle("return {value=2}"))),
            DistributedOptions(store));

        Assert.Equal(LuaPatchRingCommitStatus.CoordinationFailed, result.Status);
        Assert.Equal("store cancelled internally", result.Message);
        Assert.Equal(1, Value(host));
        Assert.False(result.Targets[0].Commit.SideEffectsMayHaveOccurred);
    }

    [Fact]
    public void DistributedStoreNullSnapshotFailsClosedBeforePublication()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var store = new ScriptedDistributedStore(_ => null!);

        var result = new LuaPatchCoordinator().CommitRing(
            "distributed-rollout",
            Ring(Target("state-a", host, CreateBundle("return {value=2}"))),
            DistributedOptions(store));

        Assert.Equal(LuaPatchRingCommitStatus.CoordinationFailed, result.Status);
        Assert.Contains("no snapshot", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Value(host));
    }

    [Fact]
    public void DistributedPreparationWaitCancellationReportsFailureAndRollsBack()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        using var cancellation = new CancellationTokenSource();
        var store = new ScriptedDistributedStore(
            request => request.Signal switch
            {
                LuaPatchDistributedBarrierSignal.Prepared => Snapshot(
                    request,
                    LuaPatchDistributedBarrierDecision.Waiting,
                    []),
                LuaPatchDistributedBarrierSignal.PreparationFailed => Snapshot(
                    request,
                    LuaPatchDistributedBarrierDecision.Rollback,
                    [],
                    message: request.Message),
                _ => throw new InvalidOperationException("unexpected distributed signal"),
            },
            request =>
            {
                if (request.Signal == LuaPatchDistributedBarrierSignal.Prepared)
                {
                    cancellation.Cancel();
                }
            });

        var result = new LuaPatchCoordinator().CommitRing(
            "distributed-rollout",
            Ring(Target("state-a", host, CreateBundle("return {value=2}"))),
            DistributedOptions(store),
            cancellation.Token);

        Assert.Equal(LuaPatchRingCommitStatus.CoordinationFailed, result.Status);
        Assert.Equal(1, Value(host));
        Assert.Equal(
            [
                LuaPatchDistributedBarrierSignal.Prepared,
                LuaPatchDistributedBarrierSignal.PreparationFailed,
            ],
            store.Requests.Select(static request => request.Signal));
    }

    [Fact]
    public void FileDistributedBarrierStoreIntegratesWithCoordinatorCommit()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "lunil-distributed-coordinator-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var host = CreateHost("return {value=1}");
            Load(host);
            var store = new LuaPatchFileDistributedBarrierStore(directory);
            var options = DistributedOptions(store);
            options = options with
            {
                DistributedBarrier = options.DistributedBarrier! with
                {
                    Participants = ["process-a"],
                    RequiredParticipantCount = 1,
                },
            };

            var result = new LuaPatchCoordinator().CommitRing(
                "file-distributed-rollout",
                Ring(Target("state-a", host, CreateBundle("return {value=2}"))),
                options);

            Assert.True(result.Succeeded, result.Message);
            Assert.Equal(2, Value(host));
            Assert.Equal(
                LuaPatchDistributedBarrierDecision.Commit,
                result.DistributedBarrier!.Decision);
            Assert.Equal(["process-a"], result.DistributedBarrier.SelectedParticipants.ToArray());
            Assert.Equal(["process-a"], result.DistributedBarrier.HealthyParticipants.ToArray());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void LocalPreparationFailureIsDurablyAcknowledged()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var lifecycle = new TestTargetLifecycle("state-a", [])
        {
            QuiescenceStatus = LuaPatchTargetQuiescenceStatus.Failed,
        };
        var store = new ScriptedDistributedStore(request => Snapshot(
            request,
            LuaPatchDistributedBarrierDecision.Rollback,
            [],
            message: request.Message));

        var result = new LuaPatchCoordinator().CommitRing(
            "distributed-rollout",
            Ring(Target("state-a", host, CreateBundle("return {value=2}")) with
            {
                Lifecycle = lifecycle,
            }),
            DistributedOptions(store));

        Assert.Equal(LuaPatchRingCommitStatus.QuiescenceFailed, result.Status);
        var request = Assert.Single(store.Requests);
        Assert.Equal(LuaPatchDistributedBarrierSignal.PreparationFailed, request.Signal);
        Assert.Equal(1, Value(host));
    }

    [Fact]
    public void DistributedBarrierPollsBothDurableDecisions()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var preparationObserved = false;
        var healthObserved = false;
        var store = new ScriptedDistributedStore(request => request.Signal switch
        {
            LuaPatchDistributedBarrierSignal.Prepared => Snapshot(
                request,
                LuaPatchDistributedBarrierDecision.Waiting,
                []),
            LuaPatchDistributedBarrierSignal.Healthy => Snapshot(
                request,
                LuaPatchDistributedBarrierDecision.Apply,
                ["process-a"]),
            LuaPatchDistributedBarrierSignal.Observe when !preparationObserved => Snapshot(
                request,
                LuaPatchDistributedBarrierDecision.Apply,
                ["process-a"]),
            LuaPatchDistributedBarrierSignal.Observe when !healthObserved => Snapshot(
                request,
                LuaPatchDistributedBarrierDecision.Commit,
                ["process-a"],
                ["process-a"]),
            _ => throw new InvalidOperationException("unexpected distributed signal"),
        }, request =>
        {
            if (request.Signal == LuaPatchDistributedBarrierSignal.Observe && !preparationObserved)
            {
                preparationObserved = true;
            }
            else if (request.Signal == LuaPatchDistributedBarrierSignal.Observe)
            {
                healthObserved = true;
            }
        });

        var result = new LuaPatchCoordinator().CommitRing(
            "distributed-rollout",
            Ring(Target("state-a", host, CreateBundle("return {value=2}"))),
            DistributedOptions(store));

        Assert.True(result.Succeeded, result.Message);
        Assert.True(preparationObserved);
        Assert.True(healthObserved);
        Assert.Equal(2, Value(host));
    }

    [Fact]
    public void DistributedPreparationTimeoutReportsFailureAndNeverPublishes()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var store = new ScriptedDistributedStore(request => request.Signal switch
        {
            LuaPatchDistributedBarrierSignal.PreparationFailed => Snapshot(
                request,
                LuaPatchDistributedBarrierDecision.Rollback,
                [],
                message: request.Message),
            _ => Snapshot(request, LuaPatchDistributedBarrierDecision.Waiting, []),
        });
        var options = DistributedOptions(store) with
        {
            DistributedBarrier = DistributedOptions(store).DistributedBarrier! with
            {
                PreparationTimeout = TimeSpan.FromMilliseconds(10),
            },
        };

        var result = new LuaPatchCoordinator().CommitRing(
            "distributed-rollout",
            Ring(Target("state-a", host, CreateBundle("return {value=2}"))),
            options);

        Assert.Equal(LuaPatchRingCommitStatus.CoordinationFailed, result.Status);
        Assert.Equal(1, Value(host));
        Assert.Contains(
            LuaPatchDistributedBarrierSignal.PreparationFailed,
            store.Requests.Select(static request => request.Signal));
    }

    [Fact]
    public void GlobalCommitIsNotLocallyReversedByALateJournalFailure()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var events = new List<string>();
        var lifecycle = new TestTargetLifecycle("state-a", events);
        var target = Target("state-a", host, CreateBundle("return {value=2}")) with
        {
            Lifecycle = lifecycle,
        };
        var store = new ScriptedDistributedStore(request => request.Signal switch
        {
            LuaPatchDistributedBarrierSignal.Prepared => Snapshot(
                request,
                LuaPatchDistributedBarrierDecision.Apply,
                ["process-a"]),
            LuaPatchDistributedBarrierSignal.Healthy => Snapshot(
                request,
                LuaPatchDistributedBarrierDecision.Commit,
                ["process-a"],
                ["process-a"]),
            _ => throw new InvalidOperationException("unexpected distributed signal"),
        });

        var result = new LuaPatchCoordinator().CommitRing(
            "distributed-rollout",
            Ring(target),
            DistributedOptions(store) with
            {
                RequireTargetIsolation = true,
                Journal = new MemoryJournal { ThrowOn = LuaPatchJournalPhase.Restoring },
            });

        Assert.Equal(LuaPatchRingCommitStatus.JournalFailed, result.Status);
        Assert.Equal(2, Value(host));
        Assert.Equal(["state-a:isolate", "state-a:quiesce"], events);
        Assert.Equal(LuaPatchDistributedBarrierDecision.Commit, result.DistributedBarrier!.Decision);
    }

    [Fact]
    public void DistributedBarrierOptionsRejectInvalidMembershipAndQuorum()
    {
        using var host = CreateHost("return {value=1}");
        Load(host);
        var target = Target("state-a", host, CreateBundle("return {value=2}"));
        var store = new ScriptedDistributedStore(request => Snapshot(
            request,
            LuaPatchDistributedBarrierDecision.Apply,
            ["process-a"]));

        var missing = DistributedOptions(store) with
        {
            DistributedBarrier = DistributedOptions(store).DistributedBarrier! with
            {
                Participants = ["process-b"],
            },
        };
        Assert.Throws<ArgumentException>(() => new LuaPatchCoordinator().CommitRing(
            "distributed-rollout",
            Ring(target),
            missing));

        var quorum = DistributedOptions(store) with
        {
            DistributedBarrier = DistributedOptions(store).DistributedBarrier! with
            {
                RequiredParticipantCount = 3,
            },
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuaPatchCoordinator().CommitRing(
            "distributed-rollout",
            Ring(target),
            quorum));
    }

    private static LuaPatchDeploymentTarget Target(
        string id,
        LuaHost host,
        LuaPatchBundle bundle)
    {
        var preparation = host.PreparePatch(bundle);
        Assert.True(preparation.Succeeded, preparation.Message);
        return new LuaPatchDeploymentTarget(id, host, preparation.PreparedPatch!);
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

    private static LuaPatchRolloutRing Ring(params LuaPatchDeploymentTarget[] targets) => new()
    {
        Name = "production",
        Targets = targets.ToImmutableArray(),
    };

    private static LuaPatchCoordinatorOptions DistributedOptions(
        ILuaPatchDistributedBarrierStore store) => new()
        {
            DistributedBarrier = new LuaPatchDistributedBarrierOptions
            {
                Store = store,
                ParticipantId = "process-a",
                Participants = ["process-a", "process-b"],
                RequiredParticipantCount = 1,
                PreparationTimeout = TimeSpan.FromSeconds(1),
                HealthTimeout = TimeSpan.FromSeconds(1),
                PollInterval = TimeSpan.FromMilliseconds(1),
            },
        };

    private static LuaPatchDistributedBarrierSnapshot Snapshot(
        LuaPatchDistributedBarrierRequest request,
        LuaPatchDistributedBarrierDecision decision,
        ImmutableArray<string> selected,
        ImmutableArray<string> healthy = default,
        string? message = null) => new()
        {
            RolloutId = request.RolloutId,
            RingName = request.RingName,
            PatchId = request.PatchId,
            TargetRevision = request.TargetRevision,
            PatchManifestIdentity = request.PatchManifestIdentity,
            Participants = request.Participants,
            RequiredParticipantCount = request.RequiredParticipantCount,
            PreparedParticipants = selected,
            SelectedParticipants = selected,
            HealthyParticipants = healthy.IsDefault ? [] : healthy,
            Decision = decision,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
            PreparationDeadline = DateTimeOffset.UnixEpoch + request.PreparationTimeout,
            HealthDeadline = decision == LuaPatchDistributedBarrierDecision.Waiting
                ? null
                : DateTimeOffset.UnixEpoch + request.HealthTimeout,
            Message = message,
        };

    private static void Load(LuaHost host, string? prefix = null)
    {
        var result = host.RunUtf8(
            "package.path='mods/?.lua'; " + (prefix ?? "require('value')"));
        Assert.True(result.Succeeded);
    }

    private static long Value(LuaHost host) => host.RunUtf8(
        "return require('value').value").Execution!.Values[0].AsInteger();

    private static LuaHost CreateHost(string source) => new(
        LuaHostOptions.Default with
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            StandardLibrary = LuaHostCapabilityProfiles.Create(LuaHostProfile.Restricted) with
            {
                FileSystem = new SingleFileSystem(source),
            },
        });

    private static LuaPatchBundle CreateBundle(string source) => CreateBundle(
        source,
        new DateTimeOffset(2099, 8, 22, 0, 0, 0, TimeSpan.Zero));

    private static LuaPatchBundle CreateBundle(
        string source,
        DateTimeOffset expiresAt)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return LuaPatchBundle.Create(
            new LuaPatchManifest
            {
                PatchId = "patch-1",
                Channel = "test",
                TargetBuild = "build-2",
                BaseRevision = "build-1",
                TargetRevision = "build-2",
                LanguageVersion = LuaLanguageVersion.Lua54,
                RuntimeAbi = "lunil-0.12",
                CreatedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero),
                ExpiresAt = expiresAt,
                Nonce = "coordinator-test",
            },
            [new LuaPatchEntry(
                "modules/value.lua",
                "value",
                LuaPatchEntryKind.Source,
                Encoding.UTF8.GetBytes(source))],
            new LuaPatchEcdsaSigner("test", key));
    }

    private static void AppendCompletedTransaction(
        LuaPatchFileJournal journal,
        string transactionId)
    {
        journal.Append(JournalEntry(LuaPatchJournalPhase.Started, transactionId));
        journal.Append(JournalEntry(LuaPatchJournalPhase.Prepared, transactionId));
        journal.Append(JournalEntry(LuaPatchJournalPhase.Publishing, transactionId));
        journal.Append(JournalEntry(LuaPatchJournalPhase.Committed, transactionId));
    }

    private static LuaPatchJournalEntry JournalEntry(
        LuaPatchJournalPhase phase,
        string transactionId = "transaction-1") => new()
        {
            Timestamp = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero),
            TransactionId = transactionId,
            RolloutId = "rollout-1",
            RingName = "production",
            PatchId = "patch-1",
            TargetRevision = "build-2",
            Phase = phase,
            TargetIds = ["state-a", "state-b"],
        };

    private sealed class SingleFileSystem(string source) : ILuaFileSystem
    {
        private readonly byte[] _source = Encoding.UTF8.GetBytes(source);

        public byte[] ReadAllBytes(string path) => path == "mods/value.lua"
            ? _source.ToArray()
            : throw new FileNotFoundException(path);

        public bool FileExists(string path) => path == "mods/value.lua";
    }

    private sealed class MemoryJournal : ILuaPatchDeploymentJournal
    {
        public List<LuaPatchJournalEntry> Entries { get; } = [];

        public LuaPatchJournalPhase? ThrowOn { get; init; }

        public void Append(LuaPatchJournalEntry entry)
        {
            if (entry.Phase == ThrowOn)
            {
                throw new IOException("journal append failure");
            }

            Entries.Add(entry);
        }

        public ImmutableArray<LuaPatchJournalEntry> ReadAll() => Entries.ToImmutableArray();
    }

    private sealed class ScriptedDistributedStore(
        Func<LuaPatchDistributedBarrierRequest, LuaPatchDistributedBarrierSnapshot> advance,
        Action<LuaPatchDistributedBarrierRequest>? observed = null)
        : ILuaPatchDistributedBarrierStore
    {
        public List<LuaPatchDistributedBarrierRequest> Requests { get; } = [];

        public LuaPatchDistributedBarrierSnapshot Advance(
            LuaPatchDistributedBarrierRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var result = advance(request);
            observed?.Invoke(request);
            return result;
        }
    }

    private sealed class RollbackRecoveryHandler : ILuaPatchCrashRecoveryHandler
    {
        public LuaPatchRecoveryResolution Recover(LuaPatchRecoveryRecord record) =>
            LuaPatchRecoveryResolution.RolledBack;
    }

    private sealed class CommitRecoveryHandler : ILuaPatchCrashRecoveryHandler
    {
        public LuaPatchRecoveryResolution Recover(LuaPatchRecoveryRecord record) =>
            LuaPatchRecoveryResolution.Committed;
    }

    private sealed class TestTargetLifecycle(
        string targetId,
        List<string> events) : ILuaPatchTargetLifecycle, ILuaPatchTargetIsolation
    {
        public LuaPatchTargetIsolationStatus IsolationStatus { get; init; } =
            LuaPatchTargetIsolationStatus.Isolated;

        public LuaPatchTargetQuiescenceStatus QuiescenceStatus { get; init; } =
            LuaPatchTargetQuiescenceStatus.Quiescent;

        public LuaPatchTargetRestoreStatus RestoreStatus { get; init; } =
            LuaPatchTargetRestoreStatus.Restored;

        public bool ReturnIsolationOnFailure { get; init; }

        public LuaPatchTargetLifecycleContext? Context { get; private set; }

        public LuaPatchTargetIsolationResult TryIsolate(
            LuaPatchTargetLifecycleContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Context = context;
            events.Add($"{targetId}:isolate");
            return new LuaPatchTargetIsolationResult(
                IsolationStatus,
                IsolationStatus == LuaPatchTargetIsolationStatus.Isolated ||
                    ReturnIsolationOnFailure
                    ? this
                    : null,
                IsolationStatus == LuaPatchTargetIsolationStatus.Isolated
                    ? null
                    : "isolation did not complete");
        }

        public LuaPatchTargetQuiescenceResult WaitForQuiescence(
            LuaPatchTargetLifecycleContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(targetId, context.TargetId);
            events.Add($"{targetId}:quiesce");
            return new LuaPatchTargetQuiescenceResult(
                QuiescenceStatus,
                QuiescenceStatus == LuaPatchTargetQuiescenceStatus.Quiescent
                    ? null
                    : "target did not quiesce");
        }

        public LuaPatchTargetRestoreResult Restore(
            LuaPatchTargetRestoreContext context,
            CancellationToken cancellationToken)
        {
            Assert.Equal(CancellationToken.None, cancellationToken);
            events.Add($"{targetId}:restore:{context.Outcome}");
            return new LuaPatchTargetRestoreResult(
                RestoreStatus,
                RestoreStatus == LuaPatchTargetRestoreStatus.Restored
                    ? null
                    : "traffic restore failed");
        }

        public void Dispose()
        {
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
