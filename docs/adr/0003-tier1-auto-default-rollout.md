# ADR 0003: Qualified Tier 1 is enabled by default

- Status: Accepted
- Date: 2026-07-13
- Target: Lua 5.4.8, .NET 10
- Depends on: [ADR 0002](0002-execution-backend-abi-v2.md)

## Context

The original CoreCLR JIT productization kept `LuaJitExecutorOptions.Default.Policy` at
`InterpreterOnly` because the M8 measurements did not satisfy the approved throughput and
compile-latency gates. M9 introduced Runtime ABI v2 direct lowering, deterministic benefit
eligibility, owner-safe verified-plan caching, bounded compilation cancellation, and repeated
cross-RID evidence collection.

The post-M9 main CI evidence covers five independent processes per RID and nine cold compilation
samples per process on win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, and osx-arm64. All six
RIDs reported:

- an arithmetic median-speedup bootstrap 95% lower bound above 2x;
- Tier 1 single-function compilation p95 below 5 ms;
- zero linear allocation slope;
- no deterministic negative-workload gate failures.

Tier 2 and Loop OSR have separate performance and allocation risks and have not inherited this
qualification.

## Decision

`LuaJitExecutorOptions.Default` and a newly constructed `LuaJitExecutorOptions` use:

```csharp
Policy = LuaJitPolicy.Auto;
EnableTier2 = false;
EnableLoopOsr = false;
```

`Auto` begins in the interpreter. A function is queued for bounded asynchronous Tier 1
compilation only after the configured entry/backedge hotness threshold and deterministic benefit
eligibility both pass. Imported profiles may satisfy hotness but may not bypass benefit
eligibility.

When dynamic code is unsupported or not compiled, including NativeAOT, default execution remains
in the interpreter and Reflection.Emit is never called. Hosts may retain a strict Tier 0 contract
with `InterpreterOnly`, force an attempted compile with `PreferJit`, or require compilation with
`RequireJit`.

Tier 2 profile import and promotion require the host to set `EnableTier2=true`. Loop OSR remains
independently gated by `EnableLoopOsr=true`.

## Consequences

- Existing hosts that relied on constructing `LuaJitExecutorOptions` without setting `Policy`
  now receive qualified Tier 1 after hotness thresholds are reached.
- Hosts requiring zero dynamic compilation must set `InterpreterOnly` explicitly or use the
  Tier 0 `LuaInterpreter`/`LuaExecutor` facade.
- NativeAOT and other dynamic-code-unavailable deployments retain exact fallback without a
  configuration change.
- Tier 2 and Loop OSR remain experimental opt-ins and require independent future ADRs before
  either default changes.
- The six-RID evidence and focused soak must be repeated for the rollout commit before merge.
