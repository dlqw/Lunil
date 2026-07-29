# 部署签名 Patch Bundle

[English](deploy-signed-patch-bundles.pub.md)

本 how-to 用于准备、发布、灰度和恢复 Lunil 签名 patch，并避免暴露只完成部分验证的 module graph。

## 前置条件

- 已配置 `LuaHost` 与 patch trust store。
- Target label 与 host 匹配的 canonical 签名 patch bundle。
- 用于 replay 与 deployment journal 的稳定存储。
- 多 target 灰度所需的应用 health check 与 traffic isolation 机制。

## 1. 配置信任、准入、Replay 与 Migration

Release key 需要轮换时，创建带重叠生效窗口的 trust store。公钥路径相对于 trust-store 文件：

```json
{
  "schema": "lunil.patch-trust.v1",
  "keys": [
    {
      "keyId": "release-2026-q3",
      "publicKey": "keys/release-2026-q3.pem",
      "validFrom": "2026-07-01T00:00:00Z",
      "validUntil": "2026-10-08T00:00:00Z"
    },
    {
      "keyId": "release-2026-q4",
      "publicKey": "keys/release-2026-q4.pem",
      "validFrom": "2026-10-01T00:00:00Z"
    }
  ]
}
```

使用私有 release key 打包，再通过有大小上限的公开 trust store 校验：

```text
lunil patch pack manifest.json payload --output update.lpatch --private-key private.pem --key-id release-2026-q3
lunil patch verify update.lpatch --trust-store patch-trust.json
lunil patch dry-run update.lpatch --trust-store patch-trust.json
```

把准入与 replay protection 绑定到一个稳定部署 target。Coordinator 要求 `ReplayScope` 等于该
target 的 `TargetId`：

```csharp
var replayStore = new LuaPatchFileReplayStore("state/accepted-patches.ndjson");
var prepareOptions = new LuaPatchPrepareOptions
{
    AcceptancePolicy = new LuaPatchAcceptancePolicy
    {
        TargetBuild = currentBuild,
        CurrentRevision = currentRevision,
        RuntimeAbi = "lunil-0.13",
        AllowedChannels = ["production"],
        GrantedCapabilities = hostPatchCapabilities,
        TargetLabels =
        [
            new("environment", deploymentEnvironment),
            new("region", region),
            new("shard", shardId),
            new("ring", rolloutRing),
        ],
        RevisionClassifier = releaseLedger.Classify,
        RollbackAuthorizer = (manifest, signer) =>
            signer.Algorithm == LuaPatchEcdsaSigner.AlgorithmName &&
            rollbackKeyIds.Contains(signer.KeyId) &&
            approvedRollbackTargets.Contains(manifest.TargetRevision),
    },
    ReplayStore = replayStore,
    ReplayScope = "state-a",
};
```

Bundle 包含 `migration/schema.json` 时，注册当前在线 version，并提供 canonical schema 引用的全部
adapter：

```csharp
host.SetPatchStateSchemaVersion("game-state", "42");
var preparation = await host.PreparePatchAsync(bundle, prepareOptions with
{
    StateMigrationAdapters = stateAdapters,
    ResourceMigrationAdapters = resourceAdapters,
}, stoppingToken);
```

打包前使用 `LuaPatchMigrationSchemaSerializer.Serialize` 生成写入 `migration/schema.json` 的准确 byte。
Patch 不迁移 state 或 host resource 时可省略 schema 与 adapter 步骤。

## 2. 预检依赖与编译

`LuaPatchDependencyPlan` 保证 required dependency 先于 dependent，并把循环依赖的 strongly
connected component 作为一个 preparation group。`LuaPatchPreflight.Analyze` 创建隔离 staging
host，校验 source、binary chunk 和由 host 解码的 canonical IR entry，不修改在线 `LuaHost`。

`LuaHost.PreparePatchAsync` 会在线程池中完成上述工作，随后短暂进入在线 host 的 execution
gate，为每个目标 module 捕获 expected revision。只有目标 module 均已加载、语言版本一致且每个
module 都使用可回滚的 cache policy 时，prepare 才会成功；prepare 阶段不会执行 candidate loader。

Rollout 向大量 host fan-out 时，隔离编译会集中消耗 CPU 与内存。应在所有 target 的 prepare options
间共享同一个 `LuaPatchPreparationLimiter`，同时限制 active work 与排队需求：

