# Lunil language server reference

[简体中文](language-server.zh-CN.pub.md)

`lunil-language-server` is a self-contained LSP 3.17 server for Lunil's Lua parser, semantic
analysis, workspace index, and external host contracts. It communicates only through JSON-RPC on
standard input and output.

## Command line

| Command | Result |
| --- | --- |
| `lunil-language-server --stdio` | Starts the server. `--stdio` is optional. |
| `lunil-language-server --version` | Prints the Lunil product version and exits. |

An unrecognized argument exits with status 2 unless `--version` is also present; version reporting
takes precedence and exits with status 0. Protocol logs must go to standard error; writing non-LSP
text to standard output corrupts the connection. Unexpected request-handler failures return a
concise JSON-RPC internal error and write the method, request ID, and full managed exception stack
to standard error. Unexpected notification-handler failures are logged there and do not stop the
connection.

## Document and workspace model

- Positions use zero-based UTF-16 line and character offsets.
- Text synchronization is incremental and rejects stale document versions.
- Unsaved open documents override the corresponding disk files.
- Workspace folders can be added or removed after initialization.
- Watched changes to indexed `.lua` files invalidate the background compact index; watched
  changes to excluded files do not reload them.
- `.git`, `.svn`, `bin`, `obj`, `node_modules`, `.vscode`, and `.idea` directories are excluded from
  workspace discovery.
- `lunil.analysis.exclude` patterns and auto-detected generated data files stay out of indexing;
  opening one in the editor analyzes it on demand.
- Compact summaries and indexes are cached below the operating system's local application-data
  directory. Full compiler models are materialized only for active queries.

## Standard capabilities

The server provides diagnostics, completion (`.`, `:`, and `@` triggers), hover, signature help,
definition, declaration, type definition, implementation, references, prepare-rename and rename,
document and workspace symbols, full and delta semantic tokens, inlay hints, call hierarchy,
folding ranges, selection ranges, and quick-fix code actions.

The embedded Lua standard library is defined with annotated signatures: hovering a stdlib member
shows its signature and documentation, completion after `string.`/`table.`/`math.` and the other
library tables lists annotated members, and go-to-definition opens a per-library readonly virtual
page at the exact member. The virtual pages are `lunil-builtin:base.lua` (global functions),
`lunil-builtin:math.lua`, `lunil-builtin:string.lua`, `lunil-builtin:table.lua`,
`lunil-builtin:os.lua`, `lunil-builtin:io.lua`, `lunil-builtin:coroutine.lua`,
`lunil-builtin:utf8.lua`, and `lunil-builtin:debug.lua`. Stdlib pages are synced Lua documents, so
they receive full semantic highlighting and navigation.

Navigation and reference results include lexical names, table members, methods, module exports and
re-exports, metatable-backed members, prototype methods, closure upvalues, callback registrations,
persistence schemas, and external host definitions where the available facts are precise. Dynamic
Lua operations remain conservative and may return candidate or unresolved results instead of an
invented target.

## Configuration

Send `workspace/didChangeConfiguration` with either a top-level object or a `settings.lunil`
object:

```json
{
  "settings": {
    "lunil": {
      "hostContractPath": "/absolute/path/to/lunil-host-contract.json",
      "hostContractJson": "",
      "locale": "auto",
      "workspace": { "library": ["/absolute/path/to/meta-stubs"] },
      "analysis": { "exclude": ["data/**"], "autoDetectDataFiles": true }
    }
  }
}
```

| Setting | Default | Result |
| --- | --- | --- |
| `hostContractPath` / `hostContractJson` | empty | Machine-generated host contract; inline JSON takes precedence. Changing either rebuilds the analysis domain. |
| `locale` | `auto` | `auto`, `en`, or `zh-cn`; localizes the server's text surfaces — hover cards, signature-help docs, and progress messages — without a restart. Also accepted as an initialization option. (The VS Code extension additionally localizes its own menu, status-bar, and index-status text.) |
| `workspace.library` | `[]` | Read-only folders of LuaLS-style `---@meta` stubs describing host-injected globals and classes. |
| `analysis.exclude` | `[]` | Glob patterns keeping matching Lua files out of indexing. Patterns match workspace-relative `/`-separated paths; a pattern without a separator matches the file name in any directory; matching is case-insensitive. |
| `analysis.autoDetectDataFiles` | `true` | Auto-detect very large generated data files (pure table-literal data without functions, requires, or control flow) and keep them out of indexing. |
| `server.suppressedDiagnosticCodes` | `[]` | Diagnostic codes (for example `LUA6022`) suppressed by the analysis. |

The table names the raw `settings.lunil` payload properties. The VS Code extension surfaces them
as flat settings (`lunil.workspace.library`, `lunil.analysis.exclude`, `lunil.locale`, ...); only
the editor configuration accepts the flat dotted names. Changing `analysis.exclude`,
`analysis.autoDetectDataFiles`, or `workspace.library` re-scans the workspace without a restart.
Excluded files resolve as untyped values when required by analyzed code, and opening one in the
editor analyzes it anyway. See [Configuring the language server](configuring-the-language-server.pub.md)
for the settings' detailed behavior and adaptive residency budgets.

## Lunil protocol extensions

| Method | Kind | Result |
| --- | --- | --- |
| `lunil/reindex` | Request | Rebuilds the current workspace index. |
| `lunil/clearCache` | Request | Clears memory and workspace index caches. |
| `lunil/virtualHostDocument` | Request | Returns a Lua declaration view of the active host contract. |
| `lunil/indexProgress` | Notification | Reports phase, completed work items, total work items, and optional module. |

The server also uses standard work-done progress when the client advertises support. Reference
requests can stream partial results through the standard partial-result token.

## Host-analysis contracts

`LuaHostAnalysisContract` schema 1 describes injected globals, modules, functions, overloads,
definition and implementation locations, side effects, callback lifetime, and persistence
operations. Generate JSON with `ToJson()`, parse it with `ParseJson()`, and create a deterministic
LuaLS-compatible declaration file with `ToLuaStub()`.

Function effects distinguish global/table access, yielding, throwing, callback registration and
unregistration, and persistence read/write/delete/clear. Persistence entries carry a schema ID,
schema version, value type, migration function, key/value parameter positions, and whether a
missing read returns `nil`.

## Resource limits

JSON-RPC headers are limited to 16 KiB and messages to 32 MiB. Workspace defaults allow 65,536
modules, 1 GiB of Lua source, 1,048,576 dependencies, 4,096 pending work items, and a 2 GiB disk
summary cache. The server's memory residency budgets scale with the memory the runtime grants the
process, and a managed-heap hard limit (see `lunil.server.gcHeapHardLimitPercent` in the editor
extension) bounds runaway growth on top of server garbage collection. See [Configuring the language
server](configuring-the-language-server.pub.md) for the budget specifics. Embedders that need
different budgets should use `LuaWorkspace` directly; the standalone server intentionally exposes
only the stable editor configuration above.
