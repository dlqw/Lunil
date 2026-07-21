# ADR 0022: Unified numeric specialization pass and retained compiler seams

- Date: 2026-07-18

## Context

Stable `0.8.0` exposed interpreter, Tier 1, Tier 2, and an independently configurable Loop OSR
mode. Internally, Tier 2 and Loop OSR already shared natural-loop analysis, exact reaching-definition
type proofs, budget planning, numeric IL generation, deoptimization PCs, and safepoint rules.
Loop OSR nevertheless retained `ReflectionEmitLuaLoopOsrCompiler`, an 857-line older emitter used
only when the shared `LuaNumericRegionPlanner` rejected a loop. That fallback had a wider,
runtime-guarded eligibility model and therefore created a second numeric code-generation contract.

The proposed source-generator work in #56/#57 also assumed build time could enumerate runtime
module/profile shapes. It cannot: Tier 2 regions, call targets, table shapes, reload generations, and
OSR entry state are runtime-owned. The remaining deterministic well-known-call resolver is warmed
once before timed compilation, while opcode and standard-library switches are direct, debuggable
C# with ordering and semantic context that differs per consumer.

Finally, the internal compiler interfaces in #59 are not unused abstractions. The JIT lifecycle
tests inject cancellation, blocking, failure, oversized code, guard failure, and invalidation
compilers through these seams. Their calls occur during cold tier-up, not per instruction.

## Decision

1. The product architecture is Interpreter → Tier 1 → Tier 2. Loop OSR remains a configuration,
   telemetry, and benchmark isolation surface, but its compiled entry is the backedge entry mode of
   the same numeric specialization pass used by Tier 2 function-entry regions.
2. Delete `ReflectionEmitLuaLoopOsrCompiler`. Both entry modes use `LuaNumericRegionAnalyzer`,
   `LuaNumericRegionPlanner`, and `ReflectionEmitLuaNumericRegionCompiler`. If the shared proof is
   unavailable, Loop OSR uses the canonical managed program; it does not invoke a legacy emitter.
3. Preserve exact budget accounting, canonical-PC deoptimization, debug/GC/invalidation guards,
   managed fallback, weak ownership, and the independent `EnableLoopOsr` benchmark switch.
4. Retain `ILuaDynamicCodeCapabilities`, `ILuaTier1Compiler`, `ILuaTier2Compiler`, and
   `ILuaLoopOsrCompiler` as internal test/lifecycle seams. Removing them for cold-path vtable savings
   is rejected unless equivalent deterministic fault injection exists and measured compile latency
   improves.
5. Do not introduce a source-generator project in this milestone. Runtime JIT/AOT replacement is
   rejected, opcode dispatch generation is rejected unless it emits a single direct switch without
   an extra call, and registration/counter/well-known-call generation requires a separate measured
   prototype that improves build/startup or eliminates a demonstrated drift failure. Lua AOT must
   not be recreated under a generator name.

## Verification and acceptance

- The CodeGen suite must cover shared numeric regions for both function-entry Tier 2 and Loop OSR,
  exact guard failure restart, managed fallback, cancellation, invalidation, and code-size limits.
- The former managed-fallback test now requires a single managed compilation when runtime exact-type
  qualification is intentionally disabled; it must record zero specialized guard failures and zero
  invalidations rather than relying on the removed emitter.
- Release builds, public/package baselines, .NET NativeAOT/trimming, backend evidence, and six-RID
  cross-runtime gates remain mandatory. Compile p95, generated code bytes, allocations, and steady
  state must not regress merely to reduce source lines.

## Consequences

There is one numeric proof/emission contract and one place to fix accounting, safepoint, or guard
bugs. Loops not proven by the shared pass can execute managed Loop OSR or remain in Tier 1; they no
longer receive speculative code from a second emitter. Internal compiler seams and direct switches
remain because they buy testability and debuggability with no demonstrated hot-path cost. Future
generator work must arrive as a bounded, measured proposal rather than a new backend architecture.
