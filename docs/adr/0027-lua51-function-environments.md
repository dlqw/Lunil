# Lua 5.1 function and thread environments

Lua 5.1 exposes function and thread environments through `getfenv`, `setfenv`, and `module`. For Lua 5.1 only, Lunil maps a closure environment to its `_ENV` upvalue when present. Replacing it creates a fresh closed upvalue so sibling closures remain independent.

Closures without an environment upvalue use a dedicated legacy environment value. Native closures may carry an environment cell; plain descriptors report state globals and reject mutation. A `LuaThread` environment defaults to state globals and seeds new main closures.

`module(name, ...)` applies option functions and rebinds the nearest Lua caller environment to the module table. The chunk converter introduces a canonical environment upvalue only where global operations require it; other descriptors retain their version identity.
