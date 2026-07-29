# 签名 Patch Bundle 参考

[English](signed-patch-bundles.pub.md)

本参考页定义 Lunil 的签名 patch manifest、信任与 replay 契约、preparation 与 commit resource、migration
rule、rollout state、持久 store 和 telemetry。操作步骤见
[部署签名 Patch Bundle](deploy-signed-patch-bundles.zh-CN.pub.md)，transaction 与 durability model 见
[热更新发布原理](signed-patch-publication.zh-CN.pub.md)。

## Canonical Manifest 与 Target Admission

版本化 manifest 记录 target build、base/target revision、`updateIntent`、请求的 `requiredCapabilities`、Lua
language version、runtime ABI、channel、expiry、nonce、`requiredTargetLabels`、dependency、entry identity
和 SHA-256 payload hash。

| 字段或 identity | 验证 |
| --- | --- |
| Capability | 区分大小写、trimmed、唯一，签名前排序，并受 count/name-byte 上限约束。它们只是 admission claim，不授予 runtime permission。 |
| Target label | 区分大小写的唯一 name/value pair，签名前排序，并以 exact-match 逻辑与计算。 |
| Update intent | 必须与 Host release ledger 的 forward/rollback 分类一致。 |
| Rollback | 同时需要已验证 signer identity 和 `RollbackAuthorizer` 肯定决策。 |
| Expiry | 验证时检查，并在 candidate 构造前再次检查，包括 coordinated commit。 |
| Path 与 entry | 拒绝不安全 path、重复 module、缺少必需 dependency、trailing data 和配置的 size-limit violation。 |

`LuaPatchAcceptancePolicy` 把已验证 bundle 绑定到当前 build、runtime ABI、revision、channel、intent、
已授予 admission capability、target label、signer、expiry 和 replay record。

## Trust-store 契约

`LuaPatchBundle.Read` 使用显式 `LuaPatchEcdsaTrustStore` 验证每个 payload hash 与 ECDSA P-256/SHA-256
signature。Trusted key 可有 `ValidFrom`、exclusive `ValidUntil` 和独立 `RevokedAt`。Revocation 优先；失败码为
`SigningKeyRevoked`、`SigningKeyNotYetValid` 或 `SigningKeyExpired`。验证使用同一份 `UtcNow`
snapshot 执行 lifecycle 与 signature check。

CLI trust-store schema 为 `lunil.patch-trust.v1`。它拒绝未知 property、重复 key id、malformed 或非 P-256
key、空 validity window 以及超过 1,024 个 key。Public-key path 相对 trust-store 文件解析。Private key
只由 `patch pack` 读取。

## Replay Reservation State

Preparation 必须同时提供 `AcceptancePolicy`、`ReplayStore` 和 `ReplayScope`。Scope 是稳定 deployment-target
id。`ILuaPatchReplayStore.TryReserve` 创建或返回幂等的 uncommitted reservation。
`TryAcquireCommit` 授予一个 exclusive commit lease；未完成 lease 被释放时保持 reservation 可重试，
`Complete` 使其 terminal，rollback compensation 使用 `Reopen`。

`LuaPatchFileReplayStore` 在有界 inter-process writer lock 和 per-reservation lock 下，追加 canonical、
SHA-256 chained `Reserved`、`Committed` 和 `Reopened` NDJSON event。Corruption、truncated tail、lock timeout
与 identity/entry/byte-limit violation 会 fail closed。Event 不会自动 compact，因为删除 terminal identity 会重新
打开 replay。

## Preparation

`LuaPatchDependencyPlan` 使 dependency 先于 dependent，并把 cyclic strongly connected component 视为一个 group。
`LuaPatchPreflight.Analyze` 在隔离 staging Host 中验证 source、binary chunk 和 Host-decoded canonical IR。
`PreparePatchAsync` 还会在 live execution gate 下捕获预期 module revision；target module 必须已加载，
language version 必须匹配，cache policy 必须可 rollback。Candidate loader 不会执行。

