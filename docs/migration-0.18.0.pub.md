# Migrate from Lunil 0.17 to 0.18

[简体中文](migration-0.18.0.zh-CN.pub.md)

Lunil 0.18 keeps the 0.17 compiler, runtime, hosting, analysis, and engine entry points source
compatible. The release focuses on workspace-level editor intelligence: configurable require
search roots, class-factory recognition, dotted annotation class names for generated host stubs,
and new VS Code productivity commands. Two presentation changes matter when upgrading: hover cards
use compact Markdown tables, and structural table hovers show a member summary instead of a bare
`table` label.

## 1. Update packages and tools

Update all Lunil package references as one compatibility line:

```xml
<PackageReference Include="Lunil.StandardLibrary" Version="0.18.0" />
<PackageReference Include="Lunil.Hosting" Version="0.18.0" />
```

```bash
dotnet tool update --global Lunil.Cli --version 0.18.0
```

## 2. Resolve require strings through search roots

`lunil.require.searchPaths` lets a workspace resolve `require("A.B.C")` against prefixed module
identities such as `Libs.client.A.B.C`. The raw name is always tried first, then each configured
root in order. This is opt-in: with an empty default, require strings must still match module
identities exactly.

```json
{
  "lunil.require.searchPaths": ["scripts/client", "scripts/shared"]
}
```

Changing the setting re-scans the workspace without restarting the language server.

## 3. Recognize class factories

`lunil.analysis.classFactories` tells analysis which global functions define classes. A factory's
first string literal argument is the class name; when `baseArguments` is true, the remaining
bare-identifier arguments are base classes.

```json
{
  "lunil.analysis.classFactories": [
    "defineView",
    { "name": "class", "baseArguments": true }
  ]
}
```

Without this setting, `local X = class("Name", Base)` is an ordinary call. With it, hover shows
the class card, member writes on `X` define methods, `X.new()` produces instances, base members
resolve, and the class hierarchy command includes the class.

## 4. Dotted annotation class names

Generated host-API stubs can declare dotted classes such as `---@class
host.Engine.Utility.TimeUtil`. The full dotted path is the class name, so navigation, hover,
references, and semantic tokens treat the namespace path as one class identity instead of only the
final segment.

## 5. Compatibility checklist

- No public member is removed or re-signed; the 0.17 API surface stays source compatible.
- New public API: `LuaWorkspaceOptions.RequireSearchPaths`,
  `LuaWorkspaceOptions.ClassFactoryCalls`, `LuaAnalysisEnvironment.ClassFactoryCalls`,
  `RequireNameExpansion`, compact-snapshot save/restore and contribution adoption APIs in
  Lunil.Workspace.
- The `api/0.18.0/` baseline replaces `api/0.17.0/` as the frozen compatibility line.
- Hover cards now render module and inheritance metadata as Markdown tables instead of bold inline
  labels; scripts that scrape hover Markdown should expect the new layout.
- Structural table hovers summarize members (for example `config: {width, height, depth, …}` with a
  member list) instead of displaying only `config: table` when the table is small enough to be
  readable.
- The VS Code extension adds `lunil.require.searchPaths`, `lunil.analysis.classFactories`,
  `lunil.searchEverywhere`, `lunil.classHierarchy`, and `lunil.findUsages`.