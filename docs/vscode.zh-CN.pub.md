# 如何在 VS Code 中使用 Lunil

[English](vscode.pub.md)

本指南安装指定平台的 Lunil VS Code 插件，启动内置 language server，并连接可选的 C++、C#、
Unity 或 Godot 宿主定义。

## 前置条件

- VS Code 1.96 或更高版本；
- 包含至少一个 `.lua` 文件的 trusted workspace；
- 与操作系统和 CPU architecture 匹配的 VSIX。

插件不会在 Restricted Mode 中启动 executable，不收集 telemetry，也不会在运行时发起网络请求。

## 1. 安装 VSIX

从 0.14.0 release 下载匹配的 `lunil-lua-0.14.0-<target>.vsix` 与 `.sha256`。Target 包括
`win32-x64`、`win32-arm64`、`linux-x64`、`linux-arm64`、`darwin-x64`、`darwin-arm64`。

使用 **Extensions: Install from VSIX...**，或执行：

```bash
code --install-extension lunil-lua-0.14.0-win32-x64.vsix
```

每个 VSIX 只包含一个目标平台的 self-contained server，不需要单独安装 .NET。打开包含 Lua 文件的
trusted folder 后，Lunil status item 会显示启动和索引进度。

## 2. 配置宿主注入

从应用或 binding generator 导出 schema 1 `LuaHostAnalysisContract`，再设置 resource-scoped 值：

```json
{
  "lunil.hostContractPath": "${workspaceFolder}/generated/lunil-host-contract.json"
}
```

生成或测试配置也可使用 `lunil.hostContractJson` 内联 JSON；它优先于 path。Contract 变化会自动重载
analysis。运行 **Lunil: Show Virtual Host Contract**，可查看 completion、hover、navigation、callback
lifetime 与 persistence analysis 实际使用的 declaration 视图。

## 3. 操作 server

在 Command Palette 中使用：

| 命令 | 用途 |
| --- | --- |
| **Lunil: Restart Language Server** | 环境或 executable 变化后重启。 |
| **Lunil: Reindex Workspace** | 重建 compact module/reference index。 |
| **Lunil: Clear Analysis Cache** | 清除内存 analysis reuse 与当前 workspace index。 |
| **Lunil: Show Language Server Output** | 查看启动、重启与 protocol trace 输出。 |
| **Lunil: Show Virtual Host Contract** | 以虚拟 Lua 文档打开当前外部 API declaration。 |

异常退出会按 backoff 进行有次数上限的自动重启。达到限制后，使用 restart command 开始新的尝试序列。

## Settings

| Setting | 默认值 | 说明 |
| --- | --- | --- |
| `lunil.server.path` | 内置 server | 兼容替代 server 的绝对路径。 |
| `lunil.server.trace` | `off` | 在 Lunil output channel 使用 `off`、`messages` 或 `verbose` LSP trace。 |
| `lunil.server.maximumRestartCount` | `5` | 自动重启上限，范围 0–20。 |
| `lunil.server.gcHeapHardLimitPercent` | `70` | 内置 server 的 managed heap hard-limit 百分比，范围 20–90。 |
| `lunil.hostContractPath` | 空 | Host-analysis contract 的 resource-relative 或绝对路径。 |
| `lunil.hostContractJson` | 空 | 内联 contract JSON；优先于 path。 |

`lunil.server.path` 必须为绝对路径。修改 server path 或 heap limit 会重启 process；修改 host contract
会重载配置并重新索引 semantic data。

## 预期结果

Lua 文件会获得 diagnostic、completion、hover、signature、跨模块与外部 navigation、reference、
rename、symbol、semantic token、inlay hint、call hierarchy、folding、selection range 与 quick fix。
准确 protocol 和动态 Lua operation 的保守行为见 [language server reference](language-server.zh-CN.pub.md)。
