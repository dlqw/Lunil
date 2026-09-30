# Migrate from Lunil 0.21 to 0.22

[简体中文](migration-0.22.0.zh-CN.pub.md)

Lunil 0.22 restructures the engine internals around instance-owned components and registry-only
CLR interop. It is a pre-1.0 minor release: Lua language behavior, verified chunk contracts, tiered
execution semantics, and the `LuaHost` construction flow are unchanged; hosting and CLR-interop
.NET APIs are partly renamed or removed. The changes below affect hosts that touched that surface.

## 1. Generate bindings for every CLR type you expose

CLR interop dispatch is registry-only. The `LuaClrBindingMode` enum and the
`LuaClrOptions.BindingMode` property are removed, together with the reflection fallback bridge:
member caches, reflection overload selection, reflection invocation, reflection-built delegates,
the loaded-assembly type scan, and `Activator`-based construction all no longer exist.

- Every type, constructor, member, and delegate exposed to Lua needs a registered binding. Declare
  requests with `[LuaClrGenerateBinding]` and register the generated provider, as described in
  [AOT CLR bindings](aot-bindings.pub.md).
- An allowlisted type without a registered binding fails with a stable
  `no registered static binding` error instead of dispatching through reflection.
- Enabling CLR interop without a `BindingRegistry` is now a configuration error.
- Indexers cannot be bound; indexer access fails closed with a member-not-found error.
- The conversion improvements are shared: task and value-task results, enums, arrays, collections,
  and default value-type arguments marshal through cached strongly typed delegates on the registry
  path.

Hosts that relied on `RegistryThenReflection` for quick unbound access should move those types to
generated bindings; the AOT guidance in earlier docs already required this and is now the only mode.

## 2. Use the state-owned runtime operations

`LuaRuntimeOperations` is a sealed instance service owned by each `LuaState`, available as
`state.Operations`. The semantic operations keep their names and behavior but drop the leading
state argument:

| 0.21 | 0.22 |
| --- | --- |
| `LuaRuntimeOperations.GetIndex(state, target, key)` | `state.Operations.GetIndex(target, key)` |
| `LuaRuntimeOperations.SetIndex(state, target, key, value)` | `state.Operations.SetIndex(target, key, value)` |
| `LuaRuntimeOperations.Unary(state, op, operand)` | `state.Operations.Unary(op, operand)` |
| `LuaRuntimeOperations.Binary(state, op, left, right)` | `state.Operations.Binary(op, left, right)` |
| `LuaRuntimeOperations.ResolveCall(state, callable, args)` | `state.Operations.ResolveCall(callable, args)` |

The service captures the state's version profile at construction, so per-version coercion and
ordering rules resolve through captured fields. Value-level computation in `LuaValueOperations` is
unchanged.

## 3. Update engine-host runtime registries

`LuaGodotRuntimeRegistry` and `LuaUnityRuntimeRegistry` are sealed instance classes with one
process-wide default:

| 0.21 | 0.22 |
| --- | --- |
| `LuaGodotRuntimeRegistry.ActiveHostCount` | `LuaGodotRuntimeRegistry.Process.ActiveHostCount` |
| `LuaGodotRuntimeRegistry.DisposeAll()` | `LuaGodotRuntimeRegistry.Process.DisposeAll()` |
| `LuaUnityRuntimeRegistry.DisposeAll()` | `LuaUnityRuntimeRegistry.Process.DisposeAll()` |

`LuaGodotGameLoop` and `LuaUnityGameLoop` expose a settable `Registry` property that defaults to the
process registry; point it at a dedicated instance to isolate loops from scene-reload or play-mode
shutdown. Unity editor lifecycle hooks and play-mode reset use the process instance unchanged.

## 4. Review observable library details

- The standard library modules that read host configuration (basic, io, os, package, debug) install
  through internal instance modules. The public `LuaStandardLibrary` facade and reinstall
  reconfiguration behavior are unchanged.
- `os.time`, `dofile`, `require`, and `debug.debug` now register through per-install native
  closures, so `debug.getinfo` reports one captured upvalue for `os.time`, `dofile`, and
  `debug.debug`, and three for `require`. `print` keeps zero captured upvalues.
- The FFI binding generator ships as an analyzer asset of the Lunil.StandardLibrary package:
  `[assembly: LuaFfiGenerateBinding("lib", "symbol", "i32(i32, i32)")]` generates strongly typed
  native invokers that work on NativeAOT and trimmed runtimes. See [Use native FFI](ffi.pub.md).

## 5. What did not change

- Tiered execution (tier 0.5 interpreter, tier 1/2, loop OSR), JIT thresholds, and backend
  selection semantics are identical; the JIT's caches and warmup state moved to instance owners.
- The generated-code ABI entry points (`LuaCodegenAbiV1`-`V5`) keep their exact static signatures.
- `LuaHost`, `LuaGameLoopHost`, patch/hot-update contracts, the language server, and the debug
  adapter public surface are unchanged; options records are unchanged except for the CLR options
  covered above.
