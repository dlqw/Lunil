# Debugging Lua with the Debug Adapter Protocol

[简体中文](debugging.zh-CN.pub.md)

This guide debugs Lua scripts with the Lunil Debug Adapter Protocol (DAP) integration: launch a
standalone script from VS Code, or attach to a running game-loop host that exposes a debug pipe.
For the supported surface and its limits, see the [debugging reference](debugging-reference.pub.md).

## Prerequisites

- VS Code 1.96 or later with the Lunil extension installed (see the
  [VS Code guide](vscode.pub.md)).
- The script must run on the reference interpreter backend. The CIL JIT backend does not dispatch
  debug hooks, so debug sessions require an interpreter host (the VS Code launch and attach
  configurations below already select one).

## 1. Launch a Lua script

Create a `.vscode/launch.json` with a `lunil` launch configuration:

```json
{
  "version": "0.2.0",
  "configurations": [
    {
      "type": "lunil",
      "request": "launch",
      "name": "Debug Lua script",
      "program": "${workspaceFolder}/main.lua"
    }
  ]
}
```

Select the configuration and press **F5**. The extension starts `lunil-debug-adapter`, which runs
the script under the interpreter with a debug session attached.

While stopped you can:

- set and clear breakpoints from the editor gutter;
- pause a running script with the pause button;
- step **into**, **over**, and **out** of functions;
- inspect the call stack, locals, and upvalues in the debug panel.

A script that completes without stopping prints its results to the Debug Console and ends the
session.

## 2. Attach to a game-loop host

A [game-loop host](game-engine-hosting.pub.md) can expose a named-pipe debug endpoint. The host
application starts the endpoint with:

```csharp
using var debugServer = gameLoop.StartDebugServer("lunil-debug");
```

The portable hosting sample accepts `--debug-pipe <name>` on the command line:

```bash
dotnet run --project samples/Lunil.Portable.Hosting -- --debug-pipe lunil-debug
```

Then attach from VS Code with an `attach` configuration that names the same pipe:

```json
{
  "type": "lunil",
  "request": "attach",
  "name": "Attach to Lunil host",
  "debugPipe": "lunil-debug"
}
```

The adapter connects to the pipe and relays the protocol between VS Code and the host. The host
serves breakpoints, stepping, pause, and stack inspection while its own tick loop stays the single
execution driver:

- breakpoints are set before the host ticks the script;
- a hit suspends the operation turn and reports `stopped`;
- `continue`, `next`, `stepIn`, and `stepOut` resume the turn on the next tick;
- when the client disconnects, the host detaches the debug session and resumes any paused turn so
  the game loop keeps running without a debugger.

## 3. Run the adapter directly

The adapter is a console program that ships inside the VSIX (`server/<rid>/lunil-debug-adapter`).
It has two modes:

```bash
# launch mode: serve a DAP session over stdio (used by VS Code launch)
lunil-debug-adapter --stdio

# attach mode: relay a DAP session over stdio to a host debug pipe (used by VS Code attach)
lunil-debug-adapter --stdio --pipe <name>
```

Attach mode forwards frames verbatim in both directions, preserving request sequence numbers so
responses match the client's pending requests exactly.

## See also

- [Debugging reference](debugging-reference.pub.md) — capabilities, limits, and host API.
- [Game-loop hosting](game-engine-hosting.pub.md) — host-side scheduling and the tick contract.
- [VS Code guide](vscode.pub.md) — extension installation and language server configuration.
