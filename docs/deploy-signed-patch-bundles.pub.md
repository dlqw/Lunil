# Deploy signed patch bundles

[简体中文](deploy-signed-patch-bundles.zh-CN.pub.md)

This how-to prepares, publishes, rolls out, and recovers a signed Lunil patch without exposing a
partially validated module graph.

## Prerequisites

- A configured `LuaHost` and patch trust store.
- A canonical signed patch bundle whose target labels match the host.
- Stable storage for replay and deployment journals.
- An application health check and traffic-isolation mechanism for multi-target rollout.

## 1. Configure trust, admission, replay, and migration

Create a trust store with overlapping activation windows when release keys must rotate. Public-key
paths are relative to the trust-store file:

```json
{
  "schema": "lunil.patch-trust.v1",
  "keys": [
    {
      "keyId": "release-2026-q3",
      "publicKey": "keys/release-2026-q3.pem",
      "validFrom": "2026-07-01T00:00:00Z",
      "validUntil": "2026-10-08T00:00:00Z"
    },
    {
      "keyId": "release-2026-q4",
      "publicKey": "keys/release-2026-q4.pem",
      "validFrom": "2026-10-01T00:00:00Z"
    }
  ]
}
```

Pack with the private release key, then verify with the bounded public trust store:

```text
lunil patch pack manifest.json payload --output update.lpatch --private-key private.pem --key-id release-2026-q3
lunil patch verify update.lpatch --trust-store patch-trust.json
lunil patch dry-run update.lpatch --trust-store patch-trust.json
```

Bind acceptance and replay protection to one stable deployment target. `RuntimeAbi` is a
host-defined application compatibility label, not the Lunil product version; keep it stable only
across application builds that can safely consume the same patch payloads. The coordinator requires
`ReplayScope` to equal that target's `TargetId`:

```csharp
var replayStore = new LuaPatchFileReplayStore("state/accepted-patches.ndjson");
var prepareOptions = new LuaPatchPrepareOptions
{
    AcceptancePolicy = new LuaPatchAcceptancePolicy
    {
        TargetBuild = currentBuild,
        CurrentRevision = currentRevision,
        RuntimeAbi = "my-game-runtime-v1",
        AllowedChannels = ["production"],
        GrantedCapabilities = hostPatchCapabilities,
        TargetLabels =
        [
            new("environment", deploymentEnvironment),
            new("region", region),
            new("shard", shardId),
            new("ring", rolloutRing),
        ],
        RevisionClassifier = releaseLedger.Classify,
        RollbackAuthorizer = (manifest, signer) =>
            signer.Algorithm == LuaPatchEcdsaSigner.AlgorithmName &&
            rollbackKeyIds.Contains(signer.KeyId) &&
            approvedRollbackTargets.Contains(manifest.TargetRevision),
    },
    ReplayStore = replayStore,
    ReplayScope = "state-a",
};
```

When the bundle contains `migration/schema.json`, register the current live version and supply every
adapter referenced by the canonical schema:

```csharp
host.SetPatchStateSchemaVersion("game-state", "42");
var preparation = await host.PreparePatchAsync(bundle, prepareOptions with
{
    StateMigrationAdapters = stateAdapters,
    ResourceMigrationAdapters = resourceAdapters,
}, stoppingToken);
```

Use `LuaPatchMigrationSchemaSerializer.Serialize` to produce the exact bytes stored at
`migration/schema.json` before packing. Omit the schema and adapter step when the patch does not
migrate state or host resources.

## 2. Preflight dependencies and compilation

`LuaPatchDependencyPlan` orders required dependencies before dependents and treats a cyclic strongly
connected component as one preparation group. `LuaPatchPreflight.Analyze` creates an isolated staging
host and validates source, binary-chunk, and host-decoded canonical-IR entries without modifying the
live `LuaHost`.

`LuaHost.PreparePatchAsync` performs that work on a worker thread and then briefly enters the live
host execution gate to capture an expected revision for every target module. Preparation succeeds
only when all target modules are already loaded, the language versions match, and every module has
a rollback-safe cache policy. No candidate loader is executed during preparation.

