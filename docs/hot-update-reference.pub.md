# Signed patch bundle reference

[简体中文](hot-update-reference.zh-CN.pub.md)

This reference defines Lunil's signed patch manifest, trust and replay contracts, preparation and
commit resources, migration rules, rollout state, durable stores, and telemetry. For operational
steps, see [How to deploy signed patch bundles](hot-update.pub.md).

## Canonical manifest and target admission

The versioned manifest records target build, base and target revisions, `updateIntent`, requested
`requiredCapabilities`, Lua language version, runtime ABI, channel, expiry, nonce,
`requiredTargetLabels`, dependencies, entry identities, and SHA-256 payload hashes.

| Field or identity | Validation |
| --- | --- |
| Capabilities | Case-sensitive, trimmed, unique, sorted before signing, and bounded by count/name-byte limits. They are admission claims and grant no runtime permission. |
| Target labels | Case-sensitive unique name/value pairs, sorted before signing and evaluated as an exact-match conjunction. |
| Update intent | Must match the host release ledger's forward/rollback classification. |
| Rollback | Requires both a verified signer identity and an affirmative `RollbackAuthorizer` decision. |
| Expiry | Checked during verification and again immediately before candidate construction, including coordinated commits. |
| Paths and entries | Unsafe paths, duplicate modules, missing required dependencies, trailing data, and configured size-limit violations are rejected. |

`LuaPatchAcceptancePolicy` binds a verified bundle to current build, runtime ABI, revision, channel,
intent, granted admission capabilities, target labels, signer, expiry, and replay record.

## Trust-store contract

`LuaPatchBundle.Read` verifies every payload hash and an ECDSA P-256/SHA-256 signature against an
explicit `LuaPatchEcdsaTrustStore`. A trusted key may have `ValidFrom`, exclusive `ValidUntil`, and
independent `RevokedAt` instants. Revocation takes precedence; failures are
`SigningKeyRevoked`, `SigningKeyNotYetValid`, or `SigningKeyExpired`. Verification uses one
`UtcNow` snapshot for lifecycle and signature checks.

The CLI trust-store schema is `lunil.patch-trust.v1`. It rejects unknown properties, duplicate key
ids, malformed or non-P-256 keys, empty validity windows, and more than 1,024 keys. Public-key paths
are relative to the trust-store file. Private keys are read only by `patch pack`.

## Replay reservation state

Preparation requires `AcceptancePolicy`, `ReplayStore`, and `ReplayScope` together. A scope is a
stable deployment-target id. `ILuaPatchReplayStore.TryReserve` creates or returns an idempotent
uncommitted reservation. `TryAcquireCommit` grants one exclusive commit lease; incomplete lease
disposal leaves the reservation retryable, `Complete` makes it terminal, and rollback compensation
uses `Reopen`.

`LuaPatchFileReplayStore` appends canonical, SHA-256-chained `Reserved`, `Committed`, and `Reopened`
NDJSON events under bounded inter-process writer and per-reservation locks. Corruption, truncated
tails, lock timeout, and identity/entry/byte-limit violations fail closed. Events are not compacted
automatically because deleting terminal identities reopens replay.

## Preparation

`LuaPatchDependencyPlan` orders dependencies before dependents and treats a cyclic strongly
connected component as one group. `LuaPatchPreflight.Analyze` verifies source, binary chunk, and
host-decoded canonical IR in an isolated staging host. `PreparePatchAsync` also captures expected
module revisions under the live execution gate; target modules must be loaded, language versions
must match, and cache policies must support rollback. Candidate loaders do not execute.

`LuaPatchPreparationLimiter` bounds active preflights and queued callers. A zero queue is fail-fast.
The wait timeout may be zero, finite up to `Int32.MaxValue` milliseconds, or
`Timeout.InfiniteTimeSpan`. Saturation or timeout returns `Deferred` before preflight, live binding,
or replay reservation. `ActiveCount` and `QueuedCount` are gauges.

## Result status enums

### Preparation

