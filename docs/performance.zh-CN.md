# 性能

[English](performance.md)

正式 `0.10.0` 结果在 `win-x64` 发布 RID 上比较默认 Auto JIT 与固定版本参考运行时。
PUC Lua 5.4.8 归一化为 `1.000x`，数值越高越快。

## `0.9.0` 历史结果

发布数据使用相同的八个 Lua 工作负载、六轮平衡采样和六个发布 RID：`win-x64`、`win-arm64`、
`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`。

| 引擎 | 版本 | 相对 PUC Lua 5.4.8 几何均值 |
| --- | --- | ---: |
| LuaJIT | 2.1（commit `3c4f9fe`） | 11.518x |
| **Lunil Auto JIT** | **0.9.0** | **1.688x** |
| PUC Lua | 5.4.8 | 1.000x |

![Lunil 0.9.0 运行时对比](../assets/performance/0.9.0-runtime-overview.svg)

| Auto JIT 工作负载 | 相对 PUC Lua 5.4.8 |
| --- | ---: |
| 算术循环 | 1.643x |
| 迭代 Fibonacci | 3.232x |
| Mandelbrot | 4.210x |
| 控制流 | 2.101x |
| 函数调用 | 2.568x |
| 表访问 | 0.478x |
| 素数筛 | 0.530x |
| 字符串构建 | 2.164x |

![Lunil 0.9.0 Auto JIT 按工作负载对比](../assets/performance/0.9.0-auto-workloads.svg)

## 参考版本

| 参考实现 | 固定版本 | 源码身份 |
| --- | --- | --- |
| PUC Lua | 5.4.8 | Lua.org 源码归档 SHA-256 `4f18ddae154e793e46eeab727c59ef1c0c0c2b744e7b94219710d76f530629ae` |
| LuaJIT | 2.1 | Git commit `3c4f9fe2052b8d08a917ac0d5f38563f0297b5a3` |

## 0.10.0 跨运行时数据集

正式 win-x64 发布级测量（`rounds=6`，`targetMilliseconds=250`，8 个 workload）已发布为
[`benchmarks/results/0.10.0-performance.json`](../benchmarks/results/0.10.0-performance.json)。
图表：[`assets/performance/0.10.0-runtime-overview.svg`](../assets/performance/0.10.0-runtime-overview.svg)、
[`assets/performance/0.10.0-auto-workloads.svg`](../assets/performance/0.10.0-auto-workloads.svg)。

已测量引擎（含固定版本身份）：

| 引擎 | 版本 / 身份 | 语义分组 |
|---|---|---|
| PUC Lua | 5.4.8 | `lua54` |
| LuaJIT | 2.1 @ `3c4f9fe` | `lua51-dialect` |
| Lunil Auto JIT | 0.10.0 | `managed-dotnet` |
| NeoLua | NuGet 1.3.19（net8 外进程 harness） | `managed-dotnet` |
| Luau | 0.623 | `lua51-dialect` |
| GopherLua | 1.1.1 | `lua51-dialect` |
| Wasmoon | 1.16.0 | `lua54` |
| UniLua | `194eb311` | `lua52-managed` |

可选引擎安装：`scripts/Install-OptionalCrossRuntimeEngines.ps1`；测量：`scripts/Measure-CrossRuntimePerformance.ps1`；
导出公开 JSON：`scripts/Export-PublicPerformanceDataset.ps1`。


## 测量方法

- 每个引擎运行 `benchmarks/cross-runtime/workloads` 中的相同源码。
- 每个引擎和工作负载预热四次后进行六轮平衡采样。
- 校准至少使用 250 ms 进程 CPU 时间，并采用 4 倍批次安全系数。
- 运行时创建、源码载入、编译和预热记为 setup 时间，不计入主要的每逻辑操作 CPU 时间。
- 结果按工作负载清单校验；比值使用匹配的平衡轮次和确定性的配对 bootstrap 区间。
- `win-x64` 正式发布数据保存在
  [`benchmarks/results/0.10.0-performance.json`](../benchmarks/results/0.10.0-performance.json)。

绝对耗时取决于机器。新的比较应保持相同工作负载源码、参考版本、语义分组和测量协议。

## 复现 Lunil 工作负载

测量指定的 Lunil 工作负载和执行路径：

```powershell
./scripts/Measure-CrossRuntimePerformance.ps1 `
  -Workloads string_build `
  -Engines lunil_auto `
  -SkipReference `
  -Rounds 6 `
  -TargetMilliseconds 500 `
  -NoProvision
```

验证已提交图表：

```powershell
./scripts/New-PerformanceCharts.ps1 -Verify
./scripts/New-PerformanceCharts.ps1 -DataPath benchmarks/results/0.10.0-performance.json -Verify
./scripts/New-PerformanceCharts.ps1 -DataPath benchmarks/results/0.9.0-performance.json -Verify
```
