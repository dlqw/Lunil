# ADR 0008: Prepare Loop OSR only after qualification and strengthen rollout evidence

- Date: 2026-07-13
- Depends on: [ADR 0007](0007-loop-osr-default-rollout-readiness.md)

## Context

ADR 0007 removed constructor-time Loop OSR preparation, delayed natural-loop analysis to the
configured hotness threshold, and required exact-numeric runtime observation before queue admission.
The six-RID qualification report proved the semantic part of that contract: every RID accepted
the arithmetic candidate, rejected every negative workload automatically, installed no managed
negative method, observed no negative guard failure, and passed the startup gate.

The run still reported `AllRidsQualify=false`. Linux-arm64 measured the metamethod Loop OSR-on/off
warm median at 0.8565x, osx-x64 measured table access at 0.8876x, and osx-x64 measured first Loop OSR
compilation p95 at 13.272 ms. Raw records showed only ten warm executions per row and an odd
five-process order split. The linux-arm64 metamethod result followed the 3:2 order imbalance, and the
osx table workload had a very wide `[0.6715x, 1.1218x]` bootstrap interval. Compilation samples showed
that the first specialized emission cost 5.3-12.1 ms while later emissions cost about 0.1-0.2 ms,
because removing constructor preparation moved one-time emitter initialization into the first loop
compilation.

Default rollout must not be authorized by lowering the 0.90 negative floor or hiding one-time work.
The runtime should prepare the specialized pipeline only for a proven positive candidate, and the
evidence should use enough work and balanced ordering to distinguish steady-state overhead from
shared-runner noise.

## Decision

`EnableLoopOsr` remains `false` in `0.6.0-alpha.12`. A later default change still requires a new real
six-RID aggregate with `AllRidsQualify=true`.

The canonical Loop OSR compiler is prepared lazily only after a loop publishes automatic
exact-numeric acceptance. The preparation path:

- does not run at executor construction, normal function entry, hotness analysis, managed-boundary
  rejection, or non-exact `JIT3105` rejection;
- is process-wide and thread-safe through the canonical compiler's existing `Lazy` barrier;
- is initiated immediately after `LoopOsrEligibilityAccepted` and is awaited again before queueing a
  specialized request, so concurrent requests cannot race first initialization; and
- publishes `LoopOsrCompilerPrepared` with an independently attributed duration.

Managed fallback requests that are not automatically exact-numeric do not initialize the specialized
emitter. Dynamic-code-unavailable runtimes retain the complete no-analysis/no-preparation/no-compile
fallback contract.

The performance evidence contract is strengthened as follows:

- the repeated process count is even and CI uses six processes;
- odd process counts are rejected by `Measure-BackendPerformance.ps1`;
- the pair order is exactly balanced 3:3 across those six processes;
- every throughput row executes at least 30 warm operations, independently of a lower CLI iteration
  argument;
- per-RID decisions persist `WarmOperationsPerProcess`, `BalancedLoopOsrPairOrder`, and
  `LoopOsrPreparationP95Ms`; and
- the six-RID aggregate publishes the minimum operation count, the all-RID balance result, and the
  maximum preparation p95.

The existing gates are not relaxed. Every negative workload still requires warm and startup medians
of at least 0.90, zero automatic acceptance, zero guard failures, and zero managed installations.
Exact-numeric arithmetic still requires guarded specialized CIL, zero allocation slope, 100% liveness
cache hits, compilation allocation below 65,536 bytes, compilation p95 below 10 ms, and now preparation
p95 below 10 ms.

## Evidence

Focused tests prove that an accepted exact-numeric loop publishes preparation after eligibility
acceptance and before compilation completion, while a short loop, a structural managed boundary, and
a non-exact runtime profile publish no preparation event. Existing specialized, fallback, widening,
concurrency, cache, lifetime, budget, debug, GC, and capability tests remain unchanged.

Diagnostic repetitions showed why the evidence needed strengthening:
ten balanced win-x64 processes produced medians of 0.96x for table access and 1.01x for metamethods,
but individual ten-operation ratios ranged from 0.87x to 1.05x and 0.81x to 1.23x. Raising each row to
30 warm operations across six balanced processes produced 1.01x and 1.07x respectively, with minima
of 0.92x and 0.99x. This changes sample quality, not the acceptance floor.

The win-x64 qualification used six balanced processes, nine cold samples per process, and 30 warm
operations per row. It reported:

- 7.547x arithmetic median speedup over the interpreter, bootstrap 95% interval
  `[6.311x, 7.860x]`;
- 119.634x median speedup over OSR-disabled execution, bootstrap interval
  `[100.740x, 127.092x]`;
- 0.527 ms preparation p95, 3.104 ms compilation p95, 45,092-byte compilation allocation p95,
  zero allocation slope, and 100% liveness-cache hits;
- `GuardedExactNumericCil`, five specialized instructions, thirteen guards, one accepted automatic
  eligibility decision, and no managed arithmetic installation; and
- warm negative medians of 1.019x, 1.000x, 0.967x, and 0.988x, with startup medians of 0.996x,
  1.016x, 0.977x, and 0.988x for lua calls, table access, metamethods, and coroutine/error/hook.

Every negative workload retained zero automatic acceptance, zero guard failures, and zero managed
installations. The six-RID aggregate, rather than this ADR alone, is the authority for a separate
default-rollout change.

## Consequences

- Negative, cold, short-loop, and dynamic-code-unavailable workloads do not pay specialized emitter
  initialization.
- A positive exact-numeric loop pays one bounded, observable preparation before its first specialized
  compilation; later loops and executors reuse the process-wide prepared pipeline.
- Preparation and per-loop compilation latency can regress independently and have separate gates.
- CI runtime increases because every RID uses six processes and at least 30 warm operations, but the
  on/off decision is materially less sensitive to order and allocation/GC noise.
- `EnableLoopOsr=false` remains the release default and complete opt-out until new real evidence passes
  every RID.
