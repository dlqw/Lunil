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

### Later Alpha: type and flow analysis

- `any`, `unknown`, `never`, literal, union, intersection, class, alias, enum,
  structural table, array, map, function, overload, generic, vararg, tuple and
  type-pack models;
- constraint generation, CFG construction, nil/type/assert/discriminant
  narrowing, definite assignment, unreachable-code analysis, and return-pack
  inference;
- deterministic budgets and widening for recursive types, module cycles, and
  deep generic instantiation.

### Workspace and incremental module analysis

- stable source and module identities;
- module resolution and a dependency graph with cyclic fixed points;
- conservative treatment of dynamic `require`;
- content-addressed analysis results and minimal invalidation;
- cancellation, bounded parallel scheduling, and deterministic result merging.

### CLI and build integration

- `lunil run`, `lunil check`, `lunil build`, and `lunil dump`;
- consistent exit codes and text/machine-readable diagnostics;
- stdin, response-file, configuration, sandbox, deterministic, and build-time AOT
  integration.

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
