# Migrate from Lunil 0.13 to 0.14

[简体中文](migration-0.14.0.zh-CN.pub.md)

Lunil 0.14 keeps the 0.13 runtime, hosting, Unity, Godot, and `netstandard2.1` entry points source
compatible. The release adds compiler and analysis surfaces; the only opt-in choices below affect
new syntax retention and detailed code-reference collection.

## 1. Update packages and tools

Update all Lunil package references as one compatibility line:

```xml
<PackageReference Include="Lunil.Hosting" Version="0.14.0" />
<PackageReference Include="Lunil.Workspace" Version="0.14.0" />
```

```bash
dotnet tool update --global Lunil.Cli --version 0.14.0
```

Do not mix 0.13 and 0.14 Lunil assemblies in one process. Unity users install
`com.dlqw.lunil-0.14.0.tgz`; Godot users update both `Lunil.Godot` and the 0.14.0 addon.

## 2. Keep existing compiler calls or adopt staged snapshots

`LuaCompiler.Compile*` remains supported. Hosts that need syntax-only, binding-only, or analysis
snapshots can use `LuaFrontEndSession.Process` and `Advance`. `LuaFrontEndSnapshot.Stage` identifies
the completed stage; `Metrics` reports elapsed time and current-thread managed allocation for each
operation.

`LuaParserOptions.UseCompactSyntaxArena` defaults to `true`. Parse-only and incremental consumers
therefore retain a compact arena and materialize the existing node facade on demand. Set it to
`false` only when a direct parser consumer will immediately bind the complete tree and wants to
avoid the compact copy. `LuaFrontEndSession` selects the materialized path automatically for
binding and later stages.

## 3. Opt in to detailed binder references when needed

`LuaSemanticModel.References` keeps the 0.13 lexical-name behavior. New member and unified indexes
are populated when `LuaBinderOptions.CollectCodeReferences` is `true`:

```csharp
var binder = LuaBinderOptions.Default with
{
    CollectCodeReferences = true,
};
```

`LuaWorkspace` enables this automatically. Standalone compiler pipelines leave it disabled by
default so they do not pay workspace-only member/reference indexing costs.

## 4. Consume the new analysis facts conservatively

`LuaAnalysisResult` now exposes metatable, object-model, host-effect, callback-registration,
persistence-access, upvalue-cell, and nil-path facts. These are additive projections over Lua's
dynamic semantics:

- check each fact's precision or resolution state before offering a definitive editor action;
- preserve candidate and unresolved cross-module call results;
- expect mutation, dynamic indexes, escapes, or open host types to widen precision;
- use raw/effective member distinctions around `rawget`, `rawset`, `__index`, and `__newindex`.

No runtime behavior is changed by consuming these facts.

## 5. Describe APIs injected by the host

Replace hand-written analysis globals with schema-1 `LuaHostAnalysisContract` where possible. The
contract can describe globals, modules, functions, overloads, external source/implementation
locations, callback lifetime, side effects, and persistence read/write/delete/clear operations.

Pass it through `LuaAnalysisEnvironment.HostContract` for standalone analysis or
`LuaWorkspaceOptions.HostContract` for a workspace. Generated C# binding registries can project a
reflection-free contract. C++ and other hosts can emit the same deterministic JSON schema.

## 6. Choose full or compact workspace snapshots

Existing `AnalyzeAsync` returns full per-module compilation results. For large or long-lived editor
workspaces, migrate to `AnalyzeCompactAsync`; it retains queryable reference/call/callback/
persistence indexes without retaining full compiler trees. Configure module, dependency, source,
queue, memory-cache, disk-cache, and diagnostic budgets explicitly.

See [large-workspace analysis](large-workspaces.pub.md) for sizing and lifetime rules.

## 7. Add editor tooling

The `lunil-language-server` package and platform-specific VSIX files are new in 0.14. The VS Code
extension requires VS Code 1.96 or later and starts only in trusted workspaces. Configure the same
host-contract JSON used by embedded analysis to obtain completion and navigation for C++, C#,
Unity, or Godot definitions.

See the [language server reference](language-server.pub.md) and [VS Code guide](vscode.pub.md).

## Compatibility checklist

- Keep the intended `LuaLanguageVersion`; the default remains the Lua 5.4 language contract, with
  PUC Lua 5.4.8 as the compatibility baseline.
- Keep `TextSpan` values tied to their owning UTF-8 source snapshot.
- Enable `CollectCodeReferences` only for direct binder/compiler consumers that need the new member
  and unified indexes.
- Reuse `LuaWorkspace` across snapshots and dispose it when its cache domain ends.
- Validate host-contract schema/version and stable module/source identities before persistence.
- Update package, CLI, Unity/Godot, language-server, and VSIX assets together to 0.14.0.