Isolated compilation can be CPU- and memory-intensive when a rollout fans out across many hosts.
Share one `LuaPatchPreparationLimiter` across their prepare options to bound both active work and
queued demand:

```csharp
// Keep this process-wide for the deployment service, not per target.
var preparationLimiter = new LuaPatchPreparationLimiter(
    maximumConcurrency: Math.Max(1, Environment.ProcessorCount / 2),
    maximumQueueLength: 64);

var prepareOptions = new LuaPatchPrepareOptions
{
    PreparationLimiter = preparationLimiter,
    PreparationWaitTimeout = TimeSpan.FromMilliseconds(250),
    // AcceptancePolicy, ReplayStore, ReplayScope, migration adapters, ...
};

var preparation = await host.PreparePatchAsync(bundle, prepareOptions, stoppingToken);
if (preparation.Status == LuaPatchPrepareStatus.Deferred)
{
    ScheduleRetry(preparation.AdmissionStatus); // Saturated or TimedOut
    return;
}
```

`MaximumConcurrency` is the number of isolated preflights allowed at once;
`MaximumQueueLength` bounds callers waiting behind them. A zero queue is fail-fast. The wait timeout
may be zero, a finite value of at most `Int32.MaxValue` milliseconds, or
`Timeout.InfiniteTimeSpan`. Queue overflow and elapsed waits return
`Deferred` before preflight, live-state binding, or replay reservation; caller cancellation still
cancels the operation. The same admission rules apply to `PreparePatch` and `PreparePatchAsync`.
Export `ActiveCount` and `QueuedCount` as gauges, and keep retry jitter outside the limiter so a
rollout controller can coordinate backoff across targets.

## 3. Commit inside a game-loop update window

Open an update window between ticks or frames and commit the prepared patch on the same thread:

```csharp
var preparation = await host.PreparePatchAsync(bundle, prepareOptions, stoppingToken);
if (!preparation.Succeeded)
{
    return;
}

var opened = host.TryOpenPatchUpdateWindow(new LuaPatchUpdateWindowOptions
{
    WaitTimeout = TimeSpan.Zero,
    MaximumDuration = TimeSpan.FromMilliseconds(8),
}, stoppingToken);
if (!opened.Succeeded)
{
    // Keep the prepared patch and retry in a later frame.
    return;
}

using var window = opened.Window!;
var commit = host.CommitPatch(
    preparation.PreparedPatch!,
    window,
    new LuaPatchCommitOptions
    {
        MaximumPauseDuration = TimeSpan.FromMilliseconds(4),
    },
    stoppingToken);
```

The update window retains the host execution gate, so normal host execution cannot observe a
partially published module set. Commit rechecks every expected revision before candidate execution.
It then evaluates candidates in dependency-first order using a temporary `package.loaded` overlay:
a dependent can observe a dependency candidate that completed earlier in the transaction. Cache
values, module records, table-identity patches, compatible closure slots, and JIT module generations
are published together. A publication failure, cancellation, or elapsed pause budget restores all
target-module records, cache values, table contents, loader upvalues, and closure slots.

Suspended frames retain the immutable function generation captured on entry. Calls made after a
successful commit read the new closure-slot generation. Module-owned coroutine entry is additionally
generation-fenced: an undeclared old coroutine cannot resume after publication. Use an explicit
runtime-owned `Coroutine`/`Continue` resource rule when a suspended coroutine must finish on its old
frames; ordinary in-flight frames that are not retained as resumable coroutines still complete on
the immutable generation they captured.

`ReplaceCache` and `PatchExistingTable` are supported by atomic patch commits. An opaque `Custom`
cache callback and a source-path override are rejected during preparation because their effects
cannot be journaled as part of the module transaction. Candidate Lua code can still perform global,
CLR, filesystem, network, or other host-visible side effects; these are not generally reversible.
Failed results therefore set `SideEffectsMayHaveOccurred` after any candidate executes even when all
target-module state was restored.

Pause and cancellation checks occur between candidate loaders and publication steps. They prevent a
half-commit but do not preempt one loader in the middle of a VM call; configure the normal Lua
instruction budget for an upper bound on loader work. Within a cyclic dependency component, members
run in deterministic name order: completed members are staged as new, while a back-edge to a member
that has not run yet observes its old loaded value.

