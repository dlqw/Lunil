# Migrate from Lunil 0.15 to 0.16

[简体中文](migration-0.16.0.zh-CN.pub.md)

Lunil 0.16 keeps the 0.15 compiler, runtime, hosting, analysis, and engine entry points source
compatible: the release adds debugger (DAP) support and annotation-driven type checking without
removing any public member. Two behavior changes matter when upgrading: type diagnostics are
now enabled by default for annotated files (projects without annotations are unaffected), and
the VM signal enum gains a new member.

## 1. Update packages and tools

Update all Lunil package references as one compatibility line:

```xml
<PackageReference Include="Lunil.StandardLibrary" Version="0.16.0" />
<PackageReference Include="Lunil.Hosting" Version="0.16.0" />
```

```bash
dotnet tool update --global Lunil.Cli --version 0.16.0
```

## 2. New debugger support

A Debug Adapter Protocol implementation ships as the `lunil-debug-adapter` executable (bundled
inside the VSIX) with two modes:

- **Launch** (`--stdio`): runs a Lua script under the reference interpreter with breakpoints,
  stepping, pause, stack, locals, and upvalues.
- **Attach** (`--stdio --pipe <name>`): relays the protocol to a game-loop host's named-pipe
  debug endpoint.

Hosts opt in by starting the endpoint:

```csharp
using var debugServer = gameLoop.StartDebugServer("lunil-debug");
```

See [Debugging Lua](debugging.pub.md) and the [debugging reference](debugging-reference.pub.md).
Debugging requires the interpreter backend; JIT hosts get a clear error instead of a silent
no-op.

## 3. Type diagnostics are enabled by default

Annotation-driven type checking is now enabled by default (`LuaAnalysisOptions.Enabled`).
Annotated files can produce `LUA6000`-line diagnostics (assignability, argument counts, nil
paths, and more); unannotated files are unaffected. New in 0.16:

- `LUA6022` — cross-module export consistency: a `---@type` on a `require` consumer that is not
  assignable to the module's exported type.
- Suppression via `SuppressedDiagnosticCodes` everywhere: CLI `--suppress <code>`,
  `lunil.server.suppressedDiagnosticCodes` (VS Code), or `LuaAnalysisOptions` when embedding.

If the new diagnostics are noisy in an existing annotated codebase, suppress the specific codes
rather than disabling analysis. See [Type checking](type-checking.pub.md).

## 4. Compatibility checklist

- No public member is removed or re-signed; the 0.15 API surface stays source compatible.
- `LuaVmSignal.Paused` is a new enum member (a binary-compatible append); switches over the
  signal must handle the new value.
- `LuaWorkspaceDiagnosticPhase.Analysis` is a new phase used by the LUA6022 workspace
  diagnostics.
- New public debug API: `LuaGameLoopHost.StartDebugServer`, `LuaGameLoopDebugServer`,
  `LuaHost.ResumeDebuggedThread`, `LuaExecutor.ResumeDebugged`,
  `LuaInterpreter.ResumeDebugged`, and the `LuaDebugApi`/`LuaDebugSession` surface.
- The managed C-stack guard is tuned (`MaximumCStackDepth`); deep `coroutine.close` chains now
  raise "C stack overflow" with margin instead of exhausting the managed stack. Behavior for
  ordinary recursion depth is unchanged.
- The CLI `check`, `build`, and `dump` commands accept the new repeatable `--suppress <code>` option.
- The VS Code extension adds the `lunil` debugger type (launch and attach configurations) and
  the `lunil.server.suppressedDiagnosticCodes` setting.
