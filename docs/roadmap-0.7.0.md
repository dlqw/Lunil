# Lunil 0.7.0 roadmap

`0.6.0-alpha.14` is the final preview on the `0.6.0` development line. Lunil will
not publish a suffix-free `0.6.0`; the next compatibility milestone is
`0.7.0-alpha.1`. Published `0.6.0-alpha.14` tags and artifacts remain immutable.

The `0.7.0` milestone turns the existing Lua execution engine and qualified
JIT/AOT backends into a complete compiler, analysis, and hosting product. It does
not treat another backend optimization as a substitute for the missing public
compiler surface.

## Alpha 1: compiler and hosting product foundation

`0.7.0-alpha.1` must provide an end-to-end public path rather than empty project
scaffolding:

- `Lunil.Compiler` owns source input, bounded lexer/parser/binder configuration,
  canonical lowering, independent IR verification, stable phase attribution for
  diagnostics, cancellation boundaries, and immutable compilation results.
- `Lunil.Hosting` composes the compiler, runtime state, standard library, and
  executor. It exposes explicit trusted, restricted, and deterministic capability
  profiles plus state, heap, stack, call-depth, and instruction budgets.
- source names flow into canonical debug metadata so tracebacks and later PDB
  generation do not depend on an unrelated host path.
- tests cover successful compilation/execution, malformed input, configured
  budgets, source identity, restricted capabilities, deterministic time, and
  reusable host state.

Alpha 1 does not freeze these new APIs. It establishes the boundary that later
annotation, workspace, CLI, and compatibility work will refine during alpha.

## Later alpha milestones

### Alpha 2: annotation front end

`0.7.0-alpha.2` delivers:

- a shared bounded annotation lexer and public type AST;
- LuaLS as the default dialect;
- separately dispatched legacy EmmyLua input compatibility;
- unknown-tag preservation, configurable diagnostics, suppression, and deterministic bounded
  random-byte parser fuzzing;
- annotation results integrated into `LuaCompiler` while remaining erased from runtime IR.

Annotation syntax remains an Alpha API. Semantic type/flow interpretation follows in later
milestones and must not treat parsed annotations as unchecked runtime truth.

### Alpha 3: type and flow analysis

`0.7.0-alpha.3` delivers:

- the public `Lunil.Analysis` package with immutable `any`, `unknown`, `never`, literal,
  union, intersection, class, alias, enum, structural table, array, map, function,
  overload, callable, generic, vararg, tuple, and type-pack models;
- annotation declaration resolution plus structural assignability, generic substitution,
  operator/callable lookup, expression/symbol inference, call/assignment/return constraints,
  and stable `LUA6xxx` diagnostics;
- per-function CFGs, nil/type/assert/discriminant and short-circuit narrowing, definite
  assignment, unreachable-code analysis, return-pack inference, and generic-for inference;
- cancellation, source/global diagnostic suppression, deterministic type/constraint/CFG/
  iteration/generic budgets, recursive-type and loop widening, and malformed annotation-byte
  fuzz coverage;
- immutable analysis results integrated into `LuaCompiler` before lowering while remaining
  erased from canonical IR and runtime behavior.

Module-cycle fixed points and content-addressed invalidation remain workspace responsibilities
for the next Alpha rather than hidden global state inside the single-document analyzer.

### Alpha 4: workspace and incremental module analysis

`0.7.0-alpha.4` delivers:

- the public `Lunil.Workspace` package with stable source/module identities, immutable snapshots,
  injectable in-memory and root-confined file resolvers, and deterministic graph results;
- direct-global static `require` extraction with shadowing awareness, unresolved-module diagnostics,
  and a conservative `any` boundary plus explicit graph markers for dynamic require names;
- dependency-aware module export types, deterministic strongly connected components, bounded cyclic
  fixed points, and stable widening when a cycle exceeds its iteration budget;
- reusable content-addressed discovery/analysis caches whose keys include source content and direct
  dependency export hashes, so implementation-only leaf changes do not invalidate dependents;
- global module/source/dependency/diagnostic/cache/fixed-point budgets, cancellation, globally bounded
  parallel compilation, deterministic result merging, cache metrics, and minimal invalidation evidence;
- `LuaHost` workspace access, `Lunil.Build` multi-source workspace preflight, and NativeAOT/trimming
  coverage while leaving runtime package loaders and canonical IR unchanged.

### Alpha 5: CLI and command-line build integration

`0.7.0-alpha.5` delivers:

- the packaged `Lunil.Cli` .NET tool and RID apphost with `lunil run`, `lunil check`,
  `lunil build`, and `lunil dump`;
- stable exit codes, stdout/stderr separation, text and `lunil.diagnostics.v1` JSON diagnostics,
  warnings-as-errors, stdin source, script varargs/`arg`, cancellation, and command-specific option
  validation;
