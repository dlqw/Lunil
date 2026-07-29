# 如何配置 CLR 互操作

[English](clr-interop.pub.md)

本指南在现有 `Lunil.Hosting` 应用中启用 Lunil 的 opt-in CLR bridge。开始前，请列出 Lua
实际需要的已加载 assembly、CLR type、member 和 operation。默认 Host 不会安装 `clr`
全局表，也不会暴露 reflection。

需要查询具体契约时，参阅 [CLR 互操作参考](clr-interop-reference.zh-CN.pub.md)。需要理解 state
ownership、callback admission 与热更新 fencing 时，参阅 [CLR bridge 生命周期原理](clr-interop-lifecycle.zh-CN.pub.md)。

## 1. 配置 Host

从 restricted Host 开始，只授予所需 capability，并使用完全限定的 allowlist entry：

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
        AllowedMemberNames =
        [
            "Example.Contracts.Point.Value",
            "Example.Contracts.Point.Translate",
        ],
        InstallGlobalModule = true,
    },
};
using var host = new LuaHost(options);
var run = host.RunUtf8("local p=clr.new('Example.Contracts.Point', 1, 2); return p:Translate(3)");
```

名称按 ordinal 且区分大小写；bridge 只搜索应用已加载的 assembly。优先使用
`Full.Type.Name.Member`；裸 member 名会应用于每个 allowlisted type。依赖 allowlist 的
capability 在对应列表为空时 fail closed。

## 2. 按需添加 Delegate 与 event

添加 `DelegateConversion`，并在 `AllowedDelegateTypeNames` 列出精确 delegate type。添加
`EventSubscription`，并在 `AllowedEventNames` 列出精确 event。必须释放每个返回的
`LuaClrSubscription`；释放是幂等的，并会解除对 Lua callback 的 root。

选择与 Host 执行模型匹配的 `ThreadPolicy`。使用 `AnyThreadWhenIdle` 时，非 owner callback
只有在能原子占用 idle state 时才会进入。Callback 尝试 yield、重入 busy state 或从不允许的
thread 进入时会 fail closed。

## 3. 从游戏循环驱动 Timer

授予 `Timers`，配置资源上限，并仅在 state idle 时调用 `DispatchClrTimers`：

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

Timer 不持有 worker，也不会从 thread-pool callback 进入 Lua。根据所需的 missed-tick 行为选择
`skip`、`coalesce` 或 `catch_up`，并把 dispatch 与 catch-up 上限控制在单帧可承受的范围。

## 4. 让单一 Host resource 跨 patch 延续

对必须保持唯一 identity 与 owner 的 native 或 Host object 创建 stable handle：

```csharp
var userdata = host.ClrBridge.CreateStableResource(
    "world-session",
    worldSession,
    ownsResource: true);
host.State.SetGlobal("world_session", LuaValue.FromUserdata(userdata));
```

继续 allowlist 该 resource 的 runtime type 与所访问 member。在签名 patch manifest 中，对其 module-cache
path 使用 `HostResource + Continue` migration rule；candidate module 必须在该 path 放入
placeholder，不得构造第二个 native resource。发布与 rollback 行为见
[热更新生命周期原理](signed-patch-publication.zh-CN.pub.md) 和
[patch manifest 参考](signed-patch-bundles.zh-CN.pub.md)。

## 5. 选择 Binding Mode

Trusted .NET Host 可以使用 `LuaClrBindingMode.RegistryThenReflection`：它保留准确 allowlist，并对
registry 中没有的 entry 使用 reflection。NativeAOT、Unity IL2CPP、严格 trimming 与 deterministic
Host 应生成准确 binding，通过 `LuaClrOptions.BindingRegistry` 传入 `LuaClrBindingRegistry`，并选择
`LuaClrBindingMode.RegistryOnly`。缺少 registry entry 时会直接 fail closed，不会使用 reflection。

声明 request、注册生成 provider、配置 allowlist 与生成 Unity linker metadata 的步骤见
[生成 AOT-safe CLR binding](aot-bindings.zh-CN.pub.md)。

## 6. 准备 trimming 与 NativeAOT 发布

在 trimmed 应用中使用 `RegistryThenReflection` 时，应通过 `DynamicDependency` 等 linker metadata
保留被反射的 public constructor、member 与 delegate signature。生成的 `RegistryOnly` invoker 不需要
应用自有的 runtime reflection metadata。完整发布流程见
[如何使用 .NET NativeAOT 与 trimming 发布](nativeaot-build-integration.zh-CN.pub.md)。

配置完成后，bridge 只暴露已请求的 CLR 表面。`NoMatchingConstructor`、`NoMatchingMember`、
`ThreadDenied` 与 generation-closed error 会标明拒绝 operation 的边界；具体规则见
[参考页](clr-interop-reference.zh-CN.pub.md)。
