# Unified numeric specialization pass

Function-entry Tier 2 and loop OSR share one numeric analysis, planning, and code-generation contract. `LuaNumericRegionAnalyzer` establishes region shape and value proofs; `LuaNumericRegionPlanner` selects guarded operations and deoptimization points; `ReflectionEmitLuaNumericRegionCompiler` emits the supported form.

Both entry modes preserve canonical-PC deoptimization, instruction budgets, debug/GC safepoints, invalidation guards, weak ownership, and managed fallback. A loop entry is an alternate entry point to the same semantic region, not a separate language engine.

Dynamic-code capability and backend compiler interfaces remain explicit cold-path seams for lifecycle handling. A missing proof selects managed or canonical execution and never widens assumptions through an alternate emitter.
