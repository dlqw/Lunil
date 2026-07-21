# ADR 0021: Bounded table/operand PICs in Tier 2

- Date: 2026-07-18
- Related: [issue #58](https://github.com/dlqw/Lunil/issues/58),
  [ADR 0011](0011-linear-numeric-regions.md),
  [ADR 0012](0012-guarded-table-call-fastpaths.md),
  [ADR 0017](0017-versioned-invalidation-and-function-slots.md),
  [ADR 0020](0020-generation-bound-direct-compiled-calls.md)

## Context

Tier 2 already specialized exact numeric operands and stable closure calls, but table-heavy loops
continued to execute a generic table operation for every access. Profile key kinds and canonical
`NewTable` definitions can identify a likely dense integer or interned string access, but neither is
a permanent Lua fact: a register can be reused, a table can acquire a metatable, a string field can
move during rehash, and `__index`/`__newindex` can change after compilation. Treating profile hints
as proofs would silently skip observable Lua behavior.

Rejecting every table operation from a numeric region was also too conservative. It forced sieve
and mixed array/string-field loops to materialize numeric induction and accumulator state for each
iteration even when the table identity was compiler-proven and every dynamic assumption could be
guarded before a side effect.

## Decision

Starting in `0.8.0-alpha.15`:

1. Tier 2 recognizes exact integer array keys, interned string fields, and exact primitive
   `boolean`/`nil`/`string` binary operands. Primitive equality and string relational operations
   use exact-kind guards; a mismatch executes the existing generic operation from the same
   canonical PC.
2. Table sites use a maximum of four weak entries. String-field entries are guarded by table,
   string identity, shape/storage/metatable versions, and an opaque existing-entry handle.
   Metatable-absence entries are guarded by metatable identity and content version. No entry owns a
   `LuaState`, table, string, metatable, closure, or retired backend generation.
3. Integer access performs one array/hash probe. A no-metatable set can update or append the dense
   array directly. The only sparse-to-array exception is an empty table receiving integer key `2`:
   it may create one leading nil slot, provided the key is not already present in the hash part.
   This bounded rule preserves the single-entry invariant and lets ordinary sieve initialization
   enter the array representation without general sparse growth.
4. A compiler-proven table identity is an exact token created by `NewTable`, propagated by `Move`,
   cleared by every register write, and retained at a CFG join only when all predecessors carry the
   same token. A table PIC may cache the table in a compiled-entry local only when this forward
   must-analysis supplies the token. Re-executing the defining `NewTable` clears that local.
5. A verified numeric region may contain an integer or interned-string table operation as a tagged
   side operation only when the exact table-definition token exists and the type solver proves the
   key representation. Numeric induction variables, arithmetic, comparisons, and accumulators stay
   in CLR locals. String constants used by such a region are materialized once per compiled entry.
6. Numeric-region table helpers do not reserve instructions or update the frame PC. The existing
   hot basic-block charge and cold one-instruction tail remain the only accounting mechanism. A
   key/table/metatable/result-kind guard failure rolls back the unexecuted block suffix, materializes
   live locals, and deoptimizes at the table instruction. A set performs no mutation until every
   guard has passed. Regions containing table side operations poll at most every 256 backedges.
7. Whole-function Tier 2 compilation waits for at least one completed Tier 1 invocation. Backedge
   hotness may request Loop OSR during the first long invocation, but it cannot publish a
   whole-function Tier 2 method from a partial function profile.
8. `TablePicHits` and `TablePicMisses` are low-overhead sampled estimates: the first observation is
   exact and steady-state events are aggregated in batches of 256. Custom counter sinks used by
   focused tests remain exact. Invalidations are always counted exactly. Benchmark and
   cross-runtime schemas publish all three fields.

## Consequences

- Dense integer, stable string-field, and primitive non-numeric operations avoid repeated generic
  dispatch without making profile evidence authoritative.
- Sieve and mixed table loops can retain numeric CLR locals across guarded table accesses while
  preserving exact budgets, GC safepoints, write barriers, ownership, and metamethod behavior.
- A cache miss is not a semantic deoptimization: bounded PIC population may use the generic raw
  table probe. A real guard failure still returns to the canonical operation before a side effect.
- Highly polymorphic keys, tables without an exact definition token, unsupported result kinds,
  and exhausted four-entry PICs remain on the existing conservative path.
- Hit/miss telemetry is suitable for rate and route diagnosis, not exact per-access billing.

## Rejected alternatives

- **Trust IR/profile key hints without a runtime guard:** rejected because Lua register values and
  metatables remain mutable.
- **Keep a strong table or string in the PIC:** rejected because compiled code would retain Lua heap
  owners and old generations.
- **Allow unbounded polymorphic entries:** rejected because unstable workloads could grow memory
  and compile work without limit.
- **Run table helpers with their own instruction reservation inside a numeric region:** rejected
  because it would double-charge hot blocks and move budget failures away from the canonical PC.
- **Deopt after a set and replay it canonically:** rejected because `__newindex`, barriers, and
  observable mutations could execute twice.
- **Use an unrestricted sparse-to-array heuristic:** rejected because it can duplicate hash/array
  keys, grow memory unexpectedly, and change table traversal behavior.
