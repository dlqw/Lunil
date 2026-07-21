# ADR 0027: Lua 5.1 function and thread environments

- Status: Accepted
- Date: 2026-07-21
- Target: `0.10.x` compatibility qualification on the stable `0.10.0` adapter line
- Related: [ADR 0023](0023-lua-language-version-contract.md), [ADR 0026](0026-lua51-lua55-version-adapters.md)

## Context

Lunil executes all language versions through a shared canonical IR that models globals as an `_ENV`
upvalue. Lua 5.1 additionally exposes `getfenv` / `setfenv` and relies on `module(...)` to rebind
the caller's environment. Without these APIs, the 5.1 standard-library contract is incomplete even
when chunk adapters and surface filtering are present.

## Decision

1. Install `getfenv` and `setfenv` only for `LuaLanguageVersion.Lua51` (basic library and
   `debug.getfenv` / `debug.setfenv`).
2. Map function environments onto the existing `_ENV` upvalue when present: `setfenv` replaces the
   closure's environment upvalue cell with a fresh closed upvalue. Lua 5.1 stores the environment
   on each closure, so this deliberately keeps sibling closures independent when only one sibling
   is passed to `setfenv`.
3. Closures without an environment upvalue store a per-closure legacy environment value used only by
   getfenv/setfenv.
4. Native closures carry an optional environment cell; plain native descriptors without captures
   report the state globals and reject environment mutation.
5. Thread environments (getfenv/setfenv level 0) live on `LuaThread`, default to the state globals
   when unset, and seed newly loaded Lua 5.1 main closures.
6. `module(name, ...)` applies option functions, then rebinds the nearest Lua caller's environment
   to the module table (PUC `setfenv(2, module)` semantics under the native call frame model).
7. The Lua 5.1 binary adapter injects a canonical environment upvalue at index zero only for a
   function (or an ancestor that creates such a function) containing `GETGLOBAL`/`SETGLOBAL`.
   Functions that never execute a global opcode retain their original upvalue layout and use the
   per-closure legacy environment fallback for `getfenv`/`setfenv`.
8. Main-chunk conversion marks only upvalue zero as `Environment`; additional root upvalues retain
   their binary descriptor kind. Module caches are state-local and therefore language-version
   homogeneous; `LuaState.CreateMainClosure` remains the fail-closed boundary for mismatched
   modules.

## Consequences

- Lua 5.1 scripts that use nested `module` names, Lua option callbacks, `package.seeall`, and
  environment rebinding become executable with PUC-compatible no-result return behavior.
- Later versions keep `_ENV` lexical semantics and do not expose getfenv/setfenv.
- JIT and interpreter share the same upvalue cells; environment replacement invalidates only the
  target closure's upvalue identity for subsequent global accesses.
