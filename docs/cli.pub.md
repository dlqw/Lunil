# Lunil command-line reference

[简体中文](cli.zh-CN.pub.md)

`Lunil.Cli` exposes Lunil's compiler, workspace, host, and binary-chunk contracts through the
`lunil` .NET tool and executable. Use this reference to look up commands, options, defaults, and
exit codes.

Signed update bundles are available through `lunil patch pack`, `verify`, `inspect`, `diff`, and
`dry-run`. Verification actions accept either one `--public-key`/`--key-id` pair or a versioned
multi-key `--trust-store` with rotation and revocation windows. See the
[signed patch bundle reference](signed-patch-bundles.pub.md) for trust and resource boundaries.
`patch inspect` reports the signed `updateIntent`, canonical `requiredCapabilities`, and exact-match
`requiredTargetLabels` claims.

## Commands

```text
lunil run <input|-> [options] [-- script-args...]
lunil check <input...> [options]
lunil build <input> --output <path> [--target chunk] [options]
lunil patch <pack|verify|inspect|dry-run|diff> ... [options]
lunil dump <input> [--kind <kind>] [--format text|json] [options]
```

- `run` analyzes a source workspace before execution. It also accepts a verified PUC Lua chunk for the selected language version. Arguments after `--` become main-chunk varargs and entries `arg[1..n]`; `arg[0]` is the input identity.
- `check` accepts one or more source roots and produces deterministic cross-module diagnostics. Binary chunks are structurally verified but do not have source annotation/type views.
- `build --target chunk` writes PUC Lua chunks for the selected version (Lua 5.4 by default). A workspace writes one `.luac` per resolved module; `--strip-debug` removes chunk debug data.
- `dump` supports `summary`, `syntax`, `annotations`, `analysis`, `ir`, and `chunk` in text or `lunil.dump.v1` JSON. `--output -`, or no output path, writes to stdout.
- `--lua-version 5.1|5.2|5.3|5.4|5.5` chooses the language and chunk contract. Unsupported identities fail with a diagnostic rather than selecting Lua 5.4.

`--output` is valid for `build`, `dump`, and `patch pack`, but its contract is command-specific:
`build` and `patch pack` require a filesystem path, while only `dump` accepts `-` for stdout.
`--target` and `--strip-debug` apply only to `build`; `--kind` and `--format` apply only to `dump`.
The only build target is `chunk`. Legacy AOT target inputs fail with `LUNIL0006`, phase
`removed-feature`, and exit code `2`.

## Options

| Option | Value/default | Applies to | Description |
| --- | --- | --- | --- |
| `-h`, `--help` | Flag | All | Show global help and command-specific additions. |
| `--version` | Flag | All | Print the Lunil version. |
| `--config` | Path | All | Read an explicit `lunil.json`. Cannot be combined with `--no-config`. |
| `--no-config` | Flag | All | Disable `lunil.json` discovery. |
| `--diagnostic-format` | `text` (default), `json` | All | Select stderr diagnostic serialization. |
| `--module-root` | Repeatable path | Source commands | Add a module resolver and sandbox root. |
| `--path-pattern` | `?.lua`, `?/init.lua` | Source commands | Add a Lua `?` path pattern. |
| `--module-name` | Name | `run`, single-root `check`, `build`, `dump` | Override the root logical module name. |
| `--profile` | `trusted` (default), `sandbox`, `deterministic` | All | Select the host capability profile; `restricted` is accepted as an alias for `sandbox`. |
| `--trusted` | Flag | All | Select the trusted profile. |
| `--sandbox` | Flag | All | Select the root-confined read-only profile. |
| `--deterministic` | Flag | All | Select sandbox capabilities with deterministic time and hashing. |
| `--lua-version` | `5.4` (default); `5.1`–`5.5` | Source/chunk commands | Select the language and chunk contract. |
| `--execution` | `auto` (default), `interpreter`, `jit` | All | Select the execution backend used by `run`. |
| `--warnings-as-errors` | Flag | Source commands | Promote analysis warnings to errors. |
| `--no-warnings-as-errors` | Flag | Source commands | Override an enabled configuration/environment setting. |
| `--suppress` | Repeatable code | `check`, `build`, `dump` | Suppress an analysis diagnostic code (for example `LUA6022`). |
| `-o`, `--output` | Path | `build` | Write a chunk file, or one `.luac` per module when the path denotes a directory; stdout lists each emitted path. |
| `-o`, `--output` | Path or `-` | `dump` | Write the dump to a file; omit the option or use `-` to write the dump payload to stdout. |
| `-o`, `--output` | Path | `patch pack` | Write the signed bundle to the path; stdout prints that bundle path. |
| `--target` | `chunk` (default) | `build` | Select the build target. |
| `--strip-debug` | `false` (default) | `build` | Remove chunk debug data. |
| `--kind` | `summary` (default), `syntax`, `annotations`, `analysis`, `ir`, `chunk` | `dump` | Select the dump view. |
| `--format` | `text` (default), `json` | `dump` | Select dump serialization. |
| `--key-id` | Identifier | `patch` | Select the signing or verification key identity. |
| `--private-key` | PEM path | `patch pack` | Read the ECDSA P-256 private key. |
| `--public-key` | PEM path | Patch verification actions | Read one ECDSA P-256 public key. |
| `--trust-store` | JSON path | Patch verification actions | Replace `--public-key`/`--key-id` with a versioned multi-key store. |
| `--maximum-input-bytes` | `67108864` | All | Bound each input and resolved module. |
| `--maximum-instructions` | `100000000` | `run` | Bound VM instructions per execution. |
| `--maximum-stack-slots` | `1000000` | `run` | Bound VM stack slots. |
| `--maximum-call-depth` | `20000` | `run` | Bound Lua call depth. |
| `--maximum-heap-bytes` | `268435456` | `run` | Bound logical Lua heap bytes. |

