# CLR 互操作参考

[English](clr-interop-reference.pub.md)

本参考页列出 Lunil CLR bridge 的 Lua 可调用表面、转换规则、timer policy、生命周期 gauge 和
ownership 契约。配置步骤见 [如何配置 CLR 互操作](clr-interop.zh-CN.pub.md)。

## 全局 `clr` 函数

| 函数 | 契约 |
| --- | --- |
| `clr.type(fullName)` | 返回 allowlisted type metadata 与 public constructor 描述。 |
| `clr.new(fullName, ...)` | 确定性选择 constructor，并返回 owned userdata。 |
| `clr.members(fullName)` | 返回 allowlisted member metadata。 |
| `clr.get(target, name [, index...])` | 读取 allowlisted property、field 或 indexer。 |
| `clr.set(target, name, value)` | 写入 allowlisted property 或 field。 |
| `clr.call(target, name, ...)` | 调用 instance method/operator；target 为 type 名时选择 static member。 |
| `clr.on(target, event, callback)` | 返回可释放的 `LuaClrSubscription`。 |
| `clr.await(task)` | 同步等待 `Task`/`ValueTask` userdata 并转换结果。 |
| `clr.cancellation()` | 创建 bridge-owned cancellation token source。 |
| `clr.cancel(value)` | 触发 bridge-owned cancellation token source。 |
| `clr.timer(callback, dueMs [, periodMs [, policy [, maxCatchUp]]])` | 创建由 Host 轮询的 timer。 |
| `clr.cancel_timer(timer)` | 无需通用 disposal capability 即可取消 timer。 |
| `clr.dispose(value)` | 幂等释放 bridge userdata 或 subscription。 |
| `clr.generic(definitionName, typeArgumentNames...)` | 解析显式注册的 closed generic type。 |
| `clr.next(iterator)` | 推进一个有边界的 projected iterator。 |
| `clr.link_iterator(iterator, cancellation)` | 把 iterator disposal 连接到 bridge cancellation。 |

构造出的 userdata 也可通过普通 Lua indexing 与 call 访问 allowlisted property、field、method、
indexer 与 CLR operator。Method 查询返回 bound function；`object.method(x)` 与 `object:method(x)`
都可用。

## Allowlist 匹配与上限

- Assembly、type、member、event 和 delegate 名称按 ordinal 且区分大小写匹配。
- Bridge 不按名称加载 assembly，只搜索已加载 assembly。
- 依赖 allowlist 的 capability 在对应列表为空时 fail closed。
- 裸 member entry 应用于每个 allowlisted type；`Full.Type.Name.Member` 将其限定为一个 type。
- 单个 type 的 allowlisted member 和 overload candidate 超过 `MaximumCachedMembers` 时，发现与访问以
  `MemberNotFound` 失败，candidate 不会被截断。

## 转换与 overload 选择

Candidate 会按参数数量、optional/default 参数和 host-side named argument 过滤。总转换成本最低者
胜出，并以参数类型签名的 ordinal 顺序打破平局。

支持 nil 到 reference/nullable、boolean、string/char、精确 enum 名与 integer、带溢出检查的 CLR
数值类型、由 Lua table 表示的 array 和 `ValueTuple`、`LuaValue`、兼容 CLR userdata 以及 primitive
`object` fallback。CLR rectangular array 与 jagged array 递归转为从 1 开始的嵌套 table。不支持的值
产生 `NoMatchingConstructor` 或 `NoMatchingMember`。

`EnumRepresentation` 默认为准确 name string，也可使用 underlying integer 或 `{ name, value }`
table。`DecimalRepresentation` 默认为 invariant exact string；`ExactInteger` 只接受能由 Lua integer
表示的整数值，`LossyFloat` 则显式允许精度损失。不超过 `long.MaxValue` 的 `ulong` 变为 Lua
integer，更大的值会明确失败。

`CollectionProjection` 默认为 `TablesAndIterators`：list 与 dictionary 投影为 table，其他 enumerable
投影为有边界 iterator；`Userdata` 会关闭 projection。`ConversionLimits` 限制递归深度、总 item、估算
byte，并拒绝 cycle。

