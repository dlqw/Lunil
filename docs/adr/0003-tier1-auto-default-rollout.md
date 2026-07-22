# Tier 1 automatic compilation policy

Tier 1 accelerates verified Lua functions while retaining interpreter-equivalent behavior. `LuaJitPolicy.Auto` permits runtime selection for eligible functions; it does not require Tier 2 or loop OSR.

Compilation is bounded and cancelable. Plans are tied to their owner and module identity, and generated entries validate current frame state. Any capability check, guard, or compilation outcome that prevents compiled execution falls back to the canonical executor. Compilation failure is not a Lua program failure.

Hosts can select an interpreter-only policy. Tier choice never changes instruction budgets, hook behavior, error propagation, coroutine scheduling, garbage-collection visibility, source compatibility, or bytecode compatibility.