```csharp
// 在部署服务进程内共享，不要为每个 target 单独创建。
var preparationLimiter = new LuaPatchPreparationLimiter(
    maximumConcurrency: Math.Max(1, Environment.ProcessorCount / 2),
    maximumQueueLength: 64);

var prepareOptions = new LuaPatchPrepareOptions
{
    PreparationLimiter = preparationLimiter,
    PreparationWaitTimeout = TimeSpan.FromMilliseconds(250),
    // AcceptancePolicy、ReplayStore、ReplayScope、migration adapter 等。
};

var preparation = await host.PreparePatchAsync(bundle, prepareOptions, stoppingToken);
if (preparation.Status == LuaPatchPrepareStatus.Deferred)
{
    ScheduleRetry(preparation.AdmissionStatus); // Saturated 或 TimedOut
    return;
}
```

`MaximumConcurrency` 是同时执行的隔离 preflight 数量，`MaximumQueueLength` 限制其后的 waiter；
queue 为 0 时立即 fail-fast。等待时间可以是 0、不超过 `Int32.MaxValue` 毫秒的有限值，或
`Timeout.InfiniteTimeSpan`。Queue overflow 或等待超时会在 preflight、live-state binding 与 replay
reservation 之前返回 `Deferred`，调用方取消仍
保持 cancellation 语义。`PreparePatch` 与 `PreparePatchAsync` 使用相同 admission 规则。应把
`ActiveCount`/`QueuedCount` 导出为 gauge，并在 limiter 外部增加 retry jitter，让 rollout controller
协调各 target 的 backoff。

## 3. 在游戏循环 Update Window 中 Commit

在 tick 或 frame 之间打开 update window，并在同一线程提交 prepared patch：

```csharp
var preparation = await host.PreparePatchAsync(bundle, prepareOptions, stoppingToken);
if (!preparation.Succeeded)
{
    return;
}

var opened = host.TryOpenPatchUpdateWindow(new LuaPatchUpdateWindowOptions
{
    WaitTimeout = TimeSpan.Zero,
    MaximumDuration = TimeSpan.FromMilliseconds(8),
}, stoppingToken);
if (!opened.Succeeded)
{
    // 保留 prepared patch，在后续 frame 重试。
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

Update window 会持续持有 host execution gate，因此普通 host 执行无法观察到只发布了一部分的
module 集合。Commit 在执行 candidate 之前重新检查全部 expected revision，然后按 dependency-first
顺序通过临时 `package.loaded` overlay 求值 candidate：dependent 可以看到本事务中已完成的新版
dependency。Cache value、module record、保留 table identity 的 patch、兼容 closure slot 与 JIT
module generation 会作为一个事务发布。发布失败、取消或 pause budget 超时会恢复所有目标 module
的 record、cache value、table 内容、loader upvalue 和 closure slot。

已挂起 frame 会继续持有进入时捕获的 immutable function generation；成功 commit 后发起的新调用
会读取 closure slot 的新 generation。Module-owned coroutine 入口还会接受 generation fencing：未声明
迁移的旧 coroutine 在发布后不能恢复。必须让挂起 coroutine 在旧 frame 上继续完成时，使用显式的
runtime-owned `Coroutine`/`Continue` resource rule；不作为 resumable coroutine 保留的普通 in-flight
frame 仍会在其进入时捕获的 immutable generation 上完成。

原子 patch commit 支持 `ReplaceCache` 和 `PatchExistingTable`。由于不透明 `Custom` cache callback
和 source-path override 的效果无法纳入 module transaction journal，prepare 阶段会拒绝它们。
Candidate Lua 代码仍可能产生 global、CLR、文件系统、网络或其他 host 可见副作用，这些副作用通常
无法回滚。因此，只要失败前执行过 candidate，即使目标 module 状态已经全部恢复，结果仍会把
`SideEffectsMayHaveOccurred` 设为 true。

Pause 与 cancellation 检查发生在 candidate loader 之间及各发布步骤之间；它们可以防止 half-commit，
但不会在一次 VM 调用中途抢占单个 loader。应同时配置常规 Lua instruction budget，以限制单个 loader
的工作量。在循环依赖 component 内，member 按名称确定性执行：已完成 member 会暂存为新版本，指向
尚未执行 member 的 back-edge 则看到其旧 loaded value。

## 4. 跨 State 与 Ring 灰度

`LuaPatchCoordinator` 在单进程内协调多个 `LuaHost` state。Barrier ring 中的 target id 与 host
instance 必须唯一，并且每个 target 都必须由同一份 canonical patch manifest prepare。Coordinator
会先打开全部 update window，再 prepare 任一 commit session；全部 state prepare 成功后才发布完整
ring。Target 配置 `ILuaPatchTargetLifecycle` 后，coordinator 会先停止新流量并等待 adapter 报告
quiescence，再进入 host update window。隔离、quiescence、window 获取、prepare、publish、finalize
或 health gate 任一步失败，都会回滚该 ring 的全部 participant。Coordinator operation 在进程范围内
串行化，避免不同 coordinator instance 形成冲突的锁顺序。

每个 prepared patch 都绑定各自 host，可据此构造 rollout。下例的 `targetLifecycles` 是应用持有的
lifecycle adapter map，底层连接游戏 router 与 in-flight work tracker：

```csharp
using var journal = new LuaPatchFileJournal("state/hot-update/deploy.ndjson");
var plan = new LuaPatchRolloutPlan
{
    RolloutId = "game-2026-07-22-01",
    Rings =
    [
        new LuaPatchRolloutRing
        {
            Name = "canary",
            Targets =
            [
                new("zone-canary", canaryHost, canaryPreparation.PreparedPatch!)
                {
                    Lifecycle = targetLifecycles["zone-canary"],
                },
            ],
        },
        new LuaPatchRolloutRing
        {
            Name = "production",
            Targets =
            [
                new("zone-01", zone01Host, zone01Preparation.PreparedPatch!)
                {
                    Lifecycle = targetLifecycles["zone-01"],
                },
                new("zone-02", zone02Host, zone02Preparation.PreparedPatch!)
                {
                    Lifecycle = targetLifecycles["zone-02"],
                },
            ],
        },
    ],
};

