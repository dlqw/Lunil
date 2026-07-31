# 分析 fact reference

[English](analysis-facts.pub.md)

本文列出 `LuaSemanticModel` 与 `LuaAnalysisResult` 公开的源码分析 fact。端到端接入流程见
[静态分析嵌入](static-analysis-embedding.zh-CN.pub.md)，Lua source 之外定义的 API 见
[外部宿主分析](external-host-analysis.zh-CN.pub.md)。

## Reference fact

Code reference 收集由 `LuaBinderOptions.CollectCodeReferences` 控制。`LuaWorkspace` 会自动启用；
standalone binder/compiler consumer 只在需要 member 与 index fact 时开启。

| 集合或查询 | 元素 | 用途 |
| --- | --- | --- |
| `LuaSemanticModel.References` | `LuaNameReference` | Local、captured upvalue 与 global name resolution。 |
| `LuaSemanticModel.MemberReferences` | `LuaMemberReference` | 带 receiver/index span 的 member 与 literal/dynamic index occurrence。 |
| `LuaSemanticModel.UnifiedReferences` | `LuaCodeReference` | 按源码顺序合并 lexical、member 与 index reference。 |
| `FindReferences(symbol)` | `LuaNameReference` | 使用同一 compilation-local symbol identity 的 reference。 |
| `FindGlobalReferences(name)` | `LuaNameReference` | 按 global name 分组的隐式 `_ENV` reference。 |
| `FindCodeReferences(symbol or span)` | `LuaCodeReference` | 按 symbol 或相交 span 选择 unified reference。 |
| `FindCodeReferenceAt(bytePosition)` | `LuaCodeReference?` | UTF-8 byte position 上最具体的 code reference。 |
| `GetContainingFunction(span)` | `LuaFunctionInfo` | 拥有某个 source span 的 lexical function。 |

`LuaCodeReference.Kind` 区分 `Name`、`Member` 与 `Index`；`Access` 可组合 `Read`、`Write`、
`Call` 和 `MethodCall`。`ResolutionKind` 区分 lexical symbol、member candidate、literal-index
candidate、dynamic index 与 incomplete syntax。展示 unresolved/conservative 结果时应保留
`ResolutionReason`。

## Call graph

`LuaAnalysisResult.CallGraph` 包含 resolved、dynamic 与 unresolved call site。
每个 site 保留 source span、containing function、可用的 direct name/symbol、receiver/callee type，
并且只在分析能够证明时提供 static target。

不能丢弃 dynamic 或 unresolved site，否则不完整 graph 会被错误解释成“不存在调用”。需要 module
identity、export、re-export、host function 与稳定 function key 时，使用
`LuaWorkspaceResult.GetCallGraph()`。

## Metatable 与 object model

| 集合 | 关键字段 | 含义 |
| --- | --- | --- |
| `MetatableFacts` | `Span`、`RawType`、`MetatableType`、`EffectiveType`、`IsPrecise` | 某个源码位置的 metatable attach 或 lookup 结果。 |
| `ObjectModels` | `Name`、`DeclaringSpan`、`PrototypeType`、`InstanceType`、`BaseTypes`、`Methods`、`IsPrecise` | 从 table/metatable 模式推断的 prototype class 与 instance model。 |

`IsPrecise == false` 表示 dynamic write、escaping table、open shape 或不兼容赋值阻止了 closed
model。Consumer 必须保留该状态，不能把已列 member 当成完整集合。
`LuaFunctionType.HasImplicitSelf` 标识 colon-call contract 中包含 receiver 的 function。

## Host effect 与 callback

| 集合 | 关键字段 | 含义 |
| --- | --- | --- |
| `HostEffects` | `FunctionPath`、`Span`、`Effects`、`Source` | 被调用 external function 声明的 effect。 |
| `CallbackRegistrations` | `FunctionPath`、`Span`、`CallbackSpan`、`CallbackFunctionId`、`Invocation`、`Cardinality`、`Retention`、`UnsubscribeFunction`、`Escapes` | 传给 host API 的 callback registration 与 lifetime。 |

`LuaHostEffectKind` 是 flags，覆盖 global/table read/write、yield/throw、callback register/remove
和 persistence operation。只有 host contract 指明 callback parameter 与 lifetime 时才会产生对应
fact。`CallbackFunctionId` 为空表示 callback target 未解析为单个 Lua function。

## Persistence

`PersistenceAccesses` 包含 `LuaPersistenceAccessFact`：

- `Operation`：`Read`、`Write`、`Delete` 或 `Clear`；
- `Key` 与 `IsDynamicKey`：已知时为精确 key，否则保留 dynamic-key 状态；
- `SchemaId` 与 `SchemaVersion`：宿主声明的 storage contract；
- `ValueType`：读取或写入的 value type；
- `MissingReturnsNil`：缺失读取是否把 `nil` 加入结果；
- `MigrationFunction`：存在时为声明的 migration hook。

Workspace compact snapshot 提供 `FindPersistenceSchemas(schemaId)` 跨模块查询，但不会把 dynamic
key 转换成具体 schema entry。

## Closure upvalue

`UpvalueCells` 包含 `LuaUpvalueCellFact`。每个 fact 将被捕获的 `Symbol` 与推断 `Type`、reader/writer
function ID、`Escapes` 和 `IsLoopCaptured` 关联。Reader/writer ID 只在 compilation 内有效；跨 snapshot
持久化时使用稳定 function key。

Captured value 被建模为一个共享 cell。构建 mutation 或 lifetime view 时不能为每个 closure 复制一份。

## Nil path

`NilPaths` 包含带 `Span`、可显示 `Path`、`HopCount`、`InputType`、`ResultType` 与
`WasNarrowed` 的 `LuaNilPathFact`。Fact 描述该 program point 的状态，不是 runtime guarantee。
Branch condition、write、call、dynamic index 和 control-flow join 可以改变或 widen 后续 fact。

## Precision 规则

- 所有 `TextSpan` 都是所属 source snapshot 的半开 UTF-8 byte range。
- Compilation-local symbol/function ID 不能作为稳定存储 identity。
- Dynamic mutation、unresolved call、escaping value 与预算耗尽必须保持显式状态。
- 空集合只表示当前 option 与 contract 下没有产生 fact，不证明 runtime 行为不可能发生。
- External effect、callback lifetime 与 persistence schema 的完整度取决于附加的
  `LuaHostAnalysisContract`。
