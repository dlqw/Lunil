# Rich Lua editing

* **Completions** for locals, globals, members after `.` and `:`, and `require`
  module names, with signature help inside calls.
* **Hover** shows inferred types; **Rename** (F2) works across the whole workspace.
* **Semantic highlighting** distinguishes variables, parameters, functions, and
  properties; **inlay hints** show inferred types.
* **Go to Definition**, **Find All References**, **Call Hierarchy**, folding, and
  selection ranges follow the cross-module index.
* Curated snippets cover common idioms (`lf`, `forn`, `class`, `pcall`) and Lunil's
  annotation comments (`--class`, `--param`, `--return`, `--type`, `--generic`).

Type diagnostics (`LUA6000` line) are annotation-driven and flow-sensitive; you can
silence individual codes with `lunil.server.suppressedDiagnosticCodes`.