## 4. Roll out across states and rings

`LuaPatchCoordinator` coordinates multiple `LuaHost` states in one process. Every target in a
barrier ring must have a unique target id and host instance and must be prepared from the same
canonical patch manifest. The coordinator opens every update window before it prepares any commit
session, prepares every state before publication, and then publishes the complete ring. When a
target has an `ILuaPatchTargetLifecycle`, the coordinator first stops its new traffic, waits for the
adapter to report quiescence, and only then enters host update windows. Failure in isolation,
quiescence, window acquisition, preparation, publication, finalization, or the health gate rolls
back every participant in that ring. Coordinator operations are serialized process-wide to prevent
conflicting lock orders across coordinator instances.

Build a rollout from separately prepared host-bound patches. In this example,
`targetLifecycles` is an application-owned map of lifecycle adapters backed by the game router and
its in-flight work tracker:

```csharp
using var journal = new LuaPatchFileJournal("state/hot-update/deploy.ndjson");
var plan = new LuaPatchRolloutPlan
{
    RolloutId = "game-2026-07-22-01",
    Rings =
    [
        new LuaPatchRolloutRing
        {
            Name = "canary",
            Targets =
            [
                new("zone-canary", canaryHost, canaryPreparation.PreparedPatch!)
                {
                    Lifecycle = targetLifecycles["zone-canary"],
                },
            ],
        },
        new LuaPatchRolloutRing
        {
            Name = "production",
            Targets =
            [
                new("zone-01", zone01Host, zone01Preparation.PreparedPatch!)
                {
                    Lifecycle = targetLifecycles["zone-01"],
                },
                new("zone-02", zone02Host, zone02Preparation.PreparedPatch!)
                {
                    Lifecycle = targetLifecycles["zone-02"],
                },
            ],
        },
    ],
};

var result = new LuaPatchCoordinator().Deploy(plan, new LuaPatchCoordinatorOptions
{
    RequireTargetIsolation = true,
    TargetLifecycle = new LuaPatchTargetLifecycleOptions
    {
        IsolationTimeout = TimeSpan.FromSeconds(5),
        QuiescenceTimeout = TimeSpan.FromSeconds(30),
        RestoreTimeout = TimeSpan.FromSeconds(5),
    },
    UpdateWindow = new LuaPatchUpdateWindowOptions
    {
        WaitTimeout = TimeSpan.FromMilliseconds(2),
        MaximumDuration = TimeSpan.FromMilliseconds(12),
    },
    Commit = new LuaPatchCommitOptions
    {
        MaximumPauseDuration = TimeSpan.FromMilliseconds(8),
    },
    Journal = journal,
    HealthCheck = context => RingHealthIsAcceptable(context)
        ? LuaPatchRingHealthDecision.Accept
        : LuaPatchRingHealthDecision.Rollback,
}, stoppingToken);
```

Rings run in order. A rejected canary prevents later rings from starting. If an accepted canary is
followed by a failing production ring, the accepted canary remains committed while the failing ring
is rolled back. The synchronous health callback runs while all ring update windows are still held
and can inspect the newly published state. Returning `Rollback`, throwing, returning an invalid enum
value, or recursively entering a coordinator operation rejects the ring.

`ILuaPatchTargetLifecycle.TryIsolate` must stop new routing/admission before returning an
`ILuaPatchTargetIsolation`. `WaitForQuiescence` then drains in-flight requests, ticks, jobs, or actor
messages within the supplied timeout. The timeout is a cooperative adapter budget: implementations
must apply it to their own router and work tracker and observe the cancellation token. `Restore` is
called in reverse isolation order with `Committed` or `RolledBack`; it receives
`CancellationToken.None` so caller cancellation cannot skip traffic recovery. Make restoration
idempotent by `TransactionId`, and make `Dispose` release resources without changing routing.

