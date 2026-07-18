# Lunil 0.9.0 roadmap

Lunil `0.9.0` focuses on execution efficiency without weakening Lua 5.4 behavior, diagnostics,
resource accounting, debugging, or .NET deployment support. Stable `0.8.0` is the compatibility and
performance baseline.

The target backend architecture is:

```text
reference interpreter -> Tier 1 -> Tier 2
                                      ^
                                      +-- function entry or loop backedge entry
```

Loop OSR is not a separate roadmap milestone: it remains independently configurable and measurable,
while sharing Tier 2's numeric analysis, planning, emission, accounting, and deoptimization contract.
In other words, OSR describes a loop-entry transfer into compiled code, not an execution tier beside
Tier 1 or Tier 2. The benchmark's separate OSR row exists only to compare loop-entry enabled and
disabled behavior.
Lua persisted/static AOT is not part of the product and will not be reintroduced.

## Stable baseline

The `0.8.0` six-RID cross-runtime baseline contains 48 measurements per engine:

| Metric | `0.8.0` baseline |
| --- | ---: |
| Auto JIT geomean vs native Lua | 0.680x |
| Auto JIT geomean vs MoonSharp | 9.974x |
| Lowest per-RID Auto geomean vs native Lua | 0.432x |
| Workload/RID MoonSharp stability gates | 96/96 passed |
| Tier 2 maximum compilation p95 | 3.672 ms |
| Tier 2 maximum compile allocation p95 | 317,776 B |
| Tier 2 maximum region allocation growth | 37,758 B/instruction |
| Loop OSR maximum compilation p95 | 5.217 ms |
| Loop OSR maximum preparation p95 | 1.150 ms |
| Loop OSR maximum compile allocation p95 | 259,232 B |

The cross-runtime release gate passed on every platform. The compile-allocation limits shown below
remain open engineering targets and become mandatory before Beta.

Full data and methodology are published in [Performance](performance.md).

## Release performance targets

The required targets are release gates. Stretch targets guide optimization but do not justify
benchmark-specific code or semantic shortcuts.

| Cross-runtime metric | Required for `0.9.0` | Stretch |
| --- | ---: | ---: |
| Auto JIT geomean vs native Lua | **at least 0.850x** | at least 1.000x |
| Auto JIT geomean vs MoonSharp | **at least 12.000x** | at least 15.000x |
| Lowest per-RID Auto geomean vs native Lua | **at least 0.600x** | at least 0.750x |
| Auto/Tier 2 MoonSharp workload gates | **all workloads on all RIDs** | same |
| Non-target workload regression vs `0.8.0` | **paired CI95 lower at least 0.95x** | at least 1.00x |

The required per-workload targets produce an aggregate target of approximately `0.850x` native Lua:

| Auto JIT workload | `0.8.0` | Required | Stretch |
| --- | ---: | ---: | ---: |
| Arithmetic | 1.110x | at least 1.100x | at least 1.150x |
| Iterative Fibonacci | 2.801x | at least 2.800x | at least 3.200x |
| Mandelbrot | 0.559x | **at least 0.750x** | at least 0.900x |
| Control flow | 2.070x | at least 2.000x | at least 2.200x |
| Function calls | 1.204x | **at least 1.500x** | at least 2.000x |
| Table access | 0.299x | **at least 0.450x** | at least 0.600x |
| Prime sieve | 0.059x | **at least 0.120x** | at least 0.200x |
| String build | 0.591x | **at least 0.750x** | at least 0.900x |

### Compilation, startup, memory, and code size

Every performance change must also satisfy these bounds on all release RIDs:

| Metric | `0.9.0` gate |
| --- | ---: |
| Tier 2 compilation p95 | at most 5.0 ms |
| Loop OSR compilation p95 | at most 7.5 ms |
| Loop OSR one-time preparation p95 | at most 2.0 ms |
| Tier 2 compile allocation p95 | at most 256 KiB |
| Loop OSR compile allocation p95 | at most 192 KiB |
| Numeric-region allocation growth | at most 32 KiB/direct instruction |
| Hot execution allocation slope | 0 B/iteration for qualified numeric loops |
| First-execution and rejected-workload startup | paired median at least 0.95x `0.8.0` |
| Generated code bytes for unchanged routes | at most 1.15x `0.8.0` |
| Execution allocation for unchanged workloads | at most 1.05x `0.8.0` |

## Delivery sequence

