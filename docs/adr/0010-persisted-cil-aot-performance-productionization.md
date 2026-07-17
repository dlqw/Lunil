# ADR 0010: Productize persisted CIL AOT performance

- Status: Superseded by ADR 0018 in `0.8.0-alpha.12`
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
`LuaAotLoadedModule`. `LuaPersistedAotStatistics` reports compiled delegate invocations, artifact
lookup fallbacks, all compiled deoptimizations, expected debug-mode deoptimizations, and unexpected
deoptimizations without retaining Lua state, closures, frames, or tables. Exact debug hooks
intentionally deopt to the shared scheduler and are not an artifact failure.

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
- every row measures at least 30 warm operations, executes persisted code, and records zero artifact
  lookup fallbacks and zero unexpected compiled deoptimizations in every process;
- arithmetic allocation slope is within 0.01 B/iteration and allocation is at most 1.10x the
  interpreter;
- every artifact has validation p95 below 40 ms, assembly-load p95 below 25 ms, delegate-binding p95
  below 30 ms, total-load p95 below 75 ms, load allocation p95 below 192 KiB, and combined PE/PDB
  size below 32 KiB for the fixed evidence corpus; and
- Lua-call, table, metamethod, and coroutine/error/hook median throughput remains at least 0.90x the
  interpreter with allocation at most 1.10x.

`Measure-BackendPerformance.ps1` writes `persisted-aot-decision.json`. Per-process invocation counts
use the minimum while fallback and deoptimization counts use the maximum, so one failed process
cannot be hidden by a median. `Merge-BackendPerformanceEvidence.ps1` selects the newest decision for every release RID and
writes `persisted-aot-six-rid-decision.json` alongside the existing Tier 1, Tier 2, and Loop OSR
aggregates.

## Evidence

The six-process win-x64 Release record at
`artifacts/backend-performance/win-x64/20260713-133602` used nine cold samples and 30 warm operations
per row. It reported:

| Metric | Result |
|---|---:|
| Arithmetic median / bootstrap 95% interval | **3.053x / [3.026x, 3.151x]** |
| Control-flow median / bootstrap 95% interval | **2.578x / [2.383x, 2.631x]** |
| Maximum validation / assembly load / delegate bind p95 | 12.061 / 0.424 / 15.814 ms |
| Maximum total load p95 | **29.741 ms** |
| Maximum load allocation p95 | **153,368 B** |
| Maximum PE + PDB size | **19,168 B** |
| Minimum compiled invocations | **286** |
| Artifact lookup fallbacks / unexpected deoptimizations | **0 / 0** |
| Expected debug-mode deoptimizations | **1,550,120** |
| Arithmetic allocation slope | **0 B/iteration** |

Semantic-workload medians were 2.032x for Lua calls, 1.529x for table access, 1.394x for
metamethods, and 0.965x for coroutine/error/hook execution. Their allocation ratios ranged from
0.9997x to 1.0001x, every row executed persisted delegates, and no process fell back.

Protected CI run `29255625454` then qualified all six release RIDs. Across those RIDs, the minimum
arithmetic and control-flow bootstrap 95% lower bounds were 2.942x and 2.482x. The maximum
validation, assembly-load, delegate-binding, and total-load p95 values were 20.692, 17.091, 20.871,
and 56.312 ms; maximum load allocation was 153,432 B and the largest artifact was 19,168 B. Every
RID executed persisted methods and recorded zero artifact fallback and zero unexpected
deoptimization. The maximum deoptimization count on every RID was 1,550,120, all attributed to
`DebugModeChanged`; the aggregate set `AllRidsQualify=true`.

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
