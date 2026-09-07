# Migrate from Lunil 0.18 to 0.19

[简体中文](migration-0.19.0.zh-CN.pub.md)

Lunil 0.19 keeps the 0.18 compiler, runtime, hosting, analysis, tooling, and engine entry points
source compatible. The release hardens the host integration boundaries: host CLR exceptions are
contained as Lua errors, the JIT requirement policy fails closed, resource budgets are bounded
across hot reloads and FFI teardown, and the debugger correlates requests by their protocol id.
The binary chunk readers share one parsing core with unified resource limits. Most upgrades need
no source change; review the four behavior notes below.

## 1. Update packages and tools

Update all Lunil package references as one compatibility line:

```xml
<PackageReference Include="Lunil.StandardLibrary" Version="0.19.0" />
<PackageReference Include="Lunil.Hosting" Version="0.19.0" />
```

```bash
dotnet tool update --global Lunil.Cli --version 0.19.0
```

## 2. Host CLR exceptions become catchable Lua errors

A native function or resumable native that throws an arbitrary CLR exception now fails the
enclosing protected call with a Lua error whose text is the exception type and message (for
example `System.IO.IOException: disk full`), so `pcall` can guard host calls and execution
continues afterwards. Unprotected paths surface a `LuaRuntimeException` carrying that converted
error instead of letting the raw host exception terminate the virtual machine.

- Code that relied on a host exception aborting the process or escaping `pcall` must now handle
  the Lua error, or derive the exception from the new `Lunil.Runtime.LuaHostException` to keep
  the typed pass-through contract.
- `LuaClrException` derives from `LuaHostException`; CLR interop failures keep their existing
  typed behavior unchanged.

## 3. `RequireJit` fails closed without dynamic code

`LuaHostExecutionBackend.Auto` combined with the `RequireJit` JIT policy now throws
`PlatformNotSupportedException` at `LuaHost` construction on runtimes without dynamic-code
support (NativeAOT, IL2CPP, `netstandard2.1`). Previously the host silently fell back to the
interpreter. Hosts that want the old tolerance should use the `PreferJit` or `Auto` JIT policy
without `RequireJit`.

## 4. Unified binary chunk resource limits

The version-specific chunk readers share one default limit source. Lua 5.1 and 5.2 readers adopt
the Lua 5.3/5.4/5.5 defaults: prototype depth 128 → 200, instruction count 4,000,000 →
10,000,000, upvalue count 100,000 → 1,000,000, string bytes 16 MiB → 64 MiB, and debug entry
count 2,000,000 → 10,000,000. This is a relaxation only: chunks accepted before remain accepted,
and large-but-valid chunks that earlier versions rejected now load.

## 5. `collectgarbage` tuning takes effect

`collectgarbage("setpause", v)` and `collectgarbage("setstepmul", v)` now scale the allocation
debt that restarts collection and the per-step work of the logical collector, with PUC-style
ratio semantics (defaults 200 and 100 keep the configured pacing unchanged). Scripts that called
these knobs for effect previously observed no change; verify collector pacing expectations when
upgrading.

## 6. Compatibility checklist

- No public member is removed; the 0.18 API surface stays source compatible.
- New public API: `Lunil.Runtime.LuaHostException`, `Lunil.IR.LuaChunkFormatException` (common
  base of the version chunk exceptions), and `Lunil.IR.LuaChunkCodec` (single chunk
  format dispatch). `LuaClassFactoryScanner` in Lunil.Analysis exposes the class-factory source
  scan. `LuaClrException` now derives from `LuaHostException`.
- Long-running hosts: invalidating compiled modules reclaims JIT bookkeeping beyond an internal
  bound, and disposing an FFI context closes live buffers and returns the allocation budget.
- DAP: the game-loop attach relay correlates requests by `id` (and `request_seq` for responses);
  clients whose request `id` differs from `seq` now work as the protocol intends.
- The `api/0.19.0/` baseline replaces `api/0.18.0/` as the frozen compatibility line.
