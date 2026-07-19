# Performance

[简体中文](performance.zh-CN.md)

The `0.9.0` results compare the default Auto JIT with pinned reference runtimes. PUC Lua 5.4.8 is
normalized to `1.000x`; values above `1.000x` are faster.

## `0.9.0` results

The release dataset uses the same eight Lua workloads, six balanced rounds, and six release RIDs:
`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`.

| Engine | Version | Geomean vs PUC Lua 5.4.8 | Geomean vs MoonSharp 2.0.0 |
| --- | --- | ---: | ---: |
| LuaJIT | 2.1 (commit `3c4f9fe`) | 11.518x | 164.301x |
| PUC Lua | 5.4.8 | 1.000x | 14.287x |
| **Lunil Auto JIT** | **0.9.0** | **1.688x** | **24.089x** |
| MoonSharp | 2.0.0 | 0.070x | 1.000x |

![Runtime comparison for Lunil 0.9.0](../assets/performance/0.9.0-runtime-overview.svg)

| Auto JIT workload | Vs PUC Lua 5.4.8 | Vs MoonSharp 2.0.0 |
| --- | ---: | ---: |
| Arithmetic | 1.643x | 36.094x |
| Iterative Fibonacci | 3.232x | 46.988x |
| Mandelbrot | 4.210x | 63.829x |
| Control flow | 2.101x | 34.773x |
| Function calls | 2.568x | 35.421x |
| Table access | 0.478x | 12.467x |
| Prime sieve | 0.530x | 12.698x |
| String build | 2.164x | 5.372x |

![Auto JIT workload comparison for Lunil 0.9.0](../assets/performance/0.9.0-auto-workloads.svg)

## Reference versions

| Reference | Pinned version | Source identity |
| --- | --- | --- |
| PUC Lua | 5.4.8 | Lua.org source archive SHA-256 `4f18ddae154e793e46eeab727c59ef1c0c0c2b744e7b94219710d76f530629ae` |
| LuaJIT | 2.1 | Git commit `3c4f9fe2052b8d08a917ac0d5f38563f0297b5a3` |
| MoonSharp | 2.0.0 | NuGet package `MoonSharp` |

## Method

- Each engine runs identical source from `benchmarks/cross-runtime/workloads`.
- Six balanced rounds are measured per engine and workload after a four-call warmup.
- Calibration uses at least 250 ms of process CPU time with a 4× measured-batch safety factor.
- Runtime creation, source loading, compilation, and warmup are recorded as setup time and excluded
  from the primary CPU-time-per-operation metric.
- Results are checked against the workload manifest; ratios use matched balanced rounds and a
  deterministic paired bootstrap interval.
- The six-RID release dataset is stored in
  [`benchmarks/results/0.9.0-performance.json`](../benchmarks/results/0.9.0-performance.json).

Absolute timings depend on the machine. New comparisons should preserve the same workload sources,
reference versions, and measurement protocol.

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
./scripts/New-PerformanceCharts.ps1 -DataPath benchmarks/results/0.9.0-performance.json -Verify
```
