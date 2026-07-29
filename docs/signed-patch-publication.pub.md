# How hot-update publication works

[简体中文](signed-patch-publication.zh-CN.pub.md)

Lunil separates expensive preparation from a short, atomic publication window. This page explains
the transaction, generation, migration, rollout, and durability model behind signed patch bundles.
Use the [deployment guide](deploy-signed-patch-bundles.pub.md) for procedures and the
[patch reference](signed-patch-bundles.pub.md) for exact contracts.

## Verification is not permission

A valid signature proves bundle integrity and signer identity. It does not grant Lua, CLR,
filesystem, network, or deployment permission. Acceptance therefore evaluates the verified bundle
against current build, runtime ABI, revision ledger, channel, target labels, admission capabilities,
rollback authorization, expiry, and replay identity.

Target labels bind a bundle to the control plane's current environment, region, shard, platform,
or ring assignment. Treating them as a snapshot prevents a patch prepared for one identity from
being committed after that identity changes. Key lifecycle checks use verifier time rather than
signer-controlled creation time, preventing backdating from bypassing retirement or revocation.

## Preparation narrows the publication window

Dependency planning, parsing, compilation, chunk verification, migration-schema validation, and JIT
warmup can consume CPU and memory. Lunil performs them in an isolated staging host, then briefly
enters the live execution gate only to bind expected module revisions. Candidate loaders are not
run during preparation.

This makes the update window responsible for only the state-sensitive work: expiry and revision
rechecks, candidate execution, migration, publication, health decisions, and rollback. A shared
preparation limiter adds backpressure before expensive work or replay reservation, allowing the
control plane to retry with coordinated jitter instead of overloading every target at once.

## Publication is atomic inside the managed module graph

The update window excludes ordinary host execution while candidates run in dependency order through
a temporary module-cache overlay. Completed dependency candidates are visible to later candidates;
an unresolved back-edge in a cycle sees the old loaded value. Publication switches module records,
cache values, table identities, compatible closure slots, and JIT generations together.

On failure, Lunil restores the managed module graph, including journaled table contents, metatables,
loader upvalues, and compatible closure slots. The atomic boundary cannot generally reverse a
candidate's calls into application services, CLR objects, filesystems, or networks. That is why a
failed commit can report `SideEffectsMayHaveOccurred` even when Lua state was restored.

Suspended frames keep the immutable function generation captured on entry. New calls after
publication resolve the successor generation. Resumable module-owned coroutines require explicit
generation admission because they can re-enter later; ordinary in-flight frames can finish on their
captured generation without becoming new resumable work.

## Migration preserves selected identity

Copying values is not enough when registries or external aliases depend on object identity.
`PatchTable` keeps the old table object and replaces its entries and metatable from the candidate,
while the transaction roots both graphs until rollback is impossible. Disjoint paths avoid
ambiguous ownership between a table rule and its descendants.

Runtime resources need lifecycle decisions rather than value copies. A continued coroutine keeps
its suspended old activation but becomes admitted into the candidate graph. A continued timer
transfers delay and counters into a pending candidate timer so future callbacks use candidate code.
A stable host resource transfers one userdata identity and owner while leases protect in-flight
application work.

Adapters are required when cancellation, restart, drain, or transformation has application-specific
external effects. Separating non-mutating `Prepare` from reversible `Apply`/`Rollback` lets the
module transaction compensate later failures.

## Generation fencing closes late-entry races

Callbacks, subscriptions, tasks, timers, and suspended native continuations created by module code
belong to that module generation. Preparation quiesces previous resources and leaves candidates
pending. Full publication activates candidates and makes previous resources stale; rollback reverses
that admission decision.

This prevents an old callback or late task result from entering newly published state. Admission is
checked at the point of entry and, for `clr.await`, again before result conversion. The underlying
host task is not automatically cancelled because admission to Lua and cancellation of application
work are different contracts.

Generation snapshots distinguish pending, quiesced, and stale resources. A stale reference is not
automatically a leak, but continued growth across patches indicates that application owners are not
releasing old resources. Guard budgets turn that operational policy into a pre-acceptance rollback
decision without forcing collection or cancelling external work.

## Rings combine isolation, publication, and health

A coordinator stops new traffic, waits for in-flight work to drain, acquires every target window,
prepares every commit session, publishes the complete ring, evaluates application health and
generation retention, then restores traffic. Holding rollback sessions until the decision keeps
all targets in one ring aligned.

Rings are sequential rather than one global transaction. An accepted canary remains committed if a
later production ring fails. Restoration is a separate lifecycle phase: if routing restoration
fails after publication, the target remains isolated and durable recovery must finish before it can
serve traffic.

Across processes, a distributed barrier first pins membership, quorum, manifest identity, revision,
and deadlines. A prepared quorum receives `Apply`; those selected processes keep rollback sessions
until every selected participant reports healthy. The resulting `Commit` or `Rollback` is immutable.
Processes outside the quorum remain on the old generation. Once distributed `Commit` exists, one
participant must not unilaterally roll its generation back because peers are already committed.

## Three durable stores solve different problems

The replay store prevents patch-id or nonce reuse for one deployment target and provides exclusive
commit ownership across process restart. The deployment journal records the phases and outcome of a
rollout transaction so routing and application state can be reconciled after a crash. The
distributed-barrier store records a shared multi-process decision.

Their hash chains detect torn writes and accidental corruption but do not authenticate storage
against an actor able to rewrite whole files. Operating-system permissions, lock-correct storage,
atomic rename, stable flush behavior, replication, and external hash anchoring remain deployment
responsibilities.

The journal deliberately does not serialize a Lua heap, CLR object, suspended frame, or external
resource. Crash recovery must consult application-owned durable state and routing state before it
declares a transaction committed or rolled back.

## Warmup and history remain bounded

Profile-remapped JIT warmup can reduce post-publication latency without executing candidate code.
Only lexically and canonically compatible functions inherit observations, and both per-module and
whole-patch budgets bound compilation. Best-effort warmup trades completeness for availability;
required-success warmup trades availability for a fully warmed candidate.

Rollout history stores terminal summaries rather than live module records or Lua graphs. This keeps
health endpoints bounded and prevents observability from extending candidate lifetimes. Durable
audit and crash recovery remain the journal's responsibility.
