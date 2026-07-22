# Lua 5.3 compatibility architecture

Lua 5.3 is an independent language adapter that shares canonical IR and the scheduler. Its parser rejects Lua 5.4-only local attributes such as `<const>` and `<close>`; integer, bitwise, floor-division, `goto`, and `_ENV` behavior follows the Lua 5.3 contract.

Library installation is version-scoped. Lua 5.3 omits Lua 5.4 additions including `warn` and `coroutine.close`. Shared library code consumes generated version features instead of duplicating a library per version.

Lua 5.3 chunks use a dedicated bounded reader, instruction model, opcode identity, and canonical converter. Loading dispatches from language version and respects host resource limits. Build-generated codec and feature metadata make adapter availability explicit; an unavailable adapter fails closed.
