# Unity integration reference

[English](unity-reference.pub.md)

## Package

| 字段 | 值 |
| --- | --- |
| Package ID | `com.dlqw.lunil` |
| 版本 | `0.13.0` |
| 最低 Unity | `2022.3` |
| Runtime backend | 可移植解释器 |
| 默认 CLR mode | 关闭；推荐生成的 `RegistryOnly` |

## 已验证矩阵

| 范围 | Unity 2022.3 LTS | Unity 6 |
| --- | --- | --- |
| Editor 与 Mono player | 稳定 | 稳定（`6000.0`、`6000.3`） |
| Windows IL2CPP | 构建并运行 | 已验证编译和 AOT 契约 |
| Android IL2CPP | 构建并运行 | 构建并运行 |
| WebGL IL2CPP | 浏览器执行 | 浏览器执行 |
| iOS IL2CPP | Generated-player 编译 | Generated-player 编译 |

最终 iOS 签名与设备执行需要 Apple 工具链。

## `LuaUnityGameLoop`

| Member | 行为 |
| --- | --- |
| `EntryScript` | Entry `LuaScriptAsset`；只能在关闭状态替换 |
| `Modules` | 暴露给 module resolver 的 asset；只能在关闭状态替换 |
| `StartOnEnable` | Play mode enable 时调用 `Initialize`；默认 `true` |
| `GameLoop` | 当前 `LuaGameLoopHost`；初始化前访问会抛出异常 |
| `EntryOperation` | 已调度的 entry operation，或 `null` |
| `ConfigureHostOptions` | 初始化前最后一次 options 转换；返回 `null` 会失败 |
| `TickCompleted` | 成功完成 Update 或 FixedUpdate tick 后触发 |
| `HostFailed` | Tick 抛出异常时触发；无 handler 时由 Unity 记录 |
| `Initialize()` | 幂等创建并注册 host |
| `TickUpdate()` / `TickFixed()` | Drain dispatcher 后执行对应 phase |
| `Shutdown()` | 注销、关闭 dispatcher 并 dispose host |

应用 pause 会停止 tick。Disable 与 destroy 都调用 `Shutdown`。Editor lifecycle 还会在
assembly reload 和 play-mode transition 前关闭 active host，包括关闭 domain reload 的项目。

## Service

- `LuaUnityDispatcher`：带边界 `Drain` 的主线程队列；`Close` 后拒绝新工作。
- `LuaUnityTimeProvider`：Unity realtime timestamp provider。
- `LuaUnityConsole`：把标准输出和错误路由到 Unity Console。
- `LuaUnityAssetResolver`：解析导入 Lua script 的 asset、module 与 file-system 访问。
- `LuaUnityPersistentStore`：默认在 `Application.persistentDataPath/Lunil` 下存储精确 bytes。
- `LuaUnityRuntimeRegistry`：跟踪 active component 以完成 lifecycle shutdown。

## Unity 6 隔离

Unity 6-only API 位于带 Unity version constraint 的 `Lunil.Unity.Unity6`。Base assembly 与
package 最低版本仍兼容 Unity 2022.3。
