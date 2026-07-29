# 热更新发布原理

[English](signed-patch-publication.pub.md)

Lunil 把昂贵的 preparation 与短暂、原子的 publication window 分离。本页解释签名 patch bundle 背后的
transaction、generation、migration、rollout 与 durability 模型。操作流程见
[部署指南](deploy-signed-patch-bundles.zh-CN.pub.md)，精确契约见
[patch 参考](signed-patch-bundles.zh-CN.pub.md)。

## 验证不等于授权

有效 signature 证明 bundle integrity 与 signer identity，但不授予 Lua、CLR、filesystem、network 或 deployment
permission。因此 acceptance 要把已验证 bundle 与当前 build、runtime ABI、revision ledger、channel、target
label、admission capability、rollback authorization、expiry 和 replay identity 比较。

Target label 把 bundle 绑定到控制面当前 environment、region、shard、platform 或 ring assignment。把它们视为
snapshot，可防止为一个 identity 准备的 patch 在 identity 变化后 commit。Key lifecycle check 使用 verifier
time，而不是 signer-controlled creation time，防止通过 backdating 绕过 retirement 或 revocation。

## Preparation 缩短 Publication Window

Dependency planning、parsing、compilation、chunk verification、migration-schema validation 和 JIT warmup 可消耗
CPU 与 memory。Lunil 在隔离 staging Host 中执行它们，然后只短暂进入 live execution gate 绑定预期
module revision。Preparation 不会执行 candidate loader。

这让 update window 只负责与 state 相关的工作：重新检查 expiry 与 revision、执行 candidate、migration、
publication、health decision 和 rollback。共享 preparation limiter 在昂贵工作或 replay reservation 之前施加
backpressure，让控制面用统一 jitter 重试，而不是同时过载所有 target。

## Publication 在受管 Module Graph 内原子化

Update window 在 candidate 经临时 module-cache overlay 按 dependency 顺序运行时排除普通 Host execution。已完成
dependency candidate 对后续 candidate 可见；cycle 中尚未解决的 back-edge 会看到旧 loaded value。Publication
同时切换 module record、cache value、table identity、compatible closure slot 与 JIT generation。

失败时，Lunil 恢复受管 module graph，包括 journaled table content、metatable、loader upvalue 与 compatible
closure slot。该原子边界不能通用地撤销 candidate 对应用服务、CLR object、filesystem 或 network 的调用。
因此，即使 Lua state 已恢复，失败 commit 仍可报告 `SideEffectsMayHaveOccurred`。

Suspended frame 保留进入时捕获的 immutable function generation。Publication 后的新 call 解析 successor
generation。可恢复 module-owned coroutine 会在日后重新进入，因此需要显式 generation admission；普通
in-flight frame 可在捕获的 generation 上完成，但不会成为新的 resumable work。

## Migration 保留被选中的 Identity

当 registry 或 external alias 依赖 object identity 时，复制 value 不够。`PatchTable` 保留旧 table object，并从
candidate 替换 entry 与 metatable；transaction 会 root 两个 graph，直到 rollback 不再可能。互不相交的 path
可避免 table rule 与 descendant 之间的 ownership 歧义。

Runtime resource 需要 lifecycle decision，而不是 value copy。Continue 的 coroutine 保留 suspended 旧 activation，但会被
admit 到 candidate graph。Continue 的 timer 把 delay 与 counter 转到 pending candidate timer，使后续 callback 使用
candidate code。Stable Host resource 转移一个 userdata identity 与 owner，lease 则保护进行中的应用工作。

Cancel、restart、drain 或 transformation 具有应用自定义 external effect 时需要 adapter。把不修改 state 的
`Prepare` 与可逆 `Apply`/`Rollback` 分开，使 module transaction 能 compensation 后续失败。

## Generation Fencing 关闭延迟进入竞态

Module code 创建的 callback、subscription、task、timer 和 suspended native continuation 属于该 module generation。
Preparation 会 quiesce 旧 resource，并让 candidate 保持 pending。完整 publication 会激活 candidate 并使旧
resource stale；rollback 会逆转该 admission decision。

这会防止旧 callback 或延迟 task result 进入新发布 state。Admission 在进入点检查；`clr.await` 还会在
result conversion 前再次检查。底层 Host task 不会自动取消，因为 Lua admission 与应用工作取消是不同契约。

Generation snapshot 区分 pending、quiesced 和 stale resource。Stale reference 不一定是 leak，但跨 patch 持续增长
表明应用 owner 未释放旧 resource。Guard budget 把该运维 policy 变为 acceptance 前的 rollback decision，但不
强制 collection，也不取消 external work。

## Ring 组合 Isolation、Publication 与 Health

Coordinator 停止新 traffic、等待 in-flight work drain、获取每个 target window、prepare 每个 commit session、
发布整个 ring、评估 application health 与 generation retention，然后恢复 traffic。在 decision 前保留 rollback
session，可使同一 ring 中所有 target 对齐。

Ring 按顺序执行，而不是一个全局 transaction。后续 production ring 失败时，已接受 canary 仍保持
committed。Restoration 是独立 lifecycle phase；publication 后 routing restoration 失败时，target 保持 isolated，
并必须在重新服务 traffic 前完成持久 recovery。

跨进程时，distributed barrier 先锁定 membership、quorum、manifest identity、revision 与 deadline。Prepared
quorum 收到 `Apply`；被选中进程保留 rollback session，直到每个被选中 participant 报告 healthy。最终
`Commit` 或 `Rollback` 是 immutable。Quorum 外进程保留旧 generation。Distributed `Commit` 产生后，单个
participant 不得单方 rollback generation，因为 peer 已经 committed。

## 三类持久 Store 解决不同问题

Replay store 防止同一 deployment target 重用 patch id 或 nonce，并提供跨 process restart 的 exclusive commit
ownership。Deployment journal 记录 rollout transaction 的 phase 与 outcome，使 crash 后能对账 routing 与应用
state。Distributed-barrier store 记录共享 multi-process decision。

它们的 hash chain 可发现 torn write 与意外 corruption，但不能验证可重写整个文件的 actor。操作系统权限、
lock-correct storage、atomic rename、稳定 flush behavior、replication 与外部 hash anchoring 仍是部署责任。

Journal 有意不序列化 Lua heap、CLR object、suspended frame 或 external resource。Crash recovery 必须查询应用自有
持久 state 与 routing state，再声明 transaction committed 或 rolled back。

## Warmup 与 History 都保持有界

Profile-remapped JIT warmup 可在不执行 candidate code 的前提下减少 publication 后 latency。只有 lexical
与 canonical 兼容 function 继承 observation，且 per-module 与 whole-patch budget 共同限制 compilation。Best-effort
warmup 以完整性换可用性；required-success warmup 以可用性换取充分预热的 candidate。

Rollout history 存储 terminal summary，不存储 live module record 或 Lua graph。这使 health endpoint 保持有界，
并防止 observability 延长 candidate lifetime。Durable audit 与 crash recovery 仍由 journal 负责。
