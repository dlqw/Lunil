# Debugging reference

[简体中文](debugging-reference.zh-CN.pub.md)

Reference for the Lunil Debug Adapter Protocol (DAP) integration: protocol surface, supported
requests and events, execution model, and the host-side API used to expose a debug pipe.

## Capabilities

| Area | Support |
| --- | --- |
| Launch | Run a `.lua` file under the reference interpreter with a debug session attached. |
| Attach | Connect to a game-loop host's named-pipe debug endpoint and relay the protocol. |
| Breakpoints | Line breakpoints set before execution; `setBreakpoints` replaces the set. |
| Stepping | `stepIn`, `next` (step over), `stepOut`; stepping granularity is not exposed. |
| Pause | Asynchronous `pause` suspends the turn at the next instruction checkpoint. |
| Stack | `stackTrace` reports Lua frames of the paused thread with source lines. |
| Scopes and variables | Locals and upvalues per frame; values are formatted read-only. |
| Threads | One DAP thread per active game-loop operation; the launch session reports the main thread. |
| Events | `initialized`, `stopped` (breakpoint / step / pause reasons), `terminated`, `output`. |

## Limits

- **Interpreter backend only.** The CIL JIT backend does not dispatch debug hooks; `StartDebugServer`
  and debugger resume reject JIT hosts with a clear error.
- **No expression evaluation.** The `evaluate` request is not implemented in v1.
- **No conditional breakpoints, hit counts, or log points.** Breakpoints are plain line sets.
- **One pipe client at a time.** The host serves one DAP connection; after a disconnect it accepts
  the next connection until the server is disposed.
- **Single pause at a time.** One debug session is attached per host state; coroutines of a paused
  turn suspend together and resume as a chain.

## Protocol surface

The adapter speaks DAP over stdio using `Content-Length` framing. In attach mode it relays frames
verbatim between the client and the host pipe, so the host serves the protocol.

| Request | Behavior |
| --- | --- |
| `initialize` | Reports `supportsConfigurationDoneRequest`; emits `initialized`. |
| `launch` | Requires `program`; defers execution until `configurationDone`. |
| `attach` | Formality for a host that is already attached to its state. |
| `setBreakpoints` | Replaces the breakpoint set for the given source path. |
| `configurationDone` | Starts execution (launch) after breakpoints are configured. |
| `continue` / `next` / `stepIn` / `stepOut` | Resumes the paused turn with the requested step mode on the next host tick. |
| `pause` | Requests a pause at the next instruction checkpoint. |
| `stackTrace` / `scopes` / `variables` | Read the paused thread's frames, locals, and upvalues. |
| `threads` | Lists active game-loop operations (attach) or the main thread (launch). |
| `disconnect` | Ends the session; the host detaches and resumes any paused turn. |

## Execution model

- **Launch:** the adapter runs the script on a dedicated execution thread; while paused it waits
  for a continue/step command before resuming through the interpreter's debug-pause path.
- **Attach:** the game-loop host is the only execution driver. A paused operation stays suspended
  across ticks; resume commands are queued and applied on the next tick through
  `LuaHost.ResumeDebuggedThread`, which reactivates the root thread and its suspended coroutine
  chain (`LuaExecutor.ResumeDebugged`).
- **Pause signal:** a debug pause surfaces as `LuaVmSignal.Paused` from the engine. Game-loop
  operations map it to the suspended state without breaking the tick contract.

## Host API

| Member | Purpose |
| --- | --- |
| `LuaGameLoopHost.StartDebugServer(pipeName)` | Starts the named-pipe DAP endpoint on the host; requires the interpreter backend. |
| `LuaGameLoopDebugServer` | The pipe server: `PipeName`, `IsConnected`, `PausedOperation`; `Dispose()` stops it. |
| `LuaHost.ResumeDebuggedThread(thread)` | Resumes a debugger-paused thread through the interpreter (rejects JIT hosts). |
| `LuaExecutor.ResumeDebugged(state, thread)` | Engine-level resume of a paused turn, reactivating the coroutine chain. |
| `LuaInterpreter.ResumeDebugged(state, thread)` | Interpreter entry point for the same resume path. |
| `LuaDebugSession` | Host-side session: `Attach`, `SetBreakpoints`, `RequestPause`, `Continue`, `StepInto/Over/Out`, `Detach`. |
| `LuaDebugApi` | Frame, local, upvalue, and hook queries used by the protocol handlers. |

Breakpoints, pause, and stepping apply to every thread of the attached state, including
coroutines; a paused turn suspends the whole scheduling chain.

## See also

- [Debugging guide](debugging.pub.md) — launch and attach walkthroughs.
- [Game-loop hosting](game-engine-hosting.pub.md) — host-side scheduling and operation lifecycle.
- [Migration guide](migration-0.16.0.pub.md) — new debug and type-checking APIs in 0.16.
