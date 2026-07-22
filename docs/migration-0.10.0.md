# Migrating to Lunil 0.10.0

Lunil 0.10 makes the Lua language version explicit in parsing, compilation, binary chunks, hosting, and runtime state. Lua 5.4 is the default when no version is selected. Use the same version for a module and the `LuaState` that creates its closures:

```csharp
using Lunil.Compiler;
using Lunil.Core;
using Lunil.Runtime;

var version = LuaLanguageVersion.Lua54;
var compilation = new LuaCompiler(new LuaCompilerOptions
{
    LanguageVersion = version,
}).CompileUtf8(source, "@module.lua");
var state = new LuaState(new LuaStateOptions { LanguageVersion = version });
var closure = state.CreateMainClosure(compilation.Module!);
```

`LuaHostOptions.LanguageVersion`, compiler/parser/binder options, and `LuaStateOptions.LanguageVersion` use the same identity. The CLI exposes it through `--lua-version` and `LUNIL_LUA_VERSION`:

```text
lunil run module.lua --lua-version 5.1
lunil build module.lua --target chunk --lua-version 5.5
LUNIL_LUA_VERSION=5.3 lunil check module.lua
```

Supported values are `5.1`, `5.2`, `5.3`, `5.4`, and `5.5`. Lunil rejects unavailable adapters and never substitutes another version. Each version has its own PUC binary-chunk reader/writer, so a chunk is not cross-version input.

## Lua 5.1 function environments

The Lua 5.1 profile provides `getfenv`, `setfenv`, and `module`. A closure's legacy environment controls its global reads and writes, including closures imported from Lua 5.1 chunks. Changing that environment changes later global lookup for that closure; it does not change the module's or state's language version.

Code that needs these functions must select Lua 5.1. Lua 5.2+ states do not infer old semantics from source text.

## `require` uses the state version

A `LuaState` has one language contract. Modules loaded by `require` compile with that state's `LanguageVersion`; a module file does not select another version implicitly. Use separate states for modules that must run under different Lua contracts:

```csharp
var state51 = new LuaState(new LuaStateOptions
{
    LanguageVersion = LuaLanguageVersion.Lua51,
});
var state54 = new LuaState(new LuaStateOptions
{
    LanguageVersion = LuaLanguageVersion.Lua54,
});
```

`LuaState.CreateMainClosure` rejects a canonical module whose language version differs from the state.

## Replacing removed Lua AOT APIs

The persisted/static Lua AOT product, disk cache, generated static registry, and `Lunil.Build` package are unavailable. Execute runtime-compiled source with `LuaHost`, `LuaInterpreter`, or `LuaJitExecutor`; distribute precompiled input as verified portable PUC chunks. See the [0.8.0 migration guide](migration-0.8.0.md) for removed API names and CLI inputs.
