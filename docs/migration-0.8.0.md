# Migrating from Lunil 0.7.0 to 0.8.0

Lunil 0.8 removes the Lua persisted/static AOT product. Runtime source/chunk compilation, the reference interpreter, managed JIT execution, loop OSR, and .NET NativeAOT/trimming deployment remain available.

## Removed Lua AOT surface

The following 0.7.x capabilities have no compatibility shim:

- `LuaAotCompiler`, `LuaPersistedAotExecutor`, `LuaStaticAotExecutor`, `LuaStaticAotRegistry`, and related artifact/load/result types;
- `Lunil.CodeGen.Cil.Artifacts`, `Lunil.CodeGen.Cil.Caching`, and `Lunil.CodeGen.Cil.Loading` persisted-AOT/cache APIs;
- persisted PE/PDB artifacts, manifests, loaders, static registries, disk caches, and generated Lua registries;
- the `Lunil.Build` package, `Lunil.Build.Tasks`, and `LunilCompile` MSBuild items.

Remove `Lunil.Build` references and `LunilCompile` items. There is no configuration flag that re-enables static or persisted Lua AOT.

## Runtime execution replacement

Compile and run source through the hosting API:

```csharp
using Lunil.Hosting;

using var host = new LuaHost();
var compilation = host.CompileUtf8("return 40 + 2");
var result = host.Execute(compilation);
```

Lower-level integrations can execute a verified canonical module through `LuaInterpreter` or `LuaJitExecutor`. For distributable precompiled input, use portable PUC chunks produced by `lunil build --target chunk`; every chunk is verified before execution.

JIT selection is a runtime optimization, not a persisted-artifact mode. If dynamic code is unavailable, `Auto` and `PreferJit` use the reference interpreter.

## Removed build inputs

Build output supports `chunk` only. These legacy inputs are rejected:

```text
lunil build app.lua --target aot
{ "buildTarget": "aot" }
LUNIL_BUILD_TARGET=aot
```

Each returns `LUNIL0006`, phase `removed-feature`, and exit code `2`; the CLI does not silently select another backend.

## .NET NativeAOT remains supported

.NET NativeAOT publishes the managed host and is distinct from the removed Lua AOT product. Standard SDK properties such as `PublishAot` and `PublishTrimmed` are supported. See [.NET NativeAOT and trimming](nativeaot-build-integration.md) for publication examples.

## Other compatibility changes

`LuaCompiledExit.InstructionsConsumed` uses `long` end to end, avoiding an instruction-count overflow beyond `Int32.MaxValue`.