Set `RequireTargetIsolation` in production so a missing adapter is rejected before the journal is
started. `LuaPatchTargetCommitResult.Lifecycle` reports the final lifecycle status. If cleanup
restored a target after an earlier isolation or quiescence failure, `Status` is `Restored` and
`Failure` retains the failed stage. If restoration fails after publication, the ring returns
`RestoreFailed`, committed module results remain observable, and the journal remains at `Restoring`
for crash recovery; do not route that target until recovery completes.

### Cross-process prepared and health quorums

Set `LuaPatchCoordinatorOptions.DistributedBarrier` when separate processes must make one durable
ring decision. Every process uses the same rollout id and ring name, lists the same stable process
identities, and prepares its own local `LuaHost` targets from the same canonical manifest. The first
accepted update pins that membership, quorum size, canonical manifest SHA-256, target revision, and
both timeout policies. A conflicting process fails before publication.

The barrier has two durable gates. First, prepared acknowledgements select exactly
`RequiredParticipantCount` participants and produce `Apply`; only those selected processes may
publish. After local publication, the application health callback, and replay acceptance succeed,
each selected process acknowledges `Healthy`. The store returns `Commit` only after every selected
participant is healthy. A selected failure or either deadline produces an immutable `Rollback`, so
surviving processes still hold their rollback sessions and restore the previous generation.
Processes outside the selected quorum return `Deferred` and stay on the old generation.

The built-in file store provides this protocol across processes that share a lock-correct file
system. Give it a dedicated directory because pruning owns its barrier JSON, temporary files, and
lock sidecars:

```csharp
var participantId = Environment.GetEnvironmentVariable("GAME_PROCESS_ID")!;
var participants = new[] { "game-a", "game-b", "game-c" }.ToImmutableArray();
var barrierStore = new LuaPatchFileDistributedBarrierStore(
    "/srv/game/shared/lunil/barriers",
    new LuaPatchFileDistributedBarrierStoreOptions
    {
        MaximumBarrierCount = 10_000,
        MaximumParticipantCount = 64,
        WriterLockTimeout = TimeSpan.FromSeconds(2),
    });

var localRing = new LuaPatchRolloutRing
{
    Name = "production", // Identical in every participant process.
    Targets = localPreparedTargets,
};

var result = new LuaPatchCoordinator().CommitRing(
    "game-2026-07-24-01", // Never reuse a rollout id for another deployment.
    localRing,
    new LuaPatchCoordinatorOptions
    {
        RequireTargetIsolation = true,
        Journal = localJournal,
        HealthCheck = CheckLocalGameHealth,
        DistributedBarrier = new LuaPatchDistributedBarrierOptions
        {
            Store = barrierStore,
            ParticipantId = participantId,
            Participants = participants,
            RequiredParticipantCount = 2,
            PreparationTimeout = TimeSpan.FromSeconds(30),
            HealthTimeout = TimeSpan.FromSeconds(30),
            PollInterval = TimeSpan.FromMilliseconds(50),
        },
    },
    stoppingToken);

if (result.Status == LuaPatchRingCommitStatus.Deferred)
{
    KeepServingThePreviousGeneration();
}
```

Use a local or shared file system that guarantees exclusive file locks and atomic same-directory
rename. The store flushes state before replacement, flushes Unix directory entries, normalizes
clock regressions, bounds identities, messages, participants, state bytes, and active barrier
files, and rejects hash mismatches or invalid transitions. Its SHA-256 protects against accidental
corruption, not an attacker who can rewrite the directory; enforce operating-system permissions.
For a database or consensus service, implement `ILuaPatchDistributedBarrierStore.Advance` with the
same atomic pin-and-decision semantics.

Retain terminal state long enough for every participant and operator to observe the decision, then
prune it explicitly. Pruning never removes waiting or apply state and also clears abandoned
temporary and lock sidecars:

```csharp
var pruned = barrierStore.PruneCompleted(TimeSpan.FromDays(7), stoppingToken);
Console.WriteLine($"Removed {pruned.RemovedBarrierCount} terminal barriers.");
```

Once the distributed store returns `Commit`, a later local journal or traffic-restoration failure
must not reverse only that process while peers remain committed. Lunil returns the local failure and
keeps the published generation; keep the target isolated and recover its journal or router before
serving traffic again. `LuaPatchRingCommitResult.DistributedBarrier` exposes the last observed
pinned membership, selected quorum, acknowledgements, deadlines, decision, and diagnostic message.

