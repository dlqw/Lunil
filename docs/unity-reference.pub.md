# Unity integration reference

[简体中文](unity-reference.zh-CN.pub.md)

## Package

| Field | Value |
| --- | --- |
| Package ID | `com.dlqw.lunil` |
| Version | `0.14.0` |
| Minimum Unity | `2022.3` |
| Runtime backend | Portable interpreter |
| Default CLR mode | Disabled; generated `RegistryOnly` is recommended |

## Verified matrix

| Area | Unity 2022.3 LTS | Unity 6 |
| --- | --- | --- |
| Editor and Mono player | Stable | Stable (`6000.0`, `6000.3`) |
| Windows IL2CPP | Build and run | Compile and AOT contract verified |
| Android IL2CPP | Build and run | Build and run |
| WebGL IL2CPP | Browser execution | Browser execution |
| iOS IL2CPP | Generated-player compilation | Generated-player compilation |

Final iOS signing and device execution require the Apple toolchain.

## `LuaUnityGameLoop`

| Member | Behavior |
| --- | --- |
| `EntryScript` | Entry `LuaScriptAsset`; replace only while shut down |
| `Modules` | Assets exposed to module resolution; replace only while shut down |
| `StartOnEnable` | Calls `Initialize` during play-mode enable; default `true` |
| `GameLoop` | Active `LuaGameLoopHost`; throws before initialization |
| `EntryOperation` | Scheduled entry operation or `null` |
| `ConfigureHostOptions` | Last pre-initialization options transform; returning `null` fails |
| `TickCompleted` | Raised after a successful Update or FixedUpdate tick |
| `HostFailed` | Raised when ticking throws; otherwise Unity logs the exception |
| `Initialize()` | Idempotently creates and registers the host |
| `TickUpdate()` / `TickFixed()` | Drains dispatcher work and ticks the matching phase |
| `Shutdown()` | Unregisters, closes the dispatcher, and disposes the host |

Application pause suppresses ticks. Disable and destroy both call `Shutdown`. Editor lifecycle code
also closes active hosts before assembly reload and play-mode transitions, including projects with
domain reload disabled.

## Services

- `LuaUnityDispatcher`: main-thread queue with bounded `Drain`; `Close` rejects new work.
- `LuaUnityTimeProvider`: Unity realtime timestamp provider.
- `LuaUnityConsole`: routes standard output and errors to the Unity Console.
- `LuaUnityAssetResolver`: asset, module, and file-system resolution for imported Lua scripts.
- `LuaUnityPersistentStore`: exact-byte storage below `Application.persistentDataPath/Lunil` by default.
- `LuaUnityRuntimeRegistry`: tracks active components for lifecycle shutdown.

## Unity 6 isolation

Unity 6-only APIs live in `Lunil.Unity.Unity6` with Unity version constraints. The base assembly and
package minimum remain Unity 2022.3-compatible.
