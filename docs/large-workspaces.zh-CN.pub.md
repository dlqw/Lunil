# 如何分析大型 Lua workspace

[English](large-workspaces.pub.md)

本指南为从数千 module 到数百万行的 repository 配置 `LuaWorkspace`。它假定已有稳定 module
identity、受预算约束的 source discovery，并且宿主会在连续 snapshot 之间复用同一个 workspace。

## 1. 选择 snapshot 形态

调用方立即需要每个 `LuaCompilationResult` 时使用 `AnalyzeAsync`；editor index、全 repository query
和长生命周期 snapshot 使用 `AnalyzeCompactAsync`：

```csharp
using var workspace = new LuaWorkspace(options);
LuaWorkspaceCompactSnapshot snapshot =
    await workspace.AnalyzeCompactAsync(documents, cancellationToken);
```

Compact snapshot 保留 module/export/function/dependency summary，以及分片的 reference、global、call、
callback 与 persistence index，不保留 syntax、semantic、analysis 或 IR model。只有 consumer 需要
完整 workspace result 时，才调用
`snapshot.MaterializeAsync(workspace, documents, cancellationToken)`。

## 2. 设置显式预算

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

默认允许 65,536 个 module、1,048,576 条 dependency、1 GiB source、4,096 个 queued work item、
512 MiB memory cache 与 2 GiB disk summary。不可信输入应降低限制，且只根据实际 repository
规模提高。预算失败是 diagnostic 或明确的 failed operation，不能静默继续并返回不完整数据。

## 3. 复用一个 workspace

一个 project/cache domain 应保持同一个 `LuaWorkspace`。每次 analysis 返回不可变 snapshot；顶层
operation 被串行化，module work 则受 `MaximumParallelism` 和 `MaximumPendingWorkItems` 约束。

Invalidation 会比较 content、export、function、dependency 与 host-contract summary。Public summary
未变化时，private implementation 变化不会使 importer 无谓失效。module key 仍匹配的重建会复用上一次
snapshot 的逐 module projection——reference、symbol 与 call edge 直接重合并，不再重新 parse/analyze；
名称与 symbol key 通过 `LuaWorkspace.StringInterner` 解析为共享的驻留实例，因此连续 snapshot 不会
滞留未变化字符串的重复副本。`RetainFullAnalysisCacheResults = false` 允许回收完整 model，同时继续查询
compact summary。只有经过测量的 latency 需求与充分 heap 预算同时存在时才应开启强 retention。

## 4. 配置 disk cache

`DiskCacheDirectory` 启用版本化、content-addressed compact summary file。Cache 会验证 format 与
content、忽略损坏 entry，并按 `MaximumDiskCacheBytes` prune。

- 使用 project 与 Lunil compatibility line 私有的目录；
- 不在互不信任的用户之间共享可写 cache；
- 把它视为可丢弃的加速数据，而不是权威 analysis output；
- `ClearCache()` 只清除内存 reuse；宿主明确需要丢弃 disk summary 时应删除配置目录。

## 5. 查询 compact index

代码 navigation 使用 `FindReferences`、`FindGlobalReferences` 与 `FindCallsToExport`。Host-aware
snapshot 还提供 `FindCallbackRegistrations` 和 `FindPersistenceSchemas`。结果携带稳定 module/source
identity 与 symbol key；dynamic export/call 保持保守。

应监控 `LuaWorkspaceMetrics`：cache hit/miss/eviction、invalidated module、dirty function/export、
reclaimed analysis、disk-cache hit、indexed reference/call、peak parallelism、pending-work high-watermark、
resident cache bytes 与 compact resident bytes。报告 `LuaWorkspaceProgress` 时不得阻塞 analysis worker。

## 6. 复现 scale profile

Repository 提供确定性 profile：

```bash
dotnet run --project benchmarks/Lunil.Workspace.Scale -c Release -- --profile=m --snapshots=10
dotnet run --project benchmarks/Lunil.Workspace.Scale -c Release -- --profile=l
dotnet run --project benchmarks/Lunil.Workspace.Scale -c Release -- --profile=xl
```

`m` 为 100,000 行，`l` 为 20,000 module/1,000,000 行，`xl` 为 50,000 module/5,000,000 行。
Profile 会在当前机器验证 corpus size、最小 indexed fact、bounded elapsed time、retained managed
memory、重复 snapshot 稳定性与 queue pressure。调整预算或部署硬件时应复用同一 fixture，而不能把
某台机器的 timing 当作通用保证。

## 预期结果

宿主可以在不 pin 全部 compiler model 的前提下保留 compact repository-wide code intelligence，
跨 snapshot 复用未变化 summary，应用有边界的 concurrency/cache policy，并且只为需要的 document
materialize 完整 analysis。
