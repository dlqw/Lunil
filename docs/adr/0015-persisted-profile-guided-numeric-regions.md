# ADR 0015: Persist exact-module profile-guided numeric regions

- Status: Superseded by ADR 0018 in `0.8.0-alpha.12`
- Date: 2026-07-17
- Target: Lua 5.4.8, .NET 10
- Depends on: [ADR 0010](0010-persisted-cil-aot-performance-productionization.md),
  [ADR 0011](0011-linear-numeric-regions.md)

## Context

Persisted CIL previously serialized only canonical method shards. It removed interpreter dispatch
overhead, but numeric loops still loaded and stored tagged `LuaValue` registers on every operation.
Tier 2 and Loop OSR already had a verified linear numeric-region planner and a Reflection.Emit
backend with unboxed `long`, `double`, and `bool` locals, exact instruction accounting, guarded
deoptimization, and bounded safepoints. Reimplementing a reduced persisted optimizer would create a
second semantic pipeline and eventually diverge on Lua overflow, mixed integer/float comparisons,
negative steps, budget PCs, frame-top updates, or GC polling.

Profiles are executable assumptions, not advisory labels. Reusing one after the canonical module
changes, silently accepting incomplete function coverage, or omitting the profile from artifact
identity could load specialized code under stale assumptions even if the ordinary module checksum
remained valid.

## Decision

`LuaAotCompilationOptions.Profile` accepts one `LuaJitModuleProfile` whose module content ID must
exactly match the canonical input. Compilation validates and deterministically serializes the full
function profile through `LuaJitProfileCodec`; duplicate, missing, malformed, or incompatible
function/site data fails with `AOT1006`, while a different module identity fails with `AOT1005`.

The SHA-256 profile fingerprint, a profile-guided flag, and profile-policy version participate in
the options fingerprint, generated assembly name, manifest, and loader compatibility checks.
Persisted artifact schema and codegen versions advance to v2, and Runtime ABI v4 publishes only the
small cross-assembly operations required by generated numeric code. A profile change therefore
produces a different content-addressed artifact even when source, canonical IR, and ordinary AOT
limits are unchanged.

The existing `ProfileGuidedLuaTier2Compiler` numeric-region planner is the sole planner for dynamic
and persisted specialization. `ReflectionEmitLuaNumericRegionCompiler` emits one backend-neutral
numeric IL instruction stream. One sink maps it to `ILGenerator`; the other maps the same labels,
typed locals, operands, calls, branches, and switch tables to `System.Reflection.Metadata`
`InstructionEncoder` and deterministic PE metadata. Persisted methods retain the same entry guards,
unboxed locals, dirty-register materialization, Lua arithmetic helpers, exact budget slow tail,
backedge safepoints, debug/GC polls, and canonical-PC deoptimization exits as dynamic Tier 2.

Each manifest function records its numeric method name, exact sorted PC set, header and backedge,
unboxed-local count, direct-numeric instruction count, and safepoint count. Canonical shard coverage
remains complete, but shards are split at transitions into and out of numeric PC sets. This forces
the shared dispatcher to regain control at a region boundary, select the numeric method first, and
resume the next canonical shard after a successful region exit. A guard failure returns the exact
PC to the scheduler; the interpreter executes canonical fallback rather than entering an alternate
unchecked persisted path.

The loader rejects stale policy versions, malformed hashes, inconsistent profile flags, invalid or
overlapping PC sets, nonpositive structural metrics, duplicate method names, and any options or
assembly identity that does not recompute from the embedded manifest. Portable PDB method rows are
kept aligned for both canonical and numeric methods.

## Verification

Focused coverage requires:

- deterministic profile and artifact identity, exact-module rejection, malformed profile rejection,
  and corrupted policy/region manifest rejection;
- real PE load and execution of persisted integer, float, boolean/control, overflow, modulo,
  division, and negative-step regions;
- mixed-kind guard deoptimization with exact canonical results, debug-mode attribution, exact
  instruction-budget failure, Portable PDB validation, and zero unexpected deoptimization on
  matching profiles; and
- backend evidence rows named `persisted_aot_pgo` that report artifact size and structural numeric
  metrics beside the unprofiled persisted, Tier 1, Tier 2, Loop OSR, and interpreter rows. Every
  measured result is compared with the reference interpreter before it contributes timing data.

For `arithmetic`, `control_flow`, `fib_iter`, and `mandelbrot`, each RID requires persisted numeric
regions, unboxed locals, direct numeric instructions, and safepoints; zero interpreter fallback and
zero unexpected deoptimization; and an artifact smaller than 64 KiB. Balanced paired samples gate
the median PGO-AOT/Tier-2 slowdown at no more than `1.50x` and the bootstrap CI95 upper bound at no
more than `1.75x`. The six-RID aggregate publishes the worst paired ratio and fails qualification
unless every RID satisfies the same thresholds.

The release gate remains the repository's formatting/package job plus all six supported RID jobs
and their aggregate conformance and backend-evidence decisions.

## Consequences

- Persisted CoreCLR execution can expose the same direct numeric CIL shape as dynamic Tier 2
  without maintaining a second optimizer.
- Artifact size and load/bind work increase for profiled numeric workloads because canonical
  fallback shards remain present by design.
- Profiles are intentionally not remapped across source edits or function-layout changes. Future
  profile migration requires a separately versioned policy rather than weakening exact identity.
- Hosts without a profile retain deterministic canonical persisted CIL behavior. Hosts without
  dynamic assembly loading still use the existing validation/static-AOT paths; this decision does
  not add runtime Reflection.Emit or dynamic loading to NativeAOT.
