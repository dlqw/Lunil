# 如何部署签名 Patch Bundle

[English](hot-update.pub.md)

本指南通过签名 patch bundle 部署 Lunil module replacement。它假设已有 `LuaHost`、部署控制面、
应用 release ledger 和 ECDSA P-256 signing key。整个流程会在打开游戏循环 update window 之前验证并
stage 所有 candidate。

字段、上限、status 与 telemetry 名称见 [patch 参考](hot-update-reference.zh-CN.pub.md)。Transaction、
generation、rollback 与 distributed barrier 语义见
[热更新发布原理](hot-update-lifecycle.zh-CN.pub.md)。

## 1. 创建并检查 Bundle

把 replacement payload 放在同一 root 下，并用 canonical manifest 描述它们。声明当前与目标 revision、
update intent、runtime 契约、target label、请求的 admission capability、dependency、expiry 和 nonce。
使用受保护的 private key 打包，再用公开 trust material 检查和 dry-run：

```text
lunil patch pack manifest.json payload --output update.lpatch --private-key private.pem --key-id release-2026
lunil patch verify update.lpatch --trust-store patch-trust.json
lunil patch inspect update.lpatch --trust-store patch-trust.json
lunil patch dry-run update.lpatch --trust-store patch-trust.json
```

从 preparation 到 commit 必须使用同一份稳定 target-label snapshot。Environment、region、shard、platform 或
ring assignment 变化后，应丢弃已准备 patch 并重新 prepare。

## 2. 配置信任、准入与重放保护

使用当前 P-256 public key 创建 trust store，并按需配置启用、退役或撤销时刻。然后把 preparation
绑定到 Host 当前 build、revision、runtime ABI、channel、已授予 admission capability、target label、
release ledger 和 rollback authorization：

```csharp
var trustStore = new LuaPatchEcdsaTrustStore([
    new LuaPatchTrustedEcdsaKey("release-2026-q3", q3PublicKey)
    {
        ValidFrom = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        ValidUntil = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero),
    },
]);

using var stream = File.OpenRead("update.lpatch");
var bundle = LuaPatchBundle.Read(stream, trustStore);
var replayStore = new LuaPatchFileReplayStore("state/accepted-patches.ndjson");
var prepareOptions = new LuaPatchPrepareOptions
{
    AcceptancePolicy = new LuaPatchAcceptancePolicy
    {
        TargetBuild = currentBuild,
        CurrentRevision = currentRevision,
        RuntimeAbi = "lunil-0.12",
        AllowedChannels = ["production"],
        GrantedCapabilities = hostPatchCapabilities,
        TargetLabels = targetLabels,
        RevisionClassifier = releaseLedger.Classify,
        RollbackAuthorizer = (manifest, signer) =>
            rollbackKeyIds.Contains(signer.KeyId) &&
            approvedRollbackTargets.Contains(manifest.TargetRevision),
    },
    ReplayStore = replayStore,
    ReplayScope = "zone-01",
};
```

`ReplayScope` 应使用稳定 deployment-target identity，而不是 process id。Replay store 应位于可为同一
target 工作的进程共享且 lock-correct 的本地存储上；也可在 transactional database 中实现同样的
reservation 与 exclusive commit-lease state machine。

## 3. 在 Update Window 外准备 Candidate

多个 Host 共享一个 limiter，使 compilation fan-out 具有有界 concurrency 与 queue depth：

```csharp
var limiter = new LuaPatchPreparationLimiter(
    maximumConcurrency: Math.Max(1, Environment.ProcessorCount / 2),
    maximumQueueLength: 64);

prepareOptions = prepareOptions with
{
    PreparationLimiter = limiter,
    PreparationWaitTimeout = TimeSpan.FromMilliseconds(250),
    StateMigrationAdapters = stateAdapters,
    ResourceMigrationAdapters = resourceAdapters,
};

var preparation = await host.PreparePatchAsync(bundle, prepareOptions, stoppingToken);
if (preparation.Status == LuaPatchPrepareStatus.Deferred)
{
    ScheduleRetry(preparation.AdmissionStatus);
    return;
}
if (!preparation.Succeeded)
{
    ReportPreparationFailure(preparation);
    return;
}
```

Preparation 会在隔离环境编译与验证 candidate，捕获预期 live module revision，并保留 replay
identity，但不执行 candidate loader。Migration schema 必须与 live schema version 匹配，且每个命名 adapter
都必须可用，preparation 才能成功。

## 4. 在游戏循环安全点 Commit

在 frame 之间打开有界 update window，并在同一 thread commit：

```csharp
var opened = host.TryOpenPatchUpdateWindow(new LuaPatchUpdateWindowOptions
{
    WaitTimeout = TimeSpan.Zero,
    MaximumDuration = TimeSpan.FromMilliseconds(8),
}, stoppingToken);
if (!opened.Succeeded)
{
    ScheduleForLaterFrame(preparation.PreparedPatch!);
    return;
}

using var window = opened.Window!;
var commit = host.CommitPatch(
    preparation.PreparedPatch!,
    window,
    new LuaPatchCommitOptions
    {
        MaximumPauseDuration = TimeSpan.FromMilliseconds(4),
    },
    stoppingToken);
```

