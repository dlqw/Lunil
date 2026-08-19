# 从 Lunil 0.16 迁移到 0.17

[English](migration-0.17.0.pub.md)

Lunil 0.17 保持 0.16 的编译器、运行时、宿主、分析与引擎入口源码兼容：本版本聚焦编辑器导航、
无注解 class 分析与 workspace 规模性能，不删除任何公共成员。三处行为变化在升级时需要留意：
跨模块类型推断让编辑器诊断更精确（诊断可能出现也可能消失）、超大的生成数据文件默认不参与索引、
language server 的驻留预算改为随机器内存自适应而非固定值。

## 1. 更新包与工具

将所有 Lunil 包引用更新到同一兼容线：

```xml
<PackageReference Include="Lunil.StandardLibrary" Version="0.17.0" />
<PackageReference Include="Lunil.Hosting" Version="0.17.0" />
```

```bash
dotnet tool update --global Lunil.Cli --version 0.17.0
```

## 2. 编辑器分析默认跨模块

`require` 结果在编辑器分析中携带被 require module 的导出类型，无注解的 metatable class 也端到端
携带实例类型。因此跨模块值上的诊断可能在过去被 `any` 隐藏的地方出现，也可能随推断变精确而消失。
`---@return` 注解按字面尊重——标注为 `Entity[]` 的查询就按 `Entity[]` 检查。

如果某些诊断码变得嘈杂，用 `lunil.server.suppressedDiagnosticCodes`（VS Code）或 CLI 的可重复
`--suppress <code>` 抑制具体代码，而不是关闭分析。

## 3. 生成数据文件默认排除出索引

`lunil.analysis.autoDetectDataFiles` 默认开启：经有界内容扫描（512 KB 起检、最多读取前 4 MB）
确认是纯生成数据（只有 key、字符串与数字的 table 字面量，没有函数、require 或控制流）的超大文件
不参与 workspace 索引。被排除但仍被代码 require 的 module 解析为 untyped value，而不是报告
unresolved-module 诊断。在编辑器中打开被排除的文件时仍会分析。

如果某个数据形态的文件必须被索引，将 `lunil.analysis.autoDetectDataFiles` 设为 `false`；
显式排除数据目录则使用 `lunil.analysis.exclude`。

## 4. Language server 内存预算自适应

Language server 的三项驻留预算——保留的 module 分析、已关闭 document 的源码与缓存的 document
分析——按运行时授予进程的内存（物理内存受 managed-heap hard limit 封顶）自适应，替代原先固定的
128/512/384 MB。每项预算是一个带夹紧的比例（下限 64–96 MiB、上限 512 MiB–1 GiB），三者合计不超过
可用内存的四分之一。内存充裕的机器为超大 workspace 获得余量；小内存机器向下限收敛。Heap hard
limit（`lunil.server.gcHeapHardLimitPercent`）仍然兜底失控增长。

Workspace snapshot 跨重建共享驻留的名称与 symbol key，未变更 module 直接从可复用的逐 module
projection 重合并而不再分析——无关编辑后的重建只分析被编辑 module 及其下游依赖模块。

## 5. 兼容性清单

- 没有公共成员被删除或改签名；0.16 API 面保持源码兼容。
- 新增公共 API：Lunil.Core 的 `SourceText.GetLineIndex(int)`、Lunil.EmmyLua 的注解
  `TagSpan`/`NameSpan`、Lunil.Workspace 的 `LuaWorkspace.StringInterner`
  （`LuaWorkspaceStringInterner`）。
- `api/0.17.0/` 基线取代 `api/0.16.0/` 成为冻结的兼容线。
- 编译器输出不变：0.17 的前端性能工作不改变语言行为、IR 或二进制 chunk（输出逐字节一致）。
  编辑器分析是独立的表面：如第 2 节所述，跨模块类型推断可能让编辑器诊断出现或消失。
- VS Code 插件新增 `lunil.locale`、`lunil.workspace.library`、`lunil.analysis.exclude`、
  `lunil.analysis.autoDetectDataFiles` 与 `lunil.statusBar.showModuleCount`。
- 注解 semantic token 为 legend 扩展了 `method` 类型；使用静态 legend 的 client 应重新生成。
