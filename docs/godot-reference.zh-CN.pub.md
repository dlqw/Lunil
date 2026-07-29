# Godot integration reference

[English](godot-reference.pub.md)

## Package

| 字段 | 值 |
| --- | --- |
| NuGet package | `Lunil.Godot` `0.13.0` |
| Addon 路径 | `res://addons/lunil` |
| 支持的 Godot | 4.4 与 4.6 .NET |
| Runtime backend | 可移植解释器 |
| 默认 CLR mode | 关闭；推荐生成的 `RegistryOnly` |

## 平台矩阵

| Target | 支持级别 | 说明 |
| --- | --- | --- |
| Godot 4.4 与 4.6 .NET editor | 稳定 | Headless 与 editor lifecycle |
| Windows desktop | 稳定 | 导出并运行 |
| Linux/macOS desktop | 稳定 | 导出兼容性 |
| Android | 稳定 | Godot 4.4 使用 .NET 8；Godot 4.6 export template 需要 .NET 9 |
| iOS | Preview | 官方 C# exporter 与 Apple build toolchain 需要 macOS |
| Web | 不在 0.13 范围 | 不提供兼容性承诺 |

## `LuaGodotGameLoop`

| Member | 行为 |
| --- | --- |
| `EntryScript` | Entry `LuaGodotScriptResource`，或 `null` |
| `Modules` | 暴露给 module 与 virtual-file resolver 的准确 resource |
| `StartOnReady` | 从 `_Ready` 初始化；非 editor hint 下默认为 `true` |
| `PauseWithTree` | Tree 暂停时停止 process/physics tick；默认 `true` |
| `MaximumDispatchedCallbacks` | Tick 前 drain 的主线程 callback 数；默认 `1024` |
| `GameLoop` | 当前 `LuaGameLoopHost`；初始化前访问会抛出异常 |
| `EntryOperation` | 已调度的 entry operation，或 `null` |
| `ConfigureHostOptions` | 初始化前最后一次 options 转换；返回 `null` 会失败 |
| `TickCompleted` | 每次 process 或 physics tick 完成后触发 |
| `HostFailed` | Tick 抛出异常时触发；无 handler 时使用 `GD.PushError` |
| `Initialize()` | 幂等创建、注册、编译并启动 host |
| `TickUpdate()` | Drain dispatcher 后执行 `LuaGameLoopPhase.Update` |
| `TickPhysics()` | Drain dispatcher 后执行 `LuaGameLoopPhase.FixedUpdate` |
| `Shutdown()` | 注销、关闭 dispatcher 并 dispose host |

`_ExitTree` 与 `NotificationPredelete` 会调用 `Shutdown`。`LuaGodotRuntimeRegistry.DisposeAll()`
关闭所有已注册 adapter，`ActiveHostCount` 提供当前数量。

## Resource 与 service

- `LuaGodotScriptResource`：UTF-8 source 加稳定 asset/module identity。
- `LuaGodotAssetResolver`：在已注册 resource 上提供准确 asset、module 与 virtual-file 解析。
- `LuaGodotDispatcher`：带边界 `Drain` 的 owner-thread queue；dispose 后拒绝新 callback。
- `LuaGodotTimeProvider`：来自 `Time.GetTicksUsec()` 的单调微秒 timestamp。
- `LuaGodotConsole`：通过 `GD.Print` 与 `GD.PushError` 输出完整 UTF-8 行。
- `LuaGodotPersistentStore`：默认把编码后的 key 限制在 `user://Lunil`，并通过同目录临时文件
  替换 value。
- `LuaGodotSignalSubscription`：带类型、可 dispose、在 node exit 时清理的 signal-to-Lua 调度。

## Resource identity

`AssetId` 会按原值使用。若为空，已保存 resource 会在 `ResourcePath` 前添加 `@` 来派生 identity。
`ModuleName` 始终必填。每个 module 还会得到一个 `name/with/slashes.lua` virtual-file alias；以
`@` 开头的 asset ID 会再提供一个去掉该前缀的 alias。
