<p align="center">
  <img src="assets/lunil-logo.svg" width="168" alt="Lunil 标志">
</p>

<h1 align="center">Lunil</h1>

<p align="center">
  面向现代 .NET、正确性优先的 Lua 5.4 编译器与托管运行时。
</p>

<p align="center">
  <a href="README.md">English</a> · <strong>简体中文</strong>
</p>

<p align="center">
  <a href="https://github.com/dlqw/Lunil/actions/workflows/ci.yml"><img alt="CI" src="https://img.shields.io/github/actions/workflow/status/dlqw/Lunil/ci.yml?branch=main&style=flat-square&label=CI"></a>
  <a href="https://github.com/dlqw/Lunil/releases"><img alt="稳定版本" src="https://img.shields.io/badge/stable-0.8.0-16a34a?style=flat-square"></a>
  <img alt="开发版本" src="https://img.shields.io/badge/development-0.9.0--alpha.2-7c3aed?style=flat-square">
  <a href="LICENSE"><img alt="许可证" src="https://img.shields.io/badge/license-MIT-22c55e?style=flat-square"></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet">
  <img alt="Lua 5.4.8" src="https://img.shields.io/badge/Lua-5.4.8-2C2D72?style=flat-square&logo=lua">
</p>

Lunil 是使用纯 C# 实现的 Lua 5.4.8 编译器、分析工具链与 .NET 10 运行时。源码和 PUC Lua
二进制 chunk 会汇入同一个经过验证的 canonical IR，再通过参考解释器或基于 profile 的 CoreCLR
JIT 执行；.NET NativeAOT 与 trimming 应用仍可使用相同编译器和解释器。

> [!NOTE]
> 稳定版 `0.8.0` 是当前支持版本与性能基线。当前源码版本为 `0.9.0-alpha.2`；Alpha 阶段的
> API 与后端行为在功能冻结前仍可能变化。

## 性能

稳定版 `0.8.0` 使用完全相同的 Lua 源码，在八个 workload、六轮平衡采样和全部六个发布 RID
上测试。原生 PUC Lua 5.4.8 归一化为 `1.000x`，数值越高越快。

| 引擎 | 相对原生 Lua 几何均值 | 相对 MoonSharp 几何均值 |
| --- | ---: | ---: |
| LuaJIT | 11.488x | 168.397x |
| 原生 Lua 5.4 | 1.000x | 14.657x |
| Lunil Tier 2 | 0.682x | 9.988x |
| **Lunil Auto JIT** | **0.680x** | **9.974x** |
| Lunil Loop OSR | 0.113x | 1.659x |
| Lunil Tier 1 | 0.105x | 1.543x |
| MoonSharp | 0.068x | 1.000x |
| Lunil 解释器 | 0.050x | 0.726x |

![Lunil 0.8.0 运行时对比](assets/performance/0.8.0-runtime-overview.svg)

| Auto JIT workload | 相对原生 Lua | 相对 MoonSharp |
| --- | ---: | ---: |
| 算术循环 | 1.110x | 24.659x |
| 迭代 Fibonacci | 2.801x | 40.813x |
| Mandelbrot | 0.559x | 8.757x |
| 控制流 | 2.070x | 34.874x |
| 函数调用 | 1.204x | 17.133x |
| 表访问 | 0.299x | 8.348x |
| 素数筛 | 0.059x | 1.464x |
| 字符串构建 | 0.591x | 1.521x |

![Lunil 0.8.0 Auto JIT 分 workload 对比](assets/performance/0.8.0-auto-workloads.svg)

当前 `0.9.0-alpha.2` 源码也通过了相同的六 RID 跨运行时矩阵，以及完整的后端正确性、
NativeAOT、路由、telemetry、启动、分配和 code-size 资格验证：

| 源码版本 | Auto 相对原生 Lua | Auto 相对 MoonSharp | Tier 2 编译分配 p95 | Loop OSR 编译分配 p95 |
| --- | ---: | ---: | ---: | ---: |
| 稳定版 `0.8.0` | 0.680x | 9.974x | 317,776 B | 259,232 B |
| `0.9.0-alpha.2` | 0.697x | 9.918x | 250,912 B | 192,112 B |

吞吐数据来自两次独立的六 RID 资格运行，不作为同硬件配对增幅结论。Alpha 2 的机器可读报告还
包含编译 p95、分配增长、启动与未改变路由的回归比例。

测试方法、源数据、置信门禁与复现命令见[性能文档](docs/performance.md)；下一阶段量化目标见
[`0.9.0` 路线图](docs/roadmap-0.9.0.md)。

## 主要能力

- **Lua 5.4 语义**：完整语法、二进制字符串、整数/浮点行为、多返回值、vararg、coroutine、
  metatable、to-be-closed 变量、binary chunk 与标准库。
- **经过验证的编译管线**：byte-oriented source text、无损语法树、绑定、类型与流分析、workspace
  分析、canonical lowering 与独立 IR 验证。
- **托管运行时**：显式 Lua value、table、closure、thread、upvalue、资源预算、protected error、
  host handle、弱表、ephemeron、finalizer 与逻辑 GC。
- **分级执行**：参考解释器、经过收益资格检查的 Tier 1、带守卫的 Tier 2，以及共享同一特化契约
  的 loop backedge 入口。
- **可嵌入与可沙箱化**：可复用 Hosting API，提供 Restricted、Trusted 与 Deterministic 能力配置。
- **跨平台**：Windows、Linux、macOS 的 x64/Arm64 bundle；动态代码不可用时 NativeAOT 与 trimming
  会确定性回退解释器。

