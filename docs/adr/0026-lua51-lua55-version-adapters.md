# ADR 0026: Lua 5.1 and Lua 5.5 adapter boundaries

## Status

Accepted for the `0.10.0-alpha.2` compatibility line.

## Decision

Lua 5.1 and Lua 5.5 are represented by independent compile-time adapter capabilities. They are
never inferred from the Lua 5.4 default and are rejected when a chunk carries a different version
marker. The adapters publish their own version identity through `LuaVersionFeatureTable` and keep
the canonical IR as the execution interchange format.

Lua 5.1 has a dedicated legacy chunk reader/writer: its header, native `size_t` strings, number-only
constants, legacy opcode ordering, and closure upvalue binding pseudo-instructions are translated at
the boundary. Lua 5.5 has a dedicated reader/writer for the official 5.5 varint chunk format, a
generated instruction codec (including the `ivABC` value bits), string-reference deduplication, and
native debug-line layout. Numeric and generic `for` register-layout differences are translated at
the chunk boundary so the shared canonical execution pipeline remains version-neutral.

## Compatibility constraints

- Lua 5.4 remains the default for all APIs that do not select a version explicitly.
- A Lua 5.1 or Lua 5.5 state cannot load a module or chunk belonging to another language version.
- The Lua 5.1 adapter preserves floating-point numeric results and does not expose Lua 5.2+ `bit32`,
  `utf8`, `string.pack`, or `table.move` surfaces.
- The generated opcode profile is selected at build time with `LUNIL_LUA51_ADAPTER` and
  `LUNIL_LUA55_ADAPTER`; disabling either symbol makes the corresponding capability fail closed.
- Lua 5.5 chunks are validated against the official 5.5 header and field widths; they are never
  accepted by disguising a Lua 5.4 chunk or silently rewriting the version marker.