`LuaPatchPreparationLimiter` 限制 active preflight 与 queued caller。Queue 为零时 fail-fast。Wait timeout 可为零、
不超过 `Int32.MaxValue` 毫秒的有限值，或 `Timeout.InfiniteTimeSpan`。Saturation 或 timeout 会在
preflight、live binding 或 replay reservation 之前返回 `Deferred`。`ActiveCount` 与 `QueuedCount` 是 gauge。

## Result Status Enum

### Preparation

| `LuaPatchPrepareStatus` | 含义 |
| --- | --- |
| `Ready` | 隔离 candidate 已绑定预期 live revision，可以 commit。 |
| `PreflightFailed` | 隔离 parsing、verification、compilation 或 dependency preflight 失败。 |
| `LanguageVersionMismatch` | Target module 与 replacement 使用不同 Lua language version。 |
| `ModuleNotLoaded` | Live Host 中没有 target module。 |
| `UnsupportedCachePolicy` | Target cache policy 无法参与 rollback-safe publication。 |
| `MigrationAdapterMissing` | Migration schema 命名了不可用的必需 adapter。 |
| `StateSchemaVersionMismatch` | Live schema version 与签名 base version 不同。 |
| `AcceptanceRejected` | Trust、policy、target、intent、expiry、signer 或 replay acceptance 拒绝 bundle。 |
| `Deferred` | Candidate 工作开始前，preparation admission 已饱和或超时。 |
| `JitWarmupFailed` | Required-success candidate JIT warmup 未成功完成。 |

| `LuaPatchPreparationAdmissionStatus` | 含义 |
| --- | --- |
| `NotConfigured` | 未配置共享 preparation limiter。 |
| `Acquired` | Caller 已获得 preparation slot。 |
| `Saturated` | Limiter queue 已无容量。 |
| `TimedOut` | Caller 未在 wait timeout 内获得 slot。 |

### Update Window 与 Module Commit

| `LuaPatchUpdateWindowStatus` | 含义 |
| --- | --- |
| `Opened` | Same-thread update window 已持有 Host execution gate。 |
| `Deferred` | 未在配置 wait budget 内获得 gate。 |
| `Cancelled` | Window acquisition 观测到 cancellation。 |

| `LuaPatchCommitStatus` | 含义 |
| --- | --- |
| `Committed` | 所有 target-module change 已发布。 |
| `Deferred` | Commit 无法在当前 safe-point budget 内继续。 |
| `Cancelled` | Commit 观测到 cancellation，并保留或恢复旧 graph。 |
| `RevisionConflict` | Live target revision 不再与 prepared revision 一致。 |
| `ExecutionFailed` | Candidate loader 失败。 |
| `MigrationFailed` | State 或 resource migration 失败。 |
| `CachePolicyFailed` | Cache publication 或 table-patch policy 失败。 |
| `PublicationFailed` | 最终 managed-graph publication 失败。 |
| `BarrierAborted` | Coordinated barrier 中止本地 publication session。 |
| `Expired` | 签名 manifest 在 candidate 执行前过期。 |
| `ReplayRejected` | 持久 replay acceptance 或 commit ownership 拒绝 transaction。 |

| `LuaPatchModuleCommitStatus` | 含义 |
| --- | --- |
| `NotExecuted` | Module loader 未运行。 |
| `RevisionConflict` | 该 module 的 live revision 在 preparation 后变化。 |
| `Executed` | Candidate loader 已完成，但 publication 尚未 terminal。 |
| `Committed` | 该 module 已发布。 |
| `ExecutionFailed` | 该 module 的 candidate loader 失败。 |
| `MigrationFailed` | 该 module 的 migration 失败。 |
| `CachePolicyFailed` | 该 module 的 cache publication policy 失败。 |
| `RolledBack` | 已 staged 或 published module 被恢复到旧 graph。 |

### Ring 与 Target Lifecycle

