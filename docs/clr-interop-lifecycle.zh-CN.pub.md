# CLR bridge 生命周期原理

[English](clr-interop-lifecycle.pub.md)

Lunil 把 CLR 访问视为明确的 Host 边界，而不是不受限制的 reflection。本页解释 allowlist、state
ownership、callback admission 和 generation fencing 为何属于同一个生命周期模型。配置步骤与精确
API 契约分别见 [指南](clr-interop.zh-CN.pub.md) 和 [参考](clr-interop-reference.zh-CN.pub.md)。

## Capability 与 allowlist 分层

Capability 回答哪一类 operation 可以发生；allowlist 回答哪个应用 identity 可以参与。同时要求
两者，可防止启用 construction、member access、delegate、event 或 timer 时意外暴露所有已加载
type。只搜索已加载 assembly，也让 assembly loading 仍由应用控制。

完全限定的 member 名可减少未来的权限扩张：添加第二个 allowlisted type 时，现有裸 member entry
不会意外应用到它。`MaximumCachedMembers` 等资源上限会让整次查询失败，而不是截断
candidate，因而保留确定性 overload 选择。

## 转换与异步边界

Overload 选择为受支持的 Lua-to-CLR 转换分配稳定成本，并以 ordinal 签名打破平局。无法保留
声明 CLR 契约的值会明确失败；例如，超过 `long.MaxValue` 的 `ulong` 不会静默变成无关
userdata。

`clr.await` 有意保持同步语义，但会拒绝在带 `SynchronizationContext` 的 thread 上阻塞未完成
task。该边界避免单线程游戏循环死锁。异步 Host 应通过 scheduler 消费 `LuaClrTask.Task`，再在
Host 明确控制的边界恢复 Lua。

## 每个 state 只有一个执行 owner

CLR userdata、callback、task、subscription 和 timer 都保留创建它们的 `LuaState`。Callback 只能经由
interpreter 与 JIT 共用的 per-state 执行边界进入。`AnyThreadWhenIdle` 允许非 owner thread 原子占用
idle state，但不允许并发进入、重入 busy state 或经 CLR callback yield。

Timer 通过完全不使用 worker thread 遵循同一规则。Host 在 state idle 时轮询 timer，使调度成本与
callback 进入都成为游戏循环的明确预算。

## 热更新期间的 generation fencing

Module frame 创建 delegate、subscription、task 或 timer 时，resource 会关联到对应 module generation。
Patch preparation 会 quiesce 旧 generation resource，并让 candidate resource 保持 pending。发布只激活
candidate；execution、migration、barrier 或 health rollback 会拒绝 candidate，并仅恢复旧 generation。

该事务防止 callback 或 task result 进入 state 契约已被替换的 code。`clr.await` 会在等待前与结果
转换前分别检查 admission。Candidate loader 可等待自己的 staged task，但其他 inactive consumer
fail closed。底层 CLR task 不会被取消，因为 generation admission 与应用工作取消是两个不同问题。

Event handler 会随同一发布事务 detach 与 reattach。Timer 在 quiesced 时保留 remaining delay。签名
`Timer + Continue` migration 会把 remaining delay 与 counter 转移给同一 state path 的 candidate timer，使下一个
tick 使用 candidate code 与 policy。

## Stable resource identity 与 lease

Native 或 Host resource 常需跨越 Lua module generation，但不应产生两个 owner。
`LuaPatchStableResourceHandle` 把稳定 identity 与 generation-specific userdata placeholder 分离。
`HostResource + Continue` rule 会转移 identity，而不是构造第二个 native object。

Lease 让进行中的 Host 工作在 handle 关闭过程中仍然有效。Dispose 会拒绝新 access，再等待最后一个
member-call、event-subscription 或显式 Host lease 结束，然后释放 owned resource。Non-owning handle 只
关闭 access，不释放应用 object。这使一个 identity、一个 owner 与 rollback-safe admission 能够跨越
patch generation。
