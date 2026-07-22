# Lua 5.2 compatibility architecture

Lua 5.2 has an independent adapter that preserves its numeric model and binary format while using the shared execution pipeline. Source numerals lower to floating-point values, while Lua 5.3 bitwise and floor-division operators are rejected.

The version-scoped library includes `bit32` and excludes later interfaces such as `utf8`, `string.pack`, `string.unpack`, `table.move`, integer math helpers, `warn`, and `coroutine.close`.

The adapter owns its bounded reader/writer, generated six-bit instruction codec, opcode identity, prototype conversion, and debug ordering. It validates the Lua 5.2 header, scalar layout, `size_t` strings, constants, and table-upvalue operations. Lua 5.2 chunks are never passed to a nearby-version reader by guesswork.
