# Signed patch bundles

[简体中文](hot-update.zh-CN.md)

Lunil patch bundles package Lua module replacements for validation before a host publishes them.
The versioned canonical manifest records the target build, base and target revisions, signed update
intent, requested admission capabilities, Lua language version, runtime ABI, channel, expiry, nonce,
required target labels, dependencies, and SHA-256 payload identities.

## Signed update intent and admission capabilities

A release pipeline declares whether a revision transition is forward or rollback and lists the
host-policy capabilities it needs:

```json
{
  "baseRevision": "build-102",
  "targetRevision": "build-101",
  "updateIntent": "Rollback",
  "requiredCapabilities": ["game.inventory-v2", "game.world-write"],
  "requiredTargetLabels": [
    { "name": "environment", "value": "production" },
    { "name": "shard", "value": "eu-2" }
  ]
}
```

`LuaPatchBundle.Create` sorts capability names before signing. Names are case-sensitive, must be
unique and trimmed, and are bounded by `MaximumCapabilityCount` and
`MaximumCapabilityNameBytes` when reading. A capability request is only an admission claim: matching
it never grants a Lua, CLR, filesystem, network, or other runtime permission.

Required target labels form a deterministic conjunction: every signed name/value pair must exactly
match a label assigned locally by the deployment control plane. Names are unique, case-sensitive,
sorted before signing, and bounded together with their values by the target-label read limits. This
prevents a valid patch for one environment, region, shard, platform, or ring from being admitted on
another target. It does not discover targets or grant permissions.

Treat policy labels as a stable target-identity snapshot for the lifetime of a prepared patch. If a
target changes environment, shard, platform, or ring before commit, discard the prepared patch and
prepare it again with the new labels.

## Trust and resource boundaries

`LuaPatchBundle.Read` verifies every payload hash and an ECDSA P-256/SHA-256 signature against an
explicit `LuaPatchEcdsaTrustStore`. It rejects untrusted keys, expired or non-canonical manifests,
unsafe paths, duplicate modules, missing required dependencies, trailing data, and size-limit
violations. `LuaPatchAcceptancePolicy` additionally binds a verified bundle to the current build,
runtime ABI, revision, channel, signed update intent, requested capabilities, required target labels,
verified signer, expiry, and host replay record. Because a prepared patch may wait for a later
game-loop safe point, commit checks the signed manifest expiry again before constructing or executing
any candidate. An expired commit returns `LuaPatchCommitStatus.Expired` without changing live state;
coordinated ring commits
apply the same check during barrier preparation.

Trusted keys can have an activation instant, an exclusive expiration instant, and an independent
revocation instant. The store validates and copies every P-256 public key at construction. Bundle
verification takes one `UtcNow` snapshot and applies it to both lifecycle evaluation and signature
verification, so a key cannot cross a rotation boundary between the two checks. Revocation takes
precedence over the scheduled window and fails with `SigningKeyRevoked`; inactive and retired keys
fail with `SigningKeyNotYetValid` and `SigningKeyExpired`.

```csharp
var trustStore = new LuaPatchEcdsaTrustStore([
    new LuaPatchTrustedEcdsaKey("release-2026-q3", q3PublicKey)
    {
        ValidFrom = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        ValidUntil = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero),
    },
    new LuaPatchTrustedEcdsaKey("release-2026-q4", q4PublicKey)
    {
        ValidFrom = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
    },
]);

using var stream = File.OpenRead("update.lpatch");
var bundle = LuaPatchBundle.Read(stream, trustStore);
```

The overlap permits a controlled rotation. Set `RevokedAt` to the incident cutoff on a compromised
key and distribute the updated trust configuration before accepting more patches. Lifecycle checks
use the verifier's current time, not the manifest's signer-controlled `CreatedAt`, so backdating a
new bundle cannot bypass retirement or revocation.

Production preparation should perform policy validation and replay recording as one host operation:

```csharp
var replayStore = new LuaPatchFileReplayStore("state/accepted-patches.ndjson");
var prepareOptions = new LuaPatchPrepareOptions
{
    AcceptancePolicy = new LuaPatchAcceptancePolicy
    {
        TargetBuild = currentBuild,
        CurrentRevision = currentRevision,
        RuntimeAbi = "lunil-0.12",
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
    ReplayScope = "state-a", // Stable target id; equals TargetId in coordinator rollouts.
};
```

