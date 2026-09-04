# Native FFI reference

[简体中文](ffi-reference.zh-CN.pub.md)

The native C ABI FFI surface is opt-in and disabled by default. This reference lists the Lua
`ffi` module, signature syntax, error categories, options, and platform loader behavior.
See [How to call native code through FFI](ffi.pub.md) for configuration steps.

## Global `ffi` module

Installed with `LuaStandardLibrary.InstallFfi` when `LuaStandardLibraryOptions.Ffi.Enabled` is
`true`. `InstallAll` does not install it by default.

| Member | Meaning |
| --- | --- |
| `ffi.enabled` | Always `true` when the module is installed. |
| `ffi.version` | `"0.15"`, the installed module surface version. |

## Functions

### `ffi.load(libraryName)`
Loads an allowlisted native library and returns a library userdata.
- Rejects names outside `AllowedLibraryNames` with `LibraryNotAllowed`; rejects NUL characters
  and `..` path segments.
- Loads through the configured `ILuaFfiLibraryLoader`; loading failures surface as
  `LibraryLoadFailed` with native error detail when available.
- Repeated loads return independent userdata; the native handle stays alive while any lease
  remains (see [Lifetime](#lifetime-and-ownership)).

### `ffi.bind(library, symbolName, signature[, convention])`
Returns a callable closure for an allowlisted `libraryName!symbolName` pair.
- `signature` uses the compact declaration form; see [Signatures](#signatures).
- `convention` is optional: `"default"`/`"platform"`, `"cdecl"`, or `"stdcall"`. It defaults to
  `LuaFfiOptions.DefaultCallingConvention`.
- With a registered binding, the requested signature must equal the registered signature
  exactly, otherwise `InvalidSignature`.
- Without a registered binding, dynamic signature adaptation is used when runtime dynamic code
  is available; otherwise `DynamicCodeUnavailable`.
- A bound function fails with `LibraryClosed` once its library is closed.

### `ffi.close(library)`
Explicitly closes a library userdata. Idempotent for the same userdata; later calls on its
bound functions fail with `LibraryClosed`.

### `ffi.alloc(size)`
Creates a zero-initialized native buffer of `size` bytes (1 to `MaximumBufferBytes`).
Returns a buffer userdata; exceeding the configured buffer or allocation budget fails with
`RangeExceeded` or `AllocationLimitExceeded`.

### `ffi.free(buffer)`
Releases a native buffer. Idempotent; access after release fails with `BufferClosed`.

### `ffi.read(buffer, offset, type)` / `ffi.write(buffer, offset, type, value)`
Reads or writes one typed value at `offset`. Access outside the buffer range fails with
`RangeExceeded`. `cstring` reads stop at the first NUL byte and are bounded by
`MaximumStringBytes`.

## Signatures

Compact C ABI declarations: `returnType(paramType1, paramType2, ...)`. Parameter types cannot
be `void`; varargs and platform-ambiguous `long`/`unsigned long` are rejected.

| Canonical | Accepted aliases | .NET equivalent | Size |
| --- | --- | --- | --- |
| `void` | — | `void` | return only |
| `bool` | `_bool`, `bool8` | `byte` | 1 |
| `i8` | `signed char`, `int8`, `int8_t` | `sbyte` | 1 |
| `u8` | `unsigned char`, `uint8`, `uint8_t` | `byte` | 1 |
| `i16` | `short`, `int16`, `int16_t` | `short` | 2 |
| `u16` | `unsigned short`, `uint16`, `uint16_t` | `ushort` | 2 |
| `i32` | `int`, `int32`, `int32_t` | `int` | 4 |
| `u32` | `unsigned`, `unsigned int`, `uint32`, `uint32_t` | `uint` | 4 |
| `i64` | `long long`, `int64`, `int64_t` | `long` | 8 |
| `u64` | `unsigned long long`, `uint64`, `uint64_t` | `ulong` | 8 |
| `isize` | `intptr_t`, `intptr` | `IntPtr` | pointer-sized |
| `usize` | `uintptr_t`, `uintptr`, `size_t` | `UIntPtr` | pointer-sized |
| `f32` | `float` | `float` | 4 |
| `f64` | `double` | `double` | 8 |
| `cstring` | `char*`, `const char*`, `utf8`, `utf8string` | UTF-8 string | terminated |
| `pointer` | `void*`, `ptr`, `pointer` | raw pointer | pointer-sized |

Lua values convert strictly: integers require exact Lua integers, numbers require Lua numbers,
`cstring` arguments require Lua strings, and `pointer` arguments accept `nil`, buffers, or
pointers. Unsigned results that exceed the Lua integer range fail with `RangeExceeded`.

## Error categories

Lua-facing failures surface as `ffi {Code}: {message}` with a stable `LuaFfiErrorCode`.

| Code | Meaning |
| --- | --- |
| `Disabled` | FFI is not enabled for this Lua state. |
| `InvalidName` | Empty or NUL-containing name. |
| `LibraryNotAllowed` | Library is not in `AllowedLibraryNames` or contains `..`. |
| `LibraryLoadFailed` | Native loader failed; native detail included when available. |
| `LibraryClosed` | Library or bound function used after close. |
| `SymbolNotAllowed` | `library!symbol` is not allowlisted. |
| `SymbolNotFound` | Export was not found in the loaded library. |
| `InvalidSignature` | Signature is malformed or mismatches a registered binding. |
| `UnsupportedSignature` | Signature cannot be adapted for invocation. |
| `InvalidArgument` | Lua value cannot represent the native argument. |
| `RangeExceeded` | Value or buffer access outside supported range. |
| `NativeInvocationFailed` | The native call itself threw. |
| `BufferClosed` | Buffer used after release. |
| `AllocationLimitExceeded` | Native allocation budget exhausted. |
| `ResourceLimitExceeded` | Open-library or dynamic delegate-type limit reached. |
| `DynamicCodeUnavailable` | Dynamic adaptation is needed but unavailable. |
| `BindingConflict` | Duplicate registry entry for the same library/symbol. |

## Options and limits

`LuaFfiOptions` defaults; invalid combinations are rejected at construction.

| Option | Default | Notes |
| --- | --- | --- |
| `Enabled` | `false` | FFI is disabled by default. |
| `AllowedLibraryNames` | empty | At least one exact name required when enabled. |
| `AllowedSymbolNames` | empty | `library!symbol` entries; registry bindings also count. |
| `BindingRegistry` | `null` | Exact AOT/trimmed bindings. |
| `LibraryLoader` | system loader | `NativeLibrary` on .NET 10; platform P/Invoke otherwise. |
| `MaximumOpenLibraries` | `32` | 1–4096. |
| `MaximumSignatureLength` | `256` | 8–65536. |
| `MaximumArgumentCount` | `8` | 0–32. |
| `MaximumStringBytes` | `1 MiB` | 1–1 GiB. |
| `MaximumBufferBytes` | `16 MiB` | 1–1 GiB. |
| `MaximumAllocationBytes` | `16 MiB` | 1–2^62. |
| `DefaultCallingConvention` | `PlatformDefault` | Used when `ffi.bind` omits a convention. |

## Platform loader behavior

- **.NET 10 targets** use `NativeLibrary.Load`/`GetExport`/`Free`.
- **Other targets** use platform P/Invoke: `LoadLibraryW`/`GetProcAddress`/`FreeLibrary` on
  Windows, `dlopen`/`dlsym`/`dlclose` on Linux and macOS. Linux loading falls back from
  `libdl.so.2` to `libdl.so` for musl-based distributions (for example Alpine Linux).
- Failure details: Windows reports the `GetLastWin32Error` message; Linux and macOS report
  `dlerror`.
- `package.loadlib` remains an explicit unsupported Lua C module diagnostic; FFI is a separate,
  allowlisted surface and does not load Lua C modules.

## Lifetime and ownership

- Library userdata and bound closures hold independent leases on the native handle. The handle
  is unloaded when the last lease is released or the library is explicitly closed.
- `__gc` and `__close` metamethods release native resources; finalizer paths never escape into
  the Lua collector.
- Pointer results are returned as `nil` when null; non-null pointers become pointer userdata
  that refuse access after their owning library closes.
