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

从 0.17.0 release 下载匹配的 `lunil-lua-0.17.0-<target>.vsix` 与 `.sha256`。Target 包括
`win32-x64`、`win32-arm64`、`linux-x64`、`linux-arm64`、`darwin-x64`、`darwin-arm64`。

使用 **Extensions: Install from VSIX...**，或执行：

```bash
code --install-extension lunil-lua-0.17.0-win32-x64.vsix
```

每个 VSIX 只包含一个目标平台的 self-contained server，不需要单独安装 .NET。打开包含 Lua 文件的
trusted folder 后，Lunil status item 会显示启动和索引进度。首次激活时，如果内置 payload 仍在
解压，checksum 校验会进行有上限的 backoff 等待；manifest 格式错误或 checksum 不匹配仍会立即失败。

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
| **Lunil: Show Index Status** | 列出已索引、失败、等待与排除的文件；失败文件提供单文件重试，排除文件显示排除原因。 |
| **Lunil: Show Language Server Output** | 查看启动、重启与 protocol trace 输出。 |
| **Lunil: Show Virtual Host Contract** | 以虚拟 Lua 文档打开当前外部 API declaration。 |

异常退出会按 backoff 进行有次数上限的自动重启。达到限制后，使用 restart command 开始新的尝试序列。

Request 或 notification handler 的意外失败会把完整 managed exception stack 写入 Lunil output
channel，同时保持 JSON-RPC error response 简洁。报告 server 故障时请附上该 stack。

## Settings

| Setting | 默认值 | 说明 |
| --- | --- | --- |
| `lunil.server.path` | 内置 server | 兼容替代 server 的绝对路径。 |
| `lunil.server.trace` | `off` | 在 Lunil output channel 使用 `off`、`messages` 或 `verbose` LSP trace。 |
| `lunil.server.maximumRestartCount` | `5` | 自动重启上限，范围 0–20。 |
| `lunil.server.gcHeapHardLimitPercent` | `70` | 内置 server 的 managed heap hard-limit 百分比，范围 20–90。 |
| `lunil.server.suppressedDiagnosticCodes` | `[]` | language server 分析抑制的诊断码数组（例如 `LUA6022`）。 |
| `lunil.locale` | `auto` | `auto`、`en` 或 `zh-cn`；本地化 hover card、菜单与状态文本，无需重启。 |
| `lunil.hostContractPath` | 空 | Host-analysis contract 的 resource-relative 或绝对路径。 |
| `lunil.hostContractJson` | 空 | 内联 contract JSON；优先于 path。 |
| `lunil.workspace.library` | `[]` | LuaLS 风格 `---@meta` stub 目录，描述宿主注入的 global 与 class。 |
| `lunil.analysis.exclude` | `[]` | Glob 模式（相对 workspace、`/` 分隔），匹配的 Lua 文件不参与索引。 |
| `lunil.analysis.autoDetectDataFiles` | `true` | 自动检测超大的生成数据文件并排除出索引。 |
| `lunil.statusBar.showModuleCount` | `true` | 索引完成后在状态项中显示已索引 module 数。 |

`lunil.server.path` 必须为绝对路径。修改 server path 或 heap limit 会重启 process；修改 host
contract、library 目录或 analysis 排除规则（`lunil.analysis.exclude` 或
`lunil.analysis.autoDetectDataFiles`）会重载配置并重新索引 semantic data。被排除的文件在
编辑器中打开时仍会被分析。设置的详细行为（glob 语义、自动检测边界、自适应预算）见
[配置 language server](configuring-the-language-server.zh-CN.pub.md)。

## 调试 Lua 代码

插件贡献了 `lunil` 调试器类型，支持两种配置：

- **Launch** 在参考解释器下运行 `.lua` 文件，支持断点、单步、暂停、调用栈、局部变量与上值
  （`program` 指向脚本）。
- **Attach** 连接暴露命名管道调试端点的游戏循环宿主（`debugPipe` 指定管道名；宿主用
  `LuaGameLoopHost.StartDebugServer` 启动）。

adapter 可执行文件（`lunil-debug-adapter`）与 language server 一样内置在 VSIX 中。操作指南见
[调试 Lua](debugging.zh-CN.pub.md)，受支持的协议面见[调试参考](debugging-reference.zh-CN.pub.md)。

## 预期结果

Lua 文件会获得 diagnostic、completion、hover、signature、跨模块与外部 navigation、reference、
rename、symbol、semantic token（含注解类型表达式）、inlay hint、call hierarchy、folding、
selection range 与 quick fix。悬停 class 或类型名会显示带继承链与成员签名的 class card；标准库
成员自带文档并链接到只读的 builtin 页面。准确 protocol 和动态 Lua operation 的保守行为见
[language server reference](language-server.zh-CN.pub.md)。
