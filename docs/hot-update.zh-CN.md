# 签名 Patch Bundle

[English](hot-update.md)

Lunil Patch Bundle 用于封装 Lua module 替换内容，并在 host 发布前完成验证。带版本的 canonical
manifest 记录目标 build、base/target revision、Lua 语言版本、runtime ABI、channel、过期时间、
nonce、依赖以及 payload 的 SHA-256 identity。

## 信任与资源边界

`LuaPatchBundle.Read` 会验证每个 payload hash，并通过显式 `LuaPatchEcdsaTrustStore` 校验 ECDSA
P-256/SHA-256 签名。未受信 key、过期或非 canonical manifest、不安全路径、重复 module、缺少
required dependency、尾随数据及超过大小限制的 bundle 均会被拒绝。`LuaPatchAcceptancePolicy`
还会将已验证 bundle 绑定到当前 build、runtime ABI、revision、channel 和 host replay 记录。

## 依赖与编译预检

`LuaPatchDependencyPlan` 保证 required dependency 先于 dependent，并把循环依赖的 strongly
connected component 作为一个 preparation group。`LuaPatchPreflight.Analyze` 创建隔离 staging
host，校验 source、binary chunk 和由 host 解码的 canonical IR entry，不修改在线 `LuaHost`。

`LuaHost.PreparePatchAsync` 会在线程池中完成上述工作，随后短暂进入在线 host 的 execution
gate，为每个目标 module 捕获 expected revision。只有目标 module 均已加载、语言版本一致且每个
module 都使用可回滚的 cache policy 时，prepare 才会成功；prepare 阶段不会执行 candidate loader。

## 游戏循环 Update Window 与原子提交

在 tick 或 frame 之间打开 update window，并在同一线程提交 prepared patch：

