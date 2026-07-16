# ADR 0012: Qualify guarded Tier 2 table and Lua-call fast paths

- Status: Accepted for the 0.8 development line
- Date: 2026-07-16
- Target: Lua 5.4.8, .NET 10
- Extends: [ADR 0005](0005-tier2-auto-default-rollout.md),
  [ADR 0011](0011-linear-numeric-regions.md)

## Context

ADR 0005 enabled only the exact-numeric Tier 2 code shape by default. Table access and Lua calls
remained scheduler or managed-helper boundaries, so table-heavy and call-heavy workloads could not
benefit from the same guarded specialization model. The 0.8 cross-runtime suite made these costs
visible against MoonSharp on every supported RID.

The runtime now has the invariants required to specialize these paths without weakening canonical
execution: versioned table storage and metatable state, owner-validated dense-array access, bounded
per-site caches, a frameless leaf-call ABI, exact instruction accounting, and explicit canonical-PC
deoptimization.

## Decision

Tier 2 may publish `GuardedSpecializedCil` when the profile contains a stable table PIC or a known
Lua-closure call target. `ExactNumericSpecializedCil` remains the code kind for methods whose
specializations are numeric only. `ManagedProfileProgram` remains opt-in through
`EnableTier2ManagedFallback=true` and is not a substitute for incomplete native emission.

Table sites use a bounded runtime cache keyed by the proven storage/metatable shape. A cache hit may
read or update a dense integer slot directly; a shape, owner, key, storage-version, or metamethod
assumption failure returns to the original canonical PC before any duplicated observable effect.
The slow path preserves normalization, array/hash migration, mutation versions, logical quota,
write barriers, and `__index`/`__newindex` behavior.

Dense array append/update helpers are restricted to tables without a metatable. Their edges are
therefore strong and use the logical heap's forward barrier, marking the inserted child directly
instead of re-graying and re-traversing the full growing array at a later incremental step. A
metatable-backed table, including any table that can acquire weak semantics through `__mode`,
fails the helper and retains the general backward-barrier path.

The compiled strong-table route combines existing-slot update and sequential append in one probe,
and common integer keys use the runtime value's internal exact-tag check before falling back to
generic Lua key normalization. This changes neither the public value ABI nor float/integer key
equivalence: keys without the integer tag still reach the canonical table operation.

Known Lua-call sites guard the closure identity and verified fixed-result window. Eligible leaf
calls use the frameless ABI and, when the scheduler state remains clean, continue in the same Tier 1
or Tier 2 method. Debug hooks, yieldable or protected calls, close/unwind state, finalizers, pending
GC work, result-window mismatch, and target changes retain the canonical scheduler path.

The outer guarded method may be composed with one or more ADR 0011 numeric-region delegates. The
plan is still classified as `GuardedSpecializedCil`; a region/outer transition is allowed only when
the returned PC matches the materialized frame PC and crosses the region boundary. Profile sites
whose PC is outside the current function or whose recorded opcode no longer matches are ignored
fail closed before optimization planning.

## Invariants and verification

- Guard failure resumes at the original canonical PC without replaying a completed table mutation
  or call.
- Dense access preserves logical heap accounting and GC barriers independently of pooled physical
  capacity.
- Incremental collection preserves newly inserted children without repeatedly traversing the same
  growing strong array; weak-table marking and clearing remain on the general mutation path.
- Frameless calls preserve exact instruction budgets, live roots, multi-result windows, hooks,
  finalizers, and coroutine/close restrictions.
- Runtime-site caches are method-owned, bounded, thread-safe, and do not retain module/state/closure
  owners after the compiled lifecycle ends.
- Combined guarded/numeric plans publish both the guarded code kind and nonzero numeric-region
  telemetry; tests exercise mutation, metamethod changes, concurrent owners, invalidation, GC, and
  scheduler fallbacks.

## Evidence

The complete win-x64 Release matrix ran nine engines, eight common-source workloads, and six
balanced rounds (432 validated samples). Auto/Tier 2 reached 1.655x/1.691x geometric means versus
MoonSharp, with every workload median above 1.05x and every paired CI95 lower bound above 1.00x.

Hosted run [29459923109](https://github.com/dlqw/Lunil/actions/runs/29459923109) then passed all six
RID jobs and the fail-closed aggregate: all 96 Auto/Tier 2 workload/RID gates passed, aggregate
MoonSharp-relative geometric means were 1.980x/1.979x, and the minimum paired medians remained
1.067x/1.054x.

## Consequences

- Stable table and known-call workloads can qualify for native Tier 2 without enabling the managed
  fallback.
- Numeric regions and guarded non-numeric sites coexist in one plan instead of forcing either
  optimization family back to Tier 1.
- Polymorphic, unsupported, metamethod-active, debug-sensitive, or scheduler-sensitive paths retain
  exact deoptimization and canonical execution.
- Persisted AOT does not inherit profile-guided guarded emission automatically; its performance
  work remains tracked separately.
