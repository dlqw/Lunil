<p align="center">
  <img src="assets/lunil-logo.svg" width="168" alt="Lunil logo">
</p>

<h1 align="center">Lunil</h1>

<p align="center">
  A correctness-first Lua 5.4 compiler and managed runtime for modern .NET.
</p>

<p align="center">
  <strong>English</strong> · <a href="README.zh-CN.md">简体中文</a>
</p>

<p align="center">
  <a href="https://github.com/dlqw/Lunil/actions/workflows/ci.yml"><img alt="CI" src="https://img.shields.io/github/actions/workflow/status/dlqw/Lunil/ci.yml?branch=main&style=flat-square&label=CI"></a>
  <a href="https://github.com/dlqw/Lunil/releases"><img alt="Version" src="https://img.shields.io/badge/version-0.9.0--alpha.1-7c3aed?style=flat-square"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-MIT-22c55e?style=flat-square"></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet">
  <img alt="Lua 5.4.8" src="https://img.shields.io/badge/Lua-5.4.8-2C2D72?style=flat-square&logo=lua">
  <img alt="Platforms" src="https://img.shields.io/badge/platforms-Windows%20%7C%20Linux%20%7C%20macOS-0ea5e9?style=flat-square">
</p>

Lunil is a pure C# implementation of a Lua 5.4.8 compiler pipeline and runtime.
It preserves Lua's byte-oriented source and binary-string semantics while providing
immutable syntax trees, semantic analysis, verified canonical IR, PUC Lua binary
chunk interoperability, a managed interpreter, and an explicit logical garbage
collector.

> [!IMPORTANT]
> The current source version is **`0.9.0-alpha.1`**; stable `0.8.0` and its API/package baselines
> remain immutable. This Alpha removes the legacy Loop OSR-only IL emitter so Tier 2 function entry
> and OSR backedge entry share one numeric proof/emission pass. Loop OSR remains an isolated
> benchmark/configuration entry, and managed fallback remains canonical. Lua AOT stays removed;
> .NET NativeAOT interpreter compatibility remains supported.

## Table of contents