| `LuaPatchRingCommitStatus` | 含义 |
| --- | --- |
| `Committed` | 完整 ring 通过 publication 与所有已配置 gate。 |
| `Deferred` | Ring 或 distributed participant 尚未被选中，或当前无法继续。 |
| `Cancelled` | Ring coordination 观测到 cancellation。 |
| `PrepareFailed` | 至少一个 local commit session 无法 prepare。 |
| `PublishFailed` | 至少一个 target 在 publication 中失败。 |
| `HealthRejected` | Application health gate 拒绝已发布 candidate ring。 |
| `JournalFailed` | 持久 deployment-journal mutation 失败。 |
| `ReplayFailed` | Replay acceptance 或 completion 失败。 |
| `IsolationFailed` | Target traffic isolation 失败或不可用。 |
| `QuiescenceFailed` | Target 进行中的工作未达到 quiescence。 |
| `RestoreFailed` | Target traffic restoration 失败；target 必须保持 isolated。 |
| `CoordinationFailed` | Local 或 distributed ring coordination 失败。 |
| `GenerationRejected` | Generation-retention snapshot 违反已配置 guard。 |

| `LuaPatchTargetLifecycleStatus` | 含义 |
| --- | --- |
| `NotConfigured` | 未配置 target lifecycle adapter。 |
| `Isolated` | 已停止新 target traffic。 |
| `Quiescent` | 现有 target work 已 drain。 |
| `Restored` | Commit 或 rollback 后已恢复 traffic routing。 |
| `IsolationDeferred` | Isolation 请求稍后重试。 |
| `IsolationCancelled` | Isolation 观测到 cancellation。 |
| `IsolationFailed` | Isolation 失败。 |
| `QuiescenceDeferred` | Quiescence 请求稍后重试。 |
| `QuiescenceCancelled` | Quiescence 观测到 cancellation。 |
| `QuiescenceFailed` | Quiescence 失败。 |
| `RestoreFailed` | Restoration 失败。 |

Adapter-level status 完整列表如下：`LuaPatchTargetIsolationStatus` 包含 `Isolated`、`Deferred`、
`Cancelled` 和 `Failed`；`LuaPatchTargetQuiescenceStatus` 包含 `Quiescent`、`Deferred`、`Cancelled`
和 `Failed`；`LuaPatchTargetRestoreStatus` 包含 `Restored` 和 `Failed`；`LuaPatchTargetRestoreOutcome`
包含 `Committed` 和 `RolledBack`。

## Commit 与 Cache Policy

Update window 持有 Host execution gate。Commit 重新检查 expiry 和每个预期 revision，经临时 `package.loaded`
overlay 按 dependency-first 顺序执行 candidate，并同时发布 cache value、module record、table-identity
patch、compatible closure slot 与 JIT generation。

受支持的原子 cache policy 为 `ReplaceCache` 和 `PatchExistingTable`。Opaque `Custom` callback 与 source-path
override 会在 preparation 被拒绝。Pause 与 cancellation check 发生在 loader 与 publication step 之间；单个
loader 由普通 Lua instruction budget 限制，不会在 VM call 中途抢占。Cyclic component member 按确定性名称顺序执行。

## State Migration Rule

可选 canonical companion entry 为 `migration/schema.json`。它命名 base/target schema version 与 per-module rule。
State path 使用 RFC 6901 JSON Pointer escaping，必须互不相交；重复与 ancestor/descendant pair 会被拒绝。

| State rule | 契约 |
| --- | --- |
| `Preserve` | 把旧值复制到 candidate。 |
| `Drop` | 删除 candidate 值。 |
| `PatchTable` | 保留旧 table identity，并用 candidate table 的 raw entry 与 metatable 替换内容；两个值必须都是 table。 |
| `HostAdapter` | 调用命名且可逆的 `ILuaPatchStateMigrationAdapter` 完成 Host-defined transformation。 |

Commit journal 在 publication 或 rollback 最终确定前 root 旧与 candidate key、value、metatable 和 detached
candidate table。Aggregate table journal entry 有上限。

## Resource Migration Rule