带 `ref`/`out` 参数的 method 使用 `RefOutRepresentation`。默认
`PositionalAndNamedTable` 返回 positional value 和以稳定 CLR parameter 名为 key 的 table；
`Positional` 与 `NamedTable` 只选择一种 representation。`Task` 与 `ValueTask` 结果变为
`LuaClrTask`。`LuaClrCancellation` 转换为 `CancellationToken`；nil 映射到
`CancellationToken.None`。CLR exception 变为 `LuaClrException`/可捕获 Lua error。
`IncludeExceptionMessages` 控制是否暴露 Host exception message。

## Callback 与 task 契约

- `LuaClrBridge.CreateDelegate` 在创建 delegate 前验证每个参数与返回类型。
- `LuaClrSubscription.Dispose` 是幂等的，会解除 handler 并释放 Lua callback。
- 直接通过 bridge 调用的 callback 不能 yield。绑定到 `LuaGameLoopHost` 的 void delegate callback 或
  host-polled timer 可以 yield，因为 game-loop Host 会将其作为已调度 coroutine 运行，并在之后匹配的
  tick 恢复。进入仍遵循 `ThreadPolicy` 与所属 `LuaState` 执行边界。
- 调用 thread 存在 `SynchronizationContext` 且 task 未完成时，`clr.await` 以 `AsyncFailed` 拒绝等待。
- 非 active 的 generation-owned task 以 `AsyncGenerationClosed` 失败；底层 CLR `Task` 不会被取消。

生命周期 property 与 gauge 为：

| Resource | Instance state | Bridge gauge |
| --- | --- | --- |
| Callback/subscription | `LuaClrSubscription.IsActive` | `ActiveCallbackCount`、`PendingCallbackCount`、`QuiescedCallbackCount`、`StaleCallbackCount` |
| Task | `LuaClrTask.IsActive` | `ActiveTaskCount`、`PendingTaskCount`、`QuiescedTaskCount`、`StaleTaskCount` |
| Timer | `LuaClrTimer.IsActive` | `ActiveTimerCount`、`PendingTimerCount`、`QuiescedTimerCount`、`StaleTimerCount` |

## Timer policy

Callback 接收从 1 开始的 dispatch tick，以及本次 dispatch 省略的 elapsed tick 数。省略
`periodMs` 时为 one-shot timer。

| Policy | 行为 |
| --- | --- |
| `skip` | 从当前 poll 时刻计算下一周期。 |
| `coalesce` | 保持原始 phase 并报告省略 tick。 |
| `catch_up` | 逐个 dispatch 已过期 tick，每次 poll 受 `MaximumCatchUpTicks` 限制。 |

Timer 数量、单次 poll dispatch、duration 与 catch-up 上限会在调度前验证。调度使用已配置
`TimeProvider` 的 monotonic timestamp。从 busy state 或非 owner thread dispatch 会 fail closed；callback 使用
Host 的 interpreter budget。

## Ownership 与 Binding Mode

`LuaClrObject` 默认拥有构造出的 `IDisposable` instance，最多调用一次 `Dispose`。Host-owned instance
应设置 `OwnConstructedObjects=false`。Userdata、callback、subscription、task、timer 与 stable-resource
userdata 都属于一个 `LuaState`，不能移动到其他 state。

`LuaPatchStableResourceHandle.AcquireLease()` 保护 Host 侧进行中的工作。Member call 在 invocation
期间持有 lease；event subscription 持有 lease 直到 unsubscribe。释放 handle 后拒绝新 access。Owned
`IDisposable` 或 `IAsyncDisposable` resource 在最后一个 lease 结束后释放；non-owning handle 只关闭
access。

`RegistryThenReflection` 是 trusted .NET Host 的 compatibility 默认值，仍要求准确 allowlist。
`RegistryOnly` 要求 `LuaClrOptions.BindingRegistry` 且绝不 fallback 到 reflection，适用于 NativeAOT、
Unity IL2CPP、严格 trimming 与 deterministic Host。生成 binding 在缺少匹配 capability 与 allowlist
时不会授予访问权限。
