# ADR 0010: Productize persisted CIL AOT performance

- Status: Accepted
- Date: 2026-07-13
- Target: Lua 5.4.8, .NET 10
- Depends on: [ADR 0002](0002-execution-backend-abi-v2.md)

## Context

Persisted CIL v1 already emitted deterministic PE/PDB artifacts, validated their manifest and
checksums, loaded compatible assemblies into collectible contexts, and exposed compiled function
delegates. The build-time NativeAOT registry also executed the same ABI without runtime reflection.
The remaining production gap was the ordinary CoreCLR runtime path: hosts had no end-to-end executor
that connected a validated loaded artifact to the shared scheduler, loader costs were not attributed
by phase, and the repeated six-RID backend evidence only recorded artifact size rather than persisted
execution throughput, allocation, or fallback behavior.

Direct delegate calls are not an acceptable substitute. They bypass scheduler-owned call, yield,
close, hook, budget, GC, and exception boundaries. Persisted execution must retain canonical-PC
fallback and must never run an artifact for a different canonical module merely because function IDs
happen to overlap.

## Decision

Starting with `0.6.0-alpha.14`, CoreCLR hosts may use `LuaPersistedAotExecutor` with a validated
`LuaAotLoadedModule`. The executor owns a shared `LuaExecutionEngine` but does not own the loaded
module. At every compiled entry it:

1. matches the closure's canonical module content ID to the loaded manifest, caching the immutable
   identity result per module owner;
2. resolves the exact function ID from the still-live loaded module;
3. invokes the persisted `LuaCompiledMethod` through the scheduler ABI; or
4. returns `UnsupportedInstruction` deopt at the current canonical PC when identity, function, or
   lifetime checks fail.

The caller controls the collectible `AssemblyLoadContext` lifetime by disposing
`LuaAotLoadedModule`. `LuaPersistedAotStatistics` reports compiled delegate invocations and
interpreter fallback attempts without retaining Lua state, closures, frames, or tables.

`LuaAotArtifactLoader.Load` now returns `LuaAotLoadMetrics` for validation, assembly loading,
delegate binding, total load duration, and current-thread allocated bytes. Failed validation and
dynamic-code-unavailable results still return metrics; they do not load or bind an assembly.

The backend evidence runner adds a `persisted_aot` row for arithmetic, control flow, Lua calls,
table access, metamethods, and coroutine/error/hook behavior. Cold samples include validation,
collectible load, binding, and first execution. Stable throughput uses 256 persisted invocations so
CoreCLR tiered compilation of the loaded methods has settled before measurement. Artifact emission
remains outside runtime load timing and is reported separately as PE, PDB, and total bytes.

Each RID qualifies only when all of the following hold:

- arithmetic and control-flow median speedups are at least 2.0x and their bootstrap 95% lower bounds
  are at least 1.5x versus the interpreter;
- every row measures at least 30 warm operations, executes persisted code, and records zero
  interpreter fallbacks in every process;
- arithmetic allocation slope is within 0.01 B/iteration and allocation is at most 1.10x the
  interpreter;
- every artifact has total-load p95 below 50 ms, load allocation p95 below 192 KiB, and combined
  PE/PDB size below 32 KiB for the fixed evidence corpus; and
- Lua-call, table, metamethod, and coroutine/error/hook median throughput remains at least 0.90x the
  interpreter with allocation at most 1.10x.

`Measure-BackendPerformance.ps1` writes `persisted-aot-decision.json`. Per-process invocation counts
use the minimum and fallback counts use the maximum, so one failed process cannot be hidden by a
median. `Merge-BackendPerformanceEvidence.ps1` selects the newest decision for every release RID and
writes `persisted-aot-six-rid-decision.json` alongside the existing Tier 1, Tier 2, and Loop OSR
aggregates.

## Evidence

The six-process win-x64 Release record at
`artifacts/backend-performance/win-x64/20260713-124741` used nine cold samples and 30 warm operations
per row. It reported:

| Metric | Result |
|---|---:|
| Arithmetic median / bootstrap 95% interval | **3.003x / [2.874x, 3.244x]** |
| Control-flow median / bootstrap 95% interval | **2.428x / [2.381x, 2.503x]** |
| Maximum validation / assembly load / delegate bind p95 | 12.045 / 0.441 / 15.667 ms |
| Maximum total load p95 | **29.567 ms** |
| Maximum load allocation p95 | **153,368 B** |
| Maximum PE + PDB size | **19,168 B** |
| Minimum compiled invocations | **286** |
| Total interpreter fallbacks | **0** |
| Arithmetic allocation slope | **0 B/iteration** |

Semantic-workload medians were 1.998x for Lua calls, 1.505x for table access, 1.315x for
metamethods, and 0.970x for coroutine/error/hook execution. Their allocation ratios ranged from
0.9997x to 1.0001x, every row executed persisted delegates, and no process fell back.

Focused tests cover scheduler execution, metrics, artifact/module mismatch, disposed artifact
fallback, collectible loading, malformed artifacts, and dynamic-code-unavailable validation.
Release solution, publish-mode, packaging, soak, formatting, synthetic aggregate, and protected real
six-RID verification remain mandatory before merge.

## Consequences

- CoreCLR hosts have a supported end-to-end persisted execution path rather than manually invoking
  generated delegates.
- Persisted artifacts retain the same scheduler and canonical fallback semantics as Tier 1, Tier 2,
  Loop OSR, and static NativeAOT execution.
- Load latency, allocation, code size, steady throughput, and fallback behavior are independently
  reviewable on all release RIDs.
- Disposing a loaded module disables compiled lookup immediately and permits collectible unloading;
  the executor itself does not silently extend the load-context lifetime.
- NativeAOT behavior is unchanged: dynamic loading still returns `AOT2010`, while build-time static
  registration continues through `LuaStaticAotExecutor`.
