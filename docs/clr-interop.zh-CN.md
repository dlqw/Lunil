# CLR 互操作

Lunil 0.11 提供 opt-in、受能力控制的 CLR bridge。bridge 只搜索已经加载的 assembly，并且要求精确
allowlist；不会暴露不受限制的 reflection。默认 Host 行为不变，也不会安装 `clr` 全局表。

## 配置 Host

```csharp
var options = LuaHostOptions.Restricted with
{
    Clr = new LuaClrOptions
    {
        Capabilities = LuaClrCapabilities.TypeDiscovery |
            LuaClrCapabilities.Construction | LuaClrCapabilities.MemberAccess |
            LuaClrCapabilities.Async,
        AllowedAssemblyNames = ["Example.Contracts"],
        AllowedTypeNames = ["Example.Contracts.Point"],
        AllowedMemberNames = ["Value", "Translate"],
        InstallGlobalModule = true,
    },
};
using var host = new LuaHost(options);
var run = host.RunUtf8("local p=clr.new('Example.Contracts.Point', 1, 2); return p:Translate(3)");
```

assembly、type 和 member 名称均使用 ordinal、大小写敏感匹配。bridge 不会按名称加载 assembly。
需要 allowlist 的 capability 在列表为空时 fail closed。Restricted、NativeAOT、trimming 和
Deterministic Host 使用相同策略。

## Lua 模块

安装后，全局 `clr` 表提供：

- `clr.type(fullName)`：类型元数据和 public constructor 描述。
- `clr.new(fullName, ...)`：确定性的 constructor 选择和有 ownership 的 userdata。
- `clr.members(fullName)`：allowlist 内的 member 元数据。
- `clr.get(target, name [, index...])`、`clr.set(target, name, value)`：显式 member 访问。
- `clr.call(target, name, ...)`：method/operator 调用；第一个参数为 type 名称时调用 static member。
- `clr.on(target, event, callback)`：可释放的 event subscription。
- `clr.await(task)`：等待 `Task`/`ValueTask` userdata 并转换结果。
- `clr.cancellation()`、`clr.cancel(value)`：创建并触发 bridge-owned cancellation token source。
- `clr.timer(callback, dueMs [, periodMs [, policy [, maxCatchUp]]])`：创建由 Host 轮询的 timer。
- `clr.cancel_timer(timer)`：无需通用 disposal capability 即可取消 timer。
- `clr.dispose(value)`：幂等释放 bridge userdata 或 subscription。

构造出的 userdata 也可以通过普通 Lua indexing/call 使用 allowlist 内的 property、field、method、
indexer 和 CLR operator。method 查询会返回 bound function；`object.method(x)` 和
`object:method(x)` 都支持。

## 转换与 overload 规则

Candidate 经过参数数量、optional/default 参数和 host-side named argument 过滤后，选择总转换成本
最低者；并以参数类型签名的 ordinal 顺序打破平局。支持 nil 到 reference/nullable、boolean、
string/char、精确 enum 名称与 integer、全部 CLR 数值类型（含溢出检查）、由 Lua table 表示的
array 与 `ValueTuple`、`LuaValue`、兼容 CLR userdata 以及 primitive `object` fallback。不支持的值
返回稳定的 `NoMatchingConstructor` 或 `NoMatchingMember`。

带 `ref`/`out` 的 method 返回普通结果，再按参数顺序返回 ref/out 值。Task/ValueTask 结果包装为
`LuaClrTask` userdata，由 `clr.await` 消费。`LuaClrCancellation` userdata 转换为 `CancellationToken`；nil 映射为 `CancellationToken.None`。CLR exception 转换为 `LuaClrException`/可捕获 Lua error；
仅在适合公开异常消息时设置 `IncludeExceptionMessages`。

## Delegate 与 event

授予 `DelegateConversion`，并在 `AllowedDelegateTypeNames` 列出精确 delegate 类型名。
`LuaClrBridge.CreateDelegate` 会在创建前验证全部参数和返回类型。授予 `EventSubscription` 并在
`AllowedEventNames` 列出 event 名称；`Subscribe` 返回幂等的 `LuaClrSubscription`。subscription
userdata 会 root Lua callback，并在释放时解除订阅。callback 遵循 `ThreadPolicy` 和 state ownership，
从不支持的线程进入 busy state 或尝试 yield 时会 fail closed。

热更新发布会按 closure 所属 `LuaIrModule` 对 delegate 进行 generation fencing。旧 module
generation 的 delegate 会以 `SubscriptionClosed` fail closed；candidate loader 执行期间创建的
delegate 保持 pending，直到整个 patch barrier 发布。Candidate 失败或 ring health rollback 会拒绝
candidate delegate，并恢复旧 generation。Event subscription 使用同一事务：发布时解除旧 handler，
rollback 时重新订阅；失败 candidate 的 handler 会被解除。可通过 `LuaClrSubscription.IsActive`
检查生命周期，并将 `LuaClrBridge` 的 `ActiveCallbackCount`、`PendingCallbackCount`、
`QuiescedCallbackCount` 和 `StaleCallbackCount` 导出为 gauge。