| Stage | Status | Outcome |
| --- | --- | --- |
| Alpha 1 | Implemented in current source | Shared Tier 2 and loop-entry specialization architecture |
| Alpha 2 | Implemented in current source | Compilation allocation, startup, and code-size closure |
| Alpha 3 | Implemented in current source | Table-heavy and mixed-loop throughput |
| Alpha 4 | Implemented in current source | Calls and floating-point regions |
| Alpha 5 | Planned | Strings and baseline execution |
| Beta | Planned | Feature freeze and complete performance qualification |
| RC / stable | Planned | Release-blocker-only validation and publication |

### Alpha 1 — shared specialization architecture

- Use one numeric analysis, planning, and emission pass for Tier 2 function entry and loop backedge
  entry.
- Remove the obsolete Loop OSR-only emitter while preserving independent configuration and
  telemetry.
- Preserve exact budget accounting, canonical-PC deoptimization, debug/GC/invalidation guards,
  weak ownership, and managed fallback.
- Keep compiler seams that provide deterministic cancellation, failure, invalidation, and code
  budget testing.

### Alpha 2 — compilation economy and startup

- Bring Tier 2 and Loop OSR compilation allocation below their release limits.
- Cache immutable analysis and emission inputs without extending module or closure lifetimes.
- Keep compilation p95 within the release bounds and prevent rejected workloads from paying
  specialization setup costs.
- Add code-size and startup regression decisions to the six-RID aggregate.

### Alpha 3 — tables and mixed loops

- Extend guarded dense-integer and interned-field table operations while preserving metatable,
  ownership, mutation-version, and write-barrier checks.
- Allow proven table side operations to coexist with unboxed numeric locals in reducible loops.
- Target `table_access` and `sieve` without specializing unknown or megamorphic shapes as facts.
- Keep guard failure side-effect ordering and canonical restart exact.

### Alpha 4 — calls and floating-point regions

- Reduce scheduler and frame traffic for stable Lua closure calls with fixed argument/result shape.
- Expand safe cross-function numeric state only where generation, upvalue, hook, yield, and budget
  boundaries are proven.
- Improve floating-point region formation for Mandelbrot-style control flow.
- Preserve fallback for varargs, open results, protected calls, coroutines, and native callbacks.

### Alpha 5 — strings and baseline execution

- Reduce transient allocations in number-to-string conversion, concatenation, and `table.concat`.
- Improve interpreter and Tier 1 fallback paths used by polymorphic, string-heavy, and cold code.
- Keep binary-string semantics, locale independence, pattern behavior, and GC visibility unchanged.
- Complete the required workload targets before feature freeze.

### Beta

- Freeze public API, package, IR, profile, telemetry, and backend configuration scope.
- Run the full six-RID cross-runtime matrix and same-machine `0.8.0` comparisons.
- Resolve every required performance, allocation, startup, code-size, correctness, NativeAOT, and
  trimming gate; new optimization features wait for the next milestone.

### Release candidate and stable release

- Accept only release-blocking correctness, compatibility, packaging, or measured regression fixes.
- Reproduce all release bundles, NuGet packages, public API baselines, conformance suites, and
  performance evidence from the accepted source.
- Publish stable `0.9.0` only from an accepted release candidate with no product-code changes.

## Correctness and portability gates

Performance targets never replace product correctness. Every promotion requires:

- the applicable official Lua 5.4.8 suite and PUC Lua differential fixtures;
- interpreter, Tier 1, Tier 2, loop-entry, debug/hook, and fallback result agreement;
- malformed input, fuzz, GC, weak table, finalizer, coroutine, close, and invalidation coverage;
- exact 64-bit instruction accounting and canonical restart after every guard or poll exit;
- Windows, Linux, and macOS coverage on x64 and Arm64;
- .NET NativeAOT, trimming, single-file, and ReadyToRun validation;
- complete public API, package consumer, CLI, and release-bundle checks.

## Non-goals

- Reintroducing Lua persisted/static AOT, its artifact format, loader, or cache.
- Treating build-time generation as a substitute for runtime profile and table/call shape data.
- Adding a new execution tier to improve one benchmark.
- Removing diagnostic or test seams for unmeasured virtual-dispatch savings.
- Weakening Lua semantics, resource budgets, GC barriers, debug hooks, or deoptimization accuracy.
- Claiming version-to-version speedups from measurements taken on different hardware.
