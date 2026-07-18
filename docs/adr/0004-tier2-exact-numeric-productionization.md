# ADR 0004: Productionize exact-numeric Tier 2 without changing the default

- Status: Accepted
- Date: 2026-07-13
- Target: Lua 5.4.8, .NET 10
- Depends on: [ADR 0002](0002-execution-backend-abi-v2.md),
  [ADR 0003](0003-tier1-auto-default-rollout.md)

## Context

The original profile-guided Tier 2 executor interpreted an optimized managed program. On the
initial arithmetic qualification workload it was slower and allocated substantially more than both the interpreter and
Tier 1, so it could not satisfy the approved 4x arithmetic gate. It also repeated canonical
liveness analysis during promotion and did not expose enough phase attribution to distinguish
profile planning, CIL emission, and delegate creation.

Tier 2 must preserve canonical program counters, instruction budgets, debug-mode boundaries,
GC write barriers, Lua integer overflow, floor division/modulo, floating-point behavior, mixed
integer/float comparison, and exact deoptimization. It must also remain safe in trimmed and
NativeAOT deployments where dynamic code is unavailable.

## Decision

Stable exact integer, float, and mixed-numeric profile sites are compiled to a guarded
`DynamicMethod`. The emitted method directly performs entry checks, budget reservation,
unchecked ABI v2 register access, canonical control flow, numeric helpers, and deopt exits.
Unsupported optimization combinations continue to use the managed profile program rather than
emitting partial or unverifiable CIL.

`LuaJitTier2Plan.CodeKind` reports either `ExactNumericSpecializedCil` or
`ManagedProfileProgram`. Tier 2 compilation events additionally report canonical verification,
liveness analysis and cache-hit state, optimization planning, CIL emission, delegate creation,
allocated bytes, code kind, optimization counts, specialized optimization counts, and deopt-site
counts.

Register liveness is cached in an owner-scoped `ConditionalWeakTable<LuaIrModule, ...>` and is
shared by Tier 1, Tier 2, loop OSR, and persisted AOT planning. Cache values do not retain the
module owner after the module becomes unreachable. A Tier 2-enabled executor prewarms the full
profile-planning and specialized-emission pipeline once per process so first promotion does not
pay generic/JIT initialization inside the compilation event.

The Tier 2 qualification record requires, for the arithmetic workload on each RID:

- median speedup and bootstrap median 95% lower bound of at least 4x versus the paired
  interpreter process;
- absolute allocation slope no greater than 0.01 byte per loop iteration;
- Tier 2 compilation p95 below 10 ms;
- `ExactNumericSpecializedCil` with at least one specialized optimization.

`EnableTier2` remains `false` by default. The exact-numeric path is productionized for explicit
Tier 2 hosts, but managed table/call/metamethod profiles have not qualified for a default rollout.
Loop OSR remains independently disabled by default.

## Evidence

Five independent win-x64 Release processes with nine cold samples each and
`iterations=1,000,000` reported:

- 9.177x arithmetic median speedup with bootstrap 95% interval `[6.557x, 10.272x]`;
- zero arithmetic allocation slope;
- 1.395 ms Tier 2 compilation p95;
- 100% liveness-cache hit rate during promotion;
- 1.199 ms optimization-planning p95, 0.087 ms CIL-emission p95, and 0.022 ms
  delegate-creation p95;
- 57,344 bytes compilation allocation, `ExactNumericSpecializedCil`, five specialized
  optimizations, and five deopt sites.

The focused backend soak completed five rounds of differential, tier/cache/profile/OSR,
AOT-fault, and MSBuild-cache groups without failure. The full Release solution, win-x64
NativeAOT fixture, trimmed single-file fixture, and ReadyToRun fixture also passed.

CI repeats the performance record on win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, and
osx-arm64 and publishes a combined Tier 2 decision. Shared-runner absolute time is recorded but
is not used to fail CI automatically.

## Consequences

- Numeric hot loops can opt into a real specialized Tier 2 code shape instead of the managed
  optimized-program dispatcher.
- Guard failures, debug-mode changes, budget exhaustion, and unsupported instructions retain the
  existing canonical exit/deopt contract.
- Dynamic-code-unavailable runtimes never reach Reflection.Emit; NativeAOT continues to use the
  interpreter or build-time persisted AOT paths.
- Enabling Tier 2 globally by default requires a separate ADR after six-RID evidence is reviewed
  and non-numeric/table/call workloads have a non-regressing qualification policy.
