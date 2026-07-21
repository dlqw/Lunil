# ADR 0002: Execution backend Runtime ABI v2

- Date: 2026-07-12
- Extends: [ADR 0001](0001-execution-backend-abi-v1.md)

## Context

ADR 0001 froze the scheduler boundary, canonical-PC, instruction-accounting, safe-point,
hook/debug, and artifact-identity rules. The first Tier 1 implementation was semantically
correct, but common arithmetic loops repeatedly crossed the generic Runtime path and performed
public-boundary register validation for every access. New persisted code therefore needs Runtime
entry validation and guarded primitive helpers that do not exist in ABI v1.

Reusing version 1 would allow a new artifact to pass an old manifest check and then fail later
with a missing method or, worse, execute with different accounting assumptions. The ABI must be
advanced explicitly.

## Decision

`LuaCodegenAbiV2.RuntimeAbiVersion` is 2 and is the exact Runtime ABI accepted by current AOT
manifests, backend cache keys, JIT registry keys, and versioned profile payloads.

ABI v2 adds:

- compiled-frame entry validation for function identity, register count, stack window, dynamic
  backend mode, and debug/hook state;
- unchecked register read/write/clear and frame-top helpers, usable only after verified entry;
- side-effect-free guards plus shared Runtime execution helpers for primitive unary, arithmetic,
  bitwise, and comparison operations;
- numeric-for prepare/loop helpers that update the canonical frame PC using the shared Runtime
  numeric semantics;
- a guarded `Close` fast path that skips the boundary only when no to-be-closed slot is in range.

The v1 facade remains in the Runtime assembly for source/binary compatibility of infrastructure
callers and historical tests. Current persisted-artifact loading does not negotiate down to v1:
an artifact, cache descriptor, or profile that declares ABI 1 is incompatible and must be
recompiled or interpreted.

## Direct-segment contract

1. A compiled entry validates the frame once before using unchecked access helpers.
2. A primitive guard runs before instruction reservation, PC commit, or externally visible
   effects. Guard failure executes the canonical generic path for the same PC and charges exactly
   one canonical instruction.
3. Guard success commits the current PC, reserves one instruction, performs the shared primitive
   operation, and then branches within the verified method plan.
4. Numeric-for helpers may select a branch target, so generated code reloads the committed frame
   PC and dispatches to the corresponding canonical label.
5. `Close` is elided only after proving the relevant close range is empty. Otherwise the generic
   scheduler-owned close machinery runs.
6. When Tier 2 and Loop OSR are disabled, Tier 1 omits per-instruction observation calls. Enabling
   either feature selects a distinct observed method plan and cache entry.

ABI v2 does not change scheduler ownership, exact instruction charging, safe-point liveness,
error/yield/close behavior, or the tagged `LuaCompiledExit` model defined by ADR 0001.

## Compatibility matrix

| Producer data | Declared ABI | Current action |
|---|---:|---|
| persisted CIL manifest | 2 | validate checksums/identity, then load |
| persisted CIL manifest | 1 or unknown | reject before assembly load |
| backend cache descriptor | 2 | normal compatibility evaluation |
| backend cache descriptor | 1 or unknown | safe cache miss |
| JIT profile payload | 2 | validate module/opcode identities, then merge owner-free facts |
| JIT profile payload | 1 or unknown | return incompatible; do not merge |
| in-process plan cache | current module owner + ABI-2 codegen | reuse owner-scoped verified plan |

## Consequences

- Reflection.Emit and managed-PE emitters resolve the same ABI v2 call targets from one typed
  method plan.
- Plan caches are owner-scoped weak caches and cannot keep a canonical module alive.
- Runtime ABI initialization and a minimal Reflection.Emit preparation compile are charged to
  executor startup rather than the first compilation event.
- Any future helper or accounting change that is not valid for an ABI-2 artifact requires another
  explicit ABI advance and matching cache/profile/artifact invalidation.

## Validation

Required tests cover exact budget stops, primitive guard fallback, numeric-for edges, close
fallback, managed-PE/Reflection.Emit parity, old-ABI rejection, concurrent plan first use, and
module collection after cached planning.
