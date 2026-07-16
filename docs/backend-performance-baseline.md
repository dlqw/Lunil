# Execution backend performance baseline

This document records local reference measurements for JIT/AOT backend work. They are regression
evidence from one machine, not portable pass/fail limits. CI publishes the same benchmark output
for every native RID without failing a build on timing variance.

These Lunil-internal qualification measurements are intentionally separate from the
[cross-runtime workflow](cross-runtime-performance.md). The cross-runtime suite pins native Lua
5.4.8 as the per-RID baseline and compares LuaJIT, MoonSharp, and all Lunil configurations using
the same portable source, balanced rounds, correctness validation, raw CSV/JSON evidence, and a
six-RID aggregate report.
Unlike the internal hosted-runner timing evidence, the cross-runtime workflow applies a hard
MoonSharp-relative product gate to Auto and Tier 2 on every workload and RID.

## M0 Windows x64 baseline

- Date: 2026-07-12
- Commit base: `main@dc38dd2` plus the M0 benchmark corpus
- Configuration: Release, `iterations=1,000,000`
- Runtime: .NET 10.0.3
- OS: Windows 11 IoT Enterprise LTSC 10.0.26100, x64
- CPU: Intel Core i7-12700H, 14 cores / 20 logical processors
- Visible memory: approximately 15.7 GiB

| Scenario | Operations | ns/op | Allocated/op |
|---|---:|---:|---:|
| `table_integer_get_set` | 1,000,000 | 115.00 | 0.04 B |
| `interpreter_empty_numeric_for` | 10 | 4,461,710 | 5,788 B |
| `interpreter_arithmetic_numeric_for` | 10 | 11,158,830 | 5,980 B |
| `interpreter_cold_compile_execute_arithmetic` | 10 | 4,082,000 | 37,012 B |
| `interpreter_warm_arithmetic_numeric_for` | 10 | 3,649,020 | 1,588 B |
| `interpreter_warm_lua_call_vararg_multireturn` | 10 | 4,901,530 | 978,337.60 B |
| `interpreter_warm_table_array_hash` | 10 | 12,120,690 | 271,360.80 B |
| `interpreter_warm_metamethod` | 10 | 5,444,960 | 740,572 B |
| `interpreter_coroutine_yield_resume` | 10 | 25,050 | 4,252 B |
| `interpreter_warm_debug_count_hook` | 10 | 1,997,410 | 245,188 B |
| `interpreter_every_allocation_gc_stress` | 10 | 2,460,060 | 285,588 B |
| `full_gc_1000_tables` | 10 | 263,360 | 164,772 B |

The benchmark performs a short warmup, but it is intentionally a compact regression runner rather
than a statistical microbenchmark framework. Before a backend becomes the default, release
evidence must include repeated cold-start, warm-throughput, compilation latency, peak-memory, and
artifact-size measurements under controlled conditions.

Run locally with:

```powershell
dotnet run --project benchmarks/Lunil.Runtime.Benchmarks/Lunil.Runtime.Benchmarks.csproj `
  --configuration Release -- 1000000
