# Backend cache contract（v1）

Lunil 的持久后端缓存使用 `LuaBackendCacheKey` 计算内容地址。cache key descriptor 是固定字段
顺序的 UTF-8 JSON，`CacheId` 是 descriptor 的小写 SHA-256。字符串标识在进入 descriptor 前
会 trim；TFM、RID 与 feature 名称会转为小写；feature 和依赖 hash 会排序、去重。因此相同输入
不受枚举顺序、大小写或进程影响。

## 完整键空间

每个条目必须包含：

- artifact kind：canonical IR、persisted CIL 或 owner-free profile；
- 原始 source/chunk 内容 SHA-256、canonical module SHA-256、排序依赖集合 SHA-256、source
  binding id；
- cache key schema、canonical IR、Runtime ABI、codegen、profile schema、artifact schema 和
  compiler package version；
- optimization、debug symbols、exact-hook mode 与 sandbox mode；
- TFM、RID、portable/CoreCLR/ReadyToRun/NativeAOT deployment mode、trimming mode；
- 排序后的后端 feature set。

任一维度变化都会产生不同的 `CacheId`。`GetCompatibility` 同时报告所有不匹配维度，调用方
必须把任何非 `None` 结果视为 cache miss，不得尝试猜测迁移或加载 payload。

当前 Runtime code-generation ABI 为 v2。ABI v1 descriptor、persisted CIL manifest 和 profile
payload 均按不兼容处理；v1 Runtime facade 的保留不代表 loader 支持向下协商。

## 持久化边界

v1 只允许以下数据持久化：

1. 已验证的 canonical IR；
2. 带 manifest/checksum 的 persisted CIL artifact；
3. 不含 `LuaState`、closure、table 或 CLR delegate owner 的 profile 数据。

Tier 1、Tier 2 和 Loop OSR 的动态机器码始终只存在于当前进程的有界 LRU code cache；它们
不属于 `LuaBackendCacheArtifactKind`，不得写入磁盘。

## Versioned profile

`LuaJitExecutor.ExportProfile(module)` 输出 `LUNIL-JIT-PROFILE` v1 payload；
`ImportProfile(module, payload)` 返回显式 `Imported`、`Rejected`、`Incompatible` 或 `Disabled`
状态。payload 包含 profile schema、IR v3、Runtime ABI、JIT codegen version、canonical module
content ID、function/site opcode identity、value-kind counters、table shape signatures 与 call-target
signatures，并以独立 footer SHA-256 覆盖完整 core。

import 会验证 module/function/PC/opcode、所有计数范围、bounded signature 集合、Lua/native target
identity 和 sample totals。通过后只合并整数、枚举、hash 与字符串；不会保留 `LuaIrModule`、
`LuaState`、table、closure、delegate 或生成代码。已导入的 function-entry/site-0 hotness 可预热
Tier 1/Tier 2 阈值，但 Tier 1/Tier 2/OSR 仍在本进程重新生成，profile payload 不包含 IL delegate
或机器码。

示例：

```csharp
using var training = new LuaJitExecutor(LuaJitExecutorOptions.Default with
{
    Policy = LuaJitPolicy.PreferJit,
});
byte[] payload = training.ExportProfile(module);

using var warmed = new LuaJitExecutor(LuaJitExecutorOptions.Default with
{
    Policy = LuaJitPolicy.Auto,
});
LuaJitProfileImportResult result = warmed.ImportProfile(module, payload);
```

release 默认 policy 是 `Auto`，同时默认设置 `EnableTier2=true`，因此动态代码可用的 CoreCLR
宿主可直接导入 profile。导入数据只能提前满足 hotness：自动 Tier 2 仍会重新检查 profile，只有可
保证生成 `ExactNumericSpecializedCil` 且未观察到 table/closure/call/upvalue/to-be-closed managed
semantic site 的函数才会晋升。`EnableTier2=false` 会让导入返回 `Disabled`；动态代码不可用时也不会
合并 profile 或采集运行时反馈。

`EnableTier2ManagedFallback` 默认是 `false`。宿主只有显式将其设为 `true`，才允许导入的 profile
进入 `ManagedProfileProgram`；自动默认路径在安装方法时还会再次校验 code kind，不能被自定义
compiler 或陈旧 profile 绕过。

