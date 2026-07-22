# Lunil architecture guide

[简体中文](roadmap.zh-CN.md)

## Language compatibility

A host selects a `LuaLanguageVersion`; that identity flows through source parsing, binary chunk loading, canonical IR, runtime state, and standard-library installation. Lua 5.4 is the default when no version is selected. A module or chunk from one version is rejected by a state configured for another version instead of inheriting nearby-version behavior.

The version adapters own their syntax gates, lexical behavior, instruction formats, binary chunk codecs, and library surfaces. The shared canonical IR and scheduler are the interchange boundary, so adapter-specific behavior is translated before ordinary execution begins.

## Execution architecture

The interpreter is the reference executor. Tier 1, Tier 2, and loop OSR are optional block executors beneath the same resumable scheduler. The scheduler owns frame lifetime, coroutines, calls, errors, close handling, hooks, logical GC, and host-boundary values.

Generated code validates its frame and runtime assumptions before use. It preserves canonical program counters and 64-bit instruction accounting. A failed guard or unavailable dynamic-code capability continues through the canonical executor without changing Lua-visible behavior.

## Optimization safety

Numeric specialization uses verified regions, runtime guards, shared ABI helpers, and explicit deoptimization points. Table PICs and direct compiled calls are bounded, generation-aware, and weakly owned. Profiles identify candidates but never prove current runtime values. Cache entries do not keep Lua states or retired modules alive.

## Hosting and interoperability

Hosts control execution policy, instruction budgets, language version, module identity, and optional CLR capabilities. CLR interoperation is capability-scoped: types and members must be explicitly exposed by the host, and unrestricted reflection or arbitrary assembly loading is not part of the Lua contract.

NativeAOT and trimmed hosts use the same public semantics through the managed execution path when dynamic code is unavailable. Module publication and function slots use content identities and generations so a stale compiled entry cannot run after replacement.

Production hot updates enter through signed, resource-bounded patch bundles. Isolated background
preparation binds a bundle to expected live module revisions; a game-loop update window then
publishes the dependency-ordered module set atomically. Suspended frames retain their captured code
generation, while calls that enter after commit observe the replacement generation. Signed state
schemas and reversible host adapters migrate Lua tables, userdata payloads, coroutines, timers,
 subscriptions, and tasks inside the same publication journal. A process-local coordinator adds
 all-State barrier publication, ordered canary/ring rollout, health-gated rollback, and a durable
 hash-chained deployment journal with host-reconciled crash recovery. The journal enforces one writer
 through a lifetime sidecar lock, serves concurrent verified readers, and uses atomic, retention-aware
 compaction so a long-running deployment service does not permanently exhaust its active file.

## Reading the design notes

The [architecture notes](adr/) document the execution ABI, numeric specialization, invalidation, heap identity, and language adapters. The compiler, runtime-continuation, conformance, and interoperability guides provide API and operational details for each public boundary.
