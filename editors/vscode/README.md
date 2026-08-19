<div align="center">

# Lunil Lua

Lua language intelligence for VS Code, powered by Lunil's versioned Lua 5.1–5.5
compiler, semantic analysis, and workspace index.

</div>

## Features

- **Full language services** — completions for locals, globals, members, and
  `require` module names; hover with inferred types; signature help; go to
  definition, type definition, and implementation; find references; workspace-wide
  rename; call hierarchy; document and workspace symbols.
- **Flow-aware type diagnostics** — annotation-driven bounded type analysis with
  assignability, argument-count, nil-path, and cross-module export diagnostics
  (`LUA6000` line), individually suppressible by code.
- **Semantic highlighting and inlay hints** — variables, parameters, functions, and
  properties are colored by role; inferred types appear as inlay hints.
- **Whole-workspace understanding** — every module is indexed with exports,
  dependencies, call and reference indexes, and incremental invalidation backed by a
  disk cache. Large repositories stay responsive across edits and restarts.
- **Host API awareness** — point Lunil at a host contract exported by Unity, Godot,
  or a .NET application, and the host's globals, modules, callbacks, and persistence
  schemas join completion, hover, and diagnostics.
- **Integrated debugging** — launch Lua scripts with the Lunil interpreter or attach
  to a running game-loop host over a named pipe, with breakpoints, stepping, stack
  traces, locals, and upvalues.
- **Getting-started walkthrough and curated snippets** — a five-step walkthrough
  covers indexing, editing, host contracts, and debugging; snippets cover common Lua
  idioms and Lunil annotation comments.

## Getting started

1. Install the extension and open a folder containing `.lua` files.
2. Lunil starts automatically for trusted workspaces. The **Lunil status item** in
   the status bar shows the server state; click it to open the Lunil menu.
3. Indexing runs in the background — the status item shows a live percentage and
   **Lunil: Show Index Status** lists any failed or pending documents.
4. Open the walkthrough with **Help → Welcome**, or browse snippets by typing a
   prefix such as `lf`, `forn`, `class`, or `--param`.

> [!TIP]
> Files in untrusted workspaces are never analyzed. Grant workspace trust to start
> the language server.

## Language features

| Capability | Details |
| --- | --- |
| Completion | Locals, globals, members after `.` and `:`, keywords, `require` module names |
| Diagnostics | Syntax (`LUA1xxx`), binding (`LUA3xxx`), and type analysis (`LUA6xxx`) codes |
| Navigation | Definition, type definition, implementation, references, symbols, call hierarchy |
| Refactoring | Workspace rename with prepare validation |
| Structure | Folding ranges, selection ranges, semantic tokens, breadcrumbs |
| Annotations | `---@class`, `---@field`, `---@alias`, `---@param`, `---@return`, `---@type`, `---@generic`, `---@overload`, `---@vararg`, `---@cast` |

## Host contracts

Hosts export a versioned contract describing their Lua-visible API surface:

```jsonc
// .vscode/settings.json
{
  "lunil.hostContractPath": "${workspaceFolder}/lunil-host-contract.json"
}
```

Run **Lunil: Show Virtual Host Contract** to inspect the indexed host surface as a
Lua document. Unity, Godot, and .NET hosting guides live in the
[repository documentation](https://github.com/dlqw/Lunil/tree/main/docs).

## Debugging

Create a `launch.json` and pick a Lunil snippet:

```jsonc
{
  "type": "lunil",
  "request": "launch",
  "name": "Debug Lua script",
  "program": "${workspaceFolder}/main.lua"
}
```

Use `"request": "attach"` with `"debugPipe"` to attach to a running Lunil game-loop
host.

## Commands

| Command | Purpose |
| --- | --- |
| `Lunil: Restart Language Server` | Stop and start the bundled server |
| `Lunil: Reindex Workspace` | Rebuild the workspace index on demand |
| `Lunil: Clear Analysis Cache` | Drop the disk-backed analysis cache |
| `Lunil: Show Index Status` | Inspect failed and pending documents |
| `Lunil: Show Virtual Host Contract` | Render the indexed host API surface |
| `Lunil: Show Language Server Output` | Open the Lunil output channel |

## Useful settings

| Setting | Purpose |
| --- | --- |
| `lunil.server.path` | Use a specific `lunil-language-server` build |
| `lunil.server.trace` | LSP protocol tracing (`off`, `messages`, `verbose`) |
| `lunil.server.suppressedDiagnosticCodes` | Suppress diagnostics by code, e.g. `["LUA6003"]` |
| `lunil.hostContractPath` / `lunil.hostContractJson` | Host analysis contract (file or inline) |

## Links

- [Repository and documentation](https://github.com/dlqw/Lunil)
- [Releases and change logs](https://github.com/dlqw/Lunil/releases)
- [Report an issue](https://github.com/dlqw/Lunil/issues)
