# Migrating to Lunil 0.10.0

`0.10.0` makes the Lua language version an explicit part of compilation and runtime state. Set
the same version on the compiler and `LuaState` when creating a module and its closures:

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
