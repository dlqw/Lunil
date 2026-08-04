# How to use Lunil in VS Code

[简体中文](vscode.zh-CN.pub.md)

This guide installs the platform-specific Lunil VS Code extension, starts its bundled language
server, and connects optional C++, C#, Unity, or Godot host definitions.

## Prerequisites

- VS Code 1.96 or later.
- A trusted workspace containing one or more `.lua` files.
- The VSIX matching the operating system and CPU architecture.

The extension does not start an executable in Restricted Mode. It collects no telemetry and makes
no runtime network requests.

## 1. Install the VSIX

Download the matching `lunil-lua-0.16.1-<target>.vsix` and its `.sha256` file from the 0.16.1
release. Targets are `win32-x64`, `win32-arm64`, `linux-x64`, `linux-arm64`, `darwin-x64`, and
`darwin-arm64`.

Install from **Extensions: Install from VSIX...**, or use:

```bash
code --install-extension lunil-lua-0.16.1-win32-x64.vsix
```

Each VSIX contains exactly one self-contained server for its target. No separate .NET installation
is required. Open a trusted folder with Lua files; the Lunil status item reports startup and index
progress. During initial activation, checksum verification waits with bounded backoff for bundled
payload files that are still being extracted. A malformed manifest or checksum mismatch fails
immediately.

## 2. Configure an injected host

Export a schema-1 `LuaHostAnalysisContract` from the application or binding generator, then set one
of these resource-scoped values:

```json
{
  "lunil.hostContractPath": "${workspaceFolder}/generated/lunil-host-contract.json"
}
```

For generated or test configurations, `lunil.hostContractJson` accepts the JSON inline and takes
precedence over the path. Contract changes reload analysis automatically. Run **Lunil: Show Virtual
Host Contract** to inspect the declaration view used for completion, hover, navigation, callback
lifetime, and persistence analysis.

## 3. Operate the server

Use the Command Palette:

| Command | Use |
| --- | --- |
| **Lunil: Restart Language Server** | Restart after an environment or executable change. |
| **Lunil: Reindex Workspace** | Rebuild the compact module and reference index. |
| **Lunil: Clear Analysis Cache** | Drop in-memory analysis reuse and the active workspace index. |
| **Lunil: Show Language Server Output** | Inspect startup, restart, and protocol trace output. |
| **Lunil: Show Virtual Host Contract** | Open the active external API declaration as a virtual Lua document. |

Unexpected termination uses bounded automatic restart with backoff. After the configured limit is
reached, use the restart command to begin a new attempt sequence.

Unexpected request or notification handler failures write the full managed exception stack to the
Lunil output channel while JSON-RPC error responses remain concise. Include that stack when
reporting a server failure.

## Settings

| Setting | Default | Description |
| --- | --- | --- |
| `lunil.server.path` | bundled server | Absolute path to a compatible replacement server. |
| `lunil.server.trace` | `off` | `off`, `messages`, or `verbose` LSP tracing in the Lunil output channel. |
| `lunil.server.maximumRestartCount` | `5` | Automatic restart limit, from 0 through 20. |
| `lunil.server.gcHeapHardLimitPercent` | `70` | Managed heap hard-limit percentage, from 20 through 90, for the bundled server. |
| `lunil.hostContractPath` | empty | Resource-relative or absolute path to a host-analysis contract. |
| `lunil.hostContractJson` | empty | Inline contract JSON; takes precedence over the path. |
| `lunil.server.suppressedDiagnosticCodes` | `[]` | Diagnostic codes (for example `LUA6022`) suppressed by the language server analysis. |

`lunil.server.path` must be absolute. Changing the server path or heap limit restarts the process;
changing the host contract reloads configuration and reindexes semantic data.

## Debug Lua code

The extension contributes a `lunil` debugger type with two configurations:

- **Launch** runs a `.lua` file under the reference interpreter with breakpoints, stepping,
  pause, stack, locals, and upvalues (`program` points at the script).
- **Attach** connects to a game-loop host that exposes a named-pipe debug endpoint
  (`debugPipe` names the pipe; the host starts it with `LuaGameLoopHost.StartDebugServer`).

The adapter executable (`lunil-debug-adapter`) is bundled inside the VSIX like the language
server. See [Debugging Lua](debugging.pub.md) for walkthroughs and the
[debugging reference](debugging-reference.pub.md) for the supported protocol surface.

## Expected result

Lua files receive diagnostics, completion, hover, signatures, cross-module and external navigation,
references, rename, symbols, semantic tokens, inlay hints, call hierarchy, folding, selection
ranges, and quick fixes. See the [language server reference](language-server.pub.md) for the exact
protocol and conservative behavior around dynamic Lua operations.