| `LuaPatchPrepareStatus` | Meaning |
| --- | --- |
| `Ready` | The isolated candidate is bound to the expected live revisions and is ready to commit. |
| `PreflightFailed` | Isolated parsing, verification, compilation, or dependency preflight failed. |
| `LanguageVersionMismatch` | A target module and replacement use different Lua language versions. |
| `ModuleNotLoaded` | A target module is absent from the live host. |
| `UnsupportedCachePolicy` | A target cache policy cannot participate in rollback-safe publication. |
| `MigrationAdapterMissing` | A migration schema names an unavailable required adapter. |
| `StateSchemaVersionMismatch` | The live schema version differs from the signed base version. |
| `AcceptanceRejected` | Trust, policy, target, intent, expiry, signer, or replay acceptance rejected the bundle. |
| `Deferred` | Preparation admission was saturated or timed out before candidate work began. |
| `JitWarmupFailed` | Required-success candidate JIT warmup did not complete successfully. |

| `LuaPatchPreparationAdmissionStatus` | Meaning |
| --- | --- |
| `NotConfigured` | No shared preparation limiter was configured. |
| `Acquired` | The caller acquired a preparation slot. |
| `Saturated` | The limiter queue had no capacity. |
| `TimedOut` | The caller did not acquire a slot within the wait timeout. |

### Update window and module commit

| `LuaPatchUpdateWindowStatus` | Meaning |
| --- | --- |
| `Opened` | The same-thread update window owns the host execution gate. |
| `Deferred` | The gate was not acquired within the configured wait budget. |
| `Cancelled` | Window acquisition observed cancellation. |

| `LuaPatchCommitStatus` | Meaning |
| --- | --- |
| `Committed` | All target-module changes were published. |
| `Deferred` | Commit could not proceed within the current safe-point budget. |
| `Cancelled` | Commit observed cancellation and retained or restored the previous graph. |
| `RevisionConflict` | A live target revision no longer matches its prepared revision. |
| `ExecutionFailed` | A candidate loader failed. |
| `MigrationFailed` | State or resource migration failed. |
| `CachePolicyFailed` | Cache publication or table-patch policy failed. |
| `PublicationFailed` | Final managed-graph publication failed. |
| `BarrierAborted` | A coordinated barrier aborted the local publication session. |
| `Expired` | The signed manifest expired before candidate execution. |
| `ReplayRejected` | Durable replay acceptance or commit ownership rejected the transaction. |

| `LuaPatchModuleCommitStatus` | Meaning |
| --- | --- |
| `NotExecuted` | The module loader was not run. |
| `RevisionConflict` | This module's live revision changed after preparation. |
| `Executed` | The candidate loader completed but publication is not yet terminal. |
| `Committed` | This module was published. |
| `ExecutionFailed` | This module's candidate loader failed. |
| `MigrationFailed` | This module's migration failed. |
| `CachePolicyFailed` | This module's cache publication policy failed. |
| `RolledBack` | A previously staged or published module was restored to its previous graph. |

### Ring and target lifecycle

| `LuaPatchRingCommitStatus` | Meaning |
| --- | --- |
| `Committed` | The complete ring passed publication and all configured gates. |
| `Deferred` | The ring or distributed participant was not selected or could not proceed yet. |
| `Cancelled` | Ring coordination observed cancellation. |
| `PrepareFailed` | At least one local commit session could not be prepared. |
| `PublishFailed` | At least one target failed during publication. |
| `HealthRejected` | The application health gate rejected the published candidate ring. |
| `JournalFailed` | Durable deployment-journal mutation failed. |
| `ReplayFailed` | Replay acceptance or completion failed. |
| `IsolationFailed` | Target traffic isolation failed or was unavailable. |
| `QuiescenceFailed` | In-flight target work did not reach quiescence. |
| `RestoreFailed` | Target traffic restoration failed; the target must remain isolated. |
| `CoordinationFailed` | Local or distributed ring coordination failed. |
| `GenerationRejected` | A generation-retention snapshot violated the configured guard. |

| `LuaPatchTargetLifecycleStatus` | Meaning |
| --- | --- |
| `NotConfigured` | No target lifecycle adapter was configured. |
| `Isolated` | New target traffic was stopped. |
| `Quiescent` | Existing target work drained. |
| `Restored` | Traffic routing was restored after commit or rollback. |
| `IsolationDeferred` | Isolation requested a later retry. |
| `IsolationCancelled` | Isolation observed cancellation. |
| `IsolationFailed` | Isolation failed. |
| `QuiescenceDeferred` | Quiescence requested a later retry. |
| `QuiescenceCancelled` | Quiescence observed cancellation. |
| `QuiescenceFailed` | Quiescence failed. |
| `RestoreFailed` | Restoration failed. |

