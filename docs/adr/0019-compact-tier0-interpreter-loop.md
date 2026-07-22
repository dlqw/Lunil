# Compact Tier 0 interpreter loop

The Tier 0 interpreter reduces dispatcher overhead while keeping one canonical opcode implementation and scheduler contract. It continues in the current frame only while the prior instruction produced `Continue` and no scheduler-owned condition is pending.

`ExecuteSingleInstruction` remains the authoritative opcode switch. Calls, returns, budget polls, errors, close handling, continuations, backend entry, debug changes, hooks, and forced results return through the shared scheduler.

The loop consumes exactly the canonical instruction count and retains per-instruction observability for single stepping and hooks. It does not duplicate opcode semantics or hide write barriers, allocations, or safepoints. The interpreter remains the reference executor when dynamic code is unavailable.
