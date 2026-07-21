# ADR 0025: Lua 5.2 end-to-end compatibility slice

- Date: 2026-07-21

## Decision

Lua 5.2 is enabled as an independent version adapter. Its source compiler and runtime preserve
the Lua 5.2 numeric model (all source numerals lower to floating-point values), reject Lua 5.3
bitwise/floor-division operators, and retain the shared canonical IR and execution scheduler.

Lua 5.2 binary chunks use a dedicated bounded reader, generated six-bit instruction codec, opcode
identity, prototype translation, and writer. The adapter accepts the official 5.2 header, scalar
layout, size_t string encoding, number-only constants, debug ordering, and version-specific
`GETTABUP`/`SETTABUP` operations. It never routes Lua 5.2 chunks through the Lua 5.3 or Lua 5.4
reader based on a best-effort header guess.

The standard-library boundary is selected from generated version capabilities. Lua 5.2 exposes
`bit32`, omits `utf8`, `string.pack`/`unpack`, `table.move`, Lua 5.3 integer math helpers,
`warn`, and `coroutine.close`. Existing Lua 5.4 behavior remains the default when no version is
selected.

## Verification

- Lua 5.2 source and produced/imported chunk smoke tests pass against a locally built PUC Lua 5.2.4.
- The opt-in `PucLua52DifferentialTests` corpus is controlled by `LUNIL_PUC_LUA52` and
  `LUNIL_PUC_LUAC52` and covers arithmetic, loops, closures, tables, generic iteration, varargs,
  imported chunks, and produced chunks.
- The Lua 5.2 generated codec and reader/writer round-trip tests pass.

## Consequences

Lua 5.1 and Lua 5.5 remain fail-closed until their own adapters are implemented. Shared runtime
and generated profile code must continue to dispatch by explicit `LuaLanguageVersion`; no future
adapter may silently inherit Lua 5.4 behavior.