Resource kind 为 `Coroutine`、`Timer`、`EventSubscription`、`Task` 和 `HostResource`。Disposition 为
`Continue`、`Cancel`、`Restart`、`Drain` 和 `RejectIfActive`。

| 组合 | Runtime 行为 |
| --- | --- |
| `Coroutine + Continue` | 把旧 suspended thread 安装到 candidate path，保留 immutable 旧 activation 与 generation admission。 |
| `Timer + Continue` | 把 remaining delay 与 dispatch counter 转给 pending candidate timer；publication 后使用 candidate callback 与 policy。 |
| `HostResource + Continue` | 把旧 stable-resource userdata 安装到 candidate graph，保留 identity 与 ownership。 |
| `RejectIfActive` | 按具体 resource 拒绝 live coroutine、scheduled timer 或持有显式 lease 的 stable resource。 |
| `Cancel`/`Restart`/`Drain` | 应用自定义 external effect 需要命名且可逆的 resource adapter。 |

Adapter `Prepare` 不得修改 state；`Apply` 必须可由 `Rollback` 精确逆转。缺少 adapter 会使 preparation
失败。Stable-resource member call 与 subscription 持有 lease；owned resource 等待最后一个 lease 后释放。

## Generation Snapshot 与 Guard Policy

`LuaHost.CapturePatchGenerationSnapshot()` 报告 callback、task、timer 和 suspended native continuation 在
`Active`、`Pending`、`Quiesced` 与 `Stale` 状态的数量，以及 aggregate count、`HasTransitionResidue`、
`HasStaleResources`、`ObservedAt` 和 `UpdateInProgress`。Stale 表示仍被引用但被 generation admission
拒绝；它本身不能证明 memory leak。

`LuaPatchGenerationGuardPolicy` 提供 per-kind stale budget，默认拒绝 pending 或 quiesced residue。`Strict`
把 stale budget 全设为零。拒绝会返回 `GenerationRejected` 并 rollback 本地 ring target。Guard 不强制
collection、不取消 Host task、不关闭 external resource，也不撤销 candidate 的任意 side effect。

## Coordinator 与 Distributed Barrier

`LuaPatchCoordinator` 在进程范围串行 coordinator operation。每个 ring 需要唯一 target id 与 Host instance，
且必须从同一 canonical manifest prepare。它依次隔离 target、等待 quiescence、打开所有 update window、
prepare 每个 commit session、发布 ring、执行 health gate，然后恢复 traffic。从 isolation 到 health evaluation
的任一失败会 rollback 该 ring。Ring 按顺序执行；后续 ring 失败不会 rollback 已接受的早期 ring。

`ILuaPatchTargetLifecycle` 提供 `TryIsolate`、`WaitForQuiescence` 与幂等 `Restore`。
`RequireTargetIsolation` 在 journaling 之前拒绝缺失 adapter。`RestoreFailed` 会让 target 保持 isolated，
并让 journal 保持 `Restoring` 以便 recovery。

Distributed barrier 会锁定 rollout id、ring 名、participant membership、required quorum、canonical manifest
SHA-256、target revision 与 preparation/health deadline。Prepared acknowledgement 精确选出 quorum 并产生 `Apply`；
被选中 participant 在 local publication、application health 和 replay acceptance 后确认 `Healthy`。所有被选中者
healthy 时产生 immutable `Commit`；被选中者失败或 deadline 到期时产生 immutable `Rollback`。未选中进程
返回 `Deferred` 并保留旧 generation。

`LuaPatchFileDistributedBarrierStore` 要求 exclusive lock 和 atomic same-directory rename。其 SHA-256 用于发现
意外 corruption，不用于抵御 hostile rewriting。Terminal state 必须显式 prune；waiting 或 apply state 不会被 prune。

## Deployment Journal 与 History