var result = new LuaPatchCoordinator().Deploy(plan, new LuaPatchCoordinatorOptions
{
    RequireTargetIsolation = true,
    TargetLifecycle = new LuaPatchTargetLifecycleOptions
    {
        IsolationTimeout = TimeSpan.FromSeconds(5),
        QuiescenceTimeout = TimeSpan.FromSeconds(30),
        RestoreTimeout = TimeSpan.FromSeconds(5),
    },
    UpdateWindow = new LuaPatchUpdateWindowOptions
    {
        WaitTimeout = TimeSpan.FromMilliseconds(2),
        MaximumDuration = TimeSpan.FromMilliseconds(12),
    },
    Commit = new LuaPatchCommitOptions
    {
        MaximumPauseDuration = TimeSpan.FromMilliseconds(8),
    },
    Journal = journal,
    HealthCheck = context => RingHealthIsAcceptable(context)
        ? LuaPatchRingHealthDecision.Accept
        : LuaPatchRingHealthDecision.Rollback,
}, stoppingToken);
```

各 ring 按顺序运行：canary 被拒绝后，后续 ring 不会启动；canary 已接受而 production ring 失败时，
已接受的 canary 保持 committed，失败 ring 则整体回滚。同步 health callback 在该 ring 全部 update
window 仍被持有时运行，可以检查刚发布的新状态。Callback 返回 `Rollback`、抛出异常、返回非法 enum
值或递归进入 coordinator operation，都会拒绝并回滚该 ring。

`ILuaPatchTargetLifecycle.TryIsolate` 必须在返回 `ILuaPatchTargetIsolation` 前停止新 routing/admission；
随后 `WaitForQuiescence` 在传入的 timeout 内排空 in-flight request、tick、job 或 actor message。该
timeout 是 adapter 必须协作遵守的预算：实现需要把它应用到自己的 router/work tracker，并观察
cancellation token。`Restore` 按隔离的逆序执行，outcome 为 `Committed` 或 `RolledBack`；它接收
`CancellationToken.None`，因此调用方取消不能跳过流量恢复。恢复操作必须以 `TransactionId` 保证
幂等，`Dispose` 只能释放资源，不能改变 routing。

生产环境应启用 `RequireTargetIsolation`，使缺少 adapter 的 target 在 journal 启动前被拒绝。
`LuaPatchTargetCommitResult.Lifecycle` 提供最终 lifecycle 结果；若早期 isolation/quiescence 失败后
cleanup 已恢复 target，则 `Status` 为 `Restored`，`Failure` 保留失败阶段。若 publish 完成后恢复失败，
ring 返回 `RestoreFailed`，已 committed 的 module result 保持可观察，journal 停留在 `Restoring` 供
crash recovery 使用；恢复完成前不得重新路由该 target。

### 跨进程 Prepared 与 Health Quorum

多个独立进程需要形成同一个持久 ring 决策时，配置
`LuaPatchCoordinatorOptions.DistributedBarrier`。每个进程使用相同 rollout id 和 ring name、列出同一组
稳定进程 identity，并从同一份 canonical manifest prepare 自己的本地 `LuaHost` target。首个被接受的
update 会固定 membership、quorum 数量、canonical manifest SHA-256、target revision 与两阶段 timeout；
任何配置冲突都会在 publication 前失败。

该协议包含两个持久 gate。首先，prepared acknowledgement 选出恰好
`RequiredParticipantCount` 个 participant 并形成 `Apply`，只有被选中的进程可以 publish。随后，每个
选中进程必须完成本地 publication、应用 health callback 与 replay acceptance，再提交 `Healthy`
acknowledgement。全部选中进程均健康后 store 才返回 `Commit`；任一选中进程失败或任一 deadline 到期，
都会形成不可逆的 `Rollback`，此时其他进程仍保留 rollback session，可以恢复旧 generation。未被选入
quorum 的进程返回 `Deferred`，继续运行旧 generation。

内置 file store 适用于共享同一套可靠 file lock 语义的多个进程。必须为它提供专用目录，因为 prune
会管理其中的 barrier JSON、temporary file 与 lock sidecar：

```csharp
var participantId = Environment.GetEnvironmentVariable("GAME_PROCESS_ID")!;
var participants = new[] { "game-a", "game-b", "game-c" }.ToImmutableArray();
var barrierStore = new LuaPatchFileDistributedBarrierStore(
    "/srv/game/shared/lunil/barriers",
    new LuaPatchFileDistributedBarrierStoreOptions
    {
        MaximumBarrierCount = 10_000,
        MaximumParticipantCount = 64,
        WriterLockTimeout = TimeSpan.FromSeconds(2),
    });

