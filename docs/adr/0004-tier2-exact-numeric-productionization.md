# Exact-numeric Tier 2 specialization

Tier 2 specializes stable integer, floating-point, and mixed-numeric regions only when its assumptions can be checked before an observable effect. A verified plan uses liveness information and emits a guarded dynamic method for supported shapes; all others use a managed profile program or canonical execution.

Generated code performs ABI entry validation, canonical control flow, budget reservation, and explicit deoptimization. It preserves Lua integer overflow, floor division, modulo, mixed comparison, register-top rules, write barriers, debug boundaries, and canonical PCs.

Planning caches are owner-scoped and use weak ownership where needed. Dynamic-code-unavailable hosts retain normal semantic execution, and hosts can disable Tier 2 when they do not want profiling or promotion work.
