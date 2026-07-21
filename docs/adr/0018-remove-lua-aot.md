# ADR 0018: Remove Lua persisted/static AOT

- Date: 2026-07-17
- Supersedes: [ADR 0010](0010-persisted-cil-aot-performance-productionization.md),
  [ADR 0015](0015-persisted-profile-guided-numeric-regions.md)
- Related: [ADR 0013](0013-64-bit-instruction-accounting.md)

## Context

Lua persisted/static AOT expanded the public API, artifact and Portable PDB formats, metadata/PE
emitter, collectible loader, executor, disk cache, MSBuild package, CLI targets, benchmarks, scripts,
and six-RID release gates. It did not remove Lua scheduler/runtime semantics, and its measured
product value did not justify maintaining every shared code-generation change across an additional
persist/load path.

Lunil `0.8` is still Alpha, so the public surface has not frozen. Keeping a compatibility shell would
continue the same maintenance burden and could silently misrepresent AOT success. Lua AOT is also
distinct from .NET NativeAOT: the latter publishes the managed host ahead of time and must continue
to run the compiler, workspace, runtime, CLI, and interpreter without dynamic code.

## Decision

Starting in `0.8.0-alpha.12`:

1. Delete persisted/static Lua AOT compiler, artifact, manifest, Portable PDB metadata, metadata/PE
   emitter, loader, executor, static registry, disk cache, `Lunil.Build`, tests, benchmark engines,
   evidence schema fields, scripts, package assets, and release gates.
2. Retain shared JIT planning/profile identity code under names matching its actual responsibility.
   Tier 1, Tier 2, and Loop OSR continue to use Reflection.Emit only when supported.
3. Legacy CLI `--target aot`, JSON `buildTarget: "aot"`, and
   `LUNIL_BUILD_TARGET=aot` fail closed with `LUNIL0006`, `removed-feature`, exit code `2`.
   Help and current configuration documentation advertise only `chunk`.
4. Remove the public AOT/cache/build API and package from the reviewed `api/0.8.0` baseline. This is
   a breaking change relative to stable `0.7.0` and is not backported to `0.7.x` patches.
5. Preserve .NET NativeAOT/trimming compatibility. When dynamic code is unavailable, Auto/PreferJit
   deterministically execute through the reference interpreter without JIT profile collection.
6. Complete #53 in the remaining generated backends: consumed instruction counts, locals, arithmetic,
   frameless merges, deopt and exit factories use `long`/`Int64` end to end.

## Consequences

- The active product has interpreter, Tier 1, Tier 2, and Loop OSR execution paths, but no portable
  generated CIL artifact or static Lua registry.
- Consumers migrate to runtime source/chunk compilation plus interpreter/JIT execution, or distribute
  portable PUC Lua chunks. There is no silent AOT-to-JIT/chunk fallback.
- Six-RID reporting becomes smaller and must reject any reintroduced AOT row or artifact gate.
- NativeAOT tests now validate source compilation, workspace analysis, interpreter execution, and
  capability fallback rather than a generated static registry.
- Historical changelogs, performance evidence, ADR 0010, and ADR 0015 remain available and are marked
  superseded; they do not describe current product capability.

## Rejected alternatives

- **Keep deprecated AOT APIs backed by the interpreter:** rejected because it reports a false backend
  identity and preserves public maintenance cost.
- **Keep artifact/cache code without a CLI/package:** rejected because unused format compatibility and
  security validation remain expensive.
- **Remove .NET NativeAOT support together with Lua AOT:** rejected because they solve different
  problems; deterministic interpreter fallback remains a supported deployment mode.
- **Replace runtime JIT/removed AOT with a source generator:** rejected because build-time generation
  does not know runtime module/profile shape and would recreate the removed product under another name.
