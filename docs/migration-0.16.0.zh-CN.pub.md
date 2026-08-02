# 从 Lunil 0.15 迁移到 0.16

[English](migration-0.16.0.pub.md)

Lunil 0.16 保持 0.15 的编译器、运行时、宿主、分析与引擎入口源码兼容：本版本新增调试器
（DAP）支持与注解驱动类型检查，不删除任何公共成员。两处行为变化在升级时需要留意：类型
诊断现在对带注解文件默认启用（无注解项目不受影响），VM 信号枚举新增一个成员。

## 1. 更新包与工具

将所有 Lunil 包引用更新到同一兼容线：

```xml
<PackageReference Include="Lunil.StandardLibrary" Version="0.16.0" />
<PackageReference Include="Lunil.Hosting" Version="0.16.0" />
```

```bash
dotnet tool update --global Lunil.Cli --version 0.16.0
```

## 2. 新的调试器支持

调试适配器协议实现以 `lunil-debug-adapter` 可执行文件发布（随 VSIX 内置），有两种模式：

- **Launch**（`--stdio`）：在参考解释器下运行 Lua 脚本，支持断点、单步、暂停、调用栈、
  局部变量与上值。
- **Attach**（`--stdio --pipe <name>`）：将协议中继到游戏循环宿主的命名管道调试端点。

宿主通过启动端点选择加入：

```csharp
using var debugServer = gameLoop.StartDebugServer("lunil-debug");
```

见 [调试 Lua](debugging.zh-CN.pub.md) 与[调试参考](debugging-reference.zh-CN.pub.md)。
调试要求解释器后端；JIT 宿主会得到明确错误而非静默无效。

## 3. 类型诊断默认启用

注解驱动类型检查现在默认启用（`LuaAnalysisOptions.Enabled`）。带注解文件可能产生
`LUA6000` 线诊断（可赋值性、实参数量、nil 路径等）；无注解文件不受影响。0.16 新增：

- `LUA6022` — 跨模块导出一致性：`require` 消费方的 `---@type` 不可赋给模块导出类型。
- 各处均可通过 `SuppressedDiagnosticCodes` 抑制：CLI `--suppress <code>`、
  `lunil.server.suppressedDiagnosticCodes`（VS Code），或嵌入时的 `LuaAnalysisOptions`。

如果新诊断在既有注解代码库中产生噪音，请抑制具体码而非关闭分析。见
[类型检查](type-checking.zh-CN.pub.md)。

## 4. 兼容性清单

- 未删除或重签名任何公共成员；0.15 API 面保持源码兼容。
- `LuaVmSignal.Paused` 是新的枚举成员（二进制兼容追加）；对信号做 switch 的代码必须处理
  新值。
- `LuaWorkspaceDiagnosticPhase.Analysis` 是 LUA6022 workspace 诊断使用的新相位。
- 新增公共调试 API：`LuaGameLoopHost.StartDebugServer`、`LuaGameLoopDebugServer`、
  `LuaHost.ResumeDebuggedThread`、`LuaExecutor.ResumeDebugged`、
  `LuaInterpreter.ResumeDebugged`，以及 `LuaDebugApi`/`LuaDebugSession` 面。
- 托管 C 栈守卫已调优（`MaximumCStackDepth`）；深 `coroutine.close` 链现在有余量地抛出
  "C stack overflow" 而不是耗尽托管栈。普通递归深度行为不变。
- CLI `check`、`build`、`dump` 命令接受新的可重复 `--suppress <code>` 选项。
- VS Code 插件新增 `lunil` 调试器类型（launch 与 attach 配置）以及
  `lunil.server.suppressedDiagnosticCodes` 设置。
