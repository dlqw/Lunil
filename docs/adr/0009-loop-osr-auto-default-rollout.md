# ADR 0009: Enable qualified Loop OSR by default

- Status: Accepted
- Date: 2026-07-13
- Target: Lua 5.4.8, .NET 10
- Depends on: [ADR 0008](0008-loop-osr-qualified-preparation-and-evidence.md)

## Context

ADR 0008 kept `EnableLoopOsr=false` until the strengthened rollout contract passed on every release
RID. That contract requires six independent processes per RID, an exact 3:3 on/off order balance,
at least 30 warm operations per throughput row, separate preparation and compilation latency gates,
automatic exact-numeric acceptance, automatic negative rejection, and unchanged 0.90 startup and
warm-throughput floors. Negative workloads must also produce zero guard failures and zero managed
installations.

The protected six-RID CI run `29244862401` evaluated commit
`dc7410225ad014f137257815a4b6c970a5ce5c0e` and reported `AllRidsQualify=true`. Across win-x64,
win-arm64, linux-x64, linux-arm64, osx-x64, and osx-arm64:

- the minimum arithmetic bootstrap 95% lower bound was 4.349x;
- the minimum Loop OSR-on/disabled bootstrap 95% lower bound was 63.933x;
- the maximum specialized-emitter preparation p95 was 1.239 ms;
- the maximum per-loop compilation p95 was 6.399 ms;
- every RID used 30 warm operations with balanced process ordering, zero allocation slope, and a
  100% liveness-cache hit rate;
- every positive row installed `GuardedExactNumericCil`, accepted automatic eligibility, and
  installed no managed arithmetic OSR; and
- every negative row passed startup and warm-throughput gates with zero automatic acceptance, zero
  guard failures, and zero managed installations.

The evidence therefore satisfies the explicit prerequisite for a separate default-rollout change.

## Decision

Starting with `0.6.0-alpha.13`, `LuaJitExecutorOptions.Default.EnableLoopOsr` and a newly constructed
`LuaJitExecutorOptions.EnableLoopOsr` are `true`.

This changes admission policy, not the code-shape or safety contract:

- automatic Loop OSR still admits only verified natural loops that pass static
  `GuardedExactNumericCil` eligibility and observe exact integer/float operands at every numeric guard
  site before queue admission;
- managed semantic boundaries and non-exact profiles remain permanently rejected automatically;
- `EnableLoopOsrManagedFallback` remains `false`, so managed canonical-loop compilation and guard
  widening still require a separate explicit opt-in;
- `EnableLoopOsr=false` remains a complete opt-out from backedge-driven analysis, runtime
  qualification, specialized-emitter preparation, compilation, installation, and OSR entry; and
- runtimes without dynamic-code support, including NativeAOT, keep the capability gate that disables
  analysis, preparation, and compilation regardless of the release default.

Performance evidence treats `loop_osr` as the actual release default and obtains the control row by
setting only `EnableLoopOsr=false`. Tier 1 and Tier 2 standalone evidence explicitly disables Loop
OSR so their independent baselines cannot inherit the new default.

## Evidence

The default-contract tests verify that both the shared default and a directly constructed options
record enable qualified Tier 1, exact-numeric Tier 2, and Loop OSR while both managed-fallback switches
remain disabled. A default exact-numeric loop reaches `Ready`, installs `GuardedExactNumericCil`, and
enters OSR. Negative managed-boundary and non-exact-profile tests use the release default and reject
before compilation.

The explicit opt-out test executes a hot loop with `EnableLoopOsr=false` and observes no eligibility,
preparation, queue, compilation, request, or entry activity. The dynamic-code-unavailable test proves
that the default-enabled option still creates no Loop OSR registry activity when the capability gate
is closed. The NativeAOT fixture asserts the complete default tuple and continues to execute through
the static AOT/interpreter path.

The rollout branch must repeat Release solution tests, formatting, six-process local evidence,
synthetic six-RID aggregation, NativeAOT, trimmed single-file, ReadyToRun, NuGet packaging, release
bundle, and public-repository hygiene before merge. Protected CI must then repeat the real six-RID
aggregate with the new default.

The six-process local win-x64 rollout record at
`artifacts/backend-performance/win-x64/20260713-113245` used the release default for its positive
row and qualified with a 7.941x arithmetic median speedup, bootstrap interval
`[6.242x, 11.615x]`, a 118.505x median over the explicit disabled pair, bootstrap interval
`[93.004x, 158.071x]`, 0.545 ms preparation p95, 3.099 ms compilation p95, 45,092-byte compilation
allocation p95, zero allocation slope, and 100% liveness-cache hits. Negative warm medians ranged
from 0.981x to 1.054x and startup medians from 0.966x to 1.029x; automatic acceptance, guard failure,
and managed installation remained zero.

## Consequences

- Qualified CoreCLR workloads receive exact-numeric Loop OSR without host configuration.
- Cold, short-loop, managed-boundary, and non-exact workloads retain delayed or permanent rejection
  and do not initialize the specialized emitter.
- Hosts can restore the previous release behavior with one complete `EnableLoopOsr=false` opt-out.
- Managed fallback remains experimental and cannot be activated by the default change.
- NativeAOT behavior is unchanged because dynamic-code capability remains authoritative.
- Future regressions are evaluated against the real release default while preserving an explicit
  disabled pair and the unchanged six-RID qualification gates.
