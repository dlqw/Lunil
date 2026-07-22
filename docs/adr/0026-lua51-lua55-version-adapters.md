# Lua 5.1 and Lua 5.5 adapter boundaries

Lua 5.1 and Lua 5.5 are independent adapter capabilities. Each publishes its identity through `LuaVersionFeatureTable` and translates version-specific constructs into canonical IR. A state rejects chunks and modules from another selected version.

The Lua 5.1 adapter validates its legacy header, native `size_t` strings, number-only constants, opcode order, and closure-upvalue binding instructions. It preserves floating-point results and omits post-5.1 library surfaces.

The Lua 5.5 adapter validates varint chunk layout, instruction value bits, string references, and debug-line data. Its numeric and generic-for register layouts are translated at the chunk boundary. Build symbols make adapter availability explicit; unavailable adapters fail closed.
