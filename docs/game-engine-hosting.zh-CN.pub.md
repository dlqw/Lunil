# 把 Lunil 接入游戏引擎循环

[English](game-engine-hosting.pub.md)

本 how-to 把引擎主线程、时钟、资产、存储与帧阶段适配到 `LuaGameLoopHost`。Unity 和
Godot package 已提供对应 adapter；自定义引擎可实现相同契约。

## 1. 创建引擎服务

只提供宿主需要的服务：

- `ILuaGameLoopDispatcher`：检查主线程访问并排队 callback。
- `TimeProvider`：向 CLR timer 提供引擎时钟。
- `ILuaGameLoopAssetResolver`：读取不可变 asset bytes。
- `ILuaModuleResolver`：为编译与 `require` 解析 Lua module。
- `ILuaGameLoopPersistentStore`：读写精确 byte snapshot。
- `ILuaConsole`：把 Lua 输出路由到引擎 console。

Dispatcher 必须认可构造线程。非法预算、错误的 dispatcher owner、tick 重入或在 tick 内
dispose 都会立即失败。

## 2. 组合 host

```csharp
var options = new LuaGameLoopHostOptions
{
    HostOptions = LuaHostOptions.Restricted with
    {
        ExecutionBackend = LuaHostExecutionBackend.Interpreter,
        ModuleResolver = moduleResolver,
    },
    Dispatcher = dispatcher,
    TimeProvider = engineTime,
    Console = console,
    ModuleResolver = moduleResolver,
    AssetResolver = assets,
    PersistentStore = store,
    MaximumCallbacksPerTick = 1_024,
    MaximumInstructionsPerTick = 1_000_000,
    MaximumQueuedWork = 65_536,
};

using var loop = new LuaGameLoopHost(options);
```

## 3. 按阶段调度

`Start` 默认进入 `Update`，yield 后会在下一个匹配 tick 恢复。

```csharp
var update = loop.Start(updateCompilation);
var physics = loop.Start(physicsCompilation, options: new LuaGameLoopStartOptions
{
    Phase = LuaGameLoopPhase.FixedUpdate,
    ResumePolicy = LuaGameLoopResumePolicy.NextTick,
});

LuaGameLoopTickResult updateResult = loop.Tick();
LuaGameLoopTickResult fixedResult = loop.TickFixed();
```

每次都检查结果中的 `Failures`。单个 callback 失败会被记录，不会静默中断剩余队列。
`ExecutedInstructionCount`、完成/挂起/取消数量与剩余工作可用于引擎 telemetry。

## 4. 在帧边界发布 patch

先在帧边界外准备并验证签名 patch，再在边界内 commit。`productionPrepareOptions` 必须包含
`AcceptancePolicy`、`ReplayStore` 与该 target 的稳定 `ReplayScope`：

```csharp
var prepared = loop.Host.PreparePatch(bundle, productionPrepareOptions);
if (!prepared.Succeeded) throw new InvalidOperationException(prepared.Message);

loop.PublishAtFrameBoundary(host =>
{
    var opened = host.TryOpenPatchUpdateWindow();
    if (!opened.Succeeded) throw new InvalidOperationException(opened.Message);
    using (opened.Window)
    {
        var committed = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        if (!committed.Succeeded) throw new InvalidOperationException(committed.Message);
    }
});
```

Publication 会在下一次 tick 的调度工作之前可见。属于已 dispose generation 的工作会被拒绝，
不会错误地应用到替换 host。

`productionPrepareOptions` 中的 trust、target/revision/channel/capability/rollback、replay 与 migration
policy 配置见[部署签名 Patch Bundle](deploy-signed-patch-bundles.zh-CN.pub.md#1-配置信任准入replay-与-migration)。

## 5. 在 owner thread 关闭

取消未完成 operation，停止接受 callback，断开引擎 event，并在构造线程调用 `Dispose`。
引擎 adapter 提供 `Shutdown` 完成这一顺序。

继续阅读 [Unity hosting](unity-hosting.zh-CN.pub.md)、[Godot hosting](godot-hosting.zh-CN.pub.md)
和[签名 Patch Bundle reference](signed-patch-bundles.zh-CN.pub.md)。
