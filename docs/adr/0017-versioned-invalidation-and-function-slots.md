# ADR 0017: Quiesce JIT generations and publish immutable function versions

- Status: Accepted
- Date: 2026-07-17
- Target: Lua 5.4.8, .NET 10
- Supersedes: the deferred concurrent/versioned parts of ADR 0016

## Context

ADR 0016 deliberately limited module reload to an idle `LuaHost`. Replacing `package.loaded` and
invalidating the method cache did not answer three harder questions:

1. an already admitted Tier 1, Tier 2, or Loop OSR delegate could continue after cache removal;
2. an existing closure alias needed to follow compatible replacement code without changing its
   captured upvalue cells, while an active or suspended frame could not safely change canonical PC
   maps in the middle of execution;
3. an exact-module JIT profile could not be imported after `ModuleContentId` changed, even when a
   function's canonical structure had not changed.

Raw function IDs are dense compiler details rather than stable cross-version identities. Upvalue
counts alone are also insufficient: source kind/index and descriptor kind determine which cell a
canonical instruction reads. Finally, reload still cannot be a transaction over arbitrary Lua,
table, I/O, or host-service effects.

## Decision

### Module generation and strict quiescence

The dynamic JIT owns one generation coordinator per module content ID. Every installed Tier 1,
Tier 2, and Loop OSR entry records the generation in which it was published. A delegate must obtain
an active lease for that exact generation before entering generated code. Generated entry checks,
Loop OSR backedges, and ABI safepoints observe the bound token; a stale token returns
`BackendInvalidated` to the shared scheduler at a canonical PC.

Invalidation is a strict quiescence boundary:

1. advance the generation and reject further old-generation leases;
2. clear installed methods, completion state, and code-byte accounting under existing cache locks;
3. wait until all admitted old-generation leases exit;
4. reopen admission for methods published for the new generation.

`Invalidate`, `ClearCache`, and executor disposal use this rule. Compilation publication retains
the existing state/completion checks, so a result cleared by invalidation cannot publish late.
Concurrent execution may warm and publish new-generation code; the guarantee is that no old
delegate is executing when invalidation returns. Persisted and static AOT executors do not bind a
dynamic generation and continue to use exact artifact/module identity and their existing guards.

### Function slot, immutable version, and frame snapshot

Each Lua closure owns an atomic `LuaFunctionSlot`. The slot points to an immutable
`LuaFunctionVersion` containing its canonical module/function, generation, logical key, upvalue
layout fingerprint, and version-owned runtime caches. Public closure properties read the current
slot. A frame captures the current version before sizing registers or copying arguments and uses
that snapshot for its entire lifetime.

Consequently:

- new calls observe an atomically published compatible version;
- active and suspended frames continue their old instruction stream and canonical PC map;
- resuming a coroutine completes its old frame version, while later calls through the same alias
  use the new version;
- constant materialization, table-allocation hints, frameless warm state, debug metadata, and
  logical-GC traversal are selected from the captured version rather than a mutable closure view.

### Stable identity and compatible closure migration

`LuaFunctionIdentity` derives a logical key from lexical parents and sibling ordinals (`root/0/1`)
instead of dense function IDs. Its layout fingerprint covers every upvalue's name, source kind,
source index, and IR descriptor kind. A source edit that inserts or reorders lexical siblings can
move later ordinals; this is an intentionally conservative limitation, not a reason to guess.

After candidate execution succeeds, reload traverses old and candidate cache graphs through raw
table keys/values, metatables, closures, and upvalue values with reference-identity cycle guards.
Old slots are restricted to the old canonical module and candidate versions to the new module, so
a shared `_ENV` cannot feed an old alias back as its own replacement. A slot is updated only when
logical key and complete layout fingerprint match. Publication uses compare-exchange and preserves
the old closure object and all existing upvalue cells. Missing replacements, layout mismatches, and
concurrent updates are returned as structured `LuaFunctionMigrationResult` values. Incompatible
closures remain valid on old code.

Slot publication occurs after cache/record commit and old-module JIT invalidation. Failures before
that point retain the previous slot. Arbitrary effects performed by candidate Lua or a custom cache
policy remain outside transactional rollback, as in ADR 0016.

### Conservative cross-version PGO remapping

`LuaJitProfileRemapper` first deserializes and validates the payload against its source module. It
aligns functions by logical key and reuses a function profile only when all of the following match:

- parameter count and vararg mode;
- complete upvalue layout fingerprint;
- canonical instruction count;
- every instruction's `Opcode`, `A`, `B`, `C`, and `D` operands.

Constant payload changes are allowed because they do not move or reinterpret a profile site.
Changed, added, or unmatched target functions receive empty profiles; removed source functions are
reported and omitted. Lua call targets that refer to the source module are remapped by logical key
to the target module content ID and function ID. External targets and table shapes remain guarded
runtime observations. Malformed, wrong-source, checksum, schema, or ABI payloads reject the whole
operation.

## Consequences and limits

- Strict invalidation can block until an admitted generated delegate reaches a safepoint or exits;
  Loop OSR emits a generation check at every backedge to bound ordinary loop latency.
- Closure aliases in tables, metatables, globals, and upvalue-reachable graphs follow compatible
  code without heap-wide closure replacement.
- Frames never attempt cross-version PC remapping. This avoids changing a live activation's
  register, close, debug, or exception layout.
- Profile remapping is fail-closed per function and deliberately sacrifices reuse when lexical
  identity or canonical operands are ambiguous.
- No file watcher is built in. Watchers remain external adapters that establish host scheduling
  and call the explicit reload API.

## Verification

Tests cover active Tier 2 and Loop OSR invalidation barriers, stale-generation ABI observation,
post-invalidation recompilation, late compilation rejection, compatible alias migration with
preserved upvalues, incompatible-layout reporting, suspended-frame snapshots, shared-environment
candidate filtering, exact-structure profile reuse, operand mismatch rejection, dense-ID shifts,
self-call target rewriting, and malformed/wrong-source payload rejection.