var localRing = new LuaPatchRolloutRing
{
    Name = "production", // 每个 participant 进程必须一致。
    Targets = localPreparedTargets,
};

var result = new LuaPatchCoordinator().CommitRing(
    "game-2026-07-24-01", // 不得把 rollout id 复用于另一轮部署。
    localRing,
    new LuaPatchCoordinatorOptions
    {
        RequireTargetIsolation = true,
        Journal = localJournal,
        HealthCheck = CheckLocalGameHealth,
        DistributedBarrier = new LuaPatchDistributedBarrierOptions
        {
            Store = barrierStore,
            ParticipantId = participantId,
            Participants = participants,
            RequiredParticipantCount = 2,
            PreparationTimeout = TimeSpan.FromSeconds(30),
            HealthTimeout = TimeSpan.FromSeconds(30),
            PollInterval = TimeSpan.FromMilliseconds(50),
        },
    },
    stoppingToken);

if (result.Status == LuaPatchRingCommitStatus.Deferred)
{
    KeepServingThePreviousGeneration();
}
```

共享目录所在文件系统必须保证 exclusive file lock 与同目录 atomic rename。Store 会在 replacement 前
flush state、在 Unix 上 flush directory entry、归一化时钟回退，并限制 identity、message、participant、
state byte 与 active barrier file 数量；hash 不匹配或非法 transition 会被拒绝。SHA-256 用于发现意外
损坏，不能抵御可重写整个目录的攻击者，因此必须配置 OS 权限。若控制面使用数据库或 consensus
service，可实现 `ILuaPatchDistributedBarrierStore.Advance`，但必须保留相同的原子 pin 与决策语义。

Terminal state 应保留到全部 participant 与 operator 都已观察到决策，再显式 prune。Prune 不会删除
`Waiting` 或 `Apply` state，同时会清理遗留 temporary file 与 lock sidecar：

```csharp
var pruned = barrierStore.PruneCompleted(TimeSpan.FromDays(7), stoppingToken);
Console.WriteLine($"Removed {pruned.RemovedBarrierCount} terminal barriers.");
```

Distributed store 一旦返回 `Commit`，后续本地 journal 或流量恢复失败时，不得只回滚该进程而让 peer
保持 committed。Lunil 会返回本地失败并保留已发布 generation；应继续隔离该 target，修复 journal 或
router 后才能重新接流量。`LuaPatchRingCommitResult.DistributedBarrier` 提供最近一次观察到的固定
membership、选中 quorum、acknowledgement、deadline、decision 与诊断 message。

## 5. 恢复持久部署 Journal

`LuaPatchFileJournal` 写入 canonical NDJSON record，使用连续 sequence 和 SHA-256 hash chain。每次
append 使用一次 record write，并在返回前完成 write-through I/O 与 stable-storage flush。Reader 会拒绝
torn record、非 canonical JSON、断裂的 sequence/hash link、非法 transaction phase transition、事务期间
发生变化的 metadata，以及超过 byte、line 或 entry 上限的内容。Transaction phase 依次为 `Started`、
`Prepared`、`Publishing`、可选的 `Restoring`，最终进入 committed、rolled-back、failed 或 recovered
terminal phase。`Restoring` 表示 module publication 与 replay acceptance 已完成，但 target 流量恢复
仍在进行。

首次执行 `Append`、`RecoverIncomplete` 或 `Compact` mutation 时，会在 `<journal>.writer.lock` 获取
OS 强制的 writer lock，并一直持有到 journal 被 dispose。竞争 writer 会收到
`LuaPatchJournalErrorCode.WriterUnavailable`；独立 `ReadAll` 在 owner append 或替换 active file 时仍可
并发使用。Reader 会在 `ConcurrentReadTimeout` 内重试短暂的 partial tail 或 replacement sharing
conflict，超时后才报告 corruption 或 I/O failure。所有 Lunil writer 都遵循该 lock，但 sidecar 不能阻止
无关代码直接改写 NDJSON。部署服务应在整个 writer 生命周期内保留 owner，并在把 ownership 交给其他
进程前 dispose。

可在不丢失未完成 transaction 的前提下压缩 completed history：

```csharp
using var journal = new LuaPatchFileJournal(
    "state/hot-update/deploy.ndjson",
    new LuaPatchFileJournalOptions
    {
        AutomaticCompaction = new LuaPatchJournalCompactionOptions
        {
            RetainCompletedTransactions = 1_024,
        },
    });

