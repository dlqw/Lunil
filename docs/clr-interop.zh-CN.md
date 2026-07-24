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

## Ownership 与部署

`LuaClrObject` 默认拥有构造出的 `IDisposable` instance，并且最多转发一次 `Dispose`；Host 自己拥有
instance 时设置 `OwnConstructedObjects=false`。userdata、callback、subscription 和 task 都属于一个
`LuaState`，不能转移到其他 state。

Trimming 和 NativeAOT 应用必须通过 `DynamicDependency` 等 linker metadata 保留每个 allowlist type 的
public constructor、member 和 delegate signature。metadata 缺失时以稳定 bridge diagnostic fail closed。
Interpreter 与 dynamic JIT 共用同一套 bridge 实现和转换规则。
