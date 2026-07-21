# Performance

[简体中文](performance.zh-CN.md)

The formal `0.10.0` results compare the default Auto JIT with pinned reference runtimes on the
`win-x64` release RID. PUC Lua 5.4.8 is normalized to `1.000x`; values above `1.000x` are faster.

## `0.9.0` historical results

The release dataset uses the same eight Lua workloads, six balanced rounds, and six release RIDs:
`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`.

| Engine | Version | Geomean vs PUC Lua 5.4.8 |
| --- | --- | ---: |
| LuaJIT | 2.1 (commit `3c4f9fe`) | 11.518x |
| **Lunil Auto JIT** | **0.9.0** | **1.688x** |
| PUC Lua | 5.4.8 | 1.000x |

![Runtime comparison for Lunil 0.9.0](../assets/performance/0.9.0-runtime-overview.svg)

| Auto JIT workload | Vs PUC Lua 5.4.8 |
| --- | ---: |
| Arithmetic | 1.643x |
| Iterative Fibonacci | 3.232x |
| Mandelbrot | 4.210x |
| Control flow | 2.101x |
| Function calls | 2.568x |
| Table access | 0.478x |
| Prime sieve | 0.530x |
| String build | 2.164x |

![Auto JIT workload comparison for Lunil 0.9.0](../assets/performance/0.9.0-auto-workloads.svg)

## Reference versions

| Reference | Pinned version | Source identity |
| --- | --- | --- |
| PUC Lua | 5.4.8 | Lua.org source archive SHA-256 `4f18ddae154e793e46eeab727c59ef1c0c0c2b744e7b94219710d76f530629ae` |
| LuaJIT | 2.1 | Git commit `3c4f9fe2052b8d08a917ac0d5f38563f0297b5a3` |

## 0.10.0 cross-runtime dataset

Formal win-x64 release measurement (`rounds=6`, `targetMilliseconds=250`, 8 workloads) is published as
[`benchmarks/results/0.10.0-performance.json`](../benchmarks/results/0.10.0-performance.json).
Charts: [`assets/performance/0.10.0-runtime-overview.svg`](../assets/performance/0.10.0-runtime-overview.svg),
[`assets/performance/0.10.0-auto-workloads.svg`](../assets/performance/0.10.0-auto-workloads.svg).

Engines measured (with pinned identity):

| Engine | Version / identity | Semantic group |
|---|---|---|
| PUC Lua | 5.4.8 | `lua54` |
| LuaJIT | 2.1 @ `3c4f9fe` | `lua51-dialect` |
| Lunil Auto JIT | 0.10.0 | `managed-dotnet` |
| NeoLua | NuGet 1.3.19 (net8 out-of-process harness) | `managed-dotnet` |
| Luau | 0.623 | `lua51-dialect` |
| GopherLua | 1.1.1 | `lua51-dialect` |
| Wasmoon | 1.16.0 | `lua54` |
| UniLua | `194eb311` | `lua52-managed` |

Provision optional engines with `scripts/Install-OptionalCrossRuntimeEngines.ps1`, then
`scripts/Measure-CrossRuntimePerformance.ps1`. Export public JSON via
`scripts/Export-PublicPerformanceDataset.ps1`.


## Method

- Each engine runs identical source from `benchmarks/cross-runtime/workloads`.
- Six balanced rounds are measured per engine and workload after a four-call warmup.
- Calibration uses at least 250 ms of process CPU time with a 4× measured-batch safety factor.
- Runtime creation, source loading, compilation, and warmup are recorded as setup time and excluded
  from the primary CPU-time-per-operation metric.
- Results are checked against the workload manifest; ratios use matched balanced rounds and a
  deterministic paired bootstrap interval.
- The formal `win-x64` release dataset is stored in
  [`benchmarks/results/0.10.0-performance.json`](../benchmarks/results/0.10.0-performance.json).

Absolute timings depend on the machine. New comparisons should preserve the same workload sources,
reference versions, semantic groups, and measurement protocol.

## Reproduce a Lunil workload

To measure a selected Lunil workload and route:

```powershell
./scripts/Measure-CrossRuntimePerformance.ps1 `
  -Workloads string_build `
  -Engines lunil_auto `
  -SkipReference `
  -Rounds 6 `
  -TargetMilliseconds 500 `
  -NoProvision
```

The committed charts can be regenerated or verified with:

```powershell
./scripts/New-PerformanceCharts.ps1 -Verify
./scripts/New-PerformanceCharts.ps1 -DataPath benchmarks/results/0.10.0-performance.json -Verify
./scripts/New-PerformanceCharts.ps1 -DataPath benchmarks/results/0.9.0-performance.json -Verify
```