```csharp
var preparation = await host.PreparePatchAsync(bundle, cancellationToken: stoppingToken);
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
会读取 closure slot 的新 generation。因此游戏可默认把 frame/tick 边界作为安全点，无需重写已经
挂起的 frame。

原子 patch commit 支持 `ReplaceCache` 和 `PatchExistingTable`。由于不透明 `Custom` cache callback
和 source-path override 的效果无法纳入 module transaction journal，prepare 阶段会拒绝它们。
Candidate Lua 代码仍可能产生 global、CLR、文件系统、网络或其他 host 可见副作用，这些副作用通常
无法回滚。因此，只要失败前执行过 candidate，即使目标 module 状态已经全部恢复，结果仍会把
`SideEffectsMayHaveOccurred` 设为 true。

Pause 与 cancellation 检查发生在 candidate loader 之间及各发布步骤之间；它们可以防止 half-commit，
但不会在一次 VM 调用中途抢占单个 loader。应同时配置常规 Lua instruction budget，以限制单个 loader
的工作量。在循环依赖 component 内，member 按名称确定性执行：已完成 member 会暂存为新版本，指向
尚未执行 member 的 back-edge 则看到其旧 loaded value。

## 状态 Schema 与异步资源迁移

Bundle 可以携带一个名称固定为 `migration/schema.json` 的签名 companion entry。该 canonical
JSON 文档标识 state schema、base/target version，以及按 module 确定性排列的 state/resource
rule。Prepare 前先注册在线 schema version：

```csharp
host.SetPatchStateSchemaVersion("game-state", "42");
```

在执行 `lunil patch pack` 前，把 `LuaPatchMigrationSchemaSerializer.Serialize` 返回的 canonical
bytes 放到该 companion entry path。Prepare 时提供 schema 引用的全部 adapter：

```csharp
var preparation = await host.PreparePatchAsync(bundle, new LuaPatchPrepareOptions
{
    StateMigrationAdapters = stateAdapters,
    ResourceMigrationAdapters = resourceAdapters,
}, stoppingToken);
```

Schema base version 与 host 值不一致时 prepare 会失败；commit 会把它和 module revision 一起
重新检查，并且只在 module transaction 成功后切换到 schema target version。

State path 使用 RFC 6901 JSON Pointer escaping，定位 module cache 以下以字符串为 key 的 table。
`Preserve` 把旧值复制到 candidate，`Drop` 删除 candidate 值，`HostAdapter` 则把 table、userdata
payload 或其他 host 自定义转换交给指定的 `ILuaPatchStateMigrationAdapter`。Adapter operation
必须实现带 journal 的 `Apply` 与 `Rollback`；commit engine 也会记录每次 path 写入，因此后续
module 失败时会同时恢复 Lua graph 与 host payload 变更。

Resource rule 覆盖 `Coroutine`、`Timer`、`EventSubscription` 和 `Task`，disposition 包括
`Continue`、`Cancel`、`Restart`、`Drain` 与 `RejectIfActive`。对于 runtime-owned coroutine，
`Continue` 会把旧 thread 安装到 candidate 的相同 state path，保留 thread identity 与当前挂起点；
`RejectIfActive` 会拒绝该路径上的非终止 thread。可逆 cancel/restart/drain，以及全部 host-owned
timer、subscription 和 task 生命周期变更，使用指定的 `ILuaPatchResourceMigrationAdapter`；
缺少 adapter 会在进入 update window 前使 prepare 失败。
Adapter 的 `Prepare` 不得修改状态；`Apply` 必须能由 `Rollback` 完整逆转，operation dispose 只能
释放 journal 资源。

兼容 closure slot 与 loader upvalue 仍会自动迁移：lexical identity 和 upvalue layout 匹配时发布
successor generation，而挂起 frame 继续持有旧 immutable generation。State rule 会在 candidate
暂存给 dependent 之前应用，因此依赖顺序中后续 module 可以看到已经 preserve 的状态。

## 多 State Barrier 与 Ring 灰度

`LuaPatchCoordinator` 在单进程内协调多个 `LuaHost` state。Barrier ring 中的 target id 与 host
instance 必须唯一，并且每个 target 都必须由同一份 canonical patch manifest prepare。Coordinator
会先打开全部 update window，再 prepare 任一 commit session；全部 state prepare 成功后才发布完整
ring。Window 获取、prepare、publish、finalize 或 health gate 任一步失败，都会回滚该 ring 的全部
participant。Coordinator operation 在进程范围内串行化，避免不同 coordinator instance 形成冲突的
锁顺序。

每个 prepared patch 都绑定各自 host，可据此构造 rollout：

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
                new("zone-canary", canaryHost, canaryPreparation.PreparedPatch!),
            ],
        },
        new LuaPatchRolloutRing
        {
            Name = "production",
            Targets =
            [
                new("zone-01", zone01Host, zone01Preparation.PreparedPatch!),
                new("zone-02", zone02Host, zone02Preparation.PreparedPatch!),
            ],
        },
    ],
};

var result = new LuaPatchCoordinator().Deploy(plan, new LuaPatchCoordinatorOptions
{
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

## 持久部署 Journal 与恢复

`LuaPatchFileJournal` 写入 canonical NDJSON record，使用连续 sequence 和 SHA-256 hash chain。每次
append 使用一次 record write，并在返回前完成 write-through I/O 与 stable-storage flush。Reader 会拒绝
torn record、非 canonical JSON、断裂的 sequence/hash link、非法 transaction phase transition、事务期间
发生变化的 metadata，以及超过 byte、line 或 entry 上限的内容。Transaction phase 依次为 `Started`、
`Prepared`、`Publishing`，最终进入 committed、rolled-back、failed 或 recovered terminal phase。

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

进程重启后，检查最后 durable phase 为 `Started`、`Prepared` 或 `Publishing` 的 transaction，结合
host 自己的持久部署状态核对 target，并记录恢复结论：

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

## 资源预算与可观测性

Bundle 解码完成后，已验证输入仍受硬上限约束。`LuaPatchPrepareOptions.ResourceLimits` 与
`LuaPatchCoordinatorOptions.ResourceLimits` 接受 `LuaPatchResourceLimits`。默认最多允许 512 个 patch
module、1 MiB migration schema、512 个 migration module、8,192 条 state rule、8,192 条 resource
rule、16 个 ring、每 ring 256 个 target，以及每 rollout 1,024 个 target。超过限制会在 candidate
执行或 update-window 获取之前抛出 `LuaPatchResourceLimitException`。Bundle byte/entry limit、
migration limit、update-window/commit pause deadline、journal byte/line/entry limit 与常规 Lua
execution budget 是相互独立的防线；提高其中一个不会关闭其他限制。

热更新诊断使用稳定的 `LuaPatchTelemetry.ActivitySourceName` 与 `LuaPatchTelemetry.MeterName`，两者
均为 `Lunil.Hosting.HotUpdate`。Activity 名称包括：

- `lunil.patch.prepare`
- `lunil.patch.commit`
- `lunil.patch.ring`
- `lunil.patch.rollout`
- `lunil.patch.recover`

Activity 可以携带 patch、rollout、ring、transaction、module-count、target-count、status 和 error
tag。Metric 只使用低基数 status tag，不包含 target id、patch payload 或 source text。Instrument
包括 `lunil.patch.preparations`、`lunil.patch.commits`、`lunil.patch.rings`、
`lunil.patch.rollbacks`、`lunil.patch.recoveries`、`lunil.patch.prepare.duration`、
`lunil.patch.commit.pause.duration` 与 `lunil.patch.ring.duration`，duration 单位为毫秒。可通过标准
.NET `ActivityListener`/OpenTelemetry 与 `MeterListener`/OpenTelemetry metrics pipeline 订阅。

## CLI

```text
lunil patch pack manifest.json payload --output update.lpatch --private-key private.pem --key-id release-2026
lunil patch verify update.lpatch --public-key public.pem --key-id release-2026
lunil patch inspect update.lpatch --public-key public.pem --key-id release-2026
lunil patch dry-run update.lpatch --public-key public.pem --key-id release-2026
lunil patch diff base.lpatch update.lpatch --public-key public.pem --key-id release-2026
```

只有 `pack` 读取私钥；verify 和 preflight 使用公钥。CLI 不负责下载 patch、管理 CDN 或保存签名密钥。
