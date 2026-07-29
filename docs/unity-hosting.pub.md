# Add Lunil to a Unity project

[简体中文](unity-hosting.zh-CN.pub.md)

This how-to installs the Lunil UPM package and starts a Lua coroutine from Unity's Update and
FixedUpdate lifecycle. Unity 2022.3 LTS and Unity 6 are both first-class targets.

## Prerequisites

- Unity 2022.3 LTS or Unity 6.
- The `com.dlqw.lunil-0.13.0.tgz` release asset.
- .NET SDK 10 only when pre-generating CLR bindings in the Editor.

## 1. Install the package

In Package Manager, choose **Add package from tarball** and select
`com.dlqw.lunil-0.13.0.tgz`. The tarball contains the portable interpreter assemblies; it does not
contain the CoreCLR JIT.

Do not open a Unity 6 project and downgrade it for 2022.3. Use the independent
[`samples/Lunil.Unity.2022.3`](../samples/Lunil.Unity.2022.3/) project instead.

## 2. Import Lua assets

Add `Assets/Scripts/main.lua`:

```lua
counter = 1
coroutine.yield()
counter = counter + 1
return counter
```

The scripted importer creates a binary-safe `LuaScriptAsset`. Its asset ID is the normalized
`@Assets/...` path. Its module name removes the leading `Assets/`, removes the extension, and
replaces `/` with `.`; `Assets/Scripts/main.lua` therefore becomes `Scripts.main`.

## 3. Add the game-loop component

Add `LuaUnityGameLoop` to a GameObject and assign `main.lua` to **Entry Script**. Leave
**Start On Enable** selected. The component:

1. creates Unity dispatcher, clock, console, asset resolver, and persistent-store adapters;
2. selects the interpreter;
3. starts the entry operation on enable;
4. calls `TickUpdate` and `TickFixed` from Unity lifecycle methods;
5. shuts down on disable, destroy, assembly reload, and play-mode transitions.

Subscribe to `TickCompleted` or `HostFailed` before calling `Initialize` when creating the component
from code.

## 4. Configure exact CLR bindings

Declare the members that Lua may use:

```csharp
[assembly: LuaClrGenerateBinding(
    typeof(Game.Inventory),
    nameof(Game.Inventory.Add))]
```

Choose **Tools > Lunil > Generate AOT CLR Bindings**. Unity writes C# 9-compatible output under
`Assets/LunilGenerated/`. Configure `LuaClrOptions` with that registry, exact allowlists, and
`LuaClrBindingMode.RegistryOnly`; see [AOT CLR bindings](aot-bindings.pub.md).

## 5. Build

- Mono and IL2CPP both use the same portable interpreter semantics.
- Use **High** managed stripping only after generated bindings and preservation metadata are present.
- WebGL callbacks remain main-thread driven; do not block on tasks.
- iOS generated-player compilation is supported, but final signing and execution require macOS/Xcode.

Open the matching sample and press Play. The Console prints a completion message with result `2`.

See the [Unity reference](unity-reference.pub.md) for the exact lifecycle and platform matrix.
