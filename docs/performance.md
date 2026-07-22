# Performance measurement

[简体中文](performance.zh-CN.md)

This guide describes how to compare Lunil with other Lua runtimes without confusing performance data with language compatibility. Use the same source workloads, language contract, host capabilities, and measurement protocol for every engine.

## Comparison boundaries

PUC Lua 5.4.8 is a useful baseline for Lua 5.4 workloads. LuaJIT, Luau, GopherLua, UniLua, NeoLua, and Wasmoon may implement a different Lua dialect or host model; compare them only within the semantic group recorded by the workload manifest. A faster or slower result is not a conformance claim.

Record the exact engine version, operating system and architecture, .NET runtime, configuration, and workload revision with each result. Do not compare results collected with different language versions, capabilities, or workload source.

## Method

- Run identical Lua source from `benchmarks/cross-runtime/workloads`.
- Warm up each engine and workload before collecting timed operations.
- Use balanced ordering so one engine does not always run before or after another.
- Calibrate the batch size to a meaningful amount of process CPU time; record setup, source loading, compilation, and warmup separately from steady-state operations.
- Check each run against the workload manifest and report ratios only for matched rounds.
- Treat absolute timings as machine-specific; use repeated paired samples and an interval estimate for comparisons.

## Reproduce a Lunil workload

Measure one Lunil workload and execution route:

```powershell
./scripts/Measure-CrossRuntimePerformance.ps1 `
  -Workloads string_build `
  -Engines lunil_auto `
  -SkipReference `
  -Rounds 6 `
  -TargetMilliseconds 500 `
  -NoProvision
```

Optional comparison engines can be installed with `scripts/Install-OptionalCrossRuntimeEngines.ps1`. Use `scripts/Export-PublicPerformanceDataset.ps1` to create a portable JSON dataset. Existing datasets and charts are examples of this protocol, not universal performance guarantees.