```

## M1 execution-kernel allocation check

The same machine and command were used after introducing the shared execution kernel, Runtime
ABI v1, stack-window calls, and inline operation arguments. Representative allocation changes:

| Scenario | M0 allocated/op | M1 allocated/op | Change |
|---|---:|---:|---:|
| Lua call + vararg + multiple return | 978,337.60 B | 690,376 B | -29.4% |
| Metamethod loop | 740,572 B | 516,412 B | -30.3% |
| Debug count-hook loop | 245,188 B | 178,588 B | -27.2% |

The new fixed-argument Lua-call scenario measured 514,376 B per 2,000-call execution. It no
longer creates a parameter array for each direct Lua-to-Lua call; the remaining approximately
257 B/call is primarily the current logical frame, continuation, and close-list representation.
Vararg frames retain a persistent vararg window by design. Later frame pooling is a separate
optimization and is not required to preserve the Runtime ABI v1 contract.

## M6 experimental loop OSR check

The M6 corpus adds a 20,000-iteration integer Fibonacci/bitwise while-loop and runs both the
reference interpreter and loop OSR after a short warmup. On the same Windows x64 machine, three
Release runs with `iterations=1,000,000` produced the following median values:

| Scenario | Operations | Median ns/op | Allocated/op |
|---|---:|---:|---:|
| `interpreter_warm_loop_osr_candidate` | 10 | 30,914,650 | 1,844 B |
| `jit_experimental_loop_osr_candidate` | 10 | 16,235,000 | 9,702.40 B |

The local steady-state throughput improvement is approximately 47.5%, above the 10% M6 benefit
threshold. OSR nevertheless remains an explicit experimental switch and stays disabled by
default: the compact runner is not cross-platform statistical evidence, one of the three process
runs showed substantial startup variance, and the current OSR path allocates about 5.3 times as
much per execution. CI records both scenarios on every native RID so a later milestone can decide
whether broader throughput and allocation evidence justify enabling it in the default policy.

## M8 backend productization evidence

M8 adds a common arithmetic/bitwise loop matrix for interpreter, Tier 1, Tier 2, and Loop OSR.
Each process records nine cold samples, compilation event p95, warm throughput/allocation,
working-set delta, estimated dynamic-code bytes, and persisted PE/PDB size. The repeat script then
uses the median of three or more independent processes.

Windows x64 evidence from 2026-07-12, Release, `iterations=1,000,000`, three processes:

| Backend | Startup median | Startup p95 | Warm ns/op | Allocated/op | Compile p95 | Estimated code |
|---|---:|---:|---:|---:|---:|---:|
| Interpreter | 6.094 ms | 6.645 ms | 5,795,700 | 1,844 B | n/a | 0 B |
| Tier 1 | 25.449 ms | 46.595 ms | 11,656,120 | 5,602,561.60 B | 18.966 ms | 8,456 B |
| Tier 2 | 30.986 ms | 54.332 ms | 12,134,690 | 14,846,433.60 B | 12.375 ms | 9,284 B |
| Loop OSR | 3.689 ms | 4.223 ms | 3,499,960 | 12,744 B | 0.086 ms | 418 B |

The same canonical workload emitted a 7,724-byte persisted PE and a 1,036-byte Portable PDB
(8,760 bytes total). Working-set deltas are retained in raw evidence but are not used as a hard
local gate because process-level RSS is noisy at this scale; the three-run medians were 815,104 B
for the interpreter, 12,828,672 B for Tier 1, 724,992 B for Tier 2, and 4,096 B for Loop OSR.

The approved gates require Tier 1 to be at least twice as fast as the interpreter with single-
function compilation p95 below 5 ms, and Tier 2 arithmetic hotspots to be at least four times as
fast. Neither tier passed this M8 snapshot. Loop OSR was around 39.6% faster for this workload,
but allocation was approximately 6.9 times the interpreter and cross-RID repeated evidence was
still pending. The M8 release default therefore became `InterpreterOnly`; the later M9/M10
qualification and rollout supersede that historical Tier 1 decision without qualifying Tier 2
or Loop OSR.

Reproduce the multi-process evidence with:

```powershell
./scripts/Measure-BackendPerformance.ps1 -Rounds 6 -Iterations 1000000 -Configuration Release
```

The script writes raw process output, `runs.csv`, and `summary.json` under the ignored
`artifacts/backend-performance/<UTC timestamp>/` directory.

## M9-1 Tier 1 attribution baseline

M9-1 separates Tier 1 compilation into canonical verification, CFG/liveness, method-plan build,
plan verification, Reflection.Emit, and delegate creation. It also reports structural direct/
slow-path coverage plus compiled instructions and scheduler exits. The benchmark corpus now has
six backend workloads: arithmetic, control flow, Lua calls, table access, metamethods, and a
coroutine/error/debug-hook stress case.

The first instrumented Windows x64 arithmetic run exposed two independent problems:

- 300,020 compiled invocations and scheduler exits for five executions, or approximately 60,004
  exits per execution;
- only 4.833 canonical instructions completed per compiled invocation;
- 31 directly lowered canonical instructions versus six slow-path instructions in the static
  function plan;
- approximately 7,458,782 allocated bytes per execution.

The linear allocation was traced to the capturing value-factory lambda passed to
`ConcurrentDictionary.GetOrAdd` on every scheduler entry. The factory object was allocated even
when the function entry already existed. Replacing the capturing factory with the state-taking
static overload reduced the same workload to approximately 2,021 allocated bytes per execution.
This removes the known allocation slope without changing the execution ABI or Lua semantics.

The focused `gc-verbose` EventPipe trace also showed instruction observation as the largest hot
managed stack in the current Tier 1 path. Compilation attribution on the compact run was dominated
by repeated plan verification and Reflection.Emit; the coldest sample recorded approximately
12.2 ms in plan verification, 5.6 ms in emission, and about 954 KiB of compilation-thread
allocation. These numbers are diagnostic evidence rather than the M9 release gate; controlled
multi-process p95 evidence is still required after the remaining work.

Collect a focused ignored trace with:

```powershell
./scripts/Trace-Tier1Allocations.ps1 -Workload arithmetic -ColdSamples 1 -Iterations 100000
```

## M9-4 local compile-latency closure check

The owner-scoped weak plan cache, cached Runtime ABI method resolution, verified-plan emitter
entry, inline verifier stack, and executor-startup Reflection.Emit preparation reduced the local
arithmetic Tier 1 compilation event substantially. A fresh Release process with one unprimed
arithmetic function recorded:

- total compilation event: 1.662 ms;
- canonical verification: 0.061 ms;
- CFG/liveness: 0.318 ms;
- method-plan build: 0.267 ms;
- plan verification: 0.646 ms;
- Reflection.Emit: 0.247 ms;
- delegate creation: 0.035 ms;
- first plan/compile allocation: approximately 501.6 KiB.

In a separate 21-sample process, the arithmetic compilation p95 was 1.242 ms; owner-plan cache
hits allocated approximately 27.9 KiB and reported zero reused planning durations. One 6.1 ms
Reflection.Emit/GC outlier remained above the p95 rank. The same run measured about 2.39 ms/op
Tier 1 steady state and 2,021 B/op. These measurements pass the local `<5 ms` compile gate, but
the required multi-process and six-RID evidence remains pending and is the release decision
source of truth.

Tier 1 benefit eligibility now rejects functions deterministically before queue admission when
verified facts show no repeated work, insufficient direct coverage, excessive slow paths or
semantic boundaries, or excessive estimated code size. Imported profiles can satisfy hotness but
cannot bypass this filter. No production wall-clock feedback is used by the eligibility model.

## M9-6/M9-7 local closure evidence

The focused soak runs Runtime, CodeGen, and backend differential suites in each round. On
2026-07-12, 20 Release rounds completed without failure:

| Suite | Tests per round | Total across 20 rounds |
|---|---:|---:|
| Runtime | 136 | 2,720 |
| CodeGen.Cil | 104 | 2,080 |
| Backend differential | 15 | 300 |
| **Total** | **255** | **5,100** |

The soak includes exact instruction-budget boundaries, ABI mismatch, cancellation and disposal
publication boundaries, weak-cache ownership, integer overflow, floor division/modulo/bitwise,
NaN and negative zero, mixed coercion, numeric-for boundaries, upvalues, and metamethod fallback.
TRX output is written under ignored `artifacts/tier1-soak/<UTC timestamp>/` directories.

Five independent win-x64 Release processes, each with nine cold samples and
`iterations=1,000,000`, produced the following arithmetic result after cancellation closure:

| Metric | Interpreter | Tier 1 |
|---|---:|---:|
| Warm median | 13,014,350 ns/op | 5,910,920 ns/op |
| Allocated/op | 1,844 B | 1,974.4 B |
| Allocation slope | 0 B/iteration | 0 B/iteration |
| Tier 1 compilation p95 | n/a | 1.636 ms |
| Plan verification p95 | n/a | 0.835 ms |
| Reflection.Emit p95 | n/a | 0.377 ms |

The same-machine Tier 1 speedup median was **2.407x**, with a deterministic bootstrap median 95%
interval of `[2.065x, 3.858x]`. The per-RID decision therefore qualifies win-x64: the approved
gate uses the same-machine median `>=2x`, compilation p95 `<5 ms`, zero linear allocation slope,
and deterministic rejection of negative workloads. Arithmetic and control flow were eligible;
Lua calls and table access were rejected for excessive slow-path density, while metamethod and
coroutine/error/hook workloads were rejected for insufficient direct coverage.

Reproduce and aggregate evidence with:

```powershell
./scripts/Measure-BackendPerformance.ps1 -Rounds 6 -ColdSamples 9 `
  -Iterations 1000000 -Configuration Release
./scripts/Merge-BackendPerformanceEvidence.ps1
```

