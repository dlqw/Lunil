# ADR 0006: Productionize exact-numeric Loop OSR without changing the default

- Status: Accepted
- Date: 2026-07-13
- Target: Lua 5.4.8, .NET 10
- Depends on: [ADR 0002](0002-execution-backend-abi-v2.md),
  [ADR 0004](0004-tier2-exact-numeric-productionization.md),
  [ADR 0005](0005-tier2-auto-default-rollout.md)

## Context

The first Loop OSR prototype proved the scheduler and canonical-PC transition, but executed a managed
canonical loop program. Its compact local result was useful for semantics work, not a production
code-shape or rollout decision: allocation increased, negative workloads had no paired disabled
baseline, compilation phases were not attributed, and no rule prevented a managed loop from being
installed when a host enabled OSR.

Tier 1 and exact-numeric Tier 2 now have deterministic eligibility, real generated CIL, owner-safe
planning caches, bounded code storage, NativeAOT fallback, and repeated multi-process evidence.
Loop OSR needs the same production contract before further rollout work can be discussed. It must
enter only after a completed backedge, preserve the Runtime ABI v2 scheduler boundaries, reject
managed semantic loops by default, and add negligible steady-state cost to rejected functions.

## Decision

`LuaJitExecutorOptions.EnableLoopOsr` remains `false` by default. When a CoreCLR host explicitly
enables it, Lunil analyzes verified natural loops whose header dominates the backedge source and
whose target is a canonical basic-block leader. The transition request is recorded only after the
backedge instruction has completed and is consumed after the frame commits the header PC.

Each loop receives a deterministic `LuaJitLoopOsrEligibility` decision. Automatic compilation is
accepted only when the whole loop can produce `GuardedExactNumericCil`, contains at least one
numeric specialization, and uses only:

- non-string constants, nil, moves, and frame-top updates;
- conditional and unconditional canonical control flow;
- guarded close operations;
- numeric-for prepare/loop operations; and
- exact integer, float, or mixed-numeric unary, binary, comparison, and bitwise operations.

Table access, upvalues, closure creation, varargs, calls/tail calls, to-be-closed marking, and any
unsupported opcode are rejected before queue admission. `EnableLoopOsrManagedFallback` is added
with a default of `false`; setting it to `true` explicitly restores the experimental
`ManagedCanonicalProgram` path. A successful compiler result is checked again during installation,
so a custom compiler cannot publish an unexpected managed code kind through the guarded default.

The generated `DynamicMethod` uses the liveness-derived canonical register map and keeps frame top,
open-upvalue state, and to-be-closed state materialized. Entry and every loop header validate the
debug/hook epoch, instruction budget, GC/finalizer poll, close/unwind state, canonical PC, and
required value tags. All exits report the exact materialized PC and consumed instruction count.
Guard failure, budget exhaustion, runtime polling, and leaving the natural loop return to the shared
scheduler without replaying completed effects. Registry invalidation does not actively interrupt a
delegate already acquired by a caller; that invocation may finish, while later dispatch cannot
re-enter the invalidated method.

Loop OSR reuses the common bounded compilation queue, concurrent request deduplication,
owner-scoped register-liveness cache, code-byte LRU, cancellation, disposal, module invalidation,
and late-result installation checks. Repeated specialized guard failures stop further observation;
they may widen to the managed program only when the explicit managed-fallback option is enabled.

`LuaJitLoopOsrCodeKind`, `LuaJitLoopOsrEligibilityReason`, `JIT3101`-`JIT3104`, accepted/rejected
events, independent eligibility statistics, `GetLoopOsrEligibility`,
`EvaluateLoopOsrEligibility`, and `LuaJitLoopOsrCompilationMetrics` expose the decision and its
verification, analysis, liveness, planning, emission, delegate, allocation, guard, and code-shape
costs.

Eligibility is initialized once at the first normal function entry. Functions with no natural
loop, or with only rejected loops, enter a permanent fast-rejection state. Their later instructions
and backedges avoid analyzer calls, loop dictionary lookups, and locks. This is required by the
paired negative-workload gate rather than treated as an optional micro-optimization.

OSR is effective only when both dynamic-code capability flags are true. Dynamic-code-unavailable
runtimes, including NativeAOT, do not prewarm, analyze, queue, or compile Loop OSR and retain exact
interpreter or build-time AOT behavior.

## Evidence

The backend runner adds a separately configured `loop_osr_off` row and compares it with the OSR row
inside each independent process. The arithmetic qualification requires a paired OSR-on/off median
of at least 2x, bootstrap median 95% lower bound of at least 1.5x, zero allocation slope,
compilation p95 below 10 ms, `GuardedExactNumericCil`, at least one specialization, no managed
arithmetic installation, an accepted eligibility decision, 100% liveness-cache hits, and
compilation-allocation p95 below 65,536 bytes. Lua calls, table access, metamethods, and
coroutine/error/hook workloads must each retain an on/off median of at least 0.90 and install no
managed OSR method.

The final five-process win-x64 Release record used nine cold samples per process and
`iterations=1,000,000`. It reported:

- 8.516x arithmetic median speedup over the interpreter, with bootstrap 95% interval
  `[6.012x, 9.406x]`;
- 128.737x paired speedup over the disabled OSR configuration, with bootstrap 95% interval
  `[93.992x, 142.973x]`;
- zero allocation slope, 3.080 ms compilation p95, 100% liveness-cache hit rate, and 44,592 bytes
  compilation-allocation p95;
- `GuardedExactNumericCil`, five specialized instructions, thirteen guards, one accepted
  eligibility decision, and zero managed arithmetic installations; and
- paired negative-workload medians of 0.935x, 1.003x, 0.992x, and 0.980x for Lua calls, table
  access, metamethods, and coroutine/error/hooks.

The six-RID aggregator now writes `loop-osr-six-rid-decision.json` alongside the Tier 1 and Tier 2
decisions and verifies code kind, liveness, allocation, managed-installation, negative-workload,
and qualification fields. Synthetic win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, and
osx-arm64 inputs exercise the complete aggregation path; release automation records the per-RID
measurements without enforcing shared-runner wall-clock timing.

Release build, the full solution test suite, focused Loop OSR tests, and backend differential tests
cover exact numeric entry, managed-boundary rejection, explicit managed fallback, guard widening,
unexpected compiler code kind, diagnostics, events, statistics, metrics, concurrency,
invalidation, and dynamic-code-unavailable fallback.

## Consequences

- Hosts may opt into a real exact-numeric OSR method rather than the managed prototype.
- Enabling OSR no longer means accepting table/call/closure managed loops; that behavior requires a
  second explicit switch.
- Rejected or loop-free functions pay a one-time analysis cost and no repeated loop-classification
  cost.
- Generated OSR code remains process-local and is never added to the persistent backend cache.
- NativeAOT behavior and the build-time AOT contract are unchanged.
- Changing `EnableLoopOsr` to a release default requires a separate rollout ADR after real six-RID
  evidence and broader workload review; this ADR deliberately does not make that default change.
