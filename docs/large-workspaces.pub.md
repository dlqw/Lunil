# How to analyze large Lua workspaces

[简体中文](large-workspaces.zh-CN.pub.md)

This guide configures `LuaWorkspace` for repositories ranging from thousands of modules to several
million lines. It assumes stable module identities, bounded source discovery, and a host that can
keep one workspace alive across successive snapshots.

## 1. Choose the snapshot shape

Use `AnalyzeAsync` when the caller immediately needs every `LuaCompilationResult`. Use
`AnalyzeCompactAsync` for editor indexes, repository-wide queries, and long-lived snapshots:

```csharp
using var workspace = new LuaWorkspace(options);
LuaWorkspaceCompactSnapshot snapshot =
    await workspace.AnalyzeCompactAsync(documents, cancellationToken);
```

A compact snapshot retains module/export/function/dependency summaries and sharded reference,
global, call, callback, and persistence indexes. It does not retain syntax, semantic, analysis, or
IR models. Call `snapshot.MaterializeAsync(workspace, documents, cancellationToken)` only when a
consumer needs a full workspace result.

## 2. Set explicit budgets

```csharp
var options = new LuaWorkspaceOptions
{
    LanguageVersion = LuaLanguageVersion.Lua54,
    MaximumModuleCount = 25_000,
    MaximumDependencyCount = 1_000_000,
    MaximumSourceBytes = 150L * 1024 * 1024,
    MaximumParallelism = Math.Max(1, Environment.ProcessorCount),
    MaximumPendingWorkItems = 2_048,
    MaximumCacheEntryCount = 16_384,
    MaximumCacheBytes = 512L * 1024 * 1024,
    IndexShardCount = 64,
    DiskCacheDirectory = ".lunil/cache",
    MaximumDiskCacheBytes = 2L * 1024 * 1024 * 1024,
    RetainFullAnalysisCacheResults = false,
};
```

The defaults allow 65,536 modules, 1,048,576 dependencies, 1 GiB of source, 4,096 queued work
items, 512 MiB of memory cache, and 2 GiB of disk summaries. Lower limits for untrusted input and
raise them only from measured repository size. Budget failures are diagnostics or explicit failed
operations; they are not permission to continue with silently incomplete data.

## 3. Reuse one workspace

Keep the same `LuaWorkspace` for a project/cache domain. Each analysis returns an immutable
snapshot; top-level operations are serialized while module work is bounded by
`MaximumParallelism` and `MaximumPendingWorkItems`.

Invalidation compares content, exports, functions, dependencies, and the host-contract summary.
Private implementation changes therefore avoid invalidating importers when their public summaries
are unchanged. `RetainFullAnalysisCacheResults = false` lets full models be reclaimed while compact
summaries remain queryable. Use strong retention only when the host has a measured latency need and
an appropriate heap budget.

## 4. Configure the disk cache

`DiskCacheDirectory` enables versioned, content-addressed compact summary files. The cache validates
its format and content, ignores corrupt entries, and prunes to `MaximumDiskCacheBytes`.

- Use a directory private to the project and Lunil compatibility line.
- Do not share a writable cache between mutually untrusted users.
- Treat it as disposable acceleration, not authoritative analysis output.
- `ClearCache()` clears in-memory reuse; delete the configured directory when the host explicitly
  needs to discard disk summaries.

## 5. Query compact indexes

Use `FindReferences`, `FindGlobalReferences`, and `FindCallsToExport` for code navigation. Host-aware
snapshots also expose `FindCallbackRegistrations` and `FindPersistenceSchemas`. Results carry stable
module/source identities and symbol keys; dynamic exports and calls remain conservative.

Monitor `LuaWorkspaceMetrics`: cache hits/misses and evictions, invalidated modules, dirty functions
and exports, reclaimed analyses, disk-cache hits, indexed references/calls, peak parallelism,
pending-work high-watermark, resident cache bytes, and compact resident bytes. Report
`LuaWorkspaceProgress` without blocking the analysis worker.

## 6. Reproduce the scale profiles

The repository includes deterministic profiles:

```bash
dotnet run --project benchmarks/Lunil.Workspace.Scale -c Release -- --profile=m --snapshots=10
dotnet run --project benchmarks/Lunil.Workspace.Scale -c Release -- --profile=l
dotnet run --project benchmarks/Lunil.Workspace.Scale -c Release -- --profile=xl
```

`m` is 100,000 lines, `l` is 20,000 modules and 1,000,000 lines, and `xl` is 50,000 modules and
5,000,000 lines. The profiles validate corpus size, minimum indexed facts, bounded elapsed time,
retained managed memory, repeated-snapshot stability, and queue pressure on the current machine.
Use the same fixture when changing budgets or deployment hardware rather than treating one machine's
timings as a universal guarantee.

## Expected result

The host retains compact repository-wide code intelligence without pinning every compiler model,
reuses unchanged summaries between snapshots, applies bounded concurrency and cache policies, and
can materialize full analysis only for the documents that need it.
