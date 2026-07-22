# 性能测量

[English](performance.md)

本指南说明如何比较 Lunil 与其他 Lua runtime 的性能，同时避免将性能数据误作语言兼容性结论。每个 engine 都应使用相同的源码 workload、语言契约、宿主 capability 和测量方法。

## 比较边界

PUC Lua 5.4.8 可作为 Lua 5.4 workload 的基准。LuaJIT、Luau、GopherLua、UniLua、NeoLua 和 Wasmoon 可能实现不同的 Lua dialect 或宿主模型；只应在 workload manifest 记录的语义分组内进行比较。更快或更慢都不代表 conformance 结论。

每份结果都应记录 engine 精确版本、操作系统与架构、.NET runtime、配置和 workload revision。不要比较在不同语言版本、capability 或 workload 源码下采集的结果。

## 测量方法

- 运行 `benchmarks/cross-runtime/workloads` 中相同的 Lua 源码。
- 在计时前预热每个 engine 和 workload。
- 使用平衡执行顺序，避免某个 engine 总在其他 engine 前后运行。
- 将批次校准到足够的进程 CPU 时间；将 setup、源码载入、编译和预热与 steady-state 操作分开记录。
- 按 workload manifest 检查每次运行，只对匹配轮次报告比值。
- 绝对耗时依赖机器；应使用重复配对采样与区间估计比较结果。

## 复现 Lunil workload

测量一个 Lunil workload 与执行路径：

```powershell
./scripts/Measure-CrossRuntimePerformance.ps1 `
  -Workloads string_build `
  -Engines lunil_auto `
  -SkipReference `
  -Rounds 6 `
  -TargetMilliseconds 500 `
  -NoProvision
```

可选 comparison engine 可通过 `scripts/Install-OptionalCrossRuntimeEngines.ps1` 安装。使用 `scripts/Export-PublicPerformanceDataset.ps1` 创建可移植 JSON dataset。已有 dataset 和 chart 是本测量方法的示例，不是通用性能保证。
