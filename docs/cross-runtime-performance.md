# Cross-runtime performance workflow

Lunil's cross-runtime suite executes identical portable Lua source on native PUC Lua, LuaJIT,
MoonSharp, and every supported Lunil configuration. Native Lua 5.4 is always the per-RID
normalization baseline (`1.000x`). MoonSharp is the managed product-performance reference: Lunil
Auto and Tier 2 must stably beat it on every workload. This complements the Lunil-internal backend
qualification gates in [backend-performance-baseline.md](backend-performance-baseline.md).

## Pinned runtimes

| Runtime | Pinned input | Role |
|---|---|---|
| Native Lua | Lua 5.4.8 source archive, SHA-256 `4f18ddae154e793e46eeab727c59ef1c0c0c2b744e7b94219710d76f530629ae` | Per-RID `1.000x` baseline |
| LuaJIT | Upstream commit `3c4f9fe2052b8d08a917ac0d5f38563f0297b5a3`, archive SHA-256 `295f9e6722a2200aaf41297b28f73d337ac12236cdf1788981e46bd0afd466ff` | Native tracing-JIT comparison |
| MoonSharp | NuGet `MoonSharp` 2.0.0 | Managed comparison and hard gate reference |
| Lunil | The checked-out commit | Interpreter, Auto, Tier 1, Tier 2, and Loop OSR |

`Install-CrossRuntimeBenchmarkTools.ps1` downloads the native source archives, verifies their
hashes before extraction, builds release executables with the platform C toolchain, verifies their
reported versions, and writes `tools.json` containing source and executable hashes. Downloaded
sources and binaries remain under ignored `artifacts/` directories.

## Common workload contract

`benchmarks/cross-runtime/suite.json` is the versioned manifest. Every workload uses the common Lua
5.1–5.4 language subset and receives its logical operation count as the chunk's first argument
(`...`). The eight workloads cover:

- numeric accumulation and iterative Fibonacci calls;
- floating-point Mandelbrot and branch-heavy control flow;
- fixed/multiple-return function calls;
- array plus string-field table traffic;
- a table/math prime sieve;
- string construction and `table.concat`.

The native harness, MoonSharp host, and Lunil host load the same workload bytes. Every result is
checked against `expectedPerOperation × operations`; an incorrect result aborts the run. Lunil rows
record the observed route and timed-region telemetry, so a configured backend cannot silently be
reported as compiled after fallback or deoptimization.

From `0.8.0-alpha.15`, the schema also records table PIC hits, misses, and invalidations. Hit/miss
values are low-overhead estimates: the first event is exact and steady-state events are aggregated
in batches of 256. Invalidations remain exact. Gates use these fields to diagnose route stability
and mutation behavior; they do not treat sampled hit/miss totals as exact per-operation counts.

## Measurement and gate protocol

1. Create a fresh engine instance and load/compile the source outside the primary interval.
2. Execute four untimed warmup calls. Lunil compilation is synchronous so promotion is
   deterministic. After managed warmup, force two blocking CLR collections with pending-finalizer
   completion between them; this reset is setup work and is excluded from the primary interval.
3. Calibrate each engine/workload independently to at least 250 ms process CPU, verify a second
   calibration interval, and measure a four-times-larger batch to stay above coarse CPU-clock
   quanta.
4. Run six balanced rounds. Engine order rotates for each workload/round to balance thermal,
   frequency, and ordering drift.
5. Match reference and candidate samples by round. The reported speedup is the median of the six
   paired `reference ns/op / candidate ns/op` ratios. A deterministic 4,000-resample bootstrap of
   those paired ratios produces the 95% confidence interval. Median, p95, MAD, setup CPU, routes,
   and raw samples are retained.
6. Native-Lua-relative results are normalized so native Lua is exactly `1.000x`; overall values are
   geometric means across workloads.
7. For every workload, both `lunil_auto` and `lunil_tier2` must achieve a paired median of at least
   **1.05x versus MoonSharp** and a paired CI95 lower bound of at least **1.00x**. Each candidate
   must use one stable route and its timed region must report zero interpreter fallback,
   deoptimization, unexpected deoptimization, and Tier 2 unsupported exit.

The primary metric is process CPU nanoseconds per logical operation. Source loading, runtime setup,
CLR heap reset, JIT compilation, and warmup are separate setup CPU evidence.
This is a steady-throughput comparison, not a CLI cold-start comparison.

## Local workflow

Build the pinned native runtimes once and run the production protocol:

