# Analysis facts reference

[简体中文](analysis-facts.zh-CN.pub.md)

This reference lists the source-analysis facts exposed by `LuaSemanticModel` and
`LuaAnalysisResult`. Use the [static analysis embedding guide](static-analysis-embedding.pub.md) for
an end-to-end integration and [external host analysis](external-host-analysis.pub.md) for APIs that
are defined outside Lua source.

## Reference facts

Code-reference collection is controlled by `LuaBinderOptions.CollectCodeReferences`. It is enabled
by `LuaWorkspace`; standalone binder or compiler consumers opt in when they need member and index
facts.

| Collection or query | Element | Purpose |
| --- | --- | --- |
| `LuaSemanticModel.References` | `LuaNameReference` | Local, captured-upvalue, and global name resolution. |
| `LuaSemanticModel.MemberReferences` | `LuaMemberReference` | Member and literal/dynamic index occurrences with receiver and index spans. |
| `LuaSemanticModel.UnifiedReferences` | `LuaCodeReference` | Ordered lexical, member, and index references in one stream. |
| `FindReferences(symbol)` | `LuaNameReference` | References with the identity of one compilation-local symbol. |
| `FindGlobalReferences(name)` | `LuaNameReference` | Implicit `_ENV` references grouped by global name. |
| `FindCodeReferences(symbol or span)` | `LuaCodeReference` | Unified references selected by symbol or intersecting span. |
| `FindCodeReferenceAt(bytePosition)` | `LuaCodeReference?` | The most specific code reference at a UTF-8 byte position. |
| `GetContainingFunction(span)` | `LuaFunctionInfo` | The lexical function that owns a source span. |

`LuaCodeReference.Kind` distinguishes `Name`, `Member`, and `Index`. `Access` combines `Read`,
`Write`, `Call`, and `MethodCall`. `ResolutionKind` distinguishes a lexical symbol, member
candidate, literal-index candidate, dynamic index, or incomplete syntax. Keep `ResolutionReason`
when displaying an unresolved or conservative result.

## Call graph

`LuaAnalysisResult.CallGraph` contains resolved, dynamic, and unresolved call sites.
Each site retains its source span, containing function, direct name or symbol when available,
receiver and callee types, and a static target only when analysis can prove one.

Do not discard dynamic or unresolved sites: their absence would turn an incomplete graph into a
false claim that no call exists. Use `LuaWorkspaceResult.GetCallGraph()` when module identities,
exports, re-exports, host functions, and stable function keys are required.

## Metatables and object models

| Collection | Key fields | Meaning |
| --- | --- | --- |
| `MetatableFacts` | `Span`, `RawType`, `MetatableType`, `EffectiveType`, `IsPrecise` | Result of a metatable attachment or lookup at one source location. |
| `ObjectModels` | `Name`, `DeclaringSpan`, `PrototypeType`, `InstanceType`, `BaseTypes`, `Methods`, `IsPrecise` | Prototype-style class and instance model inferred from table and metatable patterns. |

`IsPrecise == false` means dynamic writes, escaping tables, open shapes, or incompatible assignments
prevent a closed model. Consumers must retain that state instead of treating the listed members as
exhaustive. `LuaFunctionType.HasImplicitSelf` identifies a function whose colon-call contract
includes the receiver.

## Host effects and callbacks

| Collection | Key fields | Meaning |
| --- | --- | --- |
| `HostEffects` | `FunctionPath`, `Span`, `Effects`, `Source` | Effects declared for a called external function. |
| `CallbackRegistrations` | `FunctionPath`, `Span`, `CallbackSpan`, `CallbackFunctionId`, `Invocation`, `Cardinality`, `Retention`, `UnsubscribeFunction`, `Escapes` | Registration and lifetime of a callback passed to a host API. |

`LuaHostEffectKind` is a flag set covering global/table reads and writes, yield/throw behavior,
callback registration and removal, and persistence operations. Callback facts are available only
when the host contract identifies the callback parameter and lifetime. A missing
`CallbackFunctionId` means the callback target was not resolved to one Lua function.

## Persistence

`PersistenceAccesses` contains `LuaPersistenceAccessFact` values. The main fields are:

- `Operation`: `Read`, `Write`, `Delete`, or `Clear`;
- `Key` and `IsDynamicKey`: the exact key when known, otherwise an explicit dynamic-key state;
- `SchemaId` and `SchemaVersion`: the host-declared storage contract;
- `ValueType`: the value read or written;
- `MissingReturnsNil`: whether an absent read contributes `nil` to the result;
- `MigrationFunction`: the declared migration hook, when present.

Workspace compact snapshots expose `FindPersistenceSchemas(schemaId)` for cross-module queries.
They do not convert a dynamic key into a concrete schema entry.

## Closure upvalues

`UpvalueCells` contains `LuaUpvalueCellFact` values. Each fact associates one captured `Symbol` with
its inferred `Type`, reader and writer function IDs, `Escapes`, and `IsLoopCaptured`. Reader and
writer IDs are compilation-local; persist a stable function key rather than the raw integer across
snapshots.

A captured value is modeled as one shared cell. Consumers must not duplicate it per closure when
building mutation or lifetime views.

## Nil paths

`NilPaths` contains `LuaNilPathFact` values with `Span`, a printable `Path`, `HopCount`, `InputType`,
`ResultType`, and `WasNarrowed`. A fact describes the state at that program point; it is not a
runtime guarantee. Branch conditions, writes, calls, dynamic indexes, and control-flow joins can
change or widen later facts.

## Precision rules

- Every `TextSpan` is a half-open UTF-8 byte range owned by the result's source snapshot.
- Compilation-local symbol and function IDs are not stable storage identities.
- Dynamic mutation, unresolved calls, escaping values, and exhausted budgets remain explicit.
- Empty collections mean no fact was produced under the active options and contract; they do not
  prove that the runtime behavior cannot occur.
- External effects, callback lifetimes, and persistence schemas are only as complete as the
  attached `LuaHostAnalysisContract`.
