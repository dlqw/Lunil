# How to describe externally injected APIs to Lunil analysis

[简体中文](external-host-analysis.zh-CN.pub.md)

This guide shows how to describe C++, C#, Unity, Godot, or other host APIs that exist at runtime but
not in Lua source. The result is a validated `LuaHostAnalysisContract` that standalone compilation,
workspace analysis, the language server, and VS Code can consume.

## Prerequisites

- An inventory of injected globals, modules, and callable paths.
- Stable source or implementation URIs when definition navigation is required.
- Explicit callback, side-effect, and persistence semantics for functions that have them.

## 1. Describe host types

Use `LuaHostTypeDescriptor` for primitives, tables, functions, threads, and userdata. The following
contract describes a host module returned by `require("native")`:

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

Set `IsNullable` only when the injected value can be absent. Use `Fields` for a known table shape and
`Parameters`/`Returns` for a function shape. An omitted or `Any` type remains dynamic; it is not a
promise that every member exists.

## 2. Add modules and functions

`LuaHostContractBuilder` validates names, function paths, parameter indexes, callback contracts, and
persistence contracts when `Build()` is called:

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

`ContractId` is part of stable external symbol and function keys. Keep it unchanged for the same host
surface. `Source.Uri` is the definition target; `ImplementationUri` is the implementation target.
Lines and columns are zero-based.

Use `AddGlobal` for injected global values. Use `Overloads` when one path has multiple call shapes;
do not flatten incompatible overloads into one `Any` signature.

## 3. Declare callbacks, effects, and persistence

Add metadata only when the host behavior is known:

| Contract | Required information | Analysis produced |
| --- | --- | --- |
| `LuaHostEffectKind` | Global/table reads or writes, yield/throw, callback, and persistence flags | `HostEffects` and conservative call behavior |
| `LuaHostCallbackContract` | Parameter index, synchronous/deferred/asynchronous invocation, once/many cardinality, borrowed/stored retention, optional unsubscribe path | Callback registration and escape/lifetime facts |
| `LuaHostPersistenceContract` | Operation, key/value parameter indexes, schema ID/version, value type, missing-value behavior, optional migration path | Read/write/delete/clear facts and persistence schemas |

A callback parameter index must refer to a function-compatible parameter. A write persistence
contract must identify a valid value parameter. `Build()`, `Validate()`, and `ParseJson()` reject
invalid schema versions and inconsistent shapes instead of silently dropping metadata.

## 4. Serialize and inspect the contract

Use the built-in deterministic representations:

```csharp
string json = contract.ToJson();
LuaHostAnalysisContract loaded = LuaHostAnalysisContract.ParseJson(json);
loaded.Validate();

string declarations = loaded.ToLuaStub();
```

Store JSON as the interchange format for non-.NET generators. `ToLuaStub()` is an inspection and
editor aid; the JSON contract remains authoritative for effects, source locations, callbacks, and
persistence metadata.

A generated `LuaClrBindingRegistry` can create a matching C# contract with
`CreateAnalysisContract(contractId)`. Game-loop persistence hosts can use
`LuaGameLoopAnalysisContracts.CreatePersistenceContract(...)`.

## 5. Apply it to standalone compilation

Pass the contract through `LuaAnalysisEnvironment`:

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

The compilation now uses host types for calls, expression results, effects, callbacks, persistence,
and external source locations. Keep the environment with the immutable result when downstream code
needs to explain where a fact came from.

## 6. Apply it to a workspace

Attach the same contract to `LuaWorkspaceOptions`:

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

Here, `documents` is the host's existing `IEnumerable<LuaWorkspaceDocument>` input.

Host modules participate in dependency resolution, exports, call bindings, definition and
implementation navigation, callback edges, and persistence schemas. Use one immutable contract for
a workspace cache domain. If the contract changes, create the next workspace/configuration snapshot
with the new contract so invalidation uses the new contract hash.

For editor configuration, point the VS Code extension at the JSON file as described in the
[VS Code guide](vscode.pub.md). The language server validates the contract and reindexes semantic
data when the active configuration changes.

## Expected result

Lua code can resolve injected globals and modules without placeholder Lua implementations. Precise
host declarations produce typed calls and navigable external locations; incomplete or dynamic
contract entries remain conservative rather than inventing definitions or effects.