由于 Lunil 不公开 Lua C ABI，因此不支持原生 Lua C module。

## 快速开始

### 环境要求

- [.NET SDK 10.0.103](https://dotnet.microsoft.com/download/dotnet/10.0) 或兼容的 .NET 10 patch；
- 从源码构建时需要 Git。

### CLI

从已配置的 GitHub Packages source 安装稳定版 `0.8.0`，或直接在源码 checkout 中运行：

```bash
dotnet tool install --global Lunil.Cli --version 0.8.0
lunil --version

lunil run app.lua -- one two
lunil check app.lua --module-root . --warnings-as-errors
lunil build app.lua --target chunk --output app.luac
lunil dump app.lua --kind analysis --format json
```

使用 `-` 读取 stdin 源码，使用 `@arguments.rsp` 读取 UTF-8 响应文件，并通过 `lunil.json` 保存项目
默认值。命令、profile、诊断与退出码见 [CLI 参考](docs/cli.md)。

### 从源码构建

```bash
git clone https://github.com/dlqw/Lunil.git
cd Lunil
dotnet restore Lunil.sln
dotnet build Lunil.sln --configuration Release --no-restore
dotnet test Lunil.sln --configuration Release --no-build --no-restore
```

## 嵌入 Lunil

引用稳定版 Hosting package：

```xml
<PackageReference Include="Lunil.Hosting" Version="0.8.0" />
```

通过可复用的 Restricted host 编译并执行：

```csharp
using Lunil.Hosting;
using Lunil.Runtime.Execution;

const string lua = """
    local total = 0
    for i = 1, 10 do
        total = total + i
    end
    return total
    """;

using var host = new LuaHost(LuaHostOptions.Restricted);
var run = host.RunUtf8(lua, "@examples/sum.lua");

if (!run.CompilationSucceeded)
{
    foreach (var diagnostic in run.Compilation.Diagnostics)
    {
        Console.Error.WriteLine($"{diagnostic.Phase} {diagnostic.Code}: {diagnostic.Message}");
    }
    return;
}

if (run.Execution?.Signal != LuaVmSignal.Completed)
{
    throw new InvalidOperationException("Lua 执行未完成。");
}

Console.WriteLine(run.Execution.Values[0].AsInteger()); // 55
```

可通过 `LuaHostOptions.ExecutionBackend` 强制解释器或动态 JIT。默认 `Auto` 在动态代码可用时使用
合格 JIT，否则使用参考解释器。Compiler、Syntax、Analysis、Workspace、IR、Runtime 与标准库 package
也可独立使用。

## 架构

```mermaid
flowchart LR
    Source[Lua source bytes] --> Compiler[Compiler + analysis]
    Chunk[PUC Lua 5.4 chunk] --> Reader[Chunk reader + verifier]
    Compiler --> IR[Verified canonical IR]
    Reader --> IR
    IR --> Interpreter[Reference interpreter]
    IR --> Tier1[CoreCLR Tier 1]
    Tier1 --> Tier2[Tier 2 specialization]
    Tier2 --> Entry[Function or loop entry]
    Interpreter --> Runtime[Lua runtime + logical GC]
    Entry --> Runtime
```

所有执行路径共享 canonical PC、精确指令计数、资源预算、safe point、debug 行为、失效与 fallback
语义。完整架构见[编译器设计](docs/compiler-design.md)。

## 兼容性

- 语言目标：Lua 5.4.8。
- 运行时目标：.NET 10。
- 发布 RID：`win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`。
- Binary chunk：有界 Lua 5.4 格式与显式目标校验；不兼容的数值布局会被拒绝，而不是截断。
- 稳定线：`0.8.x`；当前开发线：`0.9.0-alpha.N`。

相对 `0.7.0` 的破坏性变更（包括移除 Lua persisted/static AOT）见
[`0.8.0` 迁移指南](docs/migration-0.8.0.md)。.NET NativeAOT 仍是受支持的宿主发布方式，详见
[.NET NativeAOT 与 trimming](docs/nativeaot-build-integration.md)。

## 文档

| 文档 | 内容 |
| --- | --- |
| [性能](docs/performance.md) | 当前测试数据、图表、方法与复现方式 |
| [`0.9.0` 路线图](docs/roadmap-0.9.0.md) | 性能目标、交付阶段与发布门禁 |
| [编译器设计](docs/compiler-design.md) | 编译器、IR、运行时与执行架构 |
| [CLI 参考](docs/cli.md) | 命令、配置、profile、诊断与退出码 |
| [API 兼容性](docs/api-compatibility.md) | 版本化公共 API 与 package baseline |
| [版本策略](docs/versioning.md) | 兼容性版本线与发布通道 |
| [更新日志](changelogs/) | 按版本组织的社区发布说明 |

## 参与贡献

欢迎提交 issue 和范围明确的 pull request。请在 `feature/*`、`perf/*`、`fix/*` 或 `docs/*` 分支上
开发，按影响
补充测试，并在请求审核前运行 build、test、format 与相关文档检查。详见[分支管理](docs/branching.md)。

## 安全问题

疑似安全漏洞请通过 [GitHub 私密漏洞报告](https://github.com/dlqw/Lunil/security/advisories/new)
提交，不要创建公开 issue。

## 许可证

Lunil 使用 [MIT License](LICENSE)。
