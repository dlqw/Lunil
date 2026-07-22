# 静态分析嵌入指南

[English](static-analysis-embedding.md)

本指南面向把 Lunil 用作编译器与代码智能库的宿主。仓库中的可执行 sample 是本文的可编译事实来源：

```bash
dotnet run --project samples/Lunil.StaticAnalysis.Embedding -c Release
dotnet test tests/Lunil.Workspace.Tests/Lunil.Workspace.Tests.csproj -c Release \
  --filter FullyQualifiedName~StaticAnalysisEmbeddingSampleTests
```

该 sample 会编译一个带 annotation 的单文件，输出 semantic/analysis index，连续三次分析一个
双模块循环依赖 workspace，展示 cache 复用与失效，并由 solution test 实际执行。

## 配置统一管线

`LuaCompilerOptions.LanguageVersion` 是单文件 compilation 的权威语言契约；
`LuaWorkspaceOptions.LanguageVersion` 是 workspace 的权威契约，并会把内部 compiler 对齐到该版本。
Analysis 限制只配置一次，同时在两个上下文选择相同版本：

```csharp
var compilerOptions = LuaCompilerOptions.Default with
{
    LanguageVersion = LuaLanguageVersion.Lua54,
    Analysis = LuaAnalysisOptions.Default with
    {
        ReportUnknownGlobals = true,
        ReportImplicitAny = true,
        MaximumTypeCount = 20_000,
        MaximumConstraintCount = 40_000,
    },
};

var result = new LuaCompiler(compilerOptions).CompileUtf8(
    source,
    sourceName: "@game/player.lua",
    cancellationToken);
```

一个 `LuaCompilationResult` 持有内部一致的不可变 snapshot：

- `Syntax`：无损 token 与强类型 syntax facade；
- `Annotations`：解析后的 EmmyLua/LuaLS directive；
- `SemanticModel`：symbol、reference、function 与稳定 key 操作；
- `Analysis`：declaration/expression/function type、CFG、预算与 call graph；
- `Diagnostics`：带 `LuaCompilationPhase` 的有序诊断；
- `Module`：完成 lowering 与 verification 后的 canonical IR。

执行或持久化 canonical IR 前必须检查 `Succeeded`。存在 warning 或可恢复源码错误时，静态分析结果
仍可能有用，因此应将 diagnostic 与 snapshot 一同保存，而不是直接丢弃整个结果。

## Byte span 与编辑器位置

`SourceText` 保存 UTF-8 byte。所有 `TextSpan` 都是半开 UTF-8 byte 区间 `[Start, End)`，不是
UTF-16 字符串索引。必须通过所属 source 转换 offset：

```csharp
SourceLocation start = result.Source.Text.GetLocation(span.Start);
SourceLocation end = result.Source.Text.GetLocation(span.End);
```

`SourceLocation.Line`、`ByteColumn` 和 `Utf16Column` 都从零开始。LSP position 可直接使用 `Line` 与
`Utf16Column`；一基 UI 则应像 sample 的 `FormatSpan` 一样对两者加一。不得把 span 应用到另一个
source snapshot。

## 关联 semantic 与 analysis 数据

`LuaSymbol.Id` 与 `LuaFunctionInfo.Id` 只在本次 compilation 内有效。在同一个 result 内：

- 通过对象身份或 symbol ID 关联 `LuaSymbolTypeInfo.Symbol`；
- 通过 ID 关联 `LuaFunctionAnalysis.FunctionId` 与 `LuaSemanticModel.Functions.Id`；
- 通过 byte span 关联 expression type 与 syntax；
- local/upvalue 使用 `LuaSemanticModel.FindReferences(symbol)`；
- 隐式 `_ENV` global 使用 `FindGlobalReferences(name)`；
- 调用关系直接读取 `LuaAnalysisResult.CallGraph`，不要重新解释 generic AST child。

需要持久化时，用逻辑 module identity 生成 `LuaSymbolKey`：

```csharp
var key = result.SemanticModel.GetSymbolKey(symbol, "game.player");
var later = anotherResult.SemanticModel.ResolveSymbolKey(key, "game.player");
```

稳定 key 不含源码 offset 与瞬时 ID。声明重命名、移动到不同 lexical owner 或更换 module identity
都可以产生新 key。Class、alias 与 enum 使用 `LuaCompilationResult.GetAnnotationKey` 和
`ResolveAnnotationKey`。

