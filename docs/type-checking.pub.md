# Type checking reference

[简体中文](type-checking.zh-CN.pub.md)

Reference for Lunil's annotation-driven type checking: the diagnostics it produces, the
suppression surface, and the bounds of the check. Type checking is **enabled by default** for
Lua files that carry EmmyLua-style annotations; files without annotations are not affected.
`---@type`, `---@param`, `---@return`, `---@class`, `---@alias`, `---@enum`, and `---@cast`
annotations feed a bounded flow analysis (`LuaTypeAnalyzer`) that reports on the `LUA6000` line;
the workspace additionally checks cross-module consistency on annotated `require` consumers.

## Diagnostics

| Code | Reports |
| --- | --- |
| `LUA6001` | Unknown annotation type name. |
| `LUA6002` | Type name declared more than once. |
| `LUA6003` | Value not assignable to an annotated type: parameter, return value, initializer, assignment, or operand. |
| `LUA6004` | Value of a known type is not callable. |
| `LUA6006` | Call argument count does not match the selected signature. |
| `LUA6007` | Indexed access has no statically exposed value. |
| `LUA6008` | Local read before an explicit assignment. |
| `LUA6009` | Statement is unreachable under the current flow types. |
| `LUA6010` | Static analysis exceeded a configured budget and widened remaining values to `unknown`. |
| `LUA6012` | Recursive type declaration widened to `unknown`. |
| `LUA6013` | `---@cast` produces the impossible type `never`. |
| `LUA6014` | Parameter has implicit type `any` (when implicit-any reporting is enabled). |
| `LUA6015` | Global has no known static type (when unknown-global reporting is enabled). |
| `LUA6016` | Condition is always truthy or always falsy under the current flow types. |
| `LUA6017` | Colon call supplies an implicit `self` to a function declared without `self`. |
| `LUA6018` | Dot call omits the implicit `self` required by a colon-declared method. |
| `LUA6019` | Runtime prototype member conflicts with its class annotation type. |
| `LUA6020` | Path may be `nil` before the access. |
| `LUA6022` | `---@type` annotation on a `require` consumer is not assignable to the module's exported type (workspace diagnostics). |

Diagnostics are warnings by default and can be raised to errors through `--warnings-as-errors`.
Reports are conservative: `any` and `unknown` values never produce a mismatch, unions are checked
per member, and recursive declarations widen instead of looping.

## Cross-module consistency (LUA6022)

When a module declares a type on a `require` local, the workspace compares the declared type
against the resolved target module's exported type:

```lua
---@type { value: string }   -- mismatch: the module exports { value = 42 }
local dep = require('dep')
return dep.value
```

Only definite incompatibilities are reported: unresolved annotation names and `any`/`unknown`
declared or exported types are skipped, so untyped code produces no noise.

## Suppression

Suppress specific codes through the `SuppressedDiagnosticCodes` pipeline:

| Surface | Configuration |
| --- | --- |
| CLI | Repeat `--suppress <code>` per code, for example `lunil check src --suppress LUA6022 --suppress LUA6016`. |
| Language server / VS Code | `lunil.server.suppressedDiagnosticCodes` as an array of codes. |
| Hosting (embedding) | `LuaAnalysisOptions.SuppressedDiagnosticCodes`. |

```json
{
  "lunil.server.suppressedDiagnosticCodes": ["LUA6022", "LUA6016"]
}
```

## Bounds

The v1 check does not perform generic instantiation inference or constraint solving, does not
type-check across modules beyond export consistency (`LUA6022`), and does not drive completion
ranking from types.

## See also

- [Debugging reference](debugging-reference.pub.md) — the DAP protocol surface.
- [Analysis facts](analysis-facts.pub.md) — facts exposed by `LuaSemanticModel` and
  `LuaAnalysisResult`.
- [Static analysis embedding](static-analysis-embedding.pub.md) — running analysis from .NET.
- [CLI reference](cli.pub.md) — `--suppress` and related source-command options.
- [Migration guide](migration-0.16.0.pub.md) — what changed with 0.16.
