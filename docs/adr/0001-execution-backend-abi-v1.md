# Execution kernel and code-generation ABI

Lunil uses one resumable Lua scheduler for interpreted and compiled execution. The scheduler exclusively owns frames, coroutine transfer, native continuations, protected-call unwinding, close handlers, hooks, logical garbage collection, and host-boundary values.

A backend is a block executor beneath that scheduler. It receives the current `LuaThread` and `LuaFrame`, executes canonical instructions, and returns a typed exit at every scheduler-owned boundary. It never substitutes the CLR call stack for Lua activation state.

## Invariants

- The canonical PC identifies the next observable instruction.
- Instruction budgets, hooks, debug mode, and safepoints have identical semantics in every backend.
- Deoptimization resumes at the same PC without replaying effects.
- Artifacts bind to canonical IR and runtime ABI identities.
- Caches do not own states, frames, closures, or retired modules.
