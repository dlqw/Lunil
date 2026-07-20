# Lunil command-line reference

`Lunil.Cli` packages the public compiler, workspace, host, and chunk contracts as the `lunil`
.NET tool and as an executable in every RID release bundle.

## Commands

```text
lunil run <input|-> [options] [-- script-args...]
lunil check <input...> [options]
lunil build <input> --output <path> [--target chunk] [options]
lunil dump <input> [--kind <kind>] [--format text|json] [options]
```

- `run` analyzes source workspaces before execution and accepts a verified PUC Lua 5.4 chunk as
  input. Arguments after `--` become main-chunk varargs and entries `arg[1..n]`; `arg[0]` is the
  input identity.
- `check` accepts one or more source roots and produces deterministic cross-module diagnostics.
  Binary chunks are structurally verified but do not have source annotation/type views.
- `build --target chunk` writes canonical PUC Lua 5.4 chunks. A workspace emits one `.luac` per
  resolved module. `--strip-debug` removes chunk debug data.
- Lua AOT was removed in `0.8.0-alpha.12`. Legacy `--target aot`, JSON
  `buildTarget: "aot"`, and `LUNIL_BUILD_TARGET=aot` inputs fail closed with diagnostic
  `LUNIL0006`, phase `removed-feature`, and exit code `2`; they never masquerade as chunk output.
- `dump` supports `summary`, `syntax`, `annotations`, `analysis`, `ir`, and `chunk`; output can be
  text or `lunil.dump.v1` JSON. `--output -` or no output path writes to stdout.
- `--lua-version 5.1|5.2|5.3|5.4|5.5` selects the explicit language contract. Lua 5.4 is the
  default. The Lua 5.3 source/interpreter path and Lua 5.3 binary-chunk importer are available in
  the current 0.10.0 development slice, including Lua 5.3 chunk build and `string.dump` output;
  versions whose semantics are not implemented yet fail
  with a diagnostic rather than silently using Lua 5.4.

`--output` is valid only for `build` and `dump`; `--target` and `--strip-debug` only for `build`;
`--kind` and `--format` only for `dump`. These are usage errors rather than silently ignored flags.

## Inputs, module resolution, and output streams

- `-` reads one complete UTF-8 source document from stdin. Binary chunks are file-only.
- `--module-name` overrides the root logical module name. Without it, the name is derived relative
  to the first matching module root, or relative to the input directory when no root was supplied.
- `--module-root` is repeatable. `--path-pattern` is repeatable and defaults to `?.lua` and
  `?/init.lua`. Static direct-global literal `require` calls are resolved and analyzed through the
  same `Lunil.Workspace` graph used by the public API and CLI build pipeline.
- Program output uses stdout. Diagnostics use stderr. Build commands print emitted artifact paths
  to stdout, so automation can consume them without parsing diagnostics.
- Every input and resolved module is bounded by `--maximum-input-bytes`.

## Configuration and precedence

The precedence order is:

```text
built-in defaults < lunil.json < LUNIL_* environment < CLI/response-file arguments
```

The CLI discovers `lunil.json` in the current directory. Use `--config <path>` for an explicit file
or `--no-config` to disable discovery. Unknown properties, invalid types, oversized files, and the
combination of `--config` with `--no-config` are usage errors.

Supported JSON properties are:

```json
{
  "profile": "deterministic",
  "luaVersion": "5.4",
  "execution": "auto",
  "diagnosticFormat": "json",
  "buildTarget": "chunk",
  "dumpKind": "analysis",
  "dumpFormat": "json",
  "moduleRoots": ["src", "vendor"],
  "pathPatterns": ["?.lua", "?/init.lua"],
  "warningsAsErrors": true,
  "stripDebug": false,
  "maximumInputBytes": 67108864,
  "maximumInstructions": 100000000,
  "maximumStackSlots": 1000000,
  "maximumCallDepth": 20000,
  "maximumHeapBytes": 268435456
}
```

Relative `moduleRoots` in a configuration file are resolved relative to that file. The equivalent
environment variables are `LUNIL_PROFILE`, `LUNIL_LUA_VERSION`, `LUNIL_EXECUTION`, `LUNIL_DIAGNOSTIC_FORMAT`,
`LUNIL_BUILD_TARGET`, `LUNIL_DUMP_KIND`, `LUNIL_DUMP_FORMAT`, `LUNIL_MODULE_ROOTS`,
`LUNIL_PATH_PATTERNS`,
`LUNIL_WARNINGS_AS_ERRORS`, `LUNIL_STRIP_DEBUG`, `LUNIL_MAXIMUM_INPUT_BYTES`,
`LUNIL_MAXIMUM_INSTRUCTIONS`, `LUNIL_MAXIMUM_STACK_SLOTS`, `LUNIL_MAXIMUM_CALL_DEPTH`, and
`LUNIL_MAXIMUM_HEAP_BYTES`.

## Response files

An argument beginning with `@` expands a UTF-8 response file. Response-file paths nested inside a
response file are relative to the containing file. Single/double quotes, backslash escaping, blank
lines, and `#` comments are supported. `@@value` emits the literal argument `@value`.

Expansion rejects cycles and applies fixed file-size, nesting-depth, and expanded-argument-count
budgets before command parsing. Windows path separators are preserved unless the backslash escapes
a quote, backslash, whitespace, or `#`.

## Capability profiles and budgets

`--execution auto|interpreter|jit` selects the runtime backend used by `run`. `auto` is the
default: it uses the qualified tiered JIT when the current .NET runtime supports compiled dynamic
code and otherwise uses the reference interpreter. `interpreter` is a deterministic opt-out;
`jit` requires dynamic-code support and still uses the reference interpreter for functions the
JIT rejects. The same selection is available through the JSON `execution` property and
`LUNIL_EXECUTION`. Build, check, and dump commands validate the option but do not execute Lua code.

- `--trusted` uses the host's normal filesystem, environment, process, clock, locale, and related
  capabilities.
- `--sandbox` installs a root-confined read-only filesystem. Current directory, explicit module
  roots, and input directories form the allowed roots. Traversal outside them, writes, temporary
  files, and symlink/reparse-point traversal are rejected.
- `--deterministic` uses sandbox capabilities plus deterministic time and hash behavior.

All profiles honor maximum instruction count, stack slots, call depth, logical heap bytes, and
input bytes. Budget failures are execution or input failures, not unhandled host exceptions.

## Diagnostics and exit codes

`--diagnostic-format text|json` selects human-readable diagnostics or the stable
`lunil.diagnostics.v1` envelope. `--warnings-as-errors` promotes analysis warnings before deciding
the exit code. Ctrl+C requests cooperative cancellation.

| Code | Meaning |
| ---: | --- |
| `0` | Success; warnings may have been emitted |
| `1` | Source, workspace, chunk, or analysis diagnostics contain an error |
| `2` | Usage or configuration error |
| `3` | Input or output acquisition failure |
| `4` | Lua execution error or runtime budget failure |
| `5` | Artifact build failure or internal host failure |
| `130` | Cancellation |

## Distribution verification

CI installs `Lunil.Cli` from the locally packed NuGet set and runs source, chunk, and JSON dump
smoke tests. Every release bundle must contain the RID apphost, `lunil.dll`, deps/runtimeconfig,
and the component assemblies. NativeAOT publication is exercised on all six release RIDs; trimmed
single-file and ReadyToRun publications additionally exercise response files, JSON configuration,
deterministic execution, warnings-as-errors diagnostics, and chunk building.