Adapter-level statuses are complete as follows: `LuaPatchTargetIsolationStatus` has `Isolated`,
`Deferred`, `Cancelled`, and `Failed`; `LuaPatchTargetQuiescenceStatus` has `Quiescent`, `Deferred`,
`Cancelled`, and `Failed`; `LuaPatchTargetRestoreStatus` has `Restored` and `Failed`; and
`LuaPatchTargetRestoreOutcome` has `Committed` and `RolledBack`.

## Commit and cache policies

An update window retains the host execution gate. Commit rechecks expiry and every expected
revision, evaluates candidates dependency-first through a temporary `package.loaded` overlay, and
publishes cache values, module records, table-identity patches, compatible closure slots, and JIT
generations together.

Supported atomic cache policies are `ReplaceCache` and `PatchExistingTable`. Opaque `Custom`
callbacks and source-path overrides are rejected during preparation. Pause and cancellation checks
occur between loaders and publication steps; one loader is bounded by the ordinary Lua instruction
budget rather than preempted mid-call. Cyclic component members execute in deterministic name order.

## State migration rules

The optional canonical companion entry is `migration/schema.json`. It names base and target schema
versions and per-module rules. State paths use RFC 6901 JSON Pointer escaping and must be disjoint;
duplicate and ancestor/descendant pairs are rejected.

| State rule | Contract |
| --- | --- |
| `Preserve` | Copies the previous value into the candidate. |
| `Drop` | Removes the candidate value. |
| `PatchTable` | Retains the previous table identity while replacing raw entries and metatable from the candidate table. Both values must be tables. |
| `HostAdapter` | Calls a named reversible `ILuaPatchStateMigrationAdapter` for host-defined transformation. |

The commit journal roots previous and candidate keys, values, metatables, and detached candidate
tables until publication or rollback becomes final. Aggregate table journal entries are bounded.

## Resource migration rules

Resource kinds are `Coroutine`, `Timer`, `EventSubscription`, `Task`, and `HostResource`.
Dispositions are `Continue`, `Cancel`, `Restart`, `Drain`, and `RejectIfActive`.

| Combination | Runtime behavior |
| --- | --- |
| `Coroutine + Continue` | Installs the previous suspended thread at the candidate path with its immutable old activation and generation admission. |
| `Timer + Continue` | Transfers remaining delay and dispatch counters into the pending candidate timer; candidate callback and policy apply after publication. |
| `HostResource + Continue` | Installs the previous stable-resource userdata into the candidate graph, preserving identity and ownership. |
| `RejectIfActive` | Rejects a live coroutine, scheduled timer, or stable resource with explicit leases, as applicable. |
| `Cancel`/`Restart`/`Drain` | Requires a named reversible resource adapter for application-defined external effects. |

Adapter `Prepare` must not mutate state; `Apply` must be exactly reversible by `Rollback`. Missing
adapters fail preparation. Stable-resource member calls and subscriptions hold leases; owned
resource disposal waits for the final lease.

## Generation snapshots and guard policy

`LuaHost.CapturePatchGenerationSnapshot()` reports callback, task, timer, and suspended-native-
continuation counts in `Active`, `Pending`, `Quiesced`, and `Stale` states, plus aggregate counts,
`HasTransitionResidue`, `HasStaleResources`, `ObservedAt`, and `UpdateInProgress`. Stale means still
referenced but rejected by generation admission; it is not by itself proof of a leak.

`LuaPatchGenerationGuardPolicy` provides per-kind stale budgets and rejects pending or quiesced
residue by default. `Strict` sets stale budgets to zero. A rejection returns `GenerationRejected`
and rolls local ring targets back. The guard does not force collection, cancel host tasks, close
external resources, or undo arbitrary candidate side effects.

## Coordinator and distributed barrier

`LuaPatchCoordinator` serializes process-wide coordinator operations. Each ring requires unique
target ids and host instances prepared from one canonical manifest. It isolates targets, waits for
quiescence, opens every update window, prepares every commit session, publishes the ring, runs the
health gate, and restores traffic. A failure through health evaluation rolls the ring back. Rings
run in order; an accepted earlier ring remains committed if a later ring fails.

`ILuaPatchTargetLifecycle` provides `TryIsolate`, `WaitForQuiescence`, and idempotent `Restore`.
`RequireTargetIsolation` rejects missing adapters before journaling. `RestoreFailed` leaves the
target isolated and the journal in `Restoring` for recovery.