## 5. Recover the durable deployment journal

`LuaPatchFileJournal` writes canonical NDJSON records with a contiguous sequence and SHA-256 hash
chain. Each append uses one record write, write-through I/O, and a stable-storage flush before it
returns. The reader rejects torn records, non-canonical JSON, broken sequence or hash links, invalid
transaction phase transitions, changed transaction metadata, and configured byte, line, or entry
limit violations. The transaction phases are `Started`, `Prepared`, `Publishing`, optional
`Restoring`, and a terminal committed, rolled-back, failed, or recovered phase. `Restoring` means
module publication and replay acceptance completed while target traffic restoration is still
pending.

The first `Append`, `RecoverIncomplete`, or `Compact` mutation acquires an OS-enforced writer lock at
`<journal>.writer.lock` and holds it until the journal is disposed. A competing writer receives
`LuaPatchJournalErrorCode.WriterUnavailable`; independent `ReadAll` calls remain available while the
owner appends or replaces the active file. Readers retry a transient partial tail or replacement
sharing conflict for `ConcurrentReadTimeout` before reporting corruption or I/O failure. All Lunil
writers honor the lock, but the sidecar is not a security boundary against unrelated code that writes
the NDJSON file directly. Keep the owner alive for the deployment service lifetime and dispose it
before ownership is transferred to another process.

Completed history can be compacted without dropping an incomplete transaction:

```csharp
using var journal = new LuaPatchFileJournal(
    "state/hot-update/deploy.ndjson",
    new LuaPatchFileJournalOptions
    {
        AutomaticCompaction = new LuaPatchJournalCompactionOptions
        {
            RetainCompletedTransactions = 1_024,
        },
    });

var result = journal.Compact(new LuaPatchJournalCompactionOptions
{
    RetainCompletedTransactions = 1_024,
});
AnchorPreviousChain(result.OriginalTailHash);
```

Compaction retains every phase of every incomplete transaction plus the requested number of most
recently completed transactions, then renumbers and re-hashes the retained records. It writes a
same-directory temporary file, flushes it, and atomically replaces the active file. Unix hosts also
flush the containing directory; on Windows the flushed file plus `File.Replace` is the managed
durability boundary, so use a local journaled file system and storage replication when the platform's
power-loss guarantees matter. `AutomaticCompaction` is opt-in and runs only when the next append
would exceed the entry or byte limit. Export records that must outlive retention before compaction,
and externally anchor `OriginalTailHash` if the previous chain must remain independently auditable.

A hash chain detects accidental corruption and unanchored rewrites; it is not an authentication
mechanism against an actor that can rewrite the entire file. Store the journal and lock sidecar under
appropriate OS permissions and externally anchor or replicate terminal records when hostile storage
modification is in scope.

After process restart, inspect transactions whose last durable phase is `Started`, `Prepared`,
`Publishing`, or `Restoring`, reconcile the named targets with host-owned deployment state and
routing state, and record the result:

```csharp
using var journal = new LuaPatchFileJournal("state/hot-update/deploy.ndjson");
var pending = journal.GetIncompleteTransactions();
var recovered = journal.RecoverIncomplete(recoveryHandler);
```

`ILuaPatchCrashRecoveryHandler` returns `Committed`, `RolledBack`, or `Manual` for each incomplete
transaction. Lunil records resolved outcomes as `RecoveredCommitted` or `RecoveredRolledBack`;
`Manual` remains incomplete for later reconciliation. The journal records deployment intent and
resolution—it does not serialize a Lua heap, suspended frames, CLR objects, or external resource
state. The handler must determine the authoritative outcome from the application's durable state or
restore it before returning a terminal resolution.

## Expected result

Every target either publishes the complete prepared generation or keeps the previous generation.
Failed preparation, commit, health, or recovery steps remain observable through explicit status,
journal, and telemetry contracts. Exact types, defaults, limits, and CLI options are listed in the
[signed patch bundle reference](signed-patch-bundles.pub.md).
