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
| Lunil | The checked-out commit | Interpreter, Auto, Tier 1, Tier 2, Loop OSR, and persisted AOT |

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
CLR heap reset, JIT/AOT compilation, artifact loading, and warmup are separate setup CPU evidence.
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

## Qualified Windows x64 report

The final local Release acceptance run on 2026-07-16 used six balanced rounds, a 250 ms calibration
floor, all nine engines, and all eight workloads. The complete evidence is stored locally at
`artifacts/cross-runtime-performance/win-x64/final-full-v20` and identifies commit `9b04cb0`.

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
