# 如何配置 Lunil language server

[English](configuring-the-language-server.pub.md)

本指南面向真实项目配置 language server：界面语言、宿主注入的 library stub、将生成数据文件排除出索引以及
内存预算。前提是插件已安装且 workspace 已索引；安装见
[VS Code](vscode.zh-CN.pub.md)，嵌入库的预算见
[面向大型仓库的 LuaWorkspace](large-workspaces.zh-CN.pub.md)。

下文设置采用 VS Code `settings.json` 格式（`lunil.*` 点分扁平名）。裸 server 载荷把同样的值嵌套在
`settings.lunil` 下（`workspace.library`、`analysis.exclude`、……）；后者形式见
[language server reference](language-server.zh-CN.pub.md)。

## 1. 选择界面语言

`lunil.locale`（`auto`、`en` 或 `zh-cn`，默认 `auto`）本地化 hover card、signature-help 文档、Lunil
菜单项、状态栏文本与索引状态消息。`auto` 跟随 VS Code 界面语言。裸 server 也接受 `locale` 作为
initialization option 或通过 `workspace/didChangeConfiguration` 传入。修改后无需重启 server 即可生效。

## 2. 用 library stub 描述宿主注入的 API

`lunil.workspace.library` 指向只读目录，内含描述宿主（C++、C#、Unity、Godot 或游戏引擎）运行时注入的
global 与 class 的 LuaLS 风格 `---@meta` declaration stub：

```json
{
  "lunil.workspace.library": ["${workspaceFolder}/meta/game", "${workspaceFolder}/meta/net"]
}
```

Stub 中的 global 会参与每一次分析，因此 `Game.Player.Move()` 之类的链式调用保留类型、文档注释、
成员补全与 signature hover，而不是退化为 `any`。声明的 `---@class` 类型加入 workspace 声明图（类型名
导航与继承），`require` 也可以解析 library 目录内的 module。修改 stub 后运行 **Lunil: Reindex
Workspace** 无需重启即可生效。这是手写、社区格式的路径；机器生成的 host contract 使用
`lunil.hostContractPath` / `lunil.hostContractJson` 代替。

## 3. 让生成数据远离索引

`lunil.analysis.autoDetectDataFiles`（默认开启）自动把超大的生成数据文件排除出索引。文件只含 key、
字符串与数字的 table 字面量（无函数、require 或控制流）时判定为纯数据。检测适用于至少 512 KB 的文件，
最多检查前 4 MB，因此普通代码和小型配置表不受影响。

`lunil.analysis.exclude` 增加显式 glob 模式：

```json
{
  "lunil.analysis.exclude": ["data/**", "**/*.data.lua", "assets/{tables,configs}/**"]
}
```

- 模式匹配相对 workspace 的路径，使用 `/` 分隔。
- 不含分隔符的模式匹配任意目录下的文件名。
- 匹配不区分大小写。

修改这两个设置都会无重启地重新扫描 workspace。被排除的文件在索引期间不会读入内存或分析，因此数 GB 的
生成语料不再撑大驻留或索引时间。被排除但仍被代码 require 的 module 解析为 untyped value，而不是报告
unresolved-module 诊断；已索引的 module 优先于排除列表。在编辑器中打开被排除的文件仍会分析，关闭后回到
排除集合。**Lunil: Show Index Status** 会列出被排除的文件及其原因（模式匹配或自动检测）。

## 4. 理解内存预算

Server 的驻留预算按运行时授予本进程的内存（受 managed-heap hard limit 封顶的物理内存）自适应：保留的
module 分析、已关闭 document 的源码与缓存的 document analysis 各占总量一个带夹紧的比例（下限 64–96
MiB、上限 512 MiB–1 GiB；合计不超过可用内存四分之一）。未变更 module 从可复用的 snapshot projection
重合并，并跨重建共享驻留的名称与 symbol key，重建峰值接近稳态。`lunil.server.gcHeapHardLimitPercent`
（20–90，默认 70）在进程整体上兜底失控增长。小内存机器把预算收向下限；大内存机器为超大 workspace 获得
余量。

预算本身无需配置，会自动适配。需要显式控制预算的宿主请改为嵌入 `LuaWorkspace`，使用
[LuaWorkspace options](large-workspaces.zh-CN.pub.md)。

## 预期结果

Server 使用所选语言、了解宿主注入的 API 面、索引时忽略生成数据，并按机器内存保持驻留成比例——每项设置的
查阅事实在 [language server reference](language-server.zh-CN.pub.md)，迁移说明在
[0.17 指南](migration-0.17.0.zh-CN.pub.md)。