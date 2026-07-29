# Migrate from Lunil 0.12 to 0.13

[简体中文](migration-0.13.0.zh-CN.pub.md)

Lunil 0.13 adds portable package assets and game-engine hosting. It is a pre-1.0 minor release and
contains source-level Hosting and CLR-interop changes. Existing Lua language and verified chunk
contracts remain version-selected.

## 1. Choose the execution asset

`Lunil.Core`, language services, compiler, IR, runtime, standard library, workspace, and Hosting now
ship `netstandard2.1` and `net10.0` assets. Portable consumers use the interpreter. .NET 10 hosts may
keep `LuaHostExecutionBackend.Auto`; explicitly requesting `Jit` on a portable or no-dynamic-code
runtime throws `PlatformNotSupportedException`.

Do not reference `Lunil.CodeGen.Cil` from Unity, Godot, or another portable host.

## 2. Update Hosting JIT contract names

The public host no longer exposes `Lunil.CodeGen.Cil.Jit` types through Hosting properties. Update
host-facing code as follows:

| 0.12 | 0.13 |
| --- | --- |
| `LuaHostOptions.Jit: LuaJitExecutorOptions` | `LuaHostOptions.Jit: LuaHostJitOptions` |
| `LuaHost.JitStatistics: LuaJitStatistics?` | `LuaHost.JitStatistics: LuaHostJitStatistics?` |
| `LuaPatchJitWarmupOptions.ExecutorOptions: LuaJitWarmupOptions` | `LuaHostJitWarmupOptions` |
| `LuaPatchJitWarmupModuleResult.Warmup: LuaJitWarmupResult?` | `LuaHostJitWarmupResult?` |

The option fields retain the same host policy meaning. Code that directly uses the .NET 10 CIL
executor may continue to reference `Lunil.CodeGen.Cil` and its `LuaJit*` types.

## 3. Review CLR conversion behavior

`LuaClrOptions` adds explicit policies for binding mode, enum and decimal representation,
collection projection, conversion limits, and ref/out results. Important defaults are:

- `BindingMode = RegistryThenReflection`;
- `EnumRepresentation = Name`;
- `DecimalRepresentation = ExactString`;
- `CollectionProjection = TablesAndIterators`;
- `RefOutRepresentation = PositionalAndNamedTable`.

If 0.12 code expected CLR `decimal` to become a possibly lossy Lua float, set
`DecimalRepresentation = LuaClrDecimalRepresentation.LossyFloat` explicitly or update the Lua
contract to accept the default invariant string.

`LuaClrInvocationResult` is now a sealed result class with `NamedRefOutValues`; do not rely on its
former record equality, init-only properties, or generated deconstruction.

## 4. Remove overlapping allowlist forms

For one member, configure only one of the bare, type-qualified, or assembly-qualified allowlist
forms. For example, do not include both `Add` and `Game.Inventory.Add`. Overlaps now fail with
`LuaClrErrorCode.BindingConflict` instead of selecting an ambiguous form.

For AOT hosts, generate exact bindings and set `BindingMode = RegistryOnly`; follow
[AOT CLR bindings](aot-bindings.pub.md).

## 5. Adopt frame hosting where needed

Replace application-owned Update/FixedUpdate queues with `LuaGameLoopHost`, or use the Unity and
Godot adapters. The construction thread owns `Tick`, `TickFixed`, `CancelAll`, and `Dispose`.
Cross-thread completions must enter through `ILuaGameLoopDispatcher`.

The default per-tick limits are 1,024 callbacks and 1,000,000 canonical instructions; the total
queued-work limit is 65,536. Set application-specific values explicitly if the previous host used
different budgets.

## 6. Update engine installation

- Unity: install `com.dlqw.lunil-0.13.0.tgz`. Unity 2022.3 LTS and Unity 6 are independent supported
  targets; do not upgrade a 2022.3 project merely to consume Lunil.
- Godot: reference `Lunil.Godot` `0.13.0`, copy the release addon to `res://addons/lunil`, and use a
  Godot 4.4 or 4.6 .NET editor.

Finally, run the application's normal source, chunk, patch, and publish checks under the exact
runtime or engine target it ships.
