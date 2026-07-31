# 从 Lunil 0.13 迁移到 0.14

[English](migration-0.14.0.pub.md)

Lunil 0.14 保持 0.13 runtime、hosting、Unity、Godot 与 `netstandard2.1` 入口的源码兼容。此版本新增
compiler/analysis surface；下列唯一需要选择的项目只影响新 syntax retention 与详细 code-reference
collection。

## 1. 更新 package 与 tool

把全部 Lunil package reference 作为同一 compatibility line 更新：

```xml
<PackageReference Include="Lunil.Hosting" Version="0.14.0" />
<PackageReference Include="Lunil.Workspace" Version="0.14.0" />
```

```bash
dotnet tool update --global Lunil.Cli --version 0.14.0
```

同一 process 中不得混用 0.13 与 0.14 assembly。Unity 用户安装
`com.dlqw.lunil-0.14.0.tgz`；Godot 用户同时更新 `Lunil.Godot` 和 0.14.0 addon。

## 2. 保留既有 compiler 调用或采用 staged snapshot

`LuaCompiler.Compile*` 继续受支持。需要 syntax-only、binding-only 或 analysis snapshot 的宿主可使用
`LuaFrontEndSession.Process` 与 `Advance`。`LuaFrontEndSnapshot.Stage` 标识已完成 stage；`Metrics`
报告各 operation 的 elapsed time 与 current-thread managed allocation。

`LuaParserOptions.UseCompactSyntaxArena` 默认为 `true`。Parse-only/incremental consumer 会保留 compact
arena，并按需 materialize 既有 node facade。只有 direct parser consumer 会立即 bind 完整 tree 且希望
避免 compact copy 时才设为 `false`。`LuaFrontEndSession` 会为 binding 及后续 stage 自动选择
materialized path。

## 3. 按需启用详细 binder reference

`LuaSemanticModel.References` 保持 0.13 lexical-name 行为。`LuaBinderOptions.CollectCodeReferences`
为 `true` 时才填充新的 member/unified index：

```csharp
var binder = LuaBinderOptions.Default with
{
    CollectCodeReferences = true,
};
```

`LuaWorkspace` 会自动启用。Standalone compiler pipeline 默认关闭，避免承担 workspace-only member/
reference indexing cost。

## 4. 保守消费新的 analysis fact

`LuaAnalysisResult` 新增 metatable、object-model、host-effect、callback-registration、
persistence-access、upvalue-cell 与 nil-path fact。这些都是 Lua dynamic semantics 上的增量 projection：

- 提供确定性 editor action 前检查 fact precision/resolution state；
- 保留 candidate 与 unresolved cross-module call result；
- mutation、dynamic index、escape 或 open host type 会使 precision widen；
- 在 `rawget`、`rawset`、`__index`、`__newindex` 周围区分 raw/effective member。

消费这些 fact 不会改变 runtime 行为。

## 5. 描述宿主注入 API

尽可能用 schema 1 `LuaHostAnalysisContract` 替代手写 analysis global。Contract 可描述 global、module、
function、overload、外部 source/implementation location、callback lifetime、side effect，以及 persistence
read/write/delete/clear operation。

Standalone analysis 通过 `LuaAnalysisEnvironment.HostContract` 传入；workspace 使用
`LuaWorkspaceOptions.HostContract`。生成的 C# binding registry 可投影 reflection-free contract；C++ 和
其他宿主也可输出同一确定性 JSON schema。

## 6. 选择 full 或 compact workspace snapshot

既有 `AnalyzeAsync` 返回每个 module 的完整 compilation result。大型或长生命周期 editor workspace
应迁移到 `AnalyzeCompactAsync`；它保留可查询 reference/call/callback/persistence index，不保留完整
compiler tree。应显式配置 module、dependency、source、queue、memory-cache、disk-cache 与 diagnostic
预算。

Sizing 与生命周期规则见[大型 workspace 分析](large-workspaces.zh-CN.pub.md)。

## 7. 添加 editor tooling

`lunil-language-server` package 与 platform-specific VSIX 是 0.14 新增项。VS Code 插件要求 VS Code
1.96 或更高版本，并且只在 trusted workspace 启动。配置 embedded analysis 使用的同一份 host-contract
JSON，即可获得 C++、C#、Unity 或 Godot 定义的 completion/navigation。

详见 [language server reference](language-server.zh-CN.pub.md) 与 [VS Code 指南](vscode.zh-CN.pub.md)。

## 兼容性检查清单

- 保持预期 `LuaLanguageVersion`；默认仍为 Lua 5.4 语言契约，兼容性基线为 PUC Lua 5.4.8；
- `TextSpan` 必须与其所属 UTF-8 source snapshot 绑定；
- 只有 direct binder/compiler consumer 需要新 member/unified index 时才启用
  `CollectCodeReferences`；
- 跨 snapshot 复用 `LuaWorkspace`，cache domain 结束时再 dispose；
- 持久化前验证 host-contract schema/version 与稳定 module/source identity；
- Package、CLI、Unity/Godot、language-server 与 VSIX asset 应一同更新到 0.14.0。