A distributed barrier pins rollout id, ring name, participant membership, required quorum,
canonical manifest SHA-256, target revision, and preparation/health deadlines. Prepared
acknowledgements select exactly the quorum and produce `Apply`; selected participants acknowledge
`Healthy` after local publication, application health, and replay acceptance. All selected healthy
participants produce immutable `Commit`; selected failure or deadline produces immutable
`Rollback`. Non-selected processes return `Deferred` and retain the old generation.

`LuaPatchFileDistributedBarrierStore` requires exclusive locks and atomic same-directory rename.
Its SHA-256 detects accidental corruption, not hostile rewriting. Terminal states are pruned
explicitly; waiting or apply state is never pruned.

## Deployment journal and history

`LuaPatchFileJournal` stores canonical, contiguous, SHA-256-chained NDJSON phases:
`Started`, `Prepared`, `Publishing`, optional `Restoring`, and a terminal committed, rolled-back,
failed, or recovered phase. A writer holds `<journal>.writer.lock` for its lifetime. Readers may run
concurrently and retry transient replacement/tail conditions for `ConcurrentReadTimeout`.

Compaction retains every incomplete transaction and the configured most recent completed
transactions, renumbers and re-hashes records, flushes a same-directory temporary file, and
atomically replaces the journal. `OriginalTailHash` can be anchored externally. A hash chain is
corruption evidence, not authentication.

`RecoverIncomplete` asks `ILuaPatchCrashRecoveryHandler` for `Committed`, `RolledBack`, or `Manual`.
The journal records deployment intent and resolution; it does not serialize Lua heaps, suspended
frames, CLR objects, or external resources.

`LuaPatchHistory` is bounded volatile health history with a capacity of 1 through 10,000. Snapshots
are oldest-to-newest and include total, dropped, recording-failure, consecutive-unsuccessful, latest
committed/unsuccessful timestamps, and stable rollout/ring/target outcome fields. Raw exceptions,
messages, module records, Lua values, payloads, and heap graphs are omitted.

## JIT warmup

`LuaPatchJitWarmupOptions` optionally remaps compatible old-module profiles to candidate functions
and compiles by descending hotness during preparation. It does not create closures, run loaders,
mutate live state, or enter the update window. Total and per-module function/duration budgets apply.

`BudgetLimited` is a successful bounded outcome; deadline expiry is `TimedOut`.
`BestEffort` preserves a ready patch after compilation/deadline failure, while `RequireSuccess`
returns `JitWarmupFailed`. Interpreter and dynamic-code-disabled hosts return `NotApplicable`.
Compiled code uses the existing bounded content-addressed JIT cache.

## Default resource limits

Defaults allow 512 patch modules, a 1 MiB migration schema, 512 migration modules, 8,192 state
rules, 8,192 resource rules, 65,536 aggregate table-patch journal entries, 16 rings, 256 targets per
ring, and 1,024 targets per rollout. Bundle, schema, ring, and rollout violations fail before
candidate execution or update-window acquisition. Other byte, journal, pause, and Lua execution
budgets remain independent.

## Telemetry

`LuaPatchTelemetry.ActivitySourceName` and `.MeterName` are both
`Lunil.Hosting.HotUpdate`.

Activities: `lunil.patch.prepare`, `lunil.patch.commit`, `lunil.patch.ring`,
`lunil.patch.rollout`, and `lunil.patch.recover`.

Metrics: `lunil.patch.preparations`, `lunil.patch.commits`, `lunil.patch.rings`,
`lunil.patch.rollbacks`, `lunil.patch.recoveries`, `lunil.patch.prepare.duration`,
`lunil.patch.commit.pause.duration`, and `lunil.patch.ring.duration`. Duration units are
milliseconds. Status tags are low-cardinality and omit target ids, payloads, and source text.

## CLI commands

```text
lunil patch pack manifest.json payload --output update.lpatch --private-key private.pem --key-id release-2026
lunil patch verify update.lpatch --trust-store patch-trust.json
lunil patch inspect update.lpatch --trust-store patch-trust.json
lunil patch dry-run update.lpatch --trust-store patch-trust.json
lunil patch diff base.lpatch update.lpatch --trust-store patch-trust.json
```

The CLI does not download patches, manage a CDN, or store signing keys.
