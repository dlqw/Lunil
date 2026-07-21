# ADR 0020: Generation-bound direct compiled calls

- Date: 2026-07-18
- Related: [issue #64](https://github.com/dlqw/Lunil/issues/64),
  [ADR 0013](0013-64-bit-instruction-accounting.md),
  [ADR 0017](0017-versioned-invalidation-and-function-slots.md),
  [ADR 0019](0019-compact-tier0-interpreter-loop.md)

## Context

Tier 2 previously optimized a known Lua closure only until the `Call` instruction. Even a stable,
non-yielding integer leaf then materialized a callee frame, returned a `LuaCompiledExit` to the
shared scheduler, and re-entered compiled code after the return. Call-heavy loops consequently lost
their CLR numeric locals at every closure boundary and paid two scheduler exits per call. Optimizing
the call-site dictionary or the interpreter could not remove this structural cost.

The replacement must not turn profile evidence into an unchecked fact. Reload, function-version
publication, invalidation, debug hooks, exact instruction budgets, logical GC/finalizers, coroutine
boundaries, errors, varargs, upvalues, and result-window semantics remain observable. Compiled
delegates and inline caches also must not keep a `LuaState`, closure, module generation, or retired
backend entry alive.

## Decision

Starting in `0.8.0-alpha.14`:

1. Tier 2 may bind a monomorphic `KnownClosureCall` to a direct compiled entry only when module
   content ID, function ID, function generation, fixed argument/result shape, and the current weak
   closure/module cache agree. The cache stores module identities in a `ConditionalWeakTable` and
   closure/backend-entry references weakly. A cache hit never owns Lua heap objects.
2. The first supported callee class is a side-effect-free, non-vararg, no-upvalue integer leaf. It
   accepts integer constants, moves, fixed tops, integer unary/binary operations, jumps,
   integer numeric-for loops, and fixed returns. Unsupported instructions, open results, yielding,
   metamethod/native continuations, or shape mismatches fail before committing callee-visible state.
3. Two execution forms share one leaf IL generator:
   - a generation-leased trampoline executes directly against the caller result window and avoids
     callee-frame construction;
   - a numeric-region form maps caller integer locals to callee parameters and callee results back
     to caller locals, preserving unboxed values across the call.
   The numeric-region planner represents the function value as a tagged local but keeps proven
   numeric arguments/results unboxed.
4. Every inline entry resets its per-call instruction and backedge locals. This is required because
   Reflection.Emit locals are initialized once per caller invocation, while the inline body can be
   reached repeatedly from a Lua loop. Callee instruction cost is accumulated into the caller's
   64-bit pending/remaining accounting exactly once.
5. Hot-quantum and cold-slow-tail budget failures use separate rollback paths. The hot path removes
   the unexecuted call suffix; the cold path cancels its one-instruction reservation. Both
   materialize the caller at the original `Call` PC. A finalizer/GC safepoint refusal is a poll exit,
   not a type guard failure, and therefore cannot poison the Tier 2 guard-failure threshold.
6. Guard mismatch, debug-mode change, reload/generation mismatch, invalidation, pending finalizer,
   exact-budget exhaustion, and unsupported callee behavior return to the existing scheduler before
   callee state is committed. Runtime errors retain the canonical traceback and result-window state.
7. Invalidation closes the caller generation to new leases, waits for active callers (including a
   currently executing direct leaf), then retires caller/callee delegates and weak cache entries.
   Eviction, explicit cache clear, and disposal use the same owner graph. Reload requires both the
   content identity and current `LuaFunctionVersion.Generation`.
8. Code growth is fail-closed: at most 8 bound direct sites and 32 KiB of bound leaf IL per Tier 2
   caller; each leaf is limited to 128 canonical instructions, 32 registers, 8 parameters, 4
   results, and 1024 backedges per entry. Compilation reuses one delegate per callee function inside
   a caller plan and never recursively binds another call graph.
9. `LuaJitStatistics` and both benchmark schemas expose direct entries, completions, fallbacks,
   invalidations, and avoided scheduler exits. A completion accounts for the eliminated call and
   return scheduler boundaries; fallback and invalidation remain independently visible.

## Consequences

- Stable fixed-shape calls can remain inside one compiled numeric region and preserve CLR locals
  across the closure boundary.
- The scheduler remains the single fallback, unwind, hook, coroutine, GC, and exact-budget oracle;
  the direct emitter does not copy those semantics.
- Direct-call caches remain generation-bound and owner-weak. Recreating an equivalent no-upvalue
  closure from the same live module does not force recompilation, while reload or function-version
  replacement cannot execute retired code.
- The initial leaf subset intentionally excludes upvalues, varargs, open results, allocation,
  tables, metamethods, native continuations, and nested direct-call graphs. Those cases retain the
  ordinary scheduler path rather than receiving partial semantics.

## Rejected alternatives

- **Guard only by function ID:** rejected because content collisions across modules and function
  generation replacement would permit stale code after reload.
- **Keep strong closure or backend-entry references:** rejected because a hot caller would retain a
  `LuaState`, old module generation, or retired code indefinitely.
- **Inline arbitrary Lua functions:** rejected because yield, upvalue, metamethod, allocation, and
  open-result semantics would duplicate the scheduler and create unbounded code growth.
- **Charge a maximum callee cost before entry:** rejected because it would move exact instruction-
  budget failures and overcharge short control-flow paths.
- **Treat GC/finalizer refusal as a guard failure:** rejected because routine safepoints would
  eventually invalidate an otherwise stable Tier 2 plan.
