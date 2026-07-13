# ADR 0007: Close Loop OSR default-rollout startup and profile gaps

- Status: Accepted
- Date: 2026-07-13
- Target: Lua 5.4.8, .NET 10
- Depends on: [ADR 0006](0006-loop-osr-performance-productionization.md)

## Context

ADR 0006 productionized `GuardedExactNumericCil` Loop OSR while retaining
`EnableLoopOsr=false`. Its local win-x64 record passed the arithmetic, allocation, compilation,
code-kind, and negative workload gates. The subsequent real six-RID CI run `29238433084` also used
the exact code kind on every RID, installed no managed arithmetic OSR, kept allocation slope at
zero, achieved a minimum arithmetic bootstrap 95% lower bound of 7.215x, and kept compilation p95
at or below 5.978 ms.

That aggregate did not authorize a default rollout. Fixed-order shared-runner comparisons reported
negative workload medians below 0.90 on win-x64, linux-x64, linux-arm64, and osx-arm64. The same
record also showed that the metamethod workload could be structurally classified as exact numeric,
compile specialized CIL, and then discover table operands only through runtime guard failures.
Finally, enabling OSR eagerly initialized its compiler and analyzed every entered function before
the function had demonstrated enough loop reuse to amortize those costs.

A default-ready contract must distinguish a structurally emittable loop from an observed exact-
numeric loop, avoid work before the configured hotness threshold, and measure paired negative
workloads without a permanent execution-order bias.

## Decision

`EnableLoopOsr` remains `false` in `0.6.0-alpha.11`. Default rollout is blocked until a new real
six-RID aggregate reports `AllRidsQualify=true` under this ADR's stronger evidence contract.

Natural-loop analysis is no longer performed at normal function entry. The registry counts cheap
verified backedges first and runs CFG/dominator/liveness analysis only when function backedges reach
`LoopOsrBackedgeThreshold`. The triggering edge seeds the matching loop counter, so qualification
adds one interpreted observation pass rather than a second complete hotness interval. The specialized
Loop OSR compiler is no longer eagerly prepared by `LuaJitExecutor` construction.

When `EnableLoopOsrManagedFallback=false`, structural `GuardedExactNumericCil` eligibility is
necessary but not sufficient for queue admission. Before compilation, the interpreter observes each
numeric unary and binary guard site in the candidate loop:

- negate and non-bitwise binary sites require exact integer or float operands;
- bitwise sites require exact integers;
- one non-exact operand permanently changes eligibility to `NonExactNumericProfile`;
- rejection publishes `JIT3105`, marks the loop `Ineligible`, and queues no compilation; and
- acceptance is published only after every required guard site has been observed successfully.

Before observation completes, registry query APIs return `AwaitingExactNumericProfile` with
`IsAutoEligible=false`; the structural evaluator remains available separately and still reports
whether the loop can produce the required code shape.

Qualification state is per-loop, owner-safe, and process-local. Once every candidate loop is accepted
or rejected, the per-instruction observer performs only a single zero-pending branch and does not
look up guard sites. Nested loops may share a guarded program counter; each loop transitions exactly
once under the function entry lock.

`EnableLoopOsrManagedFallback=true` retains the explicit experimental contract from ADR 0006:
structurally managed loops may compile `ManagedCanonicalProgram`, and a specialized method may widen
after guard failure. The specialized-only automatic path still validates the installed code kind,
and `EnableLoopOsr=false` remains a complete opt-out.

The repeated evidence runner alternates `loop_osr` and `loop_osr_off` order between processes. For
each negative workload it now requires:

- warm on/off median at least 0.90;
- first-execution startup on/off median at least 0.90;
- zero automatic Loop OSR eligibility acceptance;
- zero Loop OSR guard failures; and
- zero managed Loop OSR installations.

## Evidence

The new focused test rejects table-backed arithmetic through `JIT3105` before queue admission and
asserts zero compilation and zero guard failures. Another test proves that a short loop below the
backedge threshold produces no eligibility event or compilation request. Existing specialized,
managed fallback, guard widening, concurrency, LRU, owner lifetime, budget/debug/GC, and dynamic-code
capability tests continue to pass.

The final local win-x64 Release record at
`artifacts/backend-performance/win-x64/20260713-095107`, using five independent processes,
`iterations=1,000,000`, and nine cold samples per process, reported:

- 8.670x arithmetic median speedup over the interpreter, bootstrap 95% interval
  `[6.765x, 10.089x]`;
- 115.995x median speedup over OSR-disabled execution, bootstrap 95% interval
  `[86.944x, 127.997x]`;
- 3.538 ms compilation p95, 50,296-byte compilation allocation p95, zero allocation slope, and
  100% liveness-cache hit rate;
- `GuardedExactNumericCil`, five specialized instructions, thirteen guards, and no managed
  arithmetic installation; and
- warm on/off medians of 0.993x, 0.982x, 1.007x, and 0.987x for lua calls, table access,
  metamethods, and coroutine/error/hook respectively, with no startup, eligibility, guard, or
  managed-installation failure.

The metamethod workload now reports `NonExactNumericProfile`, zero accepted eligibility decisions,
zero completed OSR methods, and zero OSR guard failures. The real six-RID result remains required
before any default change.

## Consequences

- Cold and short-loop CoreCLR workloads do not pay Loop OSR analysis or compiler initialization.
- Runtime type evidence prevents a statically numeric opcode with metamethod operands from entering
  the specialized compiler.
- Exact-numeric loops begin compilation one observed iteration after the hotness threshold; this is
  intentional qualification cost and is bounded by the existing threshold.
- A loop that ever observes a non-exact operand is permanently rejected in the specialized-only
  path. Hosts needing polymorphic widening must explicitly enable managed fallback.
- Benchmark comparison is less sensitive to fixed order, but shared-runner timing is still evidence,
  not a hard CI failure; the aggregate JSON remains the rollout authority reviewed before merge.