The measurement script writes raw output, CSV, JSON, and `tier1-decision.json` under ignored
`artifacts/backend-performance/<RID>/<UTC timestamp>/` directories. CI runs the same six-process
measurement on win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, and osx-arm64, then publishes
the aggregate without using shared-runner timing as a CI pass/fail condition.

## M10 Tier 1 default-rollout decision

The post-merge `0.6.0-alpha.6` main CI run `29199988756` repeated the five-process measurement on
all six supported RIDs. Every RID passed the approved Tier 1 qualification contract:

| RID | Arithmetic median speedup | Bootstrap median 95% interval | Compile p95 | Allocation slope | Negative gate | Qualified |
|---|---:|---:|---:|---:|---|---|
| win-x64 | 2.753x | [2.398x, 2.976x] | 1.340 ms | 0 B/iteration | pass | yes |
| win-arm64 | 2.772x | [2.761x, 2.796x] | 1.324 ms | 0 B/iteration | pass | yes |
| linux-x64 | 2.729x | [2.417x, 2.748x] | 1.586 ms | 0 B/iteration | pass | yes |
| linux-arm64 | 2.428x | [2.419x, 2.479x] | 1.534 ms | 0 B/iteration | pass | yes |
| osx-x64 | 2.920x | [2.425x, 2.953x] | 1.259 ms | 0 B/iteration | pass | yes |
| osx-arm64 | 2.753x | [2.650x, 3.179x] | 0.748 ms | 0 B/iteration | pass | yes |

The minimum lower confidence bound was 2.398x and the maximum compile p95 was 1.586 ms. This
evidence authorizes `LuaJitExecutorOptions.Default.Policy = Auto` for Tier 1. The rollout keeps
`EnableTier2=false` and `EnableLoopOsr=false`; neither experimental tier inherits the Tier 1
qualification decision.

The M10 rollout branch repeated the focused soak after the default-contract tests were added:
20 Release rounds completed 5,120 Runtime/CodeGen/backend-differential cases without failure.
The local five-process win-x64 qualification rerun reported a 2.392x median speedup, bootstrap
95% interval `[2.306x, 2.634x]`, 1.312 ms compile p95, zero allocation slope, and no negative
workload failure. The rollout commit still requires the independent six-RID CI aggregate before
merge.

## M11 Tier 2 exact-numeric productionization

M11 replaces the arithmetic hot path of the managed Tier 2 profile program with guarded,
profile-specialized CIL. Exact integer, float, and mixed-numeric sites execute in a generated
`DynamicMethod`; other optimization combinations retain the managed fallback. The benchmark now
records the actual Tier 2 code kind, specialized/deopt counts, IR verification, liveness/cache
hit, optimization planning, CIL emission, delegate creation, and compilation allocation.

Five independent win-x64 Release processes, each with nine cold samples and
`iterations=1,000,000`, produced:

| Metric | Interpreter | Tier 1 | Exact-numeric Tier 2 |
|---|---:|---:|---:|
| Warm median | 7,453,430 ns/op | 2,405,760 ns/op | 801,230 ns/op |
| Paired median speedup | 1.000x | 3.187x | **9.177x** |
| Bootstrap median 95% interval | n/a | [2.579x, 3.433x] | **[6.557x, 10.272x]** |
| Allocated/op | 1,844 B | 1,974.4 B | 2,102.4 B |
| Allocation slope | 0 B/iteration | 0 B/iteration | 0 B/iteration |
| Tier 2 compilation p95 | n/a | n/a | **1.395 ms** |

The Tier 2 compilation attribution was:

| Phase/shape | p95 or value |
|---|---:|
| Canonical IR verification | 0.006 ms |
| Register liveness | 0.007 ms |
| Liveness cache hit rate | 100% |
| Optimization planning | 1.199 ms |
| Specialized CIL emission | 0.087 ms |
| Delegate creation | 0.022 ms |
| Compilation allocation | 57,344 B |
| Code kind | `ExactNumericSpecializedCil` |
| Specialized optimizations / deopt sites | 5 / 5 |

This passes the local exact-numeric Tier 2 gate: paired median and bootstrap lower bound are both
at least 4x, compilation p95 is below 10 ms, and the linear allocation slope is zero. The same
run also measured 10.732x on the numeric control-flow workload.

This decision does **not** enable Tier 2 by default. The current table, call, metamethod, and
coroutine/error/hook shapes still select `ManagedProfileProgram` for all or part of their hot
paths and do not have a non-regressing default policy. `EnableTier2` therefore remains explicit,
while CI records and aggregates the exact-numeric decision independently on all six native RIDs.

Reproduce the qualification record with:

```powershell
./scripts/Measure-BackendPerformance.ps1 -Rounds 6 -ColdSamples 9 `
  -Iterations 1000000 -Configuration Release
