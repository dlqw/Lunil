# How to configure the Lunil language server

[简体中文](configuring-the-language-server.zh-CN.pub.md)

This guide configures the language server for real projects: localization, host-injected library
stubs, excluding generated data files from indexing, require search roots, class factories, and
memory budgets. It assumes the extension is installed and a workspace is indexed; see
[VS Code](vscode.pub.md) for installation and [LuaWorkspace for large
repositories](large-workspaces.pub.md) for the embedding library's budgets.

The settings below are shown in the VS Code `settings.json` format (`lunil.*` flat names). The raw
server payload nests the same values under `settings.lunil` (`workspace.library`,
`analysis.exclude`, ...); see the [language server reference](language-server.pub.md) for that form.

## 1. Choose the interface language

`lunil.locale` (`auto`, `en`, or `zh-cn`, default `auto`) localizes hover cards, signature-help
documentation, Lunil menu entries, status-bar text, and index-status messages. `auto` follows the VS
Code UI language. The raw server also accepts `locale` as an initialization option or through
`workspace/didChangeConfiguration`. Changing the value applies without restarting the server.

## 2. Describe host-injected APIs with library stubs

`lunil.workspace.library` points at read-only folders of LuaLS-style `---@meta` declaration stubs
that describe globals and classes a host (C++, C#, Unity, Godot, a game engine) injects at runtime:

```json
{
  "lunil.workspace.library": ["${workspaceFolder}/meta/game", "${workspaceFolder}/meta/net"]
}
```

Stub globals seed every analysis, so chains like `Game.Player.Move()` keep types, doc comments,
member completion, and signature hovers instead of degrading to `any`. Declared `---@class` types
join the workspace declaration map (type-name navigation and inheritance), and `require` can
resolve modules inside library folders. Editing a stub and running **Lunil: Reindex Workspace**
picks up the changes without a restart. This is the hand-written, community-format path; machine-
generated host contracts use `lunil.hostContractPath` / `lunil.hostContractJson` instead.

## 3. Keep generated data out of indexing

`lunil.analysis.autoDetectDataFiles` (on by default) keeps very large generated data files out of
indexing automatically. A file is considered pure data when it consists of table literals of keys,
strings, and numbers with no functions, requires, or control flow. Detection applies to files of at
least 512 KB and inspects at most the first 4 MB, so ordinary code and small config tables are
untouched.

`lunil.analysis.exclude` adds explicit glob patterns:

```json
{
  "lunil.analysis.exclude": ["data/**", "**/*.data.lua", "assets/{tables,configs}/**"]
}
```

- Patterns match workspace-relative paths, `/`-separated.
- A pattern without a separator matches the file name in any directory.
- Matching is case-insensitive.

Both settings re-scan the workspace without a restart when changed. Excluded files are not read
into memory or analyzed during indexing, so multi-gigabyte generated corpora no longer inflate
residency or index time. A module excluded from analysis that is still required by code resolves
as an untyped value instead of reporting an unresolved-module diagnostic; a module indexed in the
workspace always wins over the exclusion list. Opening an excluded file in the editor analyzes it
anyway, and closing it returns it to the excluded set. **Lunil: Show Index Status** lists excluded
files with their reason (pattern match or auto-detected data).

## 4. Resolve require strings through search roots

`lunil.require.searchPaths` adds optional directory roots for module resolution. A require string
is first tried exactly as written, then with each root as a dotted prefix:

```json
{
  "lunil.require.searchPaths": ["scripts/client", "scripts/shared"]
}
```

With the example above, `require("Utils.HttpUtils")` can resolve a module named
`scripts.client.Utils.HttpUtils` or `scripts.shared.Utils.HttpUtils` (whichever exists first).
Roots are normalized from `/` or `\` to dots, so `scripts/client` and `scripts.shared` are
equivalent. Changing the setting re-scans the workspace without restarting the server.

## 5. Recognize class factories

`lunil.analysis.classFactories` tells analysis which global functions define classes. A factory
call's first string literal argument names the class; mark `baseArguments: true` when the remaining
bare-identifier arguments are base classes:

```json
{
  "lunil.analysis.classFactories": [
    "defineView",
    { "name": "class", "baseArguments": true }
  ]
}
```

Recognized factories make `local X = class("Name", Base)` behave like an annotated class value:
hover shows the class card, `function X:method()` writes define methods, `X.new()` produces
instances, base members resolve, and class hierarchy includes the class. Changing the setting
re-scans the workspace without restarting the server.

## 6. Understand the memory budgets

The server's residency budgets scale with the memory the runtime grants the process (physical
memory capped by the managed-heap hard limit): retained module analyses, closed-document sources,
and cached document analyses each take a clamped fraction of the total (floors of 64–96 MiB, caps
of 512 MiB–1 GiB; combined under a quarter of the available memory). Unchanged modules re-merge
from reusable snapshot projections and share interned names and symbol keys across rebuilds, so
rebuild peaks stay close to the steady state. `lunil.server.gcHeapHardLimitPercent` (20–90,
default 70) bounds runaway growth over the whole process. Small machines shrink the budgets toward
the floors; large machines gain headroom for huge workspaces.

There is nothing to configure for the budgets themselves; the values adapt automatically. Hosts
that embed `LuaWorkspace` and need explicit control use [LuaWorkspace options](large-workspaces.pub.md)
instead.

## Expected result

The server speaks the chosen language, knows the host's injected API surface, resolves configured
require prefixes, recognizes class factories, ignores generated data while indexing, and keeps its
residency proportional to the machine — with lookup facts for every setting in the [language server
reference](language-server.pub.md) and migration notes in the
[0.18 guide](migration-0.18.0.pub.md).