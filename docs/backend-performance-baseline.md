# Execution backend performance baseline

This document records local reference measurements for JIT/AOT backend work. They are regression
evidence from one machine, not portable pass/fail limits. CI publishes the same benchmark output
for every native RID without failing a build on timing variance.

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
