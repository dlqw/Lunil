# Generate FFI bindings

[简体中文](ffi-bindings.zh-CN.pub.md)

Declare the native signatures you bind and let the FFI binding generator produce strongly typed
invokers, so bound symbols work on NativeAOT and trimmed runtimes without runtime delegate
generation. This how-to covers declaring requests, registering the generated provider, and
reading the build diagnostics.

## Prerequisites

- The [FFI module is granted through standard-library options](ffi.pub.md), and the libraries
  and symbols you bind are allowlisted.
- The project references the `Lunil.StandardLibrary` package; the generator ships with it as an
  analyzer asset and needs no extra reference.

## 1. Declare one request per symbol

Add one assembly-level attribute per `(library, symbol)` pair with the exact signature that
`ffi.bind` will request:

```csharp
using Lunil.StandardLibrary;

[assembly: LuaFfiGenerateBinding("gamecore", "score_add", "i32(i32, i32)")]
[assembly: LuaFfiGenerateBinding("gamecore", "describe", "cstring(i32)", "cdecl")]
```

The signature uses the compact declaration grammar of `ffi.bind` (`i32`, `f64`, `cstring`,
`pointer`, …). The calling convention defaults to `platform` and can be declared as `cdecl` or
`stdcall`.

## 2. Register the generated provider

```csharp
var registry = new LuaFfiBindingRegistry();
new Lunil.Generated.LuaFfiGeneratedBindings().RegisterBindings(registry);
```

Pass the registry through `LuaFfiOptions.BindingRegistry` exactly as for hand-written entries;
the [hand-written registry flow](ffi.pub.md) and its allowlist rules apply unchanged. The
generated invoker resolves the symbol address from the loaded library at bind time and marshals
through a compile-time native delegate, so each call skips runtime delegate generation and
`Delegate.DynamicInvoke`.

## 3. Read the diagnostics

Unsupported signatures, unknown calling conventions, and duplicate `(library, symbol)` requests
fail the build with `LUNILFFI001` and `LUNILFFI002` instead of surfacing at runtime. A signature
mismatch between the generated binding and a later `ffi.bind` call still fails with
`InvalidSignature`, as with hand-written entries.

## Expected result

Bound symbols run on NativeAOT, IL2CPP, and trimmed runtimes through the same registry path as
hand-written bindings. For the dynamic-signature path, hand-written invokers, and buffer
ownership, see [Use native FFI](ffi.pub.md) and the [FFI reference](ffi-reference.pub.md).