## Inputs and modules

`-` reads one UTF-8 source document from stdin; binary chunks are file-only. `--module-name` overrides the root logical name. `--module-root` and `--path-pattern` are repeatable; patterns default to `?.lua` and `?/init.lua`. Static direct-global literal `require` calls use the same workspace module graph as API-based compilation.

`run` program output uses stdout. `build` and `patch pack` print emitted filesystem paths to stdout;
`dump` writes its payload there only when `--output` is omitted or set to `-`. Diagnostics use
stderr. `--maximum-input-bytes` bounds every input and resolved module.

## Configuration

Precedence is:

```text
built-in defaults < lunil.json < LUNIL_* environment < CLI/response-file arguments
```

The CLI discovers `lunil.json` in the current directory. Use `--config <path>` to select a file or `--no-config` to disable discovery. Unknown properties, invalid types, oversized files, and using both options are usage errors.

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

Relative `moduleRoots` are resolved from the configuration file. Equivalent variables are `LUNIL_PROFILE`, `LUNIL_LUA_VERSION`, `LUNIL_EXECUTION`, `LUNIL_DIAGNOSTIC_FORMAT`, `LUNIL_BUILD_TARGET`, `LUNIL_DUMP_KIND`, `LUNIL_DUMP_FORMAT`, `LUNIL_MODULE_ROOTS`, `LUNIL_PATH_PATTERNS`, `LUNIL_WARNINGS_AS_ERRORS`, `LUNIL_STRIP_DEBUG`, `LUNIL_MAXIMUM_INPUT_BYTES`, `LUNIL_MAXIMUM_INSTRUCTIONS`, `LUNIL_MAXIMUM_STACK_SLOTS`, `LUNIL_MAXIMUM_CALL_DEPTH`, and `LUNIL_MAXIMUM_HEAP_BYTES`.

## Response files

An argument beginning with `@` expands a UTF-8 response file. Nested response-file paths are relative to the containing file. Quotes, supported backslash escaping, blank lines, and `#` comments are recognized; `@@value` produces a literal `@value`. Expansion rejects cycles and enforces file-size, nesting-depth, and argument-count budgets.

## Execution profiles and budgets

`--execution auto|interpreter|jit` selects the `run` backend. `auto` uses a dynamic-code backend when supported and otherwise uses the reference interpreter. `interpreter` is a deterministic opt-out. `jit` requires dynamic-code support and falls back for functions the JIT cannot compile. `build`, `check`, and `dump` validate this option but do not execute Lua.

- `--trusted` uses ordinary host capabilities.
- `--sandbox` supplies a root-confined read-only filesystem; paths outside the allowed roots, writes, temporary files, and symlink/reparse-point traversal are rejected.
- `--deterministic` adds deterministic time and hash behavior to sandbox capabilities.

All profiles enforce instruction, stack-slot, call-depth, logical-heap, and input-byte budgets.

## Diagnostics and exit codes

`--diagnostic-format text|json` chooses text or the stable `lunil.diagnostics.v1` envelope. `--warnings-as-errors` promotes analysis warnings. Ctrl+C requests cooperative cancellation.

| Code | Meaning |
| ---: | --- |
| `0` | Success; warnings may have been emitted |
| `1` | Source, workspace, chunk, or analysis error |
| `2` | Usage or configuration error |
| `3` | Input or output acquisition failure |
| `4` | Lua execution error or runtime budget failure |
| `5` | Artifact build or host failure |
| `130` | Cancellation |
