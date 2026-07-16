# ADR 0016: Track require provenance and reload modules only while the host is idle

- Status: Accepted for manual reload v1
- Date: 2026-07-17
- Target: Lua 5.4.8, .NET 10
- Follow-up: concurrent invalidation and immutable function versions

## Context

`package.loaded` contains only the value returned by a loader. It does not retain the canonical
module, loader function, loader data, searcher provenance, or original file path. A separate
`name -> LuaIrModule` dictionary would therefore be insufficient for reload: preload and custom
searchers may not produce canonical IR, file loaders must be rebuilt from current bytes rather than
re-executing their old closure, and direct writes to `package.loaded` can make any side table stale.

Reload is also not a transaction over arbitrary Lua effects. A replacement loader can mutate
globals, tables, upvalue-reachable state, files, or host services before it returns an error. The
host can delay its own cache commit and restore the old cache entry, but it cannot generally undo
those external effects.

Finally, the current JIT invalidation boundary does not provide a concurrent execution lease.
Publishing a replacement while another thread is executing old code would claim a guarantee that
the runtime does not yet implement.

## Decision

### Require records and ownership

`LuaState` records every successful `require` as a `LuaModuleRecord`: module name, loader kind,
rooted loader, rooted loader data, rooted cached value, optional `LuaIrModule`, and a monotonic
revision. The built-in package library marks loaders as `Preload`, `LuaFile`, or `CustomSearcher`.
Only `require` creates records; `load`, `loadfile`, and `dofile` remain uncached chunk operations and
do not acquire a module name.

The live registry owns `LuaHandle` roots for all three Lua values. Public records are immutable
borrowed snapshots; consumers retaining a value beyond eviction must create their own handle.
`TryGetModule` and `GetLoadedModuleNames` compare the recorded cache value with the current
`package.loaded[name]`. A direct eviction or replacement lazily removes the stale record and
releases its handles. Reinstalling a different loaded table clears all records.

### Idle boundary

`LuaState.IsIdle` is true only when no thread, native callback, or finalizer is executing. It is an
observation, not a cross-thread barrier. `LuaHost` serializes its own execute, binary-execute,
reload, and dispose operations through one gate and rejects reentrant reload with `StateBusy`.
Hosts that expose the same state to another executor must still provide external quiescence. A
strict generation/lease barrier is deferred to the concurrent invalidation milestone.

### Prepare, execute, commit

`LuaHost.ReloadModule` performs three phases:

1. **Prepare.** It validates the require record. A `LuaFile` record rereads the recorded or
   explicitly overridden path and compiles a new canonical module and loader. Preload and custom
   records re-execute the retained loader and loader data; v1 deliberately does not rerun a
   possibly mutated searcher chain.
2. **Execute.** The candidate loader receives the original module name and effective loader data.
   The old `package.loaded` value remains visible during execution, so recursive lookup observes
   the last committed module. Any cache write made by the candidate is captured for ordinary
   nil-result handling, then the old cache is restored before policy evaluation. Compatible
   loader upvalues at the same index, name, source kind/index, and IR kind reuse the old cell;
   mismatches are counted rather than guessed.
3. **Commit.** Only a completed loader reaches a cache policy. `ReplaceCache` publishes the
   candidate value. `PatchExistingTable` replaces raw entries and the metatable while preserving
   the old table identity, with a rollback snapshot. `Custom` invokes a managed callback. The
   cache and require record are then replaced together, and an existing JIT executor invalidates
   the old optional canonical module.

Compilation, execution, or policy failure restores the previous cache, require record, and reused
upvalue cell values. `SideEffectsMayHaveOccurred` is true once candidate Lua or custom policy code
has executed because reachable table/global/I/O effects cannot be rolled back. Structured results
also publish compilation/execution details, upvalue reuse and mismatch counts, and table patch
counts.

## Consequences and limits

- File reload uses the host's configured standard-library file system and compiler, so diagnostics
  and capability restrictions remain consistent with initial loading.
- `PatchExistingTable` updates references that already point at the exported module table.
  `ReplaceCache` does not rewrite `local old = M.foo`, closure captures, or arbitrary globals.
- Suspended coroutines and existing closures continue to reference their old immutable module and
  function objects. v1 does not mutate closure code or remap program counters.
- No file watcher is included. A watcher may call this explicit API after establishing the same
  idle contract.
- Searcher reruns, cross-version profile mapping, compatible function-slot following, and bounded
  concurrent invalidation belong to the next versioned design.

## Verification

Tests cover file recompilation through JIT, recorded provenance and cache eviction, loadfile
exclusion, preload and custom loader replay, path override, replace/table-patch/custom cache
policies, compatible loader upvalues, compile/runtime/policy rollback, visible unrollbackable side
effects, rooted-record release, and reentrant busy rejection.