`RevisionClassifier` adapts the host's release ledger; Lunil does not order application-defined
revision strings. Its classification must match the signed `UpdateIntent`. Every rollback also
requires a verified signer identity and an explicit `RollbackAuthorizer` decision, which lets a host
separate ordinary release keys from rollback keys. `LuaHost.PreparePatch` derives that identity from
the verified bundle signature rather than any manifest field. A denied capability, mismatched intent,
unauthorized rollback, or target-selector mismatch fails before a replay reservation is written.

Preparation requires `AcceptancePolicy`, `ReplayStore`, and `ReplayScope` together. The scope is a
stable deployment-target identity, not a process id. The same signed patch can therefore be reserved
for every target in a rollout, while reuse of either its patch id or nonce by another patch remains
blocked inside each target scope. `LuaPatchCoordinator` verifies that a prepared patch's scope equals
its `LuaPatchDeploymentTarget.TargetId`.

`ILuaPatchReplayStore.TryReserve` durably creates a reservation or returns the existing reservation
for the same uncommitted scoped identity. This idempotent result lets a restarted host repeat
preflight and live binding after losing its in-memory `LuaPreparedPatch`. Commit then calls
`TryAcquireCommit`; only one process can hold the returned lease and execute candidates. Disposing
an incomplete lease, including operating-system handle release after process exit, leaves the
reservation retryable. `Complete` makes it terminal, while transaction rollback compensates a
completed lease with `Reopen`. A policy mismatch or committed/conflicting identity returns
`LuaPatchPrepareStatus.AcceptanceRejected` with the precise `Acceptance` result, before any candidate
executes. `ReplayLookup` is only an advisory check for direct `Evaluate` calls and is deliberately
excluded from the durable reservation path.

`LuaPatchFileReplayStore` is the built-in durable option for processes that share a local replay
file. Every reservation state change takes a bounded inter-process writer lock, verifies the complete
canonical NDJSON sequence and SHA-256 hash chain, appends a `Reserved`, `Committed`, or `Reopened`
event, and flushes it to stable storage. Per-reservation operating-system locks serialize commit
ownership without serializing different rollout targets. `ReadAll()` returns the verified audit
events. Corruption, a truncated tail, lock timeout, or a configured identity/entry/byte limit fails
closed.
Events are never compacted automatically because deleting terminal identities reopens replay. For
targets that do not share this filesystem, implement the same reservation state machine and exclusive
commit lease with a shared transactional database.

## Dependency and compilation preflight

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

## Game-loop update windows and atomic commit

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

## State schema and resource migration

A bundle can carry one signed companion entry named `migration/schema.json`. The canonical JSON
document identifies a state schema, its base and target versions, and deterministic per-module state
and resource rules. Register the live schema version before preparation:

```csharp
host.SetPatchStateSchemaVersion("game-state", "42");
```

Place the canonical bytes returned by `LuaPatchMigrationSchemaSerializer.Serialize` at that companion
entry path before `lunil patch pack`. Supply every adapter named by the schema when preparing:

```csharp
var preparation = await host.PreparePatchAsync(bundle, prepareOptions with
{
    StateMigrationAdapters = stateAdapters,
    ResourceMigrationAdapters = resourceAdapters,
}, stoppingToken);
```

Preparation rejects a schema whose base version differs from the host value. Commit rechecks that
version with the module revisions and changes it to the schema target version only after the module
transaction succeeds.

State paths use RFC 6901 JSON Pointer escaping and address string-keyed tables below the module cache.
`Preserve` copies the previous value into the candidate, `Drop` removes the candidate value, and
`HostAdapter` delegates a table, userdata payload, or other host-defined transformation to a named
`ILuaPatchStateMigrationAdapter`. Adapter operations must journal `Apply` and `Rollback`; the commit
engine also journals every path write, so a later module failure restores both the Lua graph and host
payload mutation.

`PatchTable` retains the previous table object at the target path while atomically replacing its raw
entries and metatable with the candidate table's contents:

