# 如何向 Lunil 分析描述外部注入 API

[English](external-host-analysis.pub.md)

本指南说明如何描述只在 runtime 存在、Lua source 中没有定义的 C++、C#、Unity、Godot 或其他宿主
API。结果是经过验证的 `LuaHostAnalysisContract`，可供 standalone compilation、workspace analysis、
language server 与 VS Code 使用。

## 前置条件

- 已整理 injected global、module 与 callable path。
- 需要 definition navigation 时，为其准备稳定 source/implementation URI。
- 对具有 callback、side effect 或 persistence 语义的 function 明确其契约。

## 1. 描述宿主类型

使用 `LuaHostTypeDescriptor` 描述 primitive、table、function、thread 与 userdata。下面的 contract
描述由 `require("native")` 返回的宿主模块：

```csharp
using System.Collections.Immutable;
using Lunil.Analysis;

var integer = new LuaHostTypeDescriptor { Kind = LuaHostTypeKind.Integer };
var runType = new LuaHostTypeDescriptor
{
    Kind = LuaHostTypeKind.Function,
    Returns = [integer],
};

var nativeModule = new LuaHostTypeDescriptor
{
    Kind = LuaHostTypeKind.Table,
    Fields = ImmutableDictionary<string, LuaHostTypeDescriptor>.Empty
        .Add("run", runType),
};
```

只有 injected value 可能缺失时才设置 `IsNullable`。已知 table shape 使用 `Fields`，function shape
使用 `Parameters`/`Returns`。省略类型或使用 `Any` 会保留 dynamic 状态，不代表所有 member 都存在。

## 2. 添加 module 与 function

调用 `Build()` 时，`LuaHostContractBuilder` 会验证名称、function path、parameter index、callback
contract 和 persistence contract：

```csharp
var location = new LuaHostSourceLocation
{
    Uri = "cpp://engine/native.hpp#run",
    Line = 12,
    Column = 7,
    ImplementationUri = "cpp-implementation://engine/native.cpp#run",
};

var contract = new LuaHostContractBuilder("game-host")
    .AddModule("native", nativeModule)
    .AddFunction(new LuaHostFunctionContract
    {
        Path = "native.run",
        Returns = [integer],
        Source = location,
    })
    .Build();
```

`ContractId` 是稳定 external symbol/function key 的一部分。同一个宿主表面必须保持不变。
`Source.Uri` 是 definition target，`ImplementationUri` 是 implementation target；line/column 从零开始。

Injected global value 使用 `AddGlobal`。同一路径存在多个 call shape 时使用 `Overloads`，不要把不兼容
overload 压成一个 `Any` signature。

## 3. 声明 callback、effect 与 persistence

只在宿主行为明确时添加 metadata：

| Contract | 必要信息 | 产生的分析 |
| --- | --- | --- |
| `LuaHostEffectKind` | Global/table read/write、yield/throw、callback 与 persistence flag | `HostEffects` 和保守 call behavior |
| `LuaHostCallbackContract` | Parameter index、同步/延迟/异步 invocation、once/many cardinality、borrowed/stored retention、可选 unsubscribe path | Callback registration 与 escape/lifetime fact |
| `LuaHostPersistenceContract` | Operation、key/value parameter index、schema ID/version、value type、缺失值行为、可选 migration path | Read/write/delete/clear fact 与 persistence schema |

Callback parameter index 必须指向 function-compatible parameter。Write persistence contract 必须指定有效
value parameter。`Build()`、`Validate()` 和 `ParseJson()` 会拒绝无效 schema version 与不一致 shape，
不会静默丢弃 metadata。

## 4. 序列化并检查 contract

使用内置确定性表示：

```csharp
string json = contract.ToJson();
LuaHostAnalysisContract loaded = LuaHostAnalysisContract.ParseJson(json);
loaded.Validate();

string declarations = loaded.ToLuaStub();
```

非 .NET generator 使用 JSON 作为交换格式。`ToLuaStub()` 只用于检查和 editor 辅助；effect、source
location、callback 与 persistence metadata 仍以 JSON contract 为准。

生成的 `LuaClrBindingRegistry` 可以通过 `CreateAnalysisContract(contractId)` 建立匹配的 C# contract。
游戏循环 persistence host 可使用 `LuaGameLoopAnalysisContracts.CreatePersistenceContract(...)`。

## 5. 应用于 standalone compilation

通过 `LuaAnalysisEnvironment` 传入 contract：

```csharp
using Lunil.Compiler;

var environment = new LuaAnalysisEnvironment { HostContract = loaded };
var source = LuaSourceDocument.FromUtf8(
    "local native = require('native'); return native.run()",
    "@game/app.lua");

var result = new LuaCompiler().Compile(
    source,
    environment,
    System.Threading.CancellationToken.None);
```

Compilation 会把 host type 用于 call、expression result、effect、callback、persistence 与 external
source location。下游需要解释 fact 来源时，应把 environment 与 immutable result 保持关联。

## 6. 应用于 workspace

把同一个 contract 设到 `LuaWorkspaceOptions`：

```csharp
using Lunil.Workspace;

using var workspace = new LuaWorkspace(new LuaWorkspaceOptions
{
    HostContract = loaded,
});

var result = await workspace.AnalyzeAsync(
    documents,
    System.Threading.CancellationToken.None);
```

这里的 `documents` 是宿主已有的 `IEnumerable<LuaWorkspaceDocument>` 输入。

Host module 会参与 dependency resolution、export、call binding、definition/implementation navigation、
callback edge 与 persistence schema。同一 workspace cache domain 应使用一个 immutable contract；contract
变化时，用新 contract 创建下一份 workspace/configuration snapshot，使失效依据新的 contract hash。

Editor 配置方式见 [VS Code 指南](vscode.zh-CN.pub.md)。Active configuration 变化后，language server
会验证 contract 并重新索引 semantic data。

## 预期结果

无需伪造 Lua implementation，Lua code 即可解析 injected global 与 module。精确宿主声明会产生 typed
call 与可导航 external location；不完整或 dynamic contract entry 保持保守状态，不会虚构 definition
或 effect。
