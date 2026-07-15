# ADR 0005: Enable qualified exact-numeric Tier 2 by default

- Status: Accepted
- Date: 2026-07-13
- Target: Lua 5.4.8, .NET 10
- Depends on: [ADR 0003](0003-tier1-auto-default-rollout.md),
  [ADR 0004](0004-tier2-exact-numeric-productionization.md)

## Context

ADR 0004 productionized an exact integer, float, and mixed-numeric Tier 2 `DynamicMethod`, but
kept `EnableTier2=false` because the same switch also admitted the managed profile-program path.
The final six-RID M11 evidence showed that exact-numeric Tier 2 exceeded its throughput,
allocation, compilation-latency, and code-shape gates on every supported RID. Table, closure,
call, metamethod, coroutine, and unwind-heavy profiles still did not have a non-regressing
managed Tier 2 result.

A default rollout therefore cannot treat all profile-guided plans alike. It needs a deterministic
promotion contract that admits only the code shape covered by the M11 evidence, cannot be bypassed
by imported profiles or custom compilers, and adds no profile overhead when dynamic code is
unavailable.

## Decision

`LuaJitExecutorOptions.EnableTier2` now defaults to `true`. `EnableTier2=false` remains the complete
opt-out: it disables Tier 2 profiling, profile import, promotion, and compiler prewarming.
`EnableTier2ManagedFallback` is added with a default of `false`; hosts must set it explicitly to
retain the experimental `ManagedProfileProgram` behavior for general table/call profiles.

Before automatic promotion enters the compile queue, Lunil snapshots the owner-free function
profile and reuses the production Tier 2 optimization and liveness planner. The profile is accepted
only when:

- the planned method is guaranteed to be `ExactNumericSpecializedCil`;
- at least one exact integer, float, or mixed-numeric specialization exists;
- every emitted optimization is supported by the exact numeric emitter; and
- the observed profile contains no table, upvalue, closure, call/tail-call, or to-be-closed managed
  semantic site.

Stable branches and dead moves may coexist with exact numeric sites. A polymorphic numeric profile
or a managed semantic boundary is a permanent rejection because accumulated profile facts only
widen. A profile with no numeric hotspot receives one exponentially larger second sample window so
a previously cold numeric path can still qualify; a second no-hotspot result is terminal and moves
to plain Tier 1. Imported profiles can satisfy hotness but must pass the same eligibility check.

`LuaJitTier2Eligibility`, `GetTier2PromotionEligibility`,
`Tier2EligibilityAccepted`/`Tier2EligibilityRejected` events, independent statistics, and stable
`JIT2101`-`JIT2106` diagnostics expose the decision. `JIT2106` denotes a function whose canonical
CFG contains an instruction the exact emitter cannot execute without leaving the method. The
registry also rejects a successful
compiler result unless its installed code kind is `ExactNumericSpecializedCil`; a custom compiler
cannot publish a managed method through the automatic path.

Runtime profile collection begins only after Tier 1 has been installed. The Tier 1 cache entry owns
profiled and plain delegates; a terminal Tier 2 eligibility rejection publishes the plain delegate,
so later invocations do not retain a per-instruction observer call. Functions rejected by the Tier 1
`Auto` benefit filter do not pay per-instruction Tier 2 observation cost; imported profiles may still
seed a later eligible Tier 1 function before its first compiled invocation.

Tier 2 is effective only when both dynamic-code capability flags are true. NativeAOT and other
restricted deployments report Tier 2 as disabled, do not collect Tier 2 profiles, reject profile
import as `Disabled`, and keep interpreter or build-time AOT fallback.

Loop OSR remains independently disabled by default.

## Evidence

The prerequisite six-RID M11 CI run `29227359052` reported `ExactNumericSpecializedCil` on
win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, and osx-arm64. The minimum arithmetic
bootstrap 95% lower bound was 7.086x, maximum Tier 2 compilation p95 was 3.228 ms, every RID had a
100% liveness-cache hit rate and zero allocation slope, and maximum compilation allocation was
57,344 bytes.

The rollout branch adds default-contract, exact acceptance, table/managed-boundary rejection,
polymorphic rejection, imported-profile, guard widening, unexpected compiler code-kind, concurrent
promotion, and dynamic-code-unavailable tests. The backend evidence runner now uses the release
default for its Tier 2 row and records eligibility decisions plus managed Tier 2 installation
counts. Negative workload qualification fails if any managed Tier 2 method is installed or if the
paired Tier 2-enabled/Tier 2-disabled `Auto` median falls below 0.90.

The rollout branch's five-process win-x64 Release record, with nine cold samples per process and
`iterations=1,000,000`, reported an 11.282x paired arithmetic median speedup, bootstrap 95% interval
`[8.754x, 11.864x]`, zero allocation slope, 0.234 ms Tier 2 compilation p95, 100% liveness-cache
hit rate, and 56,360 bytes compilation-allocation p95. Every arithmetic process accepted exactly
one automatic eligibility decision and installed `ExactNumericSpecializedCil`; the
lua-call/table/metamethod/coroutine-error-hook matrix installed zero managed Tier 2 methods and
reported paired default-on/default-off medians of 0.995x, 0.981x, 1.056x, and 1.221x.

## Consequences

- CoreCLR applications receive qualified exact-numeric Tier 2 without changing options.
- `EnableTier2=false` remains a deterministic performance and compatibility escape hatch.
- Hosts that depended on the former broad meaning of `EnableTier2=true` must additionally set
  `EnableTier2ManagedFallback=true`.
- Table/call/metamethod/coroutine functions may remain in Tier 1 while exact-numeric leaf functions
  in the same module promote independently.
- Profile planning is performed once at a promotion boundary; permanent rejections are cached and
  no managed Tier 2 compilation is queued by the automatic path.
- The managed fallback and Loop OSR require separate future performance qualification before their
  defaults can change.
