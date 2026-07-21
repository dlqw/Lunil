# ADR 0011: Share linear unboxed numeric regions across Tier 2 and Loop OSR

## Context

The existing exact-numeric Tier 2 and Loop OSR methods still dispatched canonical PCs, wrote the
frame PC, called budget/runtime helpers, and operated on tagged `LuaValue` registers in hot loops.
CoreCLR therefore saw a compiled interpreter rather than the straight-line numeric CIL shape it can
optimize. A one-block pattern matcher would improve a benchmark but would not preserve nested,
multi-latch, side-exit, captured-local, `SetTop`, exact-budget, and deoptimization semantics.

## Decision

Tier 2 and Loop OSR use one verified numeric-region pipeline:

1. Discover reducible natural loops from basic blocks, predecessors, dominators, and verified
   backedges. Merge all latches for the same header into the maximal region.
2. Prove exact CLR representations per reaching definition, not per physical register. Reject
   conflicting joins, unknown truthiness, cleared values, and unsupported instructions.
3. Emit versioned `long`, `double`, and `bool` locals. Track each physical register with dirty and
   active-kind locals so every boundary reconstructs the canonical frame exactly.
4. Cut verified backedges when planning instruction budgets. For every canonical PC, record its
   basic-block cost, exact deoptimization PC, semantic-failure rollback, cold slow-tail entry, and
   maximum instruction cost to the next safepoint or region exit. Reject a region if cutting
   backedges does not produce an acyclic budget graph.
5. Admit a hot quantum only when the remaining budget conservatively covers the current path plus
   every possible segment until the bounded backedge countdown expires. The qualified hot path
   charges actual work once per basic-block entry and emits no per-instruction budget comparison.
   Insufficient budget enters an independent cold slow tail that reserves one instruction at a time,
   preserving every exact budget PC, budget-before-tag-guard ordering, and failing-instruction
   rollback.
6. Poll backedges with a bounded local countdown (at most 1024). Before a poll or any other boundary,
   materialize dirty registers, captured values, frame top, canonical PC, exact instruction cost,
   and Loop OSR logical-backedge deltas. Only a `None` poll result may resume the numeric region;
   debug, GC, finalizer, budget, close, or unwind work returns to the scheduler.
7. Publish region count, unboxed numeric-local count, direct numeric-instruction count, static
   safepoint-site count, and hot instruction-budget-check count in both plan types. Arithmetic
   evidence requires the first four to be nonzero and the last to be zero; cold slow-tail checks are
   intentionally excluded from the hot-path count.

The Tier 2 process preparation compiles a representative natural loop so the first production
compile event does not include one-time CLR JIT costs for the new analyzer/planner/emitter. The
runtime Loop OSR qualification boundary remains lazy.

## Allocation gate

The former 64 KiB Loop OSR compile-allocation bound described the compact single-block emitter. A
CFG region deliberately owns reaching-definition states, versioned type maps, multiple CLR locals,
exact exit stubs, and `DynamicMethod` metadata. Keeping the old number would reject every valid
region and silently select the legacy emitter.

The replacement is not an unbounded relaxation: the runner requires Loop OSR arithmetic compile
allocation below 192 KiB and Tier 2 below 256 KiB, and independently compiles 1-operation and
8-operation regions with an allocation-growth slope below 32 KiB per added direct numeric
instruction. Compile p95 remains below 10 ms, execution allocation slope remains effectively zero,
the four structural plan telemetry fields must all be nonzero, and the hot instruction-budget-check
count must be zero. This catches fixed-cost explosions, super-linear region growth, and accidental
reintroduction of a per-instruction hot-path budget branch.

## Consequences

- Hot numeric bodies expose direct CIL arithmetic and unboxed locals to CoreCLR on every supported
  architecture.
- Exact budget PCs, Lua integer overflow/division rules, NaN/negative zero, side exits, captured
  upvalues, and GC/finalizer polls retain canonical behavior.
- Invalidation is a registry publication boundary, not an interrupt injected by a numeric-region
  poll. A delegate acquired before invalidation may finish its current invocation. Dispatch after
  invalidation cannot enter that old Tier 2/Loop OSR method, and an asynchronous result compiled
  across invalidate, clear, or dispose cannot be published into the invalidated generation.
- Unsupported or conflicting outer regions may fall back to a proven inner region; they never
  install a partial unverified region.
- Dynamic-code-unavailable runtimes and persisted AOT behavior are unchanged.
