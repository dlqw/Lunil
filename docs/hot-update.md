# Signed patch bundles

[简体中文](hot-update.zh-CN.md)

Lunil patch bundles package Lua module replacements for validation before a host publishes them.
The versioned canonical manifest records the target build, base and target revisions, Lua language
version, runtime ABI, channel, expiry, nonce, dependencies, and SHA-256 payload identities.

## Trust and resource boundaries

`LuaPatchBundle.Read` verifies every payload hash and an ECDSA P-256/SHA-256 signature against an
explicit `LuaPatchEcdsaTrustStore`. It rejects untrusted keys, expired or non-canonical manifests,
unsafe paths, duplicate modules, missing required dependencies, trailing data, and size-limit
violations. `LuaPatchAcceptancePolicy` additionally binds a verified bundle to the current build,
runtime ABI, revision, channel, and host replay record.

## Dependency and compilation preflight

`LuaPatchDependencyPlan` orders required dependencies before dependents and treats a cyclic strongly
connected component as one preparation group. `LuaPatchPreflight.Analyze` creates an isolated staging
host and validates source, binary-chunk, and host-decoded canonical-IR entries without modifying the
live `LuaHost`.

`LuaHost.PreparePatchAsync` performs that work on a worker thread and then briefly enters the live
host execution gate to capture an expected revision for every target module. Preparation succeeds
only when all target modules are already loaded, the language versions match, and every module has
a rollback-safe cache policy. No candidate loader is executed during preparation.

## Game-loop update windows and atomic commit

Open an update window between ticks or frames and commit the prepared patch on the same thread:

```csharp
var preparation = await host.PreparePatchAsync(bundle, cancellationToken: stoppingToken);
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
successful commit read the new closure-slot generation. This makes a frame/tick boundary a safe
default for games without rewriting frames that are already suspended.

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
var preparation = await host.PreparePatchAsync(bundle, new LuaPatchPrepareOptions
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

Resource rules cover `Coroutine`, `Timer`, `EventSubscription`, and `Task`, with `Continue`, `Cancel`,
`Restart`, `Drain`, or `RejectIfActive` dispositions. For a runtime-owned coroutine, `Continue`
installs the previous thread at the same candidate state path, preserving its identity and suspended
execution state; `RejectIfActive` rejects a non-terminal thread at that path. Reversible cancellation,
restart, and drain—and all
host-owned timer, subscription, and task lifecycle changes—use a named
`ILuaPatchResourceMigrationAdapter`. Missing adapters fail preparation, before the update window.
Adapter `Prepare` methods must not mutate state; `Apply` must be exactly reversible by `Rollback`, and
operation disposal only releases journal resources.

Compatible closure slots and loader upvalues remain automatic: matching lexical identities and
upvalue layouts publish a successor generation while suspended frames retain the previous immutable
generation. State rules are applied before a candidate is staged for its dependents, so preserved
state is visible to later modules in dependency order.

## Multi-State barriers and ring rollout

`LuaPatchCoordinator` coordinates multiple `LuaHost` states in one process. Every target in a
barrier ring must have a unique target id and host instance and must be prepared from the same
canonical patch manifest. The coordinator opens every update window before it prepares any commit
session, prepares every state before publication, and then publishes the complete ring. Failure in
window acquisition, preparation, publication, finalization, or the health gate rolls back every
participant in that ring. Coordinator operations are serialized process-wide to prevent conflicting
lock orders across coordinator instances.

Build a rollout from separately prepared host-bound patches:

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
                new("zone-canary", canaryHost, canaryPreparation.PreparedPatch!),
            ],
        },
        new LuaPatchRolloutRing
        {
            Name = "production",
            Targets =
            [
                new("zone-01", zone01Host, zone01Preparation.PreparedPatch!),
                new("zone-02", zone02Host, zone02Preparation.PreparedPatch!),
            ],
        },
    ],
};

var result = new LuaPatchCoordinator().Deploy(plan, new LuaPatchCoordinatorOptions
{
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

## Durable deployment journal and recovery

`LuaPatchFileJournal` writes canonical NDJSON records with a contiguous sequence and SHA-256 hash
chain. Each append uses one record write, write-through I/O, and a stable-storage flush before it
returns. The reader rejects torn records, non-canonical JSON, broken sequence or hash links, invalid
transaction phase transitions, changed transaction metadata, and configured byte, line, or entry
limit violations. The transaction phases are `Started`, `Prepared`, `Publishing`, and a terminal
committed, rolled-back, failed, or recovered phase.

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

After process restart, inspect transactions whose last durable phase is `Started`, `Prepared`, or
`Publishing`, reconcile the named targets with host-owned deployment state, and record the result:

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
8,192 resource rules, 16 rings, 256 targets per ring, and 1,024 targets per rollout. Exceeding a
limit throws `LuaPatchResourceLimitException` before candidate execution or update-window
acquisition. Bundle byte/entry limits, migration limits, update-window and commit pause deadlines,
journal byte/line/entry limits, and the normal Lua execution budget form separate layers; increasing
one does not disable the others.

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

Private keys are read only by `pack`; verification and preflight use a public key. The CLI does not
download patches, manage a CDN, or store signing keys.