var result = journal.Compact(new LuaPatchJournalCompactionOptions
{
    RetainCompletedTransactions = 1_024,
});
AnchorPreviousChain(result.OriginalTailHash);
```

Compaction 会保留每个 incomplete transaction 的全部 phase，以及指定数量的最新 completed
transaction，然后重新编号并计算 retained record 的 hash chain。实现先写入同目录 temporary file，
flush 后再原子替换 active file；Unix 还会 flush 所在目录，Windows 的 managed durability 边界为已
flush 的文件加 `File.Replace`，因此对掉电保证有要求时应使用本地 journaled file system 与 storage
replication。`AutomaticCompaction` 默认关闭，只会在下一次 append 将超过 entry/byte limit 时按配置
执行。需要超出 retention 生命周期保存的 record 必须先导出；若旧 chain 仍需独立审计，还应在外部
锚定 `OriginalTailHash`。

Hash chain 可以检测意外损坏和未锚定的局部改写，但不能认证拥有整文件重写能力的攻击者。Journal 与
lock sidecar 都应使用适当的 OS 文件权限；若威胁模型包含恶意 storage 修改，还应在外部锚定或复制
terminal record。

进程重启后，检查最后 durable phase 为 `Started`、`Prepared`、`Publishing` 或 `Restoring` 的
transaction，结合 host 自己的持久部署状态和 routing 状态核对 target，并记录恢复结论：

```csharp
using var journal = new LuaPatchFileJournal("state/hot-update/deploy.ndjson");
var pending = journal.GetIncompleteTransactions();
var recovered = journal.RecoverIncomplete(recoveryHandler);
```

`ILuaPatchCrashRecoveryHandler` 为每个未完成事务返回 `Committed`、`RolledBack` 或 `Manual`。Lunil
把已解决结果记录为 `RecoveredCommitted` 或 `RecoveredRolledBack`；`Manual` 会保持未完成，以便后续
再次核对。Journal 记录的是部署意图与处理结论，不会序列化 Lua heap、挂起 frame、CLR object 或外部
resource state。Handler 必须从应用自己的 durable state 判断 authoritative outcome，或先完成恢复，再
返回 terminal resolution。

## 预期结果

每个 target 要么发布完整 prepared generation，要么继续使用旧 generation。Preparation、commit、
health 或 recovery 失败均通过明确的 status、journal 与 telemetry 契约保持可观测。准确类型、默认值、
限制和 CLI option 见[签名 Patch Bundle reference](signed-patch-bundles.zh-CN.pub.md)。