`LuaPatchFileJournal` 存储 canonical、连续、SHA-256 chained NDJSON phase：`Started`、`Prepared`、
`Publishing`、可选 `Restoring`，以及 terminal committed、rolled-back、failed 或 recovered phase。Writer 在
整个生命期持有 `<journal>.writer.lock`。Reader 可并发执行，并在 `ConcurrentReadTimeout` 内重试短暂
replacement/tail condition。

Compaction 保留每个 incomplete transaction 与指定数量的最新 completed transaction，重新编号与计算 hash，
flush same-directory 临时文件，然后 atomic replace journal。`OriginalTailHash` 可以外部 anchor。Hash chain 是
corruption evidence，不是 authentication。

`RecoverIncomplete` 请求 `ILuaPatchCrashRecoveryHandler` 返回 `Committed`、`RolledBack` 或 `Manual`。
Journal 记录 deployment intent 与 resolution，不序列化 Lua heap、suspended frame、CLR object 或 external resource。

`LuaPatchHistory` 是容量 1 至 10,000 的有界 volatile health history。Snapshot 按旧到新排列，包含 total、
dropped、recording-failure、consecutive-unsuccessful、最新 committed/unsuccessful timestamp 与稳定
rollout/ring/target outcome 字段。它不包含 raw exception、message、module record、Lua value、payload 或 heap graph。

## JIT Warmup

`LuaPatchJitWarmupOptions` 可在 preparation 可选地把兼容 old-module profile remap 到 candidate function，并按
hotness 降序编译。其 `ExecutorOptions` 使用 `LuaHostJitWarmupOptions` 配置 per-module function、duration、
Tier 2 与 profiled-function limit。Warmup 不创建 closure、不运行 loader、不修改 live state，也不进入
update window。整个 patch 与 per-module function/duration budget 同时适用。

`BudgetLimited` 是成功但有界的 outcome；deadline 到期为 `TimedOut`。`BestEffort` 在 compilation/deadline
失败后保留 ready patch；`RequireSuccess` 返回 `JitWarmupFailed`。Interpreter 和 dynamic-code-disabled Host 返回
`NotApplicable`。已编译 code 使用现有 content-addressed JIT cache，并继续受
`LuaHostJitOptions.MaximumCodeCacheBytes` 与正常 eviction 约束。

## 默认资源上限

默认允许 512 个 patch module、1 MiB migration schema、512 个 migration module、8,192 条 state rule、
8,192 条 resource rule、65,536 条 aggregate table-patch journal entry、16 个 ring、每 ring 256 个 target，
以及每 rollout 1,024 个 target。Bundle、schema、ring 和 rollout violation 在 candidate 执行或 update-window acquisition
之前失败。其他 byte、journal、pause 与 Lua execution budget 互相独立。

## Telemetry

`LuaPatchTelemetry.ActivitySourceName` 与 `.MeterName` 均为 `Lunil.Hosting.HotUpdate`。

Activity：`lunil.patch.prepare`、`lunil.patch.commit`、`lunil.patch.ring`、
`lunil.patch.rollout` 和 `lunil.patch.recover`。

Metric：`lunil.patch.preparations`、`lunil.patch.commits`、`lunil.patch.rings`、
`lunil.patch.rollbacks`、`lunil.patch.recoveries`、`lunil.patch.prepare.duration`、
`lunil.patch.commit.pause.duration` 和 `lunil.patch.ring.duration`。Duration 单位为毫秒。Status tag 保持
low-cardinality，不包含 target id、payload 或 source text。

## CLI 契约

`patch` command group 包含 `pack`、`verify`、`inspect`、`dry-run` 与 `diff`。Trust-store schema 会
拒绝未知 property、重复 key id、格式错误或非 P-256 的 key、空有效期，以及超过 1,024 个 key 的
配置。只有 `pack` 读取私钥；verify 与 preflight 使用公钥。CLI 不负责下载 patch、管理 CDN 或保存
签名密钥。准确语法与 option 见 [CLI reference](cli.zh-CN.pub.md)，生产执行顺序见
[部署签名 Patch Bundle](deploy-signed-patch-bundles.zh-CN.pub.md)。
