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
./scripts/Measure-BackendPerformance.ps1 -Rounds 3 -Iterations 1000000 -Configuration Release
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
./scripts/Measure-BackendPerformance.ps1 -Rounds 5 -ColdSamples 9 `
  -Iterations 1000000 -Configuration Release
./scripts/Merge-BackendPerformanceEvidence.ps1
```

The measurement script writes raw output, CSV, JSON, and `tier1-decision.json` under ignored
`artifacts/backend-performance/<RID>/<UTC timestamp>/` directories. CI runs the same five-process
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
