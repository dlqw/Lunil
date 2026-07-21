# Migrating to Lunil 0.10.0

`0.10.0` makes the Lua language version an explicit part of parsing, compilation, binary chunks,
hosting, and runtime state. Lua 5.4 remains the default when no version is selected, preserving
the 0.9 behavior. Set the same version on the compiler and `LuaState` when creating a module and
its closures:

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

`LuaHostOptions.LanguageVersion`, compiler/parser/binder option records, and
`LuaStateOptions.LanguageVersion` accept the same `LuaLanguageVersion` identity. The CLI exposes
the contract through `--lua-version` and `LUNIL_LUA_VERSION`:

```text
lunil run module.lua --lua-version 5.1
lunil build module.lua --target chunk --lua-version 5.5
LUNIL_LUA_VERSION=5.3 lunil check module.lua
```

Supported values are `5.1`, `5.2`, `5.3`, `5.4`, and `5.5`. Lunil fails closed when a requested
adapter is unavailable; it never substitutes Lua 5.4 or another version silently. Each version
also selects its matching PUC binary-chunk reader/writer, so chunks are not cross-version input.

## Lua 5.1 function environments

The Lua 5.1 profile installs `getfenv`, `setfenv`, and `module`. A closure's legacy environment is
used by its global reads and writes, including closures converted from Lua 5.1 binary chunks.
Changing a function environment therefore changes subsequent global lookup for that closure; it
does not mutate the language version of the module or state.

These legacy functions are intentionally version-scoped. Code that depends on them must select
Lua 5.1 instead of expecting a Lua 5.2+ state to infer old semantics from the source text. Main
chunk environment injection is internal to the Lua 5.1 adapter and does not shift ordinary
captured-upvalue indexes.

## `require` uses the current state version

Lunil does not support cross-version `require` within one `LuaState`. A source module loaded by
`require` is compiled with the state's configured `LanguageVersion`; module files currently have
no version declaration or pragma. Consequently, a Lua 5.4 state does not infer that a required
file was authored for Lua 5.1, and it will not automatically run that file with Lua 5.1 rules.

To load modules under different Lua contracts, use separate states (and install the standard
library in each state as needed):

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

`LuaState.CreateMainClosure` still rejects a canonical module whose language version differs from
the state. This is a defensive boundary for modules produced by another compiler or state; it is
not a cross-version `require` mechanism.

When migrating an application that previously relied on implicit version selection, configure the
version once at the state/host boundary and keep all modules required by that state on the same
language contract. A future release may add module-level version declarations; such declarations
are not accepted by the 0.10.x `require` implementation.

## Upgrading directly from 0.7

The persisted/static Lua AOT product, disk cache, generated static registry, and `Lunil.Build`
package were removed in 0.8. Runtime compilation, the interpreter, CoreCLR JIT, and .NET
NativeAOT/trimming support remain available. Remove old `LunilCompile` MSBuild items and migrate
execution to `LuaHost`, `LuaInterpreter`, or `LuaJitExecutor`; see the
[0.8.0 migration guide](migration-0.8.0.md) for the complete removal list.
