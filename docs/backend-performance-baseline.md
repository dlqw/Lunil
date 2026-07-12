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
fast. Neither tier passes. Loop OSR remains around 39.6% faster for this workload, but allocation
is approximately 6.9 times the interpreter and cross-RID repeated evidence is still pending.
Therefore the release default is `InterpreterOnly`; `Auto`/`PreferJit`, Tier 2, and Loop OSR remain
explicit opt-ins. This decision is evidence-gated rather than a removal of the implemented tiers.

Reproduce the multi-process evidence with:

```powershell
./scripts/Measure-BackendPerformance.ps1 -Rounds 3 -Iterations 1000000 -Configuration Release
```

The script writes raw process output, `runs.csv`, and `summary.json` under the ignored
`artifacts/backend-performance/<UTC timestamp>/` directory.