Module frame 调用 CLR code 时创建的 `LuaClrTask` wrapper 使用同一 generation barrier。
`clr.await` 会在等待前和结果转换前分别检查 admission：发布后旧 task 变为 stale，candidate task
在发布前对外保持 pending，但所属 candidate loader 可在 staging 时等待自己的 task；rollback 只恢复
旧 generation。其他 inactive wrapper 以 `AsyncGenerationClosed` fail closed；底层 CLR `Task` 不会被自动取消。没有运行中 module frame
的 host-side CLR call 仍属于 host，不参与 fencing。Host integration 直接消费 `Task` 前应检查
`LuaClrTask.IsActive`，并监控 bridge 的 `ActiveTaskCount`、`PendingTaskCount`、
`QuiescedTaskCount` 与 `StaleTaskCount` gauge。

## 游戏循环 Timer

授予 `Timers` 后可创建 `LuaClrTimer`。Timer 不持有 worker，也不会从 thread pool callback 进入 Lua；
游戏主循环只在 state idle 时显式驱动：

```csharp
var options = LuaHostOptions.Restricted with
{
    Clr = new LuaClrOptions
    {
        Capabilities = LuaClrCapabilities.Timers,
        InstallGlobalModule = true,
        TimeProvider = TimeProvider.System,
        MaximumTimerCount = 4096,
        MaximumTimerDispatchCount = 256,
    },
};
using var host = new LuaHost(options);
host.RunUtf8("heartbeat=clr.timer(function(tick,missed) last_tick=tick; missed_ticks=missed end,0,50,'coalesce')");

while (running)
{
    host.DispatchClrTimers(256);
    RunGameFrame();
}
```

Callback 接收从 1 开始的 dispatch tick，以及本次省略的 elapsed tick 数。`skip` 从当前 poll 时刻
计算下一周期，`coalesce` 保持原始 phase 并报告省略数，`catch_up` 则逐个 dispatch 已经过期的 tick，
每次 poll 最多执行 `MaximumCatchUpTicks` 个。省略 `periodMs` 时为 one-shot。Timer 数量、单次 poll
dispatch 数、duration 与 catch-up 均有前置资源上限。调度使用配置的 `TimeProvider` monotonic
timestamp，wall-clock 校时不会移动 due tick。在 busy state 或非 owner thread dispatch 会
fail closed；callback 使用 Host 配置的 interpreter budget。

Module-owned timer 会加入 patch generation barrier。旧 timer 以 remaining delay 暂停，candidate timer
在 pending 阶段不调度；发布后只有 candidate active，execution/migration/barrier/health rollback 则只
恢复旧 timer。在结果确定前不能取消 quiesced timer。应同时监控 `LuaClrTimer.IsActive` 与
`ActiveTimerCount`、`PendingTimerCount`、`QuiescedTimerCount`、`StaleTimerCount`。签名 schema 中的
`Timer + Continue` 会把 remaining delay 与 counter 转给同一 state path 的 candidate timer，因此下一
tick 使用 candidate callback 与 scheduling policy。

## Ownership 与部署

`LuaClrObject` 默认拥有构造出的 `IDisposable` instance，并且最多转发一次 `Dispose`；Host 自己拥有
instance 时设置 `OwnConstructedObjects=false`。userdata、callback、subscription、task 和 timer 都
属于一个 `LuaState`，不能转移到其他 state。

当 Host 或 native object 必须跨 patch generation 保持唯一 identity 与唯一 owner 时，使用 stable
resource handle：

```csharp
var userdata = host.ClrBridge.CreateStableResource(
    "world-session",
    worldSession,
    ownsResource: true);
host.State.SetGlobal("world_session", LuaValue.FromUserdata(userdata));
```

启用 `MemberAccess` 时，resource 的精确 runtime type、assembly 及所访问 member 仍必须进入 allowlist。
Host 侧进行中的工作可调用 `LuaPatchStableResourceHandle.AcquireLease()`；CLR member call 会在本次
invocation 内自动持有 lease，event subscription 则持有到 unsubscribe。handle 被 Dispose 后拒绝新
access。owned `IDisposable` 或 `IAsyncDisposable` resource 会在最后一条 lease 关闭后释放；non-owning
handle 只关闭访问。userdata 始终绑定原来的 `LuaState`。

要在 patch 中延续该 identity，为其 module-cache path 声明 `HostResource + Continue` rule。candidate
module 应只在该 path 放置 placeholder，不得构造第二份 native resource。migration、rollback 与
`RejectIfActive` 行为参见[生产级热更新](hot-update.zh-CN.md#状态-schema-与异步资源迁移)。

Trimming 和 NativeAOT 应用必须通过 `DynamicDependency` 等 linker metadata 保留每个 allowlist type 的
public constructor、member 和 delegate signature。metadata 缺失时以稳定 bridge diagnostic fail closed。
Interpreter 与 dynamic JIT 共用同一套 bridge 实现和转换规则。