- [Why Lunil](#why-lunil)
- [Project status](#project-status)
- [Features](#features)
- [Quick start](#quick-start)
- [Using Lunil as a library](#using-lunil-as-a-library)
- [Architecture](#architecture)
- [Repository layout](#repository-layout)
- [Compatibility and platforms](#compatibility-and-platforms)
- [Packages and releases](#packages-and-releases)
- [Documentation](#documentation)
- [Contributing](#contributing)
- [Security](#security)
- [License](#license)

## Why Lunil

- **Lua fidelity first** — targets Lua 5.4.8 syntax, opcodes, number behavior,
  multiple results, varargs, coroutines, metatables, to-be-closed variables, and
  binary chunks without silently replacing Lua semantics with CLR semantics.
- **Managed and embeddable** — implemented in C# for .NET 10, with explicit runtime
  ownership, resource budgets, handles, protected errors, and host-facing APIs.
- **One verified IR** — source compilation and imported PUC Lua chunks converge on a
  shared canonical register IR with structural and control-flow verification.
- **Designed for multiple execution tiers** — the reference interpreter, profile-guided CoreCLR
  Tier 1/Tier 2 JIT, and guarded exact-numeric loop OSR share one verified execution contract.
  .NET NativeAOT deployments use the same compiler/runtime surface with deterministic interpreter
  fallback when dynamic code is unavailable.
- **Testable by construction** — deterministic fuzzing, GC stress, malformed-input
  tests, binary round trips, and PUC Lua differential fixtures are part of the design.

## Project status

| Area | Status | Notes |
| --- | --- | --- |
| Lexer and parser | Implemented | Complete Lua 5.4 grammar, lossless trivia, bounded error recovery |
| Binding and lowering | Implemented | Locals, captures, `_ENV`, attributes, labels/gotos, verified canonical IR |
| Binary chunks | Implemented | Bounded Lua 5.4 reader/writer/verifier and PUC prototype conversion |
| Reference interpreter | Implemented | Calls, varargs, multiple results, control flow, coroutines, errors and close unwinding |
| Runtime and logical GC | Implemented | Tables, values, metatables, quotas, handles, weak tables, ephemerons and finalizers |
| Standard library | Implemented | Basic, coroutine, table, string, math, utf8, package, io, os, and debug libraries |
| Execution backends | `0.9` Alpha | Interpreter, Tier 1, and Tier 2; loop OSR is a Tier 2 backedge entry over the shared numeric pass |
| Compiler product API | Stable `0.7` | Unified bounded lex/parse/bind/lower/verify pipeline, immutable results, phase diagnostics, cancellation boundaries, and canonical source identity |
| Hosting product API | Stable `0.7` | Reusable compile/execute host with explicit trusted, restricted, and deterministic capability profiles and runtime budgets |
| Annotation product API | Stable `0.7` | Shared bounded annotation lexer/type AST, LuaLS default parser, legacy EmmyLua compatibility, unknown-tag preservation, configurable diagnostics, and suppression |
| Type and flow analysis API | Stable `0.7` | Semantic type/pack model, annotation declarations, constraints, CFGs, function/return inference, nil/type/assert/discriminant narrowing, definite assignment, unreachable analysis, generics, source suppression, and deterministic widening budgets |
| Workspace product API | Stable `0.7` | Stable module/source identities, injectable resolvers, static/dynamic require classification, SCC fixed points, content-addressed caching, minimal invalidation, bounded parallelism, cancellation, and deterministic merging |
| CLI | Stable `0.7` | Packaged `lunil` tool with `run`/`check`/`build`/`dump`, stable exit codes, text/JSON diagnostics, stdin, response files, layered configuration, workspace resolution, resource budgets, and trusted/sandbox/deterministic profiles |
| Stability contract | Active `0.9` Alpha | Stable `0.8.0` remains frozen; `0.9.0-alpha.1` permits reviewed backend architecture work |

### Current backend readiness

| Execution path | Release behavior | `0.9.0-alpha.1` readiness |
| --- | --- | --- |
| Reference interpreter | Explicit Tier 0 and exact fallback | Implemented and used as the semantic reference |
| CoreCLR Tier 1 JIT | `Auto` for repeatedly hot, benefit-qualified functions | Qualified on all six release RIDs |
| Exact-numeric Tier 2 JIT | Automatic promotion after Tier 1 qualification | Qualified on all six release RIDs; managed semantic profiles stay on Tier 1 unless explicitly enabled |
| Exact-numeric loop OSR | Enabled by default after loop and runtime-value qualification | Qualified on all six release RIDs; non-exact loops are rejected before compilation |
| .NET NativeAOT / trimming | Compiler, workspace, runtime, CLI, and interpreter compatibility | Build and execution verified on all six release RIDs; JIT selection falls back deterministically |

Stable `0.8.0` remains the product and regression floor. `0.9.0-alpha.1` begins the backend
architecture work described in its [changelog](changelogs/0.9.0-alpha.1.md) without changing the
stable `api/0.8.0` contract.
Breaking migration details, including the Lua AOT removal, are documented in the
[`0.8.0` migration guide](docs/migration-0.8.0.md).

## Features

### Compiler and IR

- Immutable byte-oriented `SourceText` with byte and UTF-16 diagnostic locations.
- Lossless lexer with trivia preservation and complete numeric/string decoding.
- Error-tolerant immutable syntax trees for the complete Lua 5.4 grammar.
- Lexical binding for locals, captures, `_ENV`, attributes, labels, and gotos.
- Syntax/semantic lowering into verified canonical register IR.
- A public `LuaCompiler` pipeline with bounded phase options, stable phase-attributed
  diagnostics, cancellation boundaries, immutable results, and logical source identities.
- A public LuaLS/legacy EmmyLua annotation front end with bounded lexing/type parsing,
  independent dialect parsers, compatibility resolution, unknown-tag preservation, and
  diagnostic suppression; annotations remain erased from runtime IR.
- A public `Lunil.Analysis` phase with immutable semantic types, type packs, class/alias/enum
  declarations, structural tables, overloads, generics, constraints and CFGs; flow analysis
  performs nil/type/assert/discriminant/short-circuit narrowing, definite-assignment and
  unreachable checks, return-pack inference, source suppression, and bounded widening.
- A public `Lunil.Workspace` layer with stable module/source identities, in-memory and root-confined
  file resolvers, direct-global static `require` extraction, dynamic boundaries, deterministic SCC
  fixed points, dependency-aware export types, content-addressed caches, minimal invalidation,
  global budgets, cancellation, bounded parallel scheduling, and deterministic result merging.
- Full Lua 5.4 opcode model with binary-compatible 32-bit instruction layouts.
- Bounded PUC Lua 5.4 binary chunk reading, writing, validation, and conversion.

### Runtime

- 16-byte tagged value representation and binary Lua strings.
- Heap-owned tables, closures, threads, upvalues, and native callable descriptors.
- Array plus open-addressed-hash tables with tombstones and stable `next` traversal.
- Incremental/generational tri-color logical GC with barriers, remembered sets,
  weak tables, ephemerons, finalizers, resurrection, quotas, and host handles.
- Reference interpreter with numeric-string coercion, resource budgets, tail calls,
  multiple results, varargs, and open stack windows.
- Non-recursive coroutine scheduler and resumable native continuation ABI.
- Shared type/object metatable dispatch, `pcall`/`xpcall`, and resumable `__close`
  unwinding.

### Standard library

- Complete managed Lua 5.4 basic, coroutine, table, string, math, utf8, package, io,
  os, and debug libraries, installed together with `LuaStandardLibrary.InstallAll`.
- Explicit Lua pattern VM, `string.format`, binary `pack`/`unpack`, and canonical
  IR-to-Lua 5.4 chunk generation for `string.dump`.
- Full/light userdata, registry, file userdata lifecycle, close/finalizer behavior,
  debug hooks, nested resumable callbacks, and generic-for closing values.
- Injectable filesystem, console, environment, operating-system, clock, process,
  locale, timezone, and pipe providers for deterministic or sandboxed hosts.
- Pure Lua `package` loaders are supported. Native Lua C modules remain unsupported
  because Lunil does not expose the Lua C ABI.

### Verification and quality

- Execution-grade chunk and IR verification.
- PUC Lua 5.4.8 binary/runtime differential fixtures and portable standard-library
  suites for strings, patterns, packing, math, UTF-8, files, loading, and debug behavior.
- Deterministic malformed-IR, table, GC, and coroutine fuzzing.
- GC-stress and ownership tests plus a runtime benchmark harness.
- CI on Windows, Linux, and macOS with release bundles for x64 and Arm64.

### Command-line product

- `lunil run` preflights source workspaces or executes validated PUC Lua 5.4 chunks, publishes
  main-chunk varargs and `arg`, and keeps program output separate from diagnostics.
- `lunil check` analyzes one or more roots with cross-module types; `lunil build` emits portable
  Lua 5.4 chunks; `lunil dump` exposes summary, syntax, annotations, analysis, IR, and chunk views
  as text or JSON. Removed `--target aot` inputs fail with stable diagnostic `LUNIL0006`.
- Defaults, `lunil.json`, `LUNIL_*` environment variables, response files, and direct CLI options
  have defined precedence. Trusted, root-confined read-only sandbox, and deterministic profiles
  share explicit input, instruction, stack, call-depth, and heap budgets.
- `--execution auto|interpreter|jit`, JSON `execution`, and `LUNIL_EXECUTION` select the qualified
  execution backend. `auto` uses the JIT when dynamic code is available and the interpreter on
  NativeAOT; check/build/dump avoid initializing an execution host.
- The `Lunil.Cli` .NET tool package and RID bundles are smoke-tested; NativeAOT, trimmed
  single-file, and ReadyToRun CLI publications run parser/configuration/JSON/build checks in CI.

## Quick start

### Prerequisites

- [.NET SDK 10.0.103](https://dotnet.microsoft.com/download/dotnet/10.0) or a
  compatible 10.0 patch release;
- Git;
- optional: PUC Lua 5.4.8 tools for interoperability fixtures.

### Install and use the CLI

Install the tagged tool package from the configured GitHub Packages source, or run the project
directly from a checkout:

```bash
dotnet tool install --global Lunil.Cli --version 0.9.0-alpha.1
lunil --version

lunil run app.lua -- one two
lunil check app.lua --module-root . --warnings-as-errors
lunil build app.lua --target chunk --output app.luac
lunil dump app.lua --kind analysis --format json
```

Use `-` for source stdin, `@arguments.rsp` for UTF-8 response files, and `lunil.json` for project
defaults. See the [CLI reference](docs/cli.md) for configuration, profiles, diagnostics, and exit
codes.

### Build from source

```bash
git clone https://github.com/dlqw/Lunil.git
cd Lunil
dotnet restore Lunil.sln
dotnet build Lunil.sln --configuration Release --no-restore
dotnet test Lunil.sln --configuration Release --no-build --no-restore
```

Verify formatting or run the benchmark harness:

```bash
dotnet format Lunil.sln --verify-no-changes --no-restore
dotnet run --configuration Release \
  --project benchmarks/Lunil.Runtime.Benchmarks -- 1000000
```

The cross-runtime workflow builds pinned native Lua 5.4.8 and LuaJIT, then compares them with
MoonSharp and all Lunil configurations using identical Lua source. Native Lua is always the
`1.000x` baseline. Lunil Auto and Tier 2 are release-gated per workload at a median of at least
`1.05x` versus MoonSharp with a paired CI95 lower bound of at least `1.00x`:

```powershell
./scripts/Install-CrossRuntimeBenchmarkTools.ps1 -RuntimeIdentifier win-x64
./scripts/Measure-CrossRuntimePerformance.ps1 `
  -RuntimeIdentifier win-x64 -Rounds 6 -TargetMilliseconds 250 -NoProvision
```

The qualified Windows x64 matrix and six-RID CI run `29459923109` pass every workload gate; CI
rejects any missing, incomplete, or failed RID report. See the
[cross-runtime performance workflow](docs/cross-runtime-performance.md) for results and evidence.

## Using Lunil as a library

Tagged releases provide six RID bundles and publish the corresponding `Lunil.*`
NuGet and symbol packages to GitHub Packages. Projects may also be referenced directly
from a source checkout.

```xml
<PackageReference Include="Lunil.Hosting" Version="0.9.0-alpha.1" />
```

The high-level host compiles, verifies, installs the standard library, and executes through one
reusable state. The default profile is restricted and the default execution backend is `Auto`:
qualified JIT on dynamic-code runtimes, reference interpreter otherwise. Trusted access,
deterministic capabilities, and a forced execution backend are explicit options:

```csharp
using Lunil.Hosting;
using Lunil.Runtime.Execution;

const string lua = """
    local total = 0
    for i = 1, 10 do
        total = total + i
    end
    return total
    """;

var host = new LuaHost(LuaHostOptions.Restricted);
var run = host.RunUtf8(lua, "@examples/sum.lua");

if (!run.CompilationSucceeded)
{
    foreach (var diagnostic in run.Compilation.Diagnostics)
    {
        Console.Error.WriteLine($"{diagnostic.Phase} {diagnostic.Code}: {diagnostic.Message}");
    }

    return;
}

if (run.Execution?.Signal != LuaVmSignal.Completed)
{
    throw new InvalidOperationException("Lua execution did not complete.");
}

Console.WriteLine(run.Execution.Values[0].AsInteger()); // 55
Console.WriteLine(host.SelectedExecutionBackend);       // Jit or Interpreter
```

Set `LuaHostOptions.ExecutionBackend` to `Interpreter` for an explicit Tier 0 route or to `Jit`
to require dynamic-code support. `LuaHost.IsDynamicCodeAvailable` reports the runtime capability;
`LuaHost.JitStatistics` exposes telemetry after the lazily created JIT executor first runs.

To execute a validated PUC Lua 5.4 binary chunk:

```csharp
var bytecode = File.ReadAllBytes("program.luac");
var host = new LuaHost(LuaHostOptions.Restricted);
var result = host.ExecuteBinaryChunk(bytecode);
```

Successful `require` calls are tracked as cache-validated module records. While the host state is
idle, a file-backed module can be reread, compiled, executed, and committed without exposing a
partially loaded cache value:

```csharp
var reload = host.ReloadModule("settings", new LuaModuleReloadOptions
{
    CachePolicy = LuaModuleReloadCachePolicy.PatchExistingTable,
});

if (!reload.Succeeded)
{
    Console.Error.WriteLine($"{reload.Status}: {reload.Message}");
}
```

`ReplaceCache` publishes a new value, `PatchExistingTable` preserves existing module-table
references, and `Custom` delegates the cache merge to the host. Compile failures do not execute;
execution/policy failures restore the old `package.loaded` value and record, while
`SideEffectsMayHaveOccurred` reports that arbitrary Lua or host effects cannot be rolled back.
Preload and custom-searcher loaders are replayed with their recorded loader data. `loadfile` and
`dofile` are deliberately not registered as named modules. Reload now updates existing aliases to
exported Lua closures when the stable lexical key and complete upvalue layout match. The closure
keeps its original upvalue cells; active and suspended frames keep the immutable function version
captured on entry, while later calls observe the atomically published version. Incompatible
layouts remain on old code and are reported through `FunctionMigrations`.

The same host exposes a reusable incremental workspace without changing runtime `package` or
`require` behavior:

```csharp
using Lunil.Workspace;

using var host = new LuaHost(LuaHostOptions.Restricted);
var workspace = await host.AnalyzeWorkspaceAsync([
    LuaWorkspaceDocument.FromUtf8(
        "app",
        "local dep = require('dep')\nreturn dep.value + 1"),
    LuaWorkspaceDocument.FromUtf8("dep", "return { value = 41 }"),
]);

Console.WriteLine(workspace.GetModule("app")!.ExportedType.DisplayName); // integer
```

`LuaCompiler`, `LuaExecutor`, and the individual syntax, annotation, analysis, workspace, semantic, and IR
packages remain available to lower-level consumers. `LuaHost` is the product-level embedding
entry point; `LuaInterpreter` remains available when a host explicitly requires the Tier 0
reference backend.

`LuaJitExecutor` is the CoreCLR dynamic backend. Its release default is `Auto`: execution starts
in the interpreter and only deterministic, repeatedly hot, benefit-qualified functions are queued
for Tier 1 compilation. Exact integer, float, and mixed-numeric profiles are automatically
promoted when eligibility analysis guarantees `ExactNumericSpecializedCil`. Stable table PIC and
known-closure call sites use `GuardedSpecializedCil`; the same method can compose those guarded
outer paths with unboxed numeric regions. Unsupported or widened polymorphic shapes remain on
Tier 1. Guard failure deoptimizes exactly to canonical execution and re-runs eligibility against
the widened profile. Set
`EnableTier2=false` to disable Tier 2 profiling and profile import completely, or explicitly set
`EnableTier2ManagedFallback=true` to allow the still-experimental `ManagedProfileProgram` path.
Dynamic-code-unavailable deployments, including NativeAOT, keep exact interpreter fallback and do
not collect Tier 2 profiles.

`Invalidate`, `ClearCache`, and executor disposal form a strict module-generation quiescence
boundary for the dynamic JIT: old delegates stop admitting entries, generated backedges return
`BackendInvalidated`, and invalidation waits for every old lease to exit. For source revisions,
`LuaJitProfileRemapper.Remap` can create a target-identity payload only for functions whose lexical
identity, signature, upvalue layout, and complete canonical `Opcode/A/B/C/D` stream are unchanged;
self-module call targets are rewritten and all other changed functions receive empty profiles.

Loop OSR is independently enabled by default, but admits only verified natural loops whose
hotness-delayed eligibility analysis guarantees
`GuardedExactNumericCil`. Before queue admission, every numeric guard site must also observe exact
integer/float operands; a non-exact or metamethod operand is permanently rejected through `JIT3105`
without compilation or guard churn. The specialized emitter is prepared lazily only after that
runtime qualification succeeds, so negative and short-loop workloads do not pay its one-time
initialization cost. Load/move, control flow, numeric-for, guarded close, and exact numeric
operations execute inside the generated loop method with canonical-PC, budget, hook/debug, GC, and
deopt guards. `EnableLoopOsrManagedFallback=true` explicitly restores the experimental managed
canonical-loop and guard-widening path. Set `EnableLoopOsr=false` for a complete opt-out from
analysis, qualification, preparation, and compilation. Dynamic-code-unavailable runtimes keep the
same complete fallback regardless of the configured default.

Lua persisted/static AOT, its artifact/loader APIs, and the `Lunil.Build` package were removed in
`0.8.0-alpha.12`. Existing consumers should run verified modules through `LuaHost`,
`LuaInterpreter`, or `LuaJitExecutor`; old CLI/config/environment AOT targets fail closed with
`LUNIL0006` instead of silently selecting another backend. See the matching changelog and the
[NativeAOT compatibility guide](docs/nativeaot-build-integration.md).

Untrusted source and bytecode should use bounded parser/chunk options, interpreter
instruction and stack budgets, and heap quotas appropriate for the host.

### .NET NativeAOT and trimming

Publish ordinary Lunil consumers with `PublishAot=true` or `PublishTrimmed=true`. The compiler,
workspace, hosting, CLI, and reference interpreter remain available; `LuaJitExecutor` reports that
dynamic code is unavailable and executes through the interpreter without collecting JIT profiles.
The six-RID fixture treats trimming warnings as errors and verifies source compilation, workspace
analysis, runtime execution, and JIT capability fallback. See the
[NativeAOT compatibility guide](docs/nativeaot-build-integration.md).

## Architecture

```mermaid
flowchart LR
    Source[Lua source bytes] --> Text[SourceText]
    Text --> Lexer[Lossless lexer]
    Lexer --> Parser[Error-tolerant parser]
    Parser --> Binder[Semantic binder]
    Text --> Annotation[LuaLS / EmmyLua annotations]
    Annotation --> Analysis[Type + flow analysis]
    Binder --> Analysis
    Analysis --> Workspace[Incremental module workspace]
    Binder --> Lowerer[Canonical lowering]

    Chunk[PUC Lua 5.4 chunk] --> Reader[Reader + verifier]
    Reader --> Converter[Prototype converter]

    Lowerer --> IR[Verified canonical register IR]
    Converter --> IR
    IR --> Interpreter[Reference interpreter]
    Interpreter --> Runtime[Lua runtime model]
    Runtime --> Heap[Logical heap + GC]

    IR --> JIT[CoreCLR Tier 1 / Tier 2 JIT]
```

The compiler is byte-oriented at its boundaries, publishes immutable syntax, annotation,
semantic, type, and control-flow models, and lowers runtime behavior to a backend-neutral IR.
Static-analysis metadata remains erased from that IR. The runtime deliberately models Lua
objects, ownership, stacks, continuations, and logical GC independently of CLR object lifetime.
This keeps interpreter behavior explicit and gives future backends a common semantic contract.

## Repository layout

```text
Lunil/
├── src/
│   ├── Lunil.Core/              # source text, diagnostics, Lua numerics
│   ├── Lunil.Syntax/            # lexer, tokens, parser, immutable syntax
│   ├── Lunil.EmmyLua/           # LuaLS and legacy EmmyLua annotation front end
│   ├── Lunil.Analysis/          # semantic types, CFGs, constraints and flow analysis
│   ├── Lunil.Workspace/         # module graph, resolvers and incremental analysis cache
│   ├── Lunil.Semantics/         # binding and canonical lowering
│   ├── Lunil.Compiler/          # public bounded compilation pipeline and results
│   ├── Lunil.IR/                # canonical IR and Lua 5.4 binary chunks
│   ├── Lunil.Runtime/           # values, tables, GC, interpreter, coroutines
│   ├── Lunil.Hosting/           # reusable host, capability profiles and execution boundary
│   ├── Lunil.CodeGen.Cil/       # typed CIL plans and tiered CoreCLR JIT
│   ├── Lunil.Cli/               # packaged run/check/build/dump command-line product
│   └── Lunil.StandardLibrary/   # standard-library registration and modules
├── tests/                       # unit, differential, fuzz and GC-stress tests
├── benchmarks/                  # runtime benchmark harness
├── docs/                        # architecture, ABI, branching and versioning
├── scripts/                     # release bundle and NuGet packaging scripts
├── changelogs/                  # version-specific release notes
└── .github/workflows/           # CI and tag-driven release automation
```

## Compatibility and platforms

- **Language target:** Lua 5.4.8.
- **Runtime target:** .NET 10.
- **CI hosts:** Windows, Ubuntu Linux, and macOS.
- **Release RIDs:** `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`,
  and `osx-arm64`.
- **Binary chunks:** Lua 5.4 format with explicit target description and strict bounds;
  incompatible numeric representations are rejected instead of truncated.

See [compiler design](docs/compiler-design.md) for the compatibility contract and
known non-goals of the current milestone.

## Packages and releases

Lunil follows [SemVer 2.0](https://semver.org/). `X.Y.Z` and the prerelease suffix
describe different things:

| Version change | Use it for |
| --- | --- |
| `X` major | A new stable compatibility generation after `1.0.0`; increment when stable consumers must make intentional breaking migrations |
| `Y` minor | The next pre-1.0 development milestone, or a backward-compatible feature release after `1.0.0` |
| `Z` patch | Backward-compatible fixes and refinements to an already released stable `X.Y.0` line |
| `alpha.N` | Active feature/API development; incomplete behavior and breaking changes are still expected |
| `beta.N` | Feature and public-API scope frozen; compatibility, diagnostics, documentation, and performance hardening only |
| `rc.N` | A stable-release candidate; only release-blocking fixes may enter |
| no suffix | Stable release, promoted only from an accepted RC |

The completed stable `0.8.0` promotion sequence was:

```text
0.8.0-alpha.N -> 0.8.0-beta.N -> 0.8.0-rc.N -> 0.8.0
```

The current source version is **`0.9.0-alpha.1`**. Stable `0.8.0`, its tag, and `api/0.8.0`
remain immutable; backward-compatible fixes use `0.8.1`. The `0.9` public API and architecture
scope is open during Alpha and begins with shared Tier 2/Loop OSR numeric specialization.

An immutable `v<SemVer>` tag triggers validation, six RID bundles, symbol-enabled
NuGet packages, GitHub Packages publication, and a GitHub Release. Versions with a
suffix are automatically marked as prereleases. See the
[version policy](docs/versioning.md) for compatibility and promotion rules.

## Documentation

| Document | Description |
| --- | --- |
| [Compiler design](docs/compiler-design.md) | Architecture, compatibility contract, IR and backend design |
| [0.7.0 roadmap](docs/roadmap-0.7.0.md) | Compiler/hosting foundation, annotations, analysis, workspace, CLI and promotion gates |
| [`0.8.0` migration](docs/migration-0.8.0.md) | Lua AOT and `Lunil.Build` removal, runtime/chunk replacement, stable diagnostics, and NativeAOT distinction |
| [Versioned API/package compatibility](docs/api-compatibility.md) | Frozen `0.7` and `0.8` declarations, NuGet assets, validation policy, and update commands |
| [CLI reference](docs/cli.md) | Commands, configuration precedence, profiles, diagnostics, artifacts, and exit codes |
| [Execution backend ABI](docs/adr/0001-execution-backend-abi-v1.md) | Frozen scheduler, PC, budget, safe-point and code-generation contract |
| [Loop OSR productionization](docs/adr/0006-loop-osr-performance-productionization.md) | Exact-numeric OSR code shape, eligibility, guards, fallback, and performance gates |
| [Loop OSR rollout evidence closure](docs/adr/0008-loop-osr-qualified-preparation-and-evidence.md) | Qualified lazy emitter preparation and balanced high-sample rollout evidence |
| [Loop OSR automatic default rollout](docs/adr/0009-loop-osr-auto-default-rollout.md) | Six-RID authorization, release default, and explicit opt-out contract |
| [Historical persisted CIL AOT ADR](docs/adr/0010-persisted-cil-aot-performance-productionization.md) | Superseded design record retained for migration history |
| [Historical persisted-profile ADR](docs/adr/0015-persisted-profile-guided-numeric-regions.md) | Superseded design record retained for migration history |
| [Backend performance baseline](docs/backend-performance-baseline.md) | Interpreter/JIT baseline and benchmark procedure; removed AOT results are historical |
| [Cross-runtime performance](docs/cross-runtime-performance.md) | Native Lua baseline, LuaJIT/MoonSharp/Lunil matrix, reproducible toolchain, and six-RID reports |
| [.NET NativeAOT compatibility](docs/nativeaot-build-integration.md) | Trimming, publish modes, interpreter fallback, and six-RID verification |
| [Runtime continuation ABI](docs/runtime-continuation-abi.md) | Frozen continuation and yield boundary from `0.3.0` |
| [PUC prototype import](docs/puc-prototype-import.md) | PUC Lua prototype-to-canonical-IR conversion |
| [Versioning](docs/versioning.md) | SemVer, alpha/beta/RC promotion and release procedure |
| [Branch management](docs/branching.md) | Protected `main`, branch names and merge policy |
| [Changelogs](changelogs/) | Version-specific release notes |

## Contributing

Issues and focused pull requests are welcome. Create work on a `feature/*`, `fix/*`,
or `docs/*` branch, add tests and changelog entries where applicable, and run the full
build, test, and formatting commands before submitting. `main` is protected and uses
squash merges after all required checks pass.

Read [branch management](docs/branching.md) before contributing.

## Security

Please do not open a public issue for a suspected vulnerability. Use
[GitHub private vulnerability reporting](https://github.com/dlqw/Lunil/security/advisories/new)
with a minimal reproduction, affected version, and impact assessment.

## License

Lunil is licensed under the [MIT License](LICENSE).

Lua is a trademark of Lua.org, PUC-Rio. Lunil is an independent implementation and is
not affiliated with or endorsed by Lua.org or PUC-Rio.
