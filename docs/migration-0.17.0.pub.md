# Migrate from Lunil 0.16 to 0.17

[简体中文](migration-0.17.0.zh-CN.pub.md)

Lunil 0.17 keeps the 0.16 compiler, runtime, hosting, analysis, and engine entry points source
compatible: the release focuses on editor navigation, unannotated class analysis, and
workspace-scale performance, without removing any public member. Three behavior changes matter
when upgrading: cross-module typing makes editor diagnostics more precise (so diagnostics can
appear or disappear), very large generated data files are excluded from indexing by default, and
the language server's residency budgets adapt to machine memory instead of using fixed values.

## 1. Update packages and tools

Update all Lunil package references as one compatibility line:

```xml
<PackageReference Include="Lunil.StandardLibrary" Version="0.17.0" />
<PackageReference Include="Lunil.Hosting" Version="0.17.0" />
```

```bash
dotnet tool update --global Lunil.Cli --version 0.17.0
```

## 2. Editor analyses are cross-module by default

`require` results now carry the required module's exported type in editor analyses, and
metatable-based classes without annotations carry instance types end to end. Diagnostics on
cross-module values can therefore appear where they were previously hidden behind `any`, and can
disappear as inferred types become precise. `---@return` annotations are honored literally — a
query annotated `Entity[]` is checked as `Entity[]`.

If specific codes become noisy, suppress them with `lunil.server.suppressedDiagnosticCodes` (VS
Code) or the CLI's repeatable `--suppress <code>` rather than disabling analysis.

## 3. Generated data files are excluded from indexing

`lunil.analysis.autoDetectDataFiles` is on by default: very large files that are pure generated
data (table literals of keys, strings, and numbers with no functions, requires, or control flow)
are kept out of workspace indexing after a bounded content scan (512 KB floor, first 4 MB
inspected). Modules excluded from analysis that are still required by code resolve as untyped
values instead of reporting unresolved-module diagnostics. Opening an excluded file in the editor
analyzes it anyway.

If a data-like file must be indexed, set `lunil.analysis.autoDetectDataFiles` to `false`; use
`lunil.analysis.exclude` to keep data trees out explicitly.

## 4. Language server memory budgets are adaptive

The language server's three residency budgets — retained module analyses, closed-document
sources, and cached document analyses — scale with the memory the runtime grants the process
(physical memory capped by the managed-heap hard limit) instead of the previous fixed 128/512/384
MB values. Each budget is a clamped fraction (floors of 64–96 MiB, caps of 512 MiB–1 GiB) and the
three combined stay under a quarter of the available memory. Machines with plenty of memory gain
headroom for huge workspaces; small machines shrink toward the floors. The heap hard limit
(`lunil.server.gcHeapHardLimitPercent`) still bounds runaway growth.

Workspace snapshots share interned names and symbol keys across rebuilds, and unchanged modules
re-merge from reusable per-module projections without re-analysis — reindex after an unrelated
edit analyzes the edited module and its dependents only.

## 5. Compatibility checklist

- No public member is removed or re-signed; the 0.16 API surface stays source compatible.
- New public API: `SourceText.GetLineIndex(int)` in Lunil.Core, annotation `TagSpan`/`NameSpan`
  spans in Lunil.EmmyLua, and `LuaWorkspace.StringInterner` (`LuaWorkspaceStringInterner`) in
  Lunil.Workspace.
- The `api/0.17.0/` baseline replaces `api/0.16.0/` as the frozen compatibility line.
- Compiler output is unchanged: the 0.17 front-end performance work does not alter language
  behavior, IR, or binary chunks (byte-identical output). Editor analyses are a separate surface:
  cross-module typing can add or remove diagnostics in the editor as described in section 2.
- The VS Code extension adds `lunil.locale`, `lunil.workspace.library`, `lunil.analysis.exclude`,
  `lunil.analysis.autoDetectDataFiles`, and `lunil.statusBar.showModuleCount`.
- Annotation semantic tokens extend the token legend with the `method` type; clients with static
  legends should regenerate them.