```csharp
new LuaPatchStateRule
{
    TargetPath = "/match/state",
    Kind = LuaPatchStateRuleKind.PatchTable,
}
```

Both source and candidate values must be tables. The retained identity remains valid for game-engine
registries, userdata references, and aliases outside `package.loaded`; removed entries disappear,
candidate entries and weak-table mode take effect, and compatible functions reachable only through
the detached candidate table or its metatable participate in function-version migration. The
transaction roots previous and candidate keys, values, metatables, and the detached candidate table
with Lua handles until publication or rollback is final. A health check may therefore run a full Lua
collection without losing the graph needed for exact rollback. Candidate weak entries retained by
that journal become collectible after the transaction finishes.

State-rule target paths must be disjoint: duplicate paths and ancestor/descendant pairs are rejected
when the canonical schema is serialized or read. Use one rule for an owning table rather than
separate rules for both that table and one of its descendants. `PatchTable` can be combined with the
module-level `PatchExistingTable` cache policy to preserve both the module cache table and selected
nested table identities.

Resource rules cover `Coroutine`, `Timer`, `EventSubscription`, and `Task`, with `Continue`, `Cancel`,
`Restart`, `Drain`, or `RejectIfActive` dispositions. For a runtime-owned coroutine, `Continue`
installs the previous thread at the same candidate state path, preserving its identity and suspended
execution state. This includes resumable native `Yielded` and `CallLua` activations: the descriptor,
invocation state, callback frame, and immutable Lua function versions remain isolated in the old
activation while the thread is admitted into the candidate generation. Without `Continue`, an old
module-owned thread becomes stale and `LuaInterpreter.Start`/`Resume` fail before entering the
scheduler. `RejectIfActive` rejects a non-terminal thread at that path. Reversible cancellation,
restart, and drain—and all host-owned timer, subscription, and task lifecycle changes—use a named
`ILuaPatchResourceMigrationAdapter`. Missing adapters fail preparation, before the update window.
Adapter `Prepare` methods must not mutate state; `Apply` must be exactly reversible by `Rollback`, and
operation disposal only releases journal resources.

```csharp
new LuaPatchResourceRule
{
    ResourceId = "match-loop",
    Kind = LuaPatchResourceKind.Coroutine,
    Disposition = LuaPatchResourceDisposition.Continue,
    StatePath = "/workers/matchLoop",
}
```

`LuaClrTimer` is also runtime-owned when stored as userdata at `StatePath`. For `Continue`, both module
versions create their timer at that path; commit transfers the previous remaining delay and dispatch
counters into the pending candidate timer. Publication then uses the candidate callback, period, and
catch-up policy without restarting the delay. `RejectIfActive` aborts while the previous timer still
has a scheduled tick. `Cancel`, `Restart`, and `Drain` continue to require a reversible adapter.

```csharp
new LuaPatchResourceRule
{
    ResourceId = "heartbeat",
    Kind = LuaPatchResourceKind.Timer,
    Disposition = LuaPatchResourceDisposition.Continue,
    StatePath = "/timers/heartbeat",
}
```

Candidate coroutines remain pending until the full barrier publishes. Execution, migration, barrier,
or health rollback restores the previous coroutine generation and makes candidate coroutines stale.
`LuaThread.IsPatchGenerationActive` provides per-thread admission state; monitor
`LuaHost.ActiveNativeContinuationCount`, `PendingNativeContinuationCount`,
`QuiescedNativeContinuationCount`, and `StaleNativeContinuationCount` for suspended native work.
Threads whose entry closure and creator are both outside a module generation are not fenced or
included in those gauges. Stale threads may still be closed so their Lua cleanup can run.

CLR delegates and event subscriptions created from module-owned Lua closures also participate in the
host transaction without a schema adapter. Publication makes previous-generation delegates stale,
activates candidate delegates, and detaches superseded event handlers. Any execution, migration,
barrier, or health failure reverses that switch: previous subscriptions are restored and candidate
callbacks are rejected before they can enter Lua. Monitor the bridge callback counters described in
the CLR interop guide when a game service registers timers or events through CLR delegates.