Loop OSR 不使用持久 profile 决定 code shape。`EnableLoopOsr` 默认是 `true`；启用后先在当前进程
累计 verified backedge，达到阈值才执行 natural-loop/static eligibility。可保证生成
`GuardedExactNumericCil` 的 loop 还必须在 interpreter 中成功观察全部 exact-numeric guard site，才会
自动进入队列；一次非数值 operand 会以 `JIT3105` 永久拒绝。`EnableLoopOsrManagedFallback` 默认是
`false`，managed canonical loop 必须由宿主再次显式开启。static/runtime eligibility、guard-site
observation、资格接受后的惰性 emitter preparation、guard-failure widening、backedge counters、
生成 delegate 与 code-kind 安装状态均不持久化，因此不能由导入 profile 或磁盘 cache 绕过。
`EnableLoopOsr=false` 会完整关闭 backedge 驱动的分析、运行时资格、emitter preparation 与编译。

profile 只可以提前满足 hotness。`Auto` 仍会基于 verified function facts 检查 direct coverage、
slow-path/semantic-boundary density、backedge/reuse 和 estimated code bytes；profile 不得绕过该
最低收益资格。

## In-process verified plan cache

Tier 1 planning 另有不落盘的 owner-scoped weak cache。key 是 canonical module 对象、function id
和 instruction-observation mode；显式自定义 `CilPlanLimits` 的调用不进入共享缓存。register liveness
也按 module owner/function 使用 weak cache，在 Tier 1、Tier 2、Loop OSR 与 persisted AOT planning
之间复用；Loop OSR 的 natural-loop plan、static/runtime eligibility、guard-site map 和生成方法仍只
保存在 registry entry 中。cache value
只保存 verified plan/diagnostic，不保存 `LuaState`、closure、table 或 delegate。module owner 被
回收后，`ConditionalWeakTable` entry 及 plan 一并可回收。并发首次访问在 owner lock 下只构建一次；
cache hit 的 planning durations 为零，避免把复用结果误计为新的编译工作。planning、liveness、
method-plan verification 任一阶段收到 cancellation 时立即放弃当前 builder，取消结果和半成品不得
写入 owner cache；后续未取消调用必须重新完成 verification 后才能发布 plan。

## 兼容与损坏策略

- schema/ABI/version/flag/target 不匹配：安全 miss；
- descriptor 非法、路径 `CacheId` 与 descriptor hash 不同、payload checksum 不匹配或文件
  截断：隔离损坏条目后安全 miss；
- cache miss 后由可信本机构建重新生成；无法生成时继续使用 interpreter；
- cache 的存在、缺失或损坏不得改变 Lua values、signal、side effects、traceback、hook、GC、
  close 或 coroutine 语义。

## Disk storage contract

`LuaBackendDiskCache` 的 v1 layout 为：

```text
<root>/
  entries-v1/<cache-id-prefix>/<cache-id>/
    descriptor.json
    manifest.json
    payload.bin
    access
  locks/<cache-id>.lock
  locks/quota.lock
  tmp/<cache-id>.<nonce>/
  quarantine/<timestamp>-<cache-id>-<nonce>/
```

- writer 在同一 volume 的 `tmp` 中写完 descriptor、payload、manifest 和 access marker，对每个
  文件执行 durable flush，最后用 directory rename 一次提交；reader 不观察临时目录；
- 每个 cache id 使用 `FileShare.None` lock file，quota 清理使用独立 global lock；entry lock
  总是在 quota lock 之前释放，避免 writer/trim lock inversion；
- manifest 保存 descriptor/payload length 与 SHA-256。路径 id、descriptor hash、manifest id、
  compatibility 和 payload checksum 全部验证后才返回 hit；
- 非法 JSON、截断、篡改、缺失 marker 或同 key 不同 payload 会先移动到 `quarantine`，然后
  返回安全 miss/写入可信替代项；
- `access` timestamp 是 LRU 顺序。trim 在 global lock 下按最旧访问时间删除，直到 entry 总字节
  不超过 `MaximumBytes`；quarantine 使用独立上限，过期 `tmp` 目录同时清理；
- lock timeout、权限或 I/O 故障返回 `CACHE1001` fail-soft 结果，不向执行语义传播；超过单条目
  或总 quota 的写入以 `CACHE1004` 拒绝。

`Lunil.Build` 默认启用共享 cache，总 entry quota 为 1 GiB、单 entry 为 256 MiB、quarantine
为 64 MiB。默认 root 是 `LocalApplicationData/Lunil/backend-cache`；没有可用的 local app-data
目录时回退到 `~/.cache`，最后回退临时目录。`LunilCacheEnabled=false` 可完全绕过共享 cache，
`LunilCacheDirectory` 和三个 quota property 可由项目或 CI 覆盖。