处理每个非成功 status。Expiry、revision drift、migration failure、pause-budget exhaustion 或 cancellation
都会让旧 module graph 继续 active。Candidate code 可以调用 Host 可见服务，因此应把
`SideEffectsMayHaveOccurred` 视为执行应用级对账的要求。

## 5. 声明 State 与 Resource Migration

注册 live schema version，并使用互不相交的 RFC 6901 state path。通过 API 创建 bundle 时，序列化
完整 schema，并把它作为 canonical companion entry 追加：

```csharp
host.SetPatchStateSchemaVersion("game-state", "42");

var schema = new LuaPatchMigrationSchema
{
    SchemaId = "game-state",
    BaseVersion = "42",
    TargetVersion = "43",
    Modules =
    [
        new LuaPatchModuleMigrationSchema
        {
            ModuleName = "game.match",
            State =
            [
                new LuaPatchStateRule
                {
                    TargetPath = "/match/state",
                    Kind = LuaPatchStateRuleKind.PatchTable,
                },
            ],
            Resources =
            [
                new LuaPatchResourceRule
                {
                    ResourceId = "world-session",
                    Kind = LuaPatchResourceKind.HostResource,
                    Disposition = LuaPatchResourceDisposition.Continue,
                    StatePath = "/session",
                },
            ],
        },
    ],
};

var schemaEntry = new LuaPatchEntry(
    LuaPatchMigrationSchemaFormat.BundleEntryName,
    moduleName: null,
    LuaPatchEntryKind.CompanionData,
    LuaPatchMigrationSchemaSerializer.Serialize(schema));

var bundle = LuaPatchBundle.Create(
    manifest,
    replacementEntries.Append(schemaEntry),
    signer);
```

使用 `lunil patch pack` 时，把序列化 byte 写入 `<payload-root>/migration/schema.json`，并在输入
manifest 中把该 path 列为没有 module 名的 `CompanionData` entry。Pack command 会从这些 descriptor 与
文件重建并签名 canonical bundle。

外部 alias 需要同一 table identity 时使用 `PatchTable`。唯一 native-resource identity 使用
`HostResource + Continue`；已准入 suspended thread 使用 `Coroutine + Continue`；需把 remaining
delay 转给 candidate timer 时使用 `Timer + Continue`。不要在 candidate loader 中创建重复 native
resource。应用自定义的 cancel、restart、drain 或 transformation 需要可逆 adapter。

## 6. 通过隔离 Ring 渐进部署

从同一 canonical manifest 准备每个 target。隔离 traffic、等待 quiescence，并按顺序部署 canary
与 production ring：

```csharp
using var journal = new LuaPatchFileJournal("state/hot-update/deploy.ndjson");
var plan = new LuaPatchRolloutPlan
{
    RolloutId = "game-2026-07-22-01",
    Rings =
    [
        new LuaPatchRolloutRing { Name = "canary", Targets = canaryTargets },
        new LuaPatchRolloutRing { Name = "production", Targets = productionTargets },
    ],
};

var result = new LuaPatchCoordinator().Deploy(plan, new LuaPatchCoordinatorOptions
{
    RequireTargetIsolation = true,
    Journal = journal,
    TargetLifecycle = lifecycleOptions,
    UpdateWindow = updateWindowOptions,
    Commit = commitOptions,
    GenerationGuard = generationGuard,
    HealthCheck = CheckRingHealth,
}, stoppingToken);
```

Restoration 失败后不得向 target 路由 traffic。Ring 跨进程时，在每个 participant 中配置同一份
`DistributedBarrier` membership 与 quorum，使用相同 rollout id 和 ring 名，并保留 terminal barrier state，
直到每个进程与 operator 都能观测决策。

## 7. 恢复未完成的部署 Transaction

服务启动时，把 incomplete journal transaction 与应用自有持久状态、routing state 对账：

```csharp
using var journal = new LuaPatchFileJournal("state/hot-update/deploy.ndjson");
var pending = journal.GetIncompleteTransactions();
var recovered = journal.RecoverIncomplete(recoveryHandler);
```

只有在应用建立权威结果后才返回 `Committed` 或 `RolledBack`。无法自动对账时返回 `Manual`。
Journal、replay store 与 distributed-barrier store 应受适当的操作系统权限和存储持久性保护。

## 8. 导出 Health 与 Telemetry

导出 preparation-limiter gauge、generation snapshot、有界 rollout history、activity trace 和 patch metric。对
transition residue、stale-resource 增长、replay 或 journal corruption、history recording failure、recovery backlog
以及任何仍处于 isolated 的 target 发出告警。精确 counter、activity 名、metric 名与默认资源上限见
[patch 参考](hot-update-reference.zh-CN.pub.md)。
