# Cross-runtime performance workflow

Lunil's cross-runtime suite uses native PUC Lua as the baseline and compares the same Lua source
against LuaJIT, MoonSharp, and every supported Lunil execution configuration. It complements the
internal backend qualification gates in [backend-performance-baseline.md](backend-performance-baseline.md):
the qualification suite answers whether one Lunil backend regressed relative to another, while this
suite answers where the product sits relative to other Lua implementations.

## Pinned runtimes

| Runtime | Pinned input | Role |
|---|---|---|
| Native Lua | Lua 5.4.8 source archive, SHA-256 `4f18ddae154e793e46eeab727c59ef1c0c0c2b744e7b94219710d76f530629ae` | Per-RID `1.000x` baseline |
| LuaJIT | Upstream commit `3c4f9fe2052b8d08a917ac0d5f38563f0297b5a3`, archive SHA-256 `295f9e6722a2200aaf41297b28f73d337ac12236cdf1788981e46bd0afd466ff` | Native tracing-JIT comparison |
| MoonSharp | NuGet `MoonSharp` 2.0.0 | Managed Lua interpreter comparison |
| Lunil | The checked-out commit | Interpreter, Auto, Tier 1, Tier 2, Loop OSR, and persisted AOT |

`Install-CrossRuntimeBenchmarkTools.ps1` downloads the two native source archives, verifies their
hashes before extraction, builds release executables with the platform C toolchain, verifies their
reported versions, and writes `tools.json` containing source and executable hashes. No downloaded
source or binary is committed.

## Common workload contract

`benchmarks/cross-runtime/suite.json` is the versioned manifest. Every workload is valid in the
common Lua 5.1–5.4 language subset and receives its logical operation count as the chunk's first
argument (`...`). The suite currently covers:

- numeric accumulation and iterative Fibonacci calls;
- floating-point Mandelbrot and branch-heavy control flow;
- fixed/multiple-return function calls;
- array plus string-field table traffic;
- a table/math prime sieve;
- string construction and `table.concat`.

The native harness, MoonSharp host, and Lunil host load the exact same bytes. Each result is checked
against `expectedPerOperation × operations`; an incorrect result aborts the run before it can enter
the report. The Lunil rows also record the observed execution route, so a configured Tier 2 or Loop
OSR row cannot silently be presented as compiled when it actually used Tier 1 or interpreter
fallback.

## Measurement protocol

1. Create a fresh engine instance and load/compile the source outside the primary interval.
2. Execute four untimed warmup calls. Lunil compilation is synchronous so promotion is
   deterministic.
3. Calibrate each engine/workload independently to a minimum 250 ms process-CPU interval, verify a
   second calibration interval, then use a four-times-larger measured batch to stay above coarse
   platform CPU-clock quanta.
4. Run six rounds. Engine order rotates for every workload/round to balance thermal and ordering
   effects; each measured sample creates a fresh runtime instance.
5. Report median, p95, median absolute deviation, setup CPU time, and a deterministic 4,000-resample
   bootstrap 95% interval for `native Lua median / candidate median`.
6. Compute a geometric-mean speedup across workloads. Values above `1.000x` are faster than native
   Lua; values below `1.000x` are slower.

The primary metric is process CPU nanoseconds per logical operation. Source loading, runtime setup,
JIT/AOT compilation, artifact loading, and warmup are excluded from that interval and retained as
the separate setup CPU metric. This is a throughput comparison, not a CLI cold-start comparison.

## Local workflow

Build the pinned native runtimes once:

```powershell
./scripts/Install-CrossRuntimeBenchmarkTools.ps1 -RuntimeIdentifier win-x64
```

Run the production protocol:

```powershell
./scripts/Measure-CrossRuntimePerformance.ps1 `
  -RuntimeIdentifier win-x64 -Rounds 6 -TargetMilliseconds 250
```

For development, `-Quick` selects two rounds and a 50 ms calibration floor. Filters retain native
Lua automatically so every partial report remains normalized:

```powershell
./scripts/Measure-CrossRuntimePerformance.ps1 -Quick `
  -Workloads arithmetic,table_access `
  -Engines luajit,moonsharp,lunil_tier2
```

Supplying `-LuaPath` and `-LuaJitPath` with `-NoProvision` is supported for an already verified local
toolchain. Production and CI evidence uses the pinned installer.

Each run writes an ignored timestamped directory under
`artifacts/cross-runtime-performance/<rid>/`:

| File | Contents |
|---|---|
| `report.md` | Human-readable per-workload and overall comparison |
| `report.json` | Complete schema-versioned environment, samples, summaries, routes, and completeness |
| `summary.csv` | One aggregate row per engine/workload |
| `samples.csv` | Raw round-level samples and Lunil telemetry |
| `provenance.json` | SHA-256 hashes of suite inputs and native executables |
| `tools.json` | Pinned native source/build manifest |
| `runner.log` | Calibration, ordering, route, and sample trace |

## Six-RID automation

`.github/workflows/cross-runtime-performance.yml` runs on manual dispatch, every Monday, and when
the workflow or benchmark implementation changes on accepted branches. Its matrix is `win-x64`,
`win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`. Every runner builds its own
pinned Lua and LuaJIT executable and uploads its complete report.

The aggregate job calls `Merge-CrossRuntimePerformanceEvidence.ps1`. It rejects duplicate, missing,
or incomplete RID reports, then emits `cross-runtime-six-rid-report.json` and
`cross-runtime-six-rid-report.md` with per-RID, per-workload, and all-platform geometric means.
Timing is reported rather than used as a pass/fail product gate on shared hosted runners; semantic
completeness, source pinning, result validation, and all-six-RID presence are hard failures.

## Initial Windows x64 report

The first complete local acceptance run used Release, six balanced rounds, a 250 ms calibration
floor, and the eight-workload manifest on 2026-07-16. The runtime base was
`feature/0.8.0-performance@dee84c8` plus this benchmark implementation; the generated evidence is
kept locally at `artifacts/cross-runtime-performance/win-x64/20260716-full`.

| Engine | Workloads | Geometric mean vs native Lua | Per-workload range |
|---|---:|---:|---:|
| LuaJIT | 8 | **9.009x** | 3.091x–38.659x |
| Native Lua 5.4.8 | 8 | **1.000x** | 1.000x–1.000x |
| MoonSharp | 8 | 0.063x | 0.028x–0.422x |
| Lunil Tier 2 | 8 | 0.054x | 0.014x–0.138x |
| Lunil Auto | 8 | 0.048x | 0.013x–0.138x |
| Lunil persisted AOT | 8 | 0.041x | 0.023x–0.188x |
| Lunil Loop OSR | 8 | 0.037x | 0.012x–0.115x |
| Lunil Tier 1 | 8 | 0.035x | 0.014x–0.137x |
| Lunil interpreter | 8 | 0.018x | 0.010x–0.136x |

The report also demonstrates why route attribution is required. Arithmetic, Fibonacci, Mandelbrot,
and control-flow rows installed the requested Tier 2/Loop OSR routes, while table, sieve, and string
rows correctly recorded terminal interpreter fallback for the dynamic JIT configurations. The
function-call Loop OSR row likewise recorded interpreter fallback. All 432 measured samples passed
result validation and the report has no missing engine/workload combinations. This is the local
workflow acceptance snapshot; the scheduled/manual aggregate is the source for cross-platform
comparisons.