./scripts/Merge-BackendPerformanceEvidence.ps1
```

The measurement directory contains `tier1-decision.json`, `tier2-decision.json`, raw process
output, CSV, and summary JSON. The six-RID aggregator writes both
`tier1-six-rid-decision.json` and `tier2-six-rid-decision.json`.

## M12 Tier 2 automatic default rollout

M12 changes the Tier 2 evidence row to use the release default rather than an explicit broad
opt-in. Promotion first computes `LuaJitTier2Eligibility`; only profiles guaranteed to install
`ExactNumericSpecializedCil` may enter the automatic compile queue. Observed table, upvalue,
closure, call/tail-call, and to-be-closed semantic sites are rejected, while
`EnableTier2ManagedFallback=true` preserves the previous experimental managed path for hosts that
request it explicitly. The runner records eligibility decisions and fails the negative gate if any
`ManagedProfileProgram` is installed automatically or if the paired Tier 2-enabled/Tier 2-disabled
`Auto` median falls below 0.90.

Five independent win-x64 Release processes, each with nine cold samples and
`iterations=1,000,000`, produced the following rollout result:

| Metric | Automatic exact-numeric Tier 2 |
|---|---:|
| Paired arithmetic median speedup | **11.282x** |
| Bootstrap median 95% interval | **[8.754x, 11.864x]** |
| Allocation slope | **0 B/iteration** |
| Tier 2 compilation p95 | **0.234 ms** |
| Liveness cache hit rate | **100%** |
| Optimization planning p95 | 0.086 ms |
| Specialized CIL emission p95 | 0.095 ms |
| Delegate creation p95 | 0.020 ms |
| Compilation allocation p95 | 56,360 B |
| Code kind / specialized optimizations | `ExactNumericSpecializedCil` / 5 |
| Eligibility evaluated / accepted / rejected | 1 / 1 / 0 |
| Automatic managed Tier 2 installations | **0** |

The `lua_calls`, `table_access`, `metamethod`, and `coroutine_error_hook` negative matrix installed
zero managed Tier 2 methods. Their paired Tier 2-enabled/Tier 2-disabled `Auto` medians were
0.995x, 0.981x, 1.056x, and 1.221x respectively, so the local rollout decision qualifies. The
prerequisite M11 six-RID CI record already showed an exact-numeric bootstrap 95% lower bound of at
least 7.086x and a Tier 2 compile p95 no greater than 3.228 ms on every RID. The rollout CI repeats
the six-RID record with the new default and managed-installation gate before merge.

## M13 Loop OSR exact-numeric productionization

M13 replaces the managed arithmetic loop prototype with `GuardedExactNumericCil`. The Loop OSR
row now has an independently configured `loop_osr_off` baseline, and qualification compares each
OSR-on result with its disabled pair from the same process. The runner records canonical
verification, natural-loop analysis, liveness/cache hit, specialization planning, CIL emission,
delegate creation, compilation allocation, code kind, specialized-instruction/guard counts,
eligibility decisions, and managed installation count.

Automatic OSR eligibility accepts only verified natural loops whose complete code shape can be
emitted as exact integer, float, or mixed-numeric CIL. Managed table/call/upvalue/closure/vararg/
to-be-closed loops are rejected unless the host separately enables `EnableLoopOsrManagedFallback`.
Loop-free and fully rejected functions are analyzed once at entry and then use a permanent
fast-rejection state so negative workloads do not classify every instruction as a possible
backedge.

Five independent win-x64 Release processes, each with nine cold samples and
`iterations=1,000,000`, produced:

| Metric | Production exact-numeric Loop OSR |
|---|---:|
| Arithmetic median speedup vs interpreter | **8.516x** |
| Bootstrap median 95% interval vs interpreter | **[6.012x, 9.406x]** |
| Arithmetic median speedup vs OSR disabled | **128.737x** |
| Bootstrap median 95% interval vs OSR disabled | **[93.992x, 142.973x]** |
| Allocation slope | **0 B/iteration** |
| Loop OSR compilation p95 | **3.080 ms** |
| Liveness cache hit rate | **100%** |
| Canonical IR verification p95 | 0.004 ms |
| Natural-loop analysis p95 | 0.089 ms |
| Specialization planning p95 | 0.053 ms |
| Specialized CIL emission p95 | 1.525 ms |
| Delegate creation p95 | 0.018 ms |
| Compilation allocation p95 | 44,592 B |
| Code kind | `GuardedExactNumericCil` |
| Specialized instructions / guards | 5 / 13 |
| Eligibility evaluated / accepted / rejected | 1 / 1 / 0 |
| Automatic managed arithmetic installations | **0** |

The negative workload matrix also installed zero managed OSR methods. Paired OSR-on/off medians
were 0.935x for `lua_calls`, 1.003x for `table_access`, 0.992x for `metamethod`, and 0.980x for
`coroutine_error_hook`; all meet the unchanged 0.90 floor. This local Loop OSR decision qualifies
without changing `EnableLoopOsr=false`.

`Measure-BackendPerformance.ps1` now writes `loop-osr-decision.json` in addition to the Tier 1 and
Tier 2 decisions. `Merge-BackendPerformanceEvidence.ps1` selects the newest decision for each of
win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, and osx-arm64 and writes
`loop-osr-six-rid-decision.json`. A synthetic six-RID corpus validated the full aggregate contract,
including minimum confidence bounds, maximum compile/allocation values, exact code kind,
liveness, automatic eligibility, managed-installation avoidance, negative gates, and the final
`AllRidsQualify` flag. Protected CI publishes the real six-RID evidence without enforcing absolute
wall-clock timing on shared runners.

The productionization decision and unchanged default are recorded in
[ADR 0006](adr/0006-loop-osr-performance-productionization.md).

## M14 Loop OSR default-rollout readiness

The real M13 six-RID CI run `29238433084` passed every arithmetic, code-kind, allocation,
compilation, liveness, and managed-installation requirement. Its minimum arithmetic bootstrap 95%
lower bound was 7.215x, maximum Loop OSR compilation p95 was 5.978 ms, and every RID emitted
`GuardedExactNumericCil`. It did not pass the rollout gate: fixed-order shared-runner comparisons
placed one or more negative workload medians below 0.90 on win-x64, linux-x64, linux-arm64, and
osx-arm64. The metamethod row also installed specialized OSR before discovering non-numeric table
operands through guards.

M14 makes three contract changes before any default rollout:

1. natural-loop analysis and specialized-emitter initialization are delayed until verified function
   backedges reach `LoopOsrBackedgeThreshold`;
2. structurally specialized loops must observe every exact-numeric guard site successfully before
   queue admission, while a non-exact operand produces permanent `JIT3105` rejection; and
3. repeated processes alternate the `loop_osr`/`loop_osr_off` execution order. Negative qualification
   now includes startup, warm throughput, automatic acceptance, guard failures, and managed installs.

Five independent win-x64 Release processes, each with nine cold samples and
`iterations=1,000,000`, produced:

| Metric | M14 rollout-readiness Loop OSR |
|---|---:|
| Arithmetic median speedup vs interpreter | **8.670x** |
| Bootstrap median 95% interval vs interpreter | **[6.765x, 10.089x]** |
| Arithmetic median speedup vs OSR disabled | **115.995x** |
| Bootstrap median 95% interval vs OSR disabled | **[86.944x, 127.997x]** |
| Allocation slope | **0 B/iteration** |
| Loop OSR compilation p95 | **3.538 ms** |
| Compilation allocation p95 | 50,296 B |
| Liveness cache hit rate | **100%** |
| Code kind | `GuardedExactNumericCil` |
| Specialized instructions / guards | 5 / 13 |
| Eligibility evaluated / accepted / rejected | 1 / 1 / 0 |
| Automatic managed arithmetic installations | **0** |

The paired warm on/off medians were 0.993x for `lua_calls`, 0.982x for `table_access`, 1.007x for
`metamethod`, and 0.987x for `coroutine_error_hook`. Their paired first-execution startup medians
were 1.005x, 1.027x, 0.984x, and 1.002x. All four workloads reported zero automatic acceptance,
zero managed installations, and zero OSR guard failures. The metamethod workload now reports
`NonExactNumericProfile` instead of compiling specialized CIL.

This local result qualifies the implementation but does not change `EnableLoopOsr=false`. A new
real six-RID aggregate must report `AllRidsQualify=true` before the separate default-rollout change.
The readiness decision is recorded in
[ADR 0007](adr/0007-loop-osr-default-rollout-readiness.md).

## M15 Loop OSR rollout evidence closure

The real M14 six-RID CI run `29241821560` proved that runtime qualification closes the semantic
gap: every RID rejected all four negative workloads automatically, installed no managed OSR,
observed no guard failures, passed every startup gate, and accepted the exact-numeric arithmetic
loop. It still did not authorize default rollout. The linux-arm64 metamethod warm median was
0.8565x, the osx-x64 table-access warm median was 0.8876x, and osx-x64 reported a 13.272 ms first
Loop OSR compilation p95 after constructor-time emitter preparation had been removed.

Raw process data showed that the negative throughput gate used only ten warm executions and an odd
five-process order split. The failing linux-arm64 metamethod ratios followed the 3:2 order imbalance,
while osx table access showed wide allocation/GC noise. M15 strengthens rather than lowers the gate:

1. qualification uses six independent processes, enforcing an exact 3:3 `loop_osr`/`loop_osr_off`
   order balance;
2. every backend throughput row measures at least 30 warm executions;
3. `WarmOperationsPerProcess` and `BalancedLoopOsrPairOrder` are persisted in each RID decision and
   aggregated across all six RIDs; and
4. the specialized emitter is prepared only after exact-numeric runtime qualification succeeds.
   `LoopOsrCompilerPrepared` attributes this one-time cost separately, and both preparation p95 and
   per-loop compilation p95 retain independent `<10 ms` gates.

The negative workload floor remains 0.90, the startup floor remains 0.90, and automatic acceptance,
guard failure, and managed installation must remain zero. `EnableLoopOsr=false` remains unchanged
until a new real six-RID aggregate reports `AllRidsQualify=true`.

The final local win-x64 record at
`artifacts/backend-performance/win-x64/20260713-104847` passed the strengthened contract: 7.547x
arithmetic median speedup over the interpreter, 119.634x over OSR-disabled execution, 0.527 ms
preparation p95, 3.104 ms compilation p95, 45,092-byte compilation allocation p95, zero allocation
slope, and 100% liveness-cache hits. Warm negative medians were 1.019x, 1.000x, 0.967x, and 0.988x;
startup medians were 0.996x, 1.016x, 0.977x, and 0.988x. Automatic negative acceptance, guard
failures, and managed installations remained zero. See
[ADR 0008](adr/0008-loop-osr-qualified-preparation-and-evidence.md).

## M16 Loop OSR automatic default rollout

The strengthened M15 contract was repeated by protected CI run `29244862401` on win-x64, win-arm64,
linux-x64, linux-arm64, osx-x64, and osx-arm64. Every RID used six independent processes with an
exact 3:3 Loop OSR on/off order balance, nine cold samples, and 30 warm operations per throughput row.
The aggregate reported `AllRidsQualify=true`:

| Six-RID aggregate metric | M16 authorization result |
|---|---:|
| Minimum arithmetic bootstrap 95% lower bound | **4.349x** |
| Minimum OSR-on/disabled bootstrap 95% lower bound | **63.933x** |
| Maximum specialized-emitter preparation p95 | **1.239 ms** |
| Maximum Loop OSR compilation p95 | **6.399 ms** |
| Maximum compilation allocation p95 | 44,824 B |
| Maximum absolute allocation slope | **0 B/iteration** |
| Minimum liveness-cache hit rate | **100%** |
| Minimum warm operations per process | **30** |
| All RIDs balanced pair order | **yes** |
| All RIDs use `GuardedExactNumericCil` | **yes** |
| All RIDs accept the automatic exact-numeric candidate | **yes** |
| All RIDs reject negative automatic OSR | **yes** |
| All RIDs avoid negative guard failures | **yes** |
| All RIDs avoid managed installations | **yes** |
| All RIDs pass negative startup and throughput gates | **yes** |

This closes the prerequisite recorded by ADR 0008. M16 changes
`LuaJitExecutorOptions.EnableLoopOsr` to `true` by default while retaining the same automatic
eligibility, runtime qualification, code-kind validation, preparation, compilation, and negative
workload gates. `EnableLoopOsrManagedFallback=false` remains unchanged, and
`EnableLoopOsr=false` remains a complete opt-out.

The `loop_osr` evidence row now uses the actual release default. Its paired control changes only
`EnableLoopOsr=false`, while standalone Tier 1 and Tier 2 measurements explicitly disable Loop OSR
so each tier keeps an independent performance baseline. NativeAOT and other dynamic-code-unavailable
runtimes continue to close the capability gate before analysis, preparation, or registry entry
creation. The decision is recorded in
[ADR 0009](adr/0009-loop-osr-auto-default-rollout.md).

The rollout branch repeated the same contract locally at
`artifacts/backend-performance/win-x64/20260713-113245`. Six balanced Release processes produced a
7.941x arithmetic median with bootstrap interval `[6.242x, 11.615x]`, and a 118.505x median over the
explicit disabled pair with interval `[93.004x, 158.071x]`. Preparation p95 was 0.545 ms,
compilation p95 was 3.099 ms, compilation allocation p95 was 45,092 bytes, allocation slope was zero,
and liveness-cache hit rate was 100%. Negative warm medians were 0.984x, 1.021x, 0.981x, and 1.054x;
startup medians were 0.966x, 1.029x, 0.983x, and 0.982x. All automatic negative acceptance, guard
failure, and managed-installation counts remained zero. The compact benchmark now names this path
`jit_default_loop_osr_candidate` and consumes the same release default.

## M17 persisted CIL AOT performance productionization

Persisted CIL previously contributed deterministic PE/PDB size to the backend runner but was not an
executable evidence row. M17 adds a caller-owned `LuaPersistedAotExecutor` that binds a validated
`LuaAotLoadedModule` to the shared scheduler. Compiled lookup requires an exact canonical module
content-ID match; a mismatched, missing, or disposed artifact returns `UnsupportedInstruction` at
the current canonical PC. Loader metrics attribute validation, assembly loading, delegate binding,
total duration, and allocated bytes independently.

The repeated runner now measures `persisted_aot` for arithmetic, control flow, Lua calls, table
access, metamethods, and coroutine/error/hook behavior. Cold startup contains validation, collectible
load, binding, and first execution. Steady-state measurement performs 256 warm persisted calls first
so CoreCLR tiering of the loaded methods is not mistaken for persisted-code throughput. Every row
records PE/PDB size, minimum compiled invocation count, maximum artifact fallback/deoptimization
counts, and at least 30 warm operations. Exact debug-hook execution is expected to deopt with
`DebugModeChanged`; only artifact lookup fallback or any non-debug compiled deoptimization fails the
gate.

Six independent win-x64 Release processes with nine cold samples produced:

| Metric | M17 persisted CIL AOT |
|---|---:|
| Arithmetic median speedup vs interpreter | **3.053x** |
| Arithmetic bootstrap median 95% interval | **[3.026x, 3.151x]** |
| Control-flow median speedup vs interpreter | **2.578x** |
| Control-flow bootstrap median 95% interval | **[2.383x, 2.631x]** |
| Maximum validation p95 | 12.061 ms |
| Maximum assembly-load p95 | 0.424 ms |
| Maximum delegate-binding p95 | 15.814 ms |
| Maximum total-load p95 | **29.741 ms** |
| Maximum load allocation p95 | **153,368 B** |
| Largest PE + PDB in the fixed workload matrix | **19,168 B** |
| Minimum compiled invocations | **286** |
| Artifact lookup fallbacks / unexpected deoptimizations | **0 / 0** |
| Expected debug-mode deoptimizations | **1,550,120** |
| Arithmetic allocation slope | **0 B/iteration** |

Semantic medians were 2.032x for `lua_calls`, 1.529x for `table_access`, 1.394x for
`metamethod`, and 0.965x for `coroutine_error_hook`; allocation ratios remained between 0.9997x and
1.0001x. The local decision at
`artifacts/backend-performance/win-x64/20260713-133602` qualifies.

Per-RID qualification requires arithmetic and control-flow medians of at least 2.0x with bootstrap
95% lower bounds of at least 1.5x, zero artifact fallback and unexpected deoptimization in every
process, validation/assembly-load/delegate-binding p95 below 40/25/30 ms, total-load p95 below 75 ms,
load allocation p95 below 192 KiB, fixed-corpus artifact size below 32 KiB, arithmetic allocation ratio
at most 1.10x, and semantic throughput/allocation floors of 0.90x/1.10x. The six-RID aggregator emits
`persisted-aot-six-rid-decision.json` alongside the existing tier decisions. See
[ADR 0010](adr/0010-persisted-cil-aot-performance-productionization.md).

The first protected six-RID run, `29251784825`, confirmed strong throughput and bounded individual
load phases but exposed two evidence-model errors. Exact debug hooks produced only
`DebugModeChanged` deoptimizations and were incorrectly counted as artifact fallback, while the
single 50 ms combined-load bound conflated first CoreCLR loader initialization on shared Windows
runners with an artifact-specific phase regression. M17 therefore keeps expected debug deopt as a
separate positive attribution, fails any other deopt, and replaces the single bound with the phase
and 75 ms end-to-end limits above rather than removing cold-load controls.

The corrected protected run, `29255625454`, qualified all six release RIDs:

| RID | Arithmetic median / CI95 lower | Control-flow median / CI95 lower | Maximum total-load p95 |
|---|---:|---:|---:|
| `win-x64` | 3.374x / 3.262x | 2.829x / 2.482x | 56.312 ms |
| `win-arm64` | 3.754x / 3.709x | 3.264x / 3.185x | 53.074 ms |
| `linux-x64` | 3.410x / 3.323x | 2.838x / 2.732x | 27.308 ms |
| `linux-arm64` | 3.740x / 3.671x | 3.387x / 3.215x | 24.932 ms |
| `osx-x64` | 3.444x / 3.057x | 3.143x / 2.914x | 34.054 ms |
| `osx-arm64` | 3.745x / 2.942x | 3.426x / 2.706x | 22.328 ms |

The aggregate maximum validation/load/binding p95 values were 20.692/17.091/20.871 ms, maximum
load allocation was 153,432 B, and maximum artifact size was 19,168 B. Every RID executed persisted
methods with zero artifact fallback and zero unexpected deoptimization; all maximum deoptimizations
were the expected 1,550,120 `DebugModeChanged` exits per RID. The aggregate records
`AllRidsExecutePersistedAot=true`, `AllRidsAttributeExpectedDebugDeoptimization=true`, and
`AllRidsQualify=true`.

## M18 continuous Tier 2 numeric-for dispatch

M18 removes the scheduler round trip previously taken by every
`NumericForPrepare`/`NumericForLoop` execution in exact-numeric Tier 2. The exact emitter now keeps
canonical PC dispatch inside one generated method and rejects any function CFG that would require
an unsupported slow path. Evidence distinguishes method entries, completed Lua invocations, and
unsupported exits; arithmetic records one completed Tier 2 entry per warm execution rather than one
entry per loop iteration.

The runner adds iterative Fibonacci and floating-point Mandelbrot rows and pairs each Tier 2 process
with its Tier 1 process. A local win-x64 Release smoke run (30 warm operations, three cold samples)
produced:

| Workload | Tier 1 warm | Tier 2 warm | Tier 2 / Tier 1 | Allocation |
|---|---:|---:|---:|---:|
| arithmetic (5,000 iterations) | 6.022 ms | 1.516 ms | **3.97x** | 1,913 B / 1,913 B |
| fib_iter (1,000 calls) | 6.843 ms | 6.251 ms | **1.09x** | 330,838 B / 330,854 B |
| mandelbrot (48x48, 50 max iterations) | 86.513 ms | 11.330 ms | **7.64x** | 759,783 B / 759,839 B |

The arithmetic Tier 2 row reported `method_entries=34`, `completed_invocations=34`, and
`unsupported_exits=0`; its 5,075,350 compiled canonical instructions produced only 35 total
scheduler exits across warmup and measurement. The six-RID decision now additionally requires
arithmetic Tier 2 not to regress Tier 1 (paired median at least 1.0x and CI95 lower at least 0.95x),
fib_iter at least 0.95x, Mandelbrot at least 0.90x, completed-entry parity, and zero unsupported
exits. Each paired allocation median must remain within 1.10x of Tier 1. These local measurements
are smoke evidence; the protected six-RID run remains the rollout
authority.

## M19 cross-runtime table, call, and string throughput

M19 adds a separate common-source comparison against native Lua 5.4.8, LuaJIT, and MoonSharp.
Native Lua is always normalized to `1.000x`; MoonSharp is the hard managed product reference.
Auto and Tier 2 must exceed MoonSharp by at least 1.05x median on every workload, with a paired
bootstrap CI95 lower bound of at least 1.00x, one stable route, and clean timed-region fallback and
deoptimization telemetry.

Targeted sampled-CPU traces, rather than repeated full-suite runs, identified three dominant
non-numeric costs:

1. sieve spent most compiled time routing existing integer array slots through generic table-key
   normalization and probing;
2. string construction repeated numeric parsing/conversion after the primitive guard had already
   established exact numeric kinds; and
3. Tier 2 known-closure calls returned to the scheduler even after a frameless leaf completed,
   while Tier 1 already continued inside its emitted method.

The runtime now updates/reads proven dense slots directly, uses exact numeric operations after the
primitive guard, pools table/string backing storage with allocation-site capacity feedback, and
continues successful known frameless calls inside the same Tier 2 method. Table ownership, logical
quota, mutation versions, write barriers, exact instruction budget, GC/debug/finalizer, and
fallback contracts remain covered by focused tests.

The final complete win-x64 Release record at
`artifacts/cross-runtime-performance/win-x64/final-full-v20` ran nine engines, eight workloads, and
six balanced rounds (432 validated samples). Auto/Tier 2 achieved a 1.655x/1.691x geometric mean
versus MoonSharp. Their per-workload paired medians were:

| Workload | Auto / MoonSharp | Tier 2 / MoonSharp |
|---|---:|---:|
| arithmetic | 2.388x | 2.372x |
| fib_iter | 1.463x | 1.484x |
| mandelbrot | 1.352x | 1.339x |
| control_flow | 1.960x | 1.918x |
| function_calls | 1.970x | 2.198x |
| table_access | 1.915x | 2.015x |
| sieve | 1.304x | 1.352x |
| string_build | 1.233x | 1.233x |

Every paired CI95 lower bound exceeded 1.00x; `string_build` was the narrowest at 1.126x for Auto
and 1.145x for Tier 2. See [cross-runtime-performance.md](cross-runtime-performance.md) for the
pinned supply chain, paired estimator, raw report schema, and six-RID fail-closed aggregation.

Hosted CI run [`29459923109`](https://github.com/dlqw/Lunil/actions/runs/29459923109) then passed
all six RID measurement jobs and the fail-closed aggregate. All 96 Auto/Tier 2 workload/RID gates
passed. Across 48 measurements per candidate, Auto/Tier 2 reached 1.980x/1.979x geometric means
versus MoonSharp; the minimum paired medians were 1.067x/1.054x on win-x64 `string_build`, whose
CI95 lower bounds remained above one at 1.026x/1.010x.

## M19 linear unboxed numeric regions

M19 replaces the hot-loop portion of exact-numeric Tier 2 and Loop OSR with one reducible-CFG
numeric-region emitter. The emitted loop uses versioned unboxed CLR locals, direct arithmetic CIL,
local instruction accounting, boundary-only PC/materialization, and a bounded backedge countdown.
The arithmetic evidence row now reports these five plan facts independently for both backends:

- `numeric_region_count`;
- `unboxed_numeric_local_count`;
- `direct_numeric_instruction_count`;
- `numeric_region_safepoint_count` (static backedge poll sites, not dynamic poll executions);
- `numeric_region_hot_instruction_budget_check_count` (qualified hot path only; cold slow-tail
  checks are excluded).

The four structural facts must be nonzero and the hot budget-check count must be exactly zero. The
numeric-region planner cuts backedges and proves a conservative instruction quantum; admitted hot
execution charges actual work at basic-block boundaries. Budgets too small for that quantum use a
separate per-instruction cold slow tail, retaining exact budget PCs without contaminating hot-path
telemetry. This prevents the legacy helper/switch emitter, or a region that regressed to a hot
per-instruction budget branch, from satisfying the gate under the same public code-kind name. The
Tier 2 preparation fixture includes a real natural loop, so the `<10 ms` compilation gate measures a
warmed production pipeline instead of the CLR's one-time JIT of the planner.

The old 64 KiB Loop OSR allocation limit applied to the previous compact single-block emitter and
would force every CFG region back to legacy code. The implementation first removed duplicated
per-PC materialization IL and replaced reaching-definition `HashSet` graphs plus sparse immutable
dictionaries with compact immutable definition vectors and dense kind maps. Warm arithmetic Loop
OSR compilation then measured roughly 162 KiB instead of about 1 MiB. The gate is replaced with two
strict dimensions rather than removed:

| Gate | Tier 2 | Loop OSR |
|---|---:|---:|
| Compile allocation p95 | < 256 KiB | < 192 KiB |
| 1-op to 8-op region allocation slope | < 32 KiB/direct instruction | < 32 KiB/direct instruction |
| Compilation p95 | < 10 ms | < 10 ms |

The local sizing smoke measured about 21.4 KiB/direct instruction for Tier 2 and 14.4 KiB/direct
instruction for Loop OSR. Execution allocation slope remains independently constrained, so the new
compile bound cannot hide per-iteration allocation. See
[ADR 0011](adr/0011-linear-numeric-regions.md).

The completed six-process win-x64 Release record for the linear-region emitter is
`artifacts/backend-performance/win-x64/20260715-173950`. Both arithmetic paths installed one numeric
region with eight unboxed locals, five direct numeric instructions, one static safepoint site, and
zero hot instruction-budget checks. Median arithmetic time fell from the saved pre-change WIP's
205.403/191.895 microseconds for Tier 2/Loop OSR to 31.747/39.118 microseconds, improvements of
6.47x/4.91x. The resulting interpreter speedups were 240.945x and 196.688x respectively.

Tier 2/Loop OSR compilation p95 was 1.789/2.228 ms, compile allocation p95 was 240,280/191,672 B,
and allocation growth was 20,712/15,694 B per added direct instruction. These satisfy the `<10 ms`,
`<256 KiB`/`<192 KiB`, and `<32 KiB/direct instruction` numeric-region gates with zero execution
allocation slope. Tier 1, Tier 2, and persisted-AOT decisions qualified. The first protected six-RID
run then isolated a non-region regression in the Loop OSR disabled-control comparison: before the
1024-backedge threshold, every interpreted backedge re-entered the tier controller, no-backedge
callees created frame observations, and one-time structural rejection could fall after the five
warmup operations. This affected `lua_calls`, `metamethod`, and other structurally rejected loops;
it did not change numeric-region shape or arithmetic throughput.

The corrected six-process win-x64 Release record is
`artifacts/backend-performance/win-x64/20260716-031813`. Pre-qualification backedges are now counted
by a frame-local countdown, partial counts are committed on return/tail-call/unwind, no-backedge
functions skip frame observations, and a repeated structurally impossible loop is rejected after at
least four entries and 64 backedges. Structural exact-numeric candidates still publish nothing
before the configured 1024-backedge threshold. Loop OSR arithmetic reached **200.499x** interpreter
and **198.231x** disabled-control median speedup; compilation p95 was **2.171 ms**, compile allocation
p95 was **191,672 B**, region growth was **15,694 B/direct instruction**, execution allocation slope
was zero, all four region-shape facts were nonzero, and hot instruction-budget checks remained zero.

The corrected rejected-workload on/off medians were `lua_calls` **0.999x**, `table_access`
**0.987x**, `metamethod` **0.996x**, and `coroutine_error_hook` **1.007x**. Every allocation comparison
was at parity and the local Tier 1, Tier 2, Loop OSR, and persisted-AOT decisions all qualified.

The final protected run is [CI 29468655163](https://github.com/dlqw/Lunil/actions/runs/29468655163).
All six RIDs qualified for Tier 1, Tier 2, Loop OSR, and persisted AOT. The minimum Loop OSR
arithmetic CI95 lower bound was **108.615x** versus interpreter and **133.644x** versus the disabled
control; maximum Loop OSR compilation p95 was **3.448 ms** and maximum compile allocation p95 was
**191,136 B**. The minimum Tier 2 arithmetic CI95 lower bound was **84.039x**, maximum compilation
p95 was **2.797 ms**, and maximum compile allocation p95 was **241,648 B**. Every RID installed the
required numeric region with zero hot instruction-budget checks, and every rejected-workload
timing, startup, allocation, eligibility, managed-installation, and guard-failure gate passed.
