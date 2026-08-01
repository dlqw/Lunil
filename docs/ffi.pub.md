# How to call native code through FFI

[简体中文](ffi.zh-CN.pub.md)

This how-to enables the opt-in native C ABI FFI surface for an explicitly configured
`LuaHost` and uses it from Lua. The surface is disabled by default; hosts that never grant it
keep the restricted behavior unchanged.

## Prerequisites

- Lunil `0.15.0` or newer with the `Lunil.StandardLibrary` package referenced.
- A trusted host decision to grant native loading, plus exact library and symbol identities.
- For AOT or trimmed publication, exact host-registered bindings (see [AOT bindings](aot-bindings.pub.md)).

## 1. Grant FFI through standard-library options

`LuaStandardLibraryOptions.Ffi` is disabled by default. Enable it with exact allowlists and,
optionally, a host-controlled loader:

```csharp
using Lunil.StandardLibrary;

var options = new LuaStandardLibraryOptions
{
    Ffi = new LuaFfiOptions
    {
        Enabled = true,
        AllowedLibraryNames = ["gamecore"],
        AllowedSymbolNames = ["gamecore!score_add"],
        MaximumOpenLibraries = 8,
    },
};
```

An empty library allowlist or an empty symbol allowlist rejects the configuration at
construction time. NUL characters and path traversal segments are rejected everywhere; only
exact allowlist entries can be loaded or bound.

## 2. Load a library and bind a function

`ffi.load` returns a library userdata, `ffi.bind` returns a callable closure:

```lua
local lib = ffi.load('gamecore')
local add = ffi.bind(lib, 'score_add', 'i32(i32, i32)')
local value = add(20, 22)
ffi.close(lib)
```

Signatures use the compact C ABI declaration form `returnType(param1, param2)`. The supported
types are fixed-width integers, pointer-sized integers, booleans, `float`/`double`, UTF-8
strings (`cstring`), and raw pointers; see the [FFI reference](ffi-reference.pub.md) for the
complete alias table. Variadic declarations and platform-ambiguous types such as `long` are not
accepted; use `intptr_t`/`uintptr_t`/`size_t` for pointer-sized values.

Dynamic signature adaptation requires runtime dynamic code. On AOT or trimmed hosts, bind the
exact symbol through a registry instead (see [step 5](#5-bind-exact-registry-entries-for-aot-and-trimming)).

## 3. Own the native lifetime

Library userdata keeps its native handle alive through a lease. Each `ffi.load` result and each
bound function holds one lease; the handle is unloaded when the last lease is released or
explicitly closed:

```lua
ffi.close(lib)          -- explicit close; later calls fail with 'LibraryClosed'
lib = nil               -- or release references and let the garbage collector close it
```

`__gc` and `__close` metamethods release native resources when the collection or to-be-closed
path applies. Calling a bound function after its library is closed fails with a stable
`ffi LibraryClosed` error instead of touching native memory.

## 4. Exchange bounded memory with native code

`ffi.alloc` creates a zero-initialized native buffer, and `ffi.read`/`ffi.write` access it with
bounds checks:

```lua
local buffer = ffi.alloc(32)
ffi.write(buffer, 0, 'i32', 42)
ffi.write(buffer, 4, 'cstring', 'hello')
local number = ffi.read(buffer, 0, 'i32')      -- 42
local text = ffi.read(buffer, 4, 'cstring')    -- 'hello'
ffi.free(buffer)
```

Every native allocation counts against the configured allocation budget; exceeding it fails
with `AllocationLimitExceeded`. `ffi.free` is idempotent, and access after release fails with
`BufferClosed`. Buffers can be passed to native functions as `pointer` arguments.

## 5. Bind exact registry entries for AOT and trimming

Hosts without dynamic code register exact bindings and install a registry-only loader:

```csharp
var registry = new LuaFfiBindingRegistry();
registry.Register("gamecore", "score_add", "i32(i32, i32)", AddNative);

var options = new LuaStandardLibraryOptions
{
    Ffi = new LuaFfiOptions
    {
        Enabled = true,
        AllowedLibraryNames = ["gamecore"],
        AllowedSymbolNames = ["gamecore!score_add"],
        BindingRegistry = registry,
        LibraryLoader = RegistryOnlyLoader.Instance,
    },
};
```

```csharp
private static object? AddNative(ReadOnlySpan<object?> arguments) =>
    checked((int)arguments[0]! + (int)arguments[1]!);
```

The registered signature must match the signature requested by `ffi.bind` exactly; a mismatch
fails with `InvalidSignature`. When the runtime cannot provide dynamic code, the registry path
is the only supported route and any dynamic resolution attempt fails with
`DynamicCodeUnavailable`.

## 6. Diagnose failures

All Lua-facing failures carry a stable error category as `ffi {Code}: {message}` — for example
`ffi LibraryNotAllowed: native library 'other' is not allowlisted.`. Platform loader errors
include the native detail when available: Windows reports the `GetLastWin32Error` message, and
Unix-like systems report `dlerror`; Linux loading falls back from `libdl.so.2` to `libdl.so`
for musl-based distributions. The complete error code list is in the
[FFI reference](ffi-reference.pub.md).
