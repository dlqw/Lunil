# Godot integration reference

[简体中文](godot-reference.zh-CN.pub.md)

## Package

| Field | Value |
| --- | --- |
| NuGet package | `Lunil.Godot` `0.13.0` |
| Addon path | `res://addons/lunil` |
| Supported Godot | 4.4 and 4.6 .NET |
| Runtime backend | Portable interpreter |
| Default CLR mode | Disabled; generated `RegistryOnly` is recommended |

## Platform matrix

| Target | Support | Notes |
| --- | --- | --- |
| Godot 4.4 and 4.6 .NET editor | Stable | Headless and editor lifecycle |
| Windows desktop | Stable | Export and execution |
| Linux/macOS desktop | Stable | Export compatibility |
| Android | Stable | Godot 4.4 uses .NET 8; Godot 4.6 export templates require .NET 9 |
| iOS | Preview | Official C# exporter and Apple build toolchain require macOS |
| Web | Not in 0.13 | No compatibility commitment |

## `LuaGodotGameLoop`

| Member | Behavior |
| --- | --- |
| `EntryScript` | Entry `LuaGodotScriptResource`, or `null` |
| `Modules` | Exact resources exposed to module and virtual-file resolution |
| `StartOnReady` | Initializes from `_Ready`; default `true` outside the editor hint |
| `PauseWithTree` | Suppresses process and physics ticks while the tree is paused; default `true` |
| `MaximumDispatchedCallbacks` | Main-thread callbacks drained before a tick; default `1024` |
| `GameLoop` | Active `LuaGameLoopHost`; throws before initialization |
| `EntryOperation` | Scheduled entry operation or `null` |
| `ConfigureHostOptions` | Last pre-initialization options transform; returning `null` fails |
| `TickCompleted` | Raised after each completed process or physics tick |
| `HostFailed` | Raised when ticking throws; otherwise Godot receives `GD.PushError` |
| `Initialize()` | Idempotently creates, registers, compiles, and starts the host |
| `TickUpdate()` | Drains dispatcher work and ticks `LuaGameLoopPhase.Update` |
| `TickPhysics()` | Drains dispatcher work and ticks `LuaGameLoopPhase.FixedUpdate` |
| `Shutdown()` | Unregisters, closes the dispatcher, and disposes the host |

`_ExitTree` and `NotificationPredelete` call `Shutdown`. `LuaGodotRuntimeRegistry.DisposeAll()` closes
all registered adapters, and `ActiveHostCount` exposes the current count.

## Resources and services

- `LuaGodotScriptResource`: UTF-8 source plus stable asset and module identities.
- `LuaGodotAssetResolver`: exact asset, module, and virtual-file resolver over registered resources.
- `LuaGodotDispatcher`: owner-thread queue with bounded `Drain`; disposal rejects new callbacks.
- `LuaGodotTimeProvider`: monotonic microsecond timestamps from `Time.GetTicksUsec()`.
- `LuaGodotConsole`: emits complete UTF-8 lines through `GD.Print` and `GD.PushError`.
- `LuaGodotPersistentStore`: confines encoded keys below `user://Lunil` by default and replaces each
  value from a same-directory temporary file.
- `LuaGodotSignalSubscription`: typed, disposable signal-to-Lua scheduling with node-exit cleanup.

## Resource identities

`AssetId` is used exactly as supplied. If it is empty, a saved resource derives the identity by
prefixing its `ResourcePath` with `@`. `ModuleName` is always required. Each module also receives a
`name/with/slashes.lua` virtual-file alias; an asset ID beginning with `@` supplies a second alias
without the prefix.