## 读取 CFG、调用与类型声明

每个 `LuaFunctionAnalysis` 都提供推断 function type、return pack、flow iteration、widening 状态和
`LuaControlFlowGraph`。判断活跃路径应读取 `block.IsReachable`，不能假设所有生成 block 都可达。
Call graph edge 会保留 resolved、dynamic、unresolved 与 unreachable call，并携带 containing
function 和可选 target function。

`LuaAnalysisResult.TypeDeclarations` 是 class、alias 与 enum directive 的类型解析视图；
`LuaAnnotationDocument.Annotations` 是 syntax 视图。类型消费使用前者，需要保留精确 directive 与
source span 的工具使用后者。

## 使用可复用 workspace

`LuaModuleIdentity` 应使用逻辑 module 名称，`SourceIdentity` 应使用稳定的源码来源字符串：

```csharp
var documents = new[]
{
    LuaWorkspaceDocument.FromUtf8("game.client", client, "@game/client.lua"),
    LuaWorkspaceDocument.FromUtf8("game.service", service, "@game/service.lua"),
};

using var workspace = new LuaWorkspace(new LuaWorkspaceOptions
{
    LanguageVersion = LuaLanguageVersion.Lua54,
    Compiler = compilerOptions,
    MaximumModuleCount = 128,
    MaximumDependencyCount = 512,
    MaximumFixedPointIterations = 8,
    MaximumParallelism = 4,
});

LuaWorkspaceResult snapshot = await workspace.AnalyzeAsync(documents, cancellationToken);
```

Module name 是 `require` graph identity。`SourceIdentity` 用于诊断与持久化的源码来源，不应使用不稳定
临时路径或部署绝对路径；content hash 由源码 byte 生成。同一 snapshot 中 module name 必须唯一。

磁盘模块可使用 `LuaFileSystemModuleResolver`，它会在受 root 限制的目录内按 `?.lua` 和
`?/init.lua` 映射请求。`game.player` 会对应 `game/player.lua` 或 `game/player/init.lua`。自定义 resolver
应对相同请求返回相同逻辑 module，并正确响应 cancellation。

## 循环、cache 复用与失效

Workspace result 的 graph 包含强连通分量；`IsCyclic` 标记需要 fixed-point 分析的分量。对每个 module
应保留 exported type/hash、`FixedPointIterationCount` 与 `WasWidened`，同时保留 fixed-point 达到上限
等 workspace diagnostic。

同一个项目/cache domain 的连续不可变 snapshot 应复用同一个 `LuaWorkspace`。
`LuaWorkspaceMetrics` 区分 cache hit、miss、失效 module、fixed-point iteration 与内部峰值并行度。
完全相同的 snapshot 应产生 cache hit；module export 变化时，其 dependent 会被保守失效。
`ClearCache()` 会显式丢弃复用状态。

`FindReferences(LuaSymbolKey)`、`FindGlobalReferences(string)` 与 `GetCallGraph()` 可跨完整 workspace
投影代码索引，并附带 module/source identity 与稳定 containing-function key。

## 生命周期、并发、诊断与预算

- Compilation/workspace result 是不可变 snapshot，可以并发读取。
- `LuaWorkspace` 接受并发调用，但会串行化顶层 operation 以保持 cache 顺序；`MaximumParallelism`
  控制单次 operation 内部工作。
- 应复用 workspace，而不是每次编辑都新建；cache domain 结束时再释放。释放期间已经开始的 operation
  可以完成，之后的新 operation 会被拒绝。
- `CancellationToken` 应贯穿 compiler、workspace 与 resolver。
- Compilation diagnostic 应保存 `LuaCompilationPhase`；workspace diagnostic 应保存
  `LuaWorkspaceDiagnosticPhase`、可选内部 compilation phase、module、code、severity、span 与
  source identity。
- 面向不可信或大型工程，应配置 compiler 的 type/constraint/CFG/generic 预算，以及 workspace 的
  module/dependency/source/cache/fixed-point/diagnostic 预算。预算耗尽或 widening 是明确的分析状态，
  不能当作精确成功结果。

以上全部操作的完整可编译实现位于
[`samples/Lunil.StaticAnalysis.Embedding`](../samples/Lunil.StaticAnalysis.Embedding/EmbeddingScenario.cs)。
