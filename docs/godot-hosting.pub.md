# Add Lunil to a Godot .NET project

[简体中文](godot-hosting.zh-CN.pub.md)

This how-to installs the `Lunil.Godot` package and addon, then runs a Lua coroutine from Godot's
process and physics lifecycle. The verified integration targets are Godot 4.4 and 4.6 .NET.

## Prerequisites

- A Godot 4.4 or 4.6 .NET project.
- The `Lunil.Godot` `0.13.0` NuGet package.
- The `addons/lunil` directory from the 0.13.0 release.

## 1. Install the package and addon

Add the package to the Godot C# project:

```xml
<PackageReference Include="Lunil.Godot" Version="0.13.0" />
```

Download `Lunil.Godot.addon-0.13.0.zip` from the `v0.13.0` GitHub Release, extract it, and copy the
archive's `addons/lunil` directory into the project as `res://addons/lunil`. Then enable **Lunil**
under **Project > Project Settings > Plugins**. The addon exposes the `LunilGameLoop` node and
`LunilScript` resource while the NuGet package supplies their implementation.

## 2. Create the entry resource

Create a `LunilScript` resource and set:

- **Source** to the Lua source;
- **Asset Id** to a stable compiler identity such as `@res://scripts/main.lua`;
- **Module Name** to the exact name used by `require`, such as `main`.

Additional `LunilScript` resources assigned to **Modules** are resolved by their exact module names.
Duplicate asset IDs, module names, or virtual file paths fail during initialization.

## 3. Add the game-loop node

Add `LunilGameLoop` to the scene, assign the entry resource, and leave **Start On Ready** enabled.
The node:

1. creates Godot dispatcher, clock, console, resource, and persistent-store adapters;
2. selects the portable interpreter;
3. starts the entry coroutine from `_Ready`;
4. calls Update work from `_Process` and FixedUpdate work from `_PhysicsProcess`;
5. shuts down on tree exit and predelete notification.

`Pause With Tree` suppresses both tick phases while the scene tree is paused. Set
`Maximum Dispatched Callbacks` to bound the main-thread dispatcher drain before each tick.

## 4. Configure generated CLR bindings

Disable **Start On Ready**, then configure and initialize the addon node from a parent scene script:

```csharp
using Godot;
using Lunil.Hosting;

public partial class Main : Node
{
    public override void _Ready()
    {
        var registry = new LuaClrBindingRegistry();
        new Lunil.Generated.LuaClrGeneratedBindings().RegisterBindings(registry);

        var loop = GetNode<LunilGameLoop>("LunilGameLoop");
        loop.ConfigureHostOptions = options => options with
        {
            HostOptions = options.HostOptions with
            {
                Clr = new LuaClrOptions
                {
                    Capabilities = LuaClrCapabilities.TypeDiscovery |
                        LuaClrCapabilities.MemberAccess,
                    AllowedAssemblyNames =
                        [typeof(Game.Inventory).Assembly.GetName().Name!],
                    AllowedTypeNames = [typeof(Game.Inventory).FullName!],
                    AllowedMemberNames =
                        [$"{typeof(Game.Inventory).FullName}.Add"],
                    BindingRegistry = registry,
                    BindingMode = LuaClrBindingMode.RegistryOnly,
                    InstallGlobalModule = true,
                },
            },
        };
        loop.Initialize();
    }
}
```

Declare requested bindings with assembly-level `LuaClrGenerateBinding` attributes. See
[AOT CLR bindings](aot-bindings.pub.md).

## 5. Connect Godot signals

`LuaGodotSignalSubscription.Connect` roots a Lua callback, schedules it through the game-loop host,
and disconnects when disposed. Typed overloads accept up to three signal values and require an
explicit conversion from each Godot value to `LuaValue`. A subscription attached to a `Node`
automatically disconnects when that node exits the tree.

## 6. Run and export

Open the project that matches the editor and run its main scene:

- [`samples/Lunil.Godot.4.4`](../samples/Lunil.Godot.4.4/) uses `Godot.NET.Sdk/4.4.1` and `net8.0`;
- [`samples/Lunil.Godot.4.6`](../samples/Lunil.Godot.4.6/) uses `Godot.NET.Sdk/4.6.3` and `net9.0`.

Each sample prints a completion message with result `2` and exits successfully. Keep the projects
separate so one editor does not replace the other version's generated `.godot` metadata.

Desktop and Android are stable. Godot iOS support is preview because the official .NET exporter,
Xcode build, signing, and device execution require macOS. Godot Web is outside the 0.13 support
matrix.

See the [Godot reference](godot-reference.pub.md) for lifecycle and platform details.
