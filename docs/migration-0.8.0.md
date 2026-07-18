# Migrating from Lunil 0.7.0 to 0.8.0

`0.8.0` is the next pre-1.0 minor compatibility line. It intentionally removes the Lua
persisted/static AOT product while retaining runtime source/chunk compilation, the reference
interpreter, CoreCLR Tier 1/Tier 2 JIT, loop OSR, and .NET NativeAOT/trimming compatibility.

## Removed Lua AOT product surface

The following `0.7.x` capabilities do not have compatibility shims in `0.8.0`:

- `LuaAotCompiler`, `LuaPersistedAotExecutor`, `LuaStaticAotExecutor`,
  `LuaStaticAotRegistry`, and their artifact/load/result types;
- `Lunil.CodeGen.Cil.Artifacts`, `Lunil.CodeGen.Cil.Caching`, and
  `Lunil.CodeGen.Cil.Loading` persisted-AOT/cache APIs;
- persisted PE/PDB artifacts, manifests, loaders, static registries, disk caches, and their
  benchmark/report rows;
- the `Lunil.Build` NuGet package, `Lunil.Build.Tasks`, `LunilCompile` MSBuild items, generated
  registries, and build-time Lua AOT artifacts.

Remove `Lunil.Build` package references and `LunilCompile` items from project files. Do not replace
them with a compatibility flag: `0.8.0` has no static or persisted Lua AOT mode.

## Runtime execution replacement

Compile and execute source at runtime through the hosting API:

```csharp
using Lunil.Hosting;

using var host = new LuaHost();
var compilation = host.CompileUtf8("return 40 + 2");
var result = host.Execute(compilation);
```

For lower-level integrations, compile a verified canonical module and execute it with
`LuaInterpreter` or `LuaJitExecutor`. Distributable precompiled Lua input should use portable PUC
Lua 5.4 chunks produced by `lunil build --target chunk`; chunks remain verified before execution.

JIT selection is a runtime optimization, not a persisted artifact contract. When dynamic code is
unavailable, `Auto` and `PreferJit` deterministically use the reference interpreter without
pretending that a Lua AOT artifact was loaded.

## Removed CLI and configuration inputs

Current build output supports only `chunk`. The following legacy inputs are deliberately rejected:

```text
lunil build app.lua --target aot
{ "buildTarget": "aot" }
LUNIL_BUILD_TARGET=aot
```

Each form returns diagnostic `LUNIL0006`, phase `removed-feature`, and process exit code `2`.
This fail-closed diagnostic is stable for the `0.8` line and never silently selects another
backend.

## .NET NativeAOT is still supported

.NET NativeAOT publishes the managed Lunil host; it is distinct from the removed Lua AOT product.
Applications may continue to use standard SDK properties such as `PublishAot` and
`PublishTrimmed`. The compiler, workspace, runtime, standard library, hosting layer, CLI, and
interpreter remain exercised under NativeAOT on all six release RIDs. See
[.NET NativeAOT and trimming](nativeaot-build-integration.md) for publication examples.

## Other compatibility changes

- `LuaCompiledExit.InstructionsConsumed` and the generated instruction-accounting ABI use `long`
  end to end, preventing overflow beyond `Int32.MaxValue` instructions.
- `api/0.8.0/` freezes 13 assemblies and 13 packages at `0.8.0-beta.1`. `api/0.7.0/` remains the
  immutable stable `0.7.0` contract; no `0.8` removal is backported to `0.7.x` patches.
- Current JIT profile, telemetry, and performance schemas contain only the remaining interpreter,
  Tier 1, Tier 2, and loop-OSR product paths. Historical AOT ADRs and changelogs remain records,
  not supported `0.8` entry points.