Module-owned `LuaClrTask` wrappers are fenced by that barrier as well. Awaiting an old task cannot
deliver a late result into the new module generation; candidate task results become externally
consumable only after the whole barrier publishes (the owning loader can consume them while staging),
and rollback restores the old task generation. This wrapper
fencing does not cancel the underlying operation, so use cancellation tokens or a resource migration
adapter when the external operation itself must stop, drain, or restart.

Host-polled module timers use the same barrier: old timers pause with their remaining delay,
candidate timers cannot dispatch before full publication, and rollback makes candidates stale while
restoring old schedules. Monitor `ActiveTimerCount`, `PendingTimerCount`, `QuiescedTimerCount`, and
`StaleTimerCount` on `LuaClrBridge`; dispatch due work through `LuaHost.DispatchClrTimers` only while
the state is idle.

Compatible closure slots and loader upvalues remain automatic: matching lexical identities and
upvalue layouts publish a successor generation while suspended frames retain the previous immutable
generation. State rules are applied before a candidate is staged for its dependents, so preserved
state is visible to later modules in dependency order.

## Multi-State barriers and ring rollout

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

## Durable deployment journal and recovery

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

## Resource budgets and observability

Verified input remains bounded after bundle decoding. `LuaPatchPrepareOptions.ResourceLimits` and
`LuaPatchCoordinatorOptions.ResourceLimits` accept a `LuaPatchResourceLimits` value. The defaults
allow at most 512 patch modules, a 1 MiB migration schema, 512 migration modules, 8,192 state rules,
8,192 resource rules, 65,536 aggregate table-patch journal entries, 16 rings, 256 targets per ring,
and 1,024 targets per rollout. Static bundle, schema, ring, and rollout limit violations throw
`LuaPatchResourceLimitException` before candidate execution or update-window acquisition. The table
journal bound is checked after candidate tables exist but before publication; it is shared by
`PatchTable` state rules and module-level `PatchExistingTable`, and an over-limit commit fails closed
with `MigrationFailed` or `CachePolicyFailed` while retaining the old graph. Bundle byte/entry limits,
migration limits, update-window and commit pause deadlines, journal byte/line/entry limits, and the
normal Lua execution budget form separate layers; increasing one does not disable the others.

Hot-update diagnostics use the stable `LuaPatchTelemetry.ActivitySourceName` and
`LuaPatchTelemetry.MeterName`, both `Lunil.Hosting.HotUpdate`. Activities are emitted for:

- `lunil.patch.prepare`
- `lunil.patch.commit`
- `lunil.patch.ring`
- `lunil.patch.rollout`
- `lunil.patch.recover`

Activities can carry patch, rollout, ring, transaction, module-count, target-count, status, and
error tags. Metrics use low-cardinality status tags and never include target ids, patch payloads, or
source text. The instruments are `lunil.patch.preparations`, `lunil.patch.commits`,
`lunil.patch.rings`, `lunil.patch.rollbacks`, `lunil.patch.recoveries`,
`lunil.patch.prepare.duration`, `lunil.patch.commit.pause.duration`, and
`lunil.patch.ring.duration`; duration units are milliseconds. Subscribe with standard .NET
`ActivityListener`/OpenTelemetry and `MeterListener`/OpenTelemetry metrics pipelines.

## CLI

```text
lunil patch pack manifest.json payload --output update.lpatch --private-key private.pem --key-id release-2026
lunil patch verify update.lpatch --public-key public.pem --key-id release-2026
lunil patch inspect update.lpatch --public-key public.pem --key-id release-2026
lunil patch dry-run update.lpatch --public-key public.pem --key-id release-2026
lunil patch diff base.lpatch update.lpatch --public-key public.pem --key-id release-2026
```

For production rotation, replace `--public-key` and `--key-id` on every verification action with a
bounded trust-store file. Public-key paths are relative to that file:

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
      "validFrom": "2026-10-01T00:00:00Z",
      "revokedAt": null
    }
  ]
}
```

```text
lunil patch verify update.lpatch --trust-store patch-trust.json
lunil patch dry-run update.lpatch --trust-store patch-trust.json
```

The schema rejects unknown properties, duplicate key ids, malformed/non-P-256 keys, empty validity
windows, and more than 1,024 keys. Private keys are read only by `pack`; verification and preflight
use public keys. The CLI does not download patches, manage a CDN, or store signing keys.