- bounded nested UTF-8 response files, `lunil.json`, `LUNIL_*` environment variables, and the
  precedence defaults < config < environment < CLI;
- trusted, root-confined read-only sandbox, and deterministic capability profiles with explicit
  input, instruction, stack, call-depth, and heap budgets;
- workspace-aware source preflight and module resolution, canonical PUC Lua 5.4 chunk emission,
  persisted CIL AOT assembly/PDB/canonical payload/manifest emission, and source/annotation/
  analysis/IR/chunk dumps;
- tool-package installation smoke tests, RID bundle apphosts, and NativeAOT, trimmed single-file,
  and ReadyToRun CLI publication gates.

Alpha 5 completes the planned product feature surface. Complete applicable Lua 5.4.8 conformance,
API/package baselines, six-RID differential/stress evidence, and scope-freeze review remain required
before `0.7.0-beta.1`.

## Beta entry gate

`0.7.0-beta.1` is allowed only when:

1. the complete `0.7.0` feature and public-package scope is frozen;
2. no promised annotation, type, flow, workspace, hosting, or CLI subsystem is a
   placeholder or known feature hole;
3. the complete applicable Lua 5.4.8 user-mode suite is integrated and every
   exclusion is classified as a documented platform difference or explicit
   non-goal;
4. public API baselines and package compatibility validation are enforced in CI;
5. all six release RIDs pass compiler, runtime, backend differential, NativeAOT,
   package, and bundle gates.

Beta accepts compatibility, diagnostics, documentation, reliability, and
performance fixes. New planned features move to the next numeric milestone.

## Beta 1 freeze status

`0.7.0-beta.1` satisfies the Beta entry gate:

- the Alpha 1-5 compiler, annotation, analysis, workspace, hosting, build, and CLI scope is complete
  and Alpha 6 passes the unmodified official Lua 5.4.8 user-mode suite through `final OK !!!`;
- all six release RIDs enforce the same PUC-Lua observable goldens, six backend catalog,
  deterministic fuzz seeds, GC/coroutine soak contract, NativeAOT and publish-mode gates;
- all 14 shipped assemblies have exact generated public API baselines and all 14 NuGet/symbol
  packages have version-independent metadata, dependency, and asset baselines;
- the SDK strict package validator, an all-library local consumer, the installed CLI tool smoke, and
  both API/package baseline checks run in CI and the tag-triggered release workflow.

The feature and API scope is therefore frozen. Beta hardening confirmed clean API/package
compatibility, clean-checkout release evidence, and no open release blocker.

## RC 1 candidate status

`0.7.0-rc.1` satisfies the RC entry gate without changing product code, any of the 14 public API
baselines, or the frozen 14-package scope:

- the accepted Beta commit passed main-branch six-RID CI and the tag-triggered release workflow;
- the release workflow reproduced all six RID bundles, all 14 NuGet and symbol packages, clean
  local consumers, installed-tool smoke, and compatibility checks;
- official conformance, six-backend differential, deterministic fuzz, GC/coroutine soak, and
  multi-publish evidence remains unchanged and accepted;
- no release blocker is open.

RC accepted only release blockers. None was found.

## Stable release status

Stable `0.7.0` satisfies the final gate by removing the suffix from the accepted
`0.7.0-rc.1` candidate without changing product code, any of the 14 public API baselines, or the
frozen 14-package scope:

- both RC branch/PR CI runs and the RC main-branch six-RID CI passed;
- the immutable RC tag reproduced six RID bundles, 14 NuGet packages, 14 symbol packages,
  compatibility checks, clean local consumers, and publication in the release workflow;
- no release blocker was found after the candidate release;
- the same version-derived release gates are retained for stable packaging and publication.

The `0.7.0` milestone is complete. Backward-compatible fixes use `0.7.1`; new feature/API work
belongs to `0.8.0-alpha.1`.

## RC and stable gate

RC begins after all applicable conformance tests pass, no release-blocking defect
is open, API compatibility from beta is clean, fuzz/stress evidence is accepted,
and the release pipeline passes from a clean checkout. RC accepts only release
blockers. Stable `0.7.0` is produced from an accepted RC without product-code
changes.

## Explicit non-goals

The following remain outside `0.7.0` unless a later approved ADR changes scope:

- the Lua C API ABI and native Lua C modules;
- portable caching of CoreCLR-generated native machine code;
- dynamic CIL generation or loading inside a NativeAOT process;
- treating LuaLS/EmmyLua annotations as unchecked runtime truth;
- making the experimental managed semantic JIT fallback a release default without
  independent semantic and six-RID performance qualification;
- declaring the long-term `1.0.0` compatibility generation.