```powershell
./scripts/Install-CrossRuntimeBenchmarkTools.ps1 -RuntimeIdentifier win-x64
./scripts/Measure-CrossRuntimePerformance.ps1 `
  -RuntimeIdentifier win-x64 -Rounds 6 -TargetMilliseconds 250 -NoProvision
```

For development, `-Quick` selects two rounds and a 50 ms calibration floor. Workload and engine
filters automatically retain native Lua and MoonSharp, preserving normalization and the comparison
reference in partial reports:

```powershell
./scripts/Measure-CrossRuntimePerformance.ps1 -Quick -NoProvision `
  -Workloads function_calls,string_build `
  -Engines lunil_auto,lunil_tier2
```

Each run writes an ignored directory under `artifacts/cross-runtime-performance/<rid>/`:

| File | Contents |
|---|---|
| `report.md` | Human-readable per-workload, overall, and MoonSharp-gate report |
| `report.json` | Schema-versioned environment, samples, paired statistics, routes, telemetry, gate, and completeness |
| `summary.csv` | One aggregate row per engine/workload |
| `samples.csv` | Raw balanced-round samples and Lunil telemetry |
| `provenance.json` | SHA-256 hashes of suite inputs and native executables |
| `tools.json` | Pinned native source/build manifest |
| `runner.log` | Calibration, ordering, route, and sample trace |

A complete report with any failed MoonSharp gate exits non-zero. Partial reports retain evidence but
do not claim full-suite completeness.

## Six-RID automation

`.github/workflows/cross-runtime-performance.yml` runs on manual dispatch, every Monday, and on
relevant changes to `main`, `feature/**`, and `perf/**`. Its matrix is `win-x64`, `win-arm64`,
`linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`. Each runner builds its own pinned native Lua
and LuaJIT executables and uploads its complete evidence.

The aggregate job calls `Merge-CrossRuntimePerformanceEvidence.ps1`. It rejects duplicate, missing,
non-schema-2, or incomplete RID reports. It also rejects any RID whose Auto/Tier 2 MoonSharp gate is
incomplete or failed. Only when all six reports qualify does it emit
`cross-runtime-six-rid-report.json` and `cross-runtime-six-rid-report.md` with per-RID,
per-workload, and all-platform geometric means. One failed workload on one RID fails the workflow.

## Qualified six-RID CI report

Hosted run [`29459923109`](https://github.com/dlqw/Lunil/actions/runs/29459923109) completed the
win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, and osx-arm64 jobs plus the fail-closed
aggregate. All 96 Auto/Tier 2 workload/RID gates passed. The aggregate evidence contains 48
workload/RID measurements for each engine:

| Candidate | Geomean vs native Lua | Geomean vs MoonSharp | Minimum vs MoonSharp |
|---|---:|---:|---:|
| Lunil Auto | 0.138x | **1.980x** | **1.067x** |
| Lunil Tier 2 | 0.138x | **1.979x** | **1.054x** |

The minimum was win-x64 `string_build`; its paired CI95 lower bounds were 1.026x for Auto and
1.010x for Tier 2. Thus even the narrowest hosted-run result remained above MoonSharp, rather than
passing only through an unpaired or aggregate average.

## Qualified Windows x64 call-path report (`0.8.0-alpha.14`)

The direct compiled-call acceptance run used exact product commit
`b9a0a825b39ed6e5a8c582c3f144a2dbc76de4e4`, win-x64, .NET 10.0.3, six balanced rounds and the
normal 250 ms calibration floor. A same-protocol `0.8.0-alpha.13` control used exact commit
`1882ef1fb96969cc15dbf6ffd81e5b150b0f964f`. Both reports ran the common `function_calls` bytes,
validated every result, retained the stable Tier 2 route, and reported clean timed
fallback/deoptimization telemetry.

| Candidate | Median CPU | p95 CPU | vs native Lua (CI95) | vs MoonSharp (CI95) |
|---|---:|---:|---:|---:|
| alpha.13 Tier 2 | 573.932 µs | 598.822 µs | 0.143x (0.140–0.149) | 2.154x (2.081–2.252) |
| alpha.14 Tier 2 | **91.654 µs** | 112.337 µs | **0.904x (0.751–0.964)** | **13.809x (12.768–14.807)** |

The exact median improvement is **6.26x**, exceeding the 3x call-heavy gate. Each alpha.14 timed
sample completed 38,528,000 direct calls with zero direct fallback or invalidation and reported
77,056,000 avoided scheduler exits. The cross-runtime `telemetry_json` schema now includes
`directCallEntries`, `directCallCompletions`, `directCallFallbacks`, `directCallInvalidations`, and
`schedulerExitsAvoided`; these fields make a nominal Tier 2 route insufficient if the timed region
actually falls back at closure boundaries. Raw reports, samples, provenance, and the exact alpha.13
control are under `artifacts/performance/0.8.0-alpha.14/win-x64/b9a0a82/`.

## Qualified Windows x64 report (`0.8.0-alpha.13`)

The compact Tier 0 acceptance run on 2026-07-18 used exact product commit
`2bf2a014bd1a5c5589dba217b7a3e7d5fb1e956e`, win-x64, .NET 10.0.3, eight balanced rounds,
a 250 ms calibration floor, all eight current engines, and all eight workloads. The complete local
evidence is stored at `artifacts/cross-runtime-performance/win-x64/20260717-184620`. All 512 timed
samples returned the expected result. Every route was stable, fallback/deoptimization telemetry was
clean, and all 16 Auto/Tier 2 MoonSharp gates passed.

| Engine | Workloads | Geomean vs native Lua | MoonSharp-relative range | Geomean vs MoonSharp |
|---|---:|---:|---:|---:|
| LuaJIT | 8 | **9.286x** | 11.071x–598.776x | 137.075x |
| Native Lua 5.4.8 | 8 | **1.000x** | 2.202x–29.626x | 14.816x |
| Lunil Tier 2 | 8 | 0.253x | 1.236x–25.988x | **3.741x** |
| Lunil Auto | 8 | 0.252x | 1.261x–26.204x | **3.749x** |
| Lunil Loop OSR | 8 | 0.095x | 0.569x–25.312x | 1.402x |
| Lunil Tier 1 | 8 | 0.081x | 0.770x–1.691x | 1.191x |
| MoonSharp | 8 | 0.067x | 1.000x–1.000x | 1.000x |
| Lunil interpreter | 8 | 0.042x | 0.380x–0.929x | **0.625x** |

The interpreter/MoonSharp geomean rose from the `alpha.12` exact-commit baseline's 0.335x to
0.625x. To control for run-to-run machine variation, each candidate interpreter/MoonSharp ratio
was divided by the corresponding `alpha.12` ratio and paired by balanced round. The target
numeric/control-flow rows exceeded both the 1.50x median and 1.25x CI95-lower requirements; every
other workload's CI95 lower bound remained above 0.95x.

| Workload | Interpreter vs MoonSharp (CI95) | Normalized gain vs `alpha.12` (paired CI95) |
|---|---:|---:|
| arithmetic | 0.584x (0.558–0.595) | **2.579x (2.386–2.672)** |
| fib_iter | 0.610x (0.593–0.645) | **2.528x (2.328–2.647)** |
| mandelbrot | 0.380x (0.372–0.391) | **1.784x (1.685–1.871)** |
| control_flow | 0.434x (0.424–0.440) | **2.150x (2.061–2.197)** |
| function_calls | 0.844x (0.793–0.919) | **1.782x (1.291–1.965)** |
| table_access | 0.929x (0.867–0.964) | **1.568x (1.479–1.737)** |
| sieve | 0.726x (0.705–0.745) | **1.338x (1.038–1.762)** |
| string_build | 0.693x (0.677–0.710) | **1.650x (1.600–1.737)** |

The narrowest product gate was `string_build`: Auto/Tier 2 reached 1.261x/1.236x with CI95 lower
bounds of 1.226x/1.222x. Thus compact-interpreter work did not trade away the existing compiled
candidate gate. The 0.625x interpreter geomean also exceeds the `alpha.13` target of 0.55x.

## Qualified Windows x64 report (`0.8.0-alpha.12`)

The post-removal Release run on 2026-07-17 used exact product commit
`188882033ef27ce8c1ae027acb2dc9e8ce344034`, win-x64, .NET 10.0.3, six balanced rounds,
a 250 ms calibration floor, all eight current engines, and all eight workloads. The complete local
evidence is stored at `artifacts/cross-runtime-performance/win-x64/20260717-142359`. All 384 timed
samples returned the expected result, no instruction counter overflowed, and all 16 Auto/Tier 2
MoonSharp gates passed with stable routes and clean fallback/deoptimization telemetry.

| Engine | Workloads | Geomean vs native Lua | Native-Lua range | Geomean vs MoonSharp |
|---|---:|---:|---:|---:|
| LuaJIT | 8 | **9.106x** | 3.242x–38.841x | 200.989x |
| Native Lua 5.4.8 | 8 | **1.000x** | 1.000x–1.000x | 22.149x |
| Lunil Tier 2 | 8 | 0.176x | 0.059x–0.714x | **3.912x** |
| Lunil Auto | 8 | 0.169x | 0.057x–0.708x | **3.766x** |
| Lunil Tier 1 | 8 | 0.056x | 0.025x–0.580x | 1.257x |
| Lunil Loop OSR | 8 | 0.050x | 0.017x–0.623x | 1.099x |
| MoonSharp | 8 | 0.045x | 0.028x–0.425x | 1.000x |
| Lunil interpreter | 8 | 0.015x | 0.006x–0.179x | 0.335x |

| Workload | Auto vs MoonSharp (paired CI95) | Tier 2 vs MoonSharp (paired CI95) |
|---|---:|---:|
| arithmetic | **22.923x** (22.286–23.812) | **23.197x** (22.980–23.743) |
| fib_iter | **1.603x** (1.520–1.651) | **1.800x** (1.736–1.951) |
| mandelbrot | **4.287x** (4.025–4.524) | **5.391x** (4.995–5.634) |
| control_flow | **24.523x** (22.943–25.658) | **23.581x** (23.507–25.367) |
| function_calls | **1.979x** (1.877–2.556) | **1.913x** (1.836–2.510) |
| table_access | **2.372x** (2.269–2.481) | **2.429x** (2.367–2.455) |
| sieve | **1.614x** (1.375–1.813) | **1.636x** (1.130–1.937) |
| string_build | **1.381x** (1.348–1.479) | **1.359x** (1.320–1.427) |

The Lua AOT row is absent from the current schema rather than retained as a skipped engine. The
six-RID hosted workflow remains the cross-platform release qualification source; this report is the
exact-commit local win-x64 baseline for subsequent `alpha.13` work.

## Historical pre-removal Windows x64 report

This historical pre-removal Release acceptance run on 2026-07-16 used six balanced rounds, a
250 ms calibration floor, all nine then-supported engines, and all eight workloads. The complete evidence is stored locally at
`artifacts/cross-runtime-performance/win-x64/final-full-v20` and identifies commit `9b04cb0`.
The persisted-AOT row is retained only to make that report reproducible; `0.8.0-alpha.12` removes
that engine from `suite.json`, current reports, and all gates.

| Engine | Workloads | Geomean vs native Lua | Native-Lua range | Geomean vs MoonSharp |
|---|---:|---:|---:|---:|
| LuaJIT | 8 | **9.238x** | 3.115x–40.415x | 148.799x |
| Native Lua 5.4.8 | 8 | **1.000x** | 1.000x–1.000x | 16.104x |
| Lunil Auto | 8 | 0.103x | 0.050x–0.522x | **1.655x** |
| Lunil Tier 2 | 8 | 0.102x | 0.052x–0.502x | **1.691x** |
| Lunil Tier 1 | 8 | 0.069x | 0.035x–0.497x | 1.116x |
| MoonSharp | 8 | 0.062x | 0.027x–0.426x | 1.000x |
| Lunil persisted AOT | 8 | 0.057x | 0.032x–0.401x | 0.925x |
| Lunil Loop OSR | 8 | 0.038x | 0.010x–0.129x | 0.611x |
| Lunil interpreter | 8 | 0.015x | 0.007x–0.130x | 0.245x |

The production candidates passed every per-workload hard gate:

| Workload | Auto vs MoonSharp (paired CI95) | Tier 2 vs MoonSharp (paired CI95) |
|---|---:|---:|
| arithmetic | **2.388x** (2.305–2.584) | **2.372x** (2.292–2.483) |
| fib_iter | **1.463x** (1.385–1.634) | **1.484x** (1.409–1.667) |
| mandelbrot | **1.352x** (1.249–1.412) | **1.339x** (1.304–1.357) |
| control_flow | **1.960x** (1.400–2.951) | **1.918x** (1.725–2.827) |
| function_calls | **1.970x** (1.791–2.360) | **2.198x** (1.744–2.514) |
| table_access | **1.915x** (1.714–2.038) | **2.015x** (1.664–2.039) |
| sieve | **1.304x** (1.294–1.397) | **1.352x** (1.305–1.449) |
| string_build | **1.233x** (1.126–1.353) | **1.233x** (1.145–1.301) |

All 432 measured samples returned the expected value. Auto and Tier 2 used a single route per
workload and reported clean timed-region fallback/deoptimization telemetry. Native Lua remains the
normalization baseline even though MoonSharp supplies the product gate.
