# 把 Lunil 添加到 Godot .NET 项目

[English](godot-hosting.pub.md)

本 how-to 安装 `Lunil.Godot` package 与 addon，并从 Godot process/physics lifecycle 运行 Lua
coroutine。经过验证的 integration target 是 Godot 4.4 与 4.6 .NET。

## 前置条件

- Godot 4.4 或 4.6 .NET 项目。
- `Lunil.Godot` `0.14.0` NuGet package。
- 0.14.0 release 中的 `addons/lunil` 目录。

## 1. 安装 package 与 addon

在 Godot C# 项目中添加 package：

```xml
<PackageReference Include="Lunil.Godot" Version="0.14.0" />
```

从 `v0.14.0` GitHub Release 下载 `Lunil.Godot.addon-0.14.0.zip` 并解压，再把 archive 中的
`addons/lunil` 目录复制到项目的 `res://addons/lunil`。随后在
**Project > Project Settings > Plugins** 中启用 **Lunil**。Addon 暴露 `LunilGameLoop` node
和 `LunilScript` resource，NuGet package 提供具体实现。

## 2. 创建 entry resource

创建 `LunilScript` resource，并设置：

- **Source**：Lua source；
- **Asset Id**：稳定的 compiler identity，例如 `@res://scripts/main.lua`；
- **Module Name**：`require` 使用的准确名称，例如 `main`。

分配给 **Modules** 的其他 `LunilScript` resource 会按准确 module name 解析。Asset ID、module
name 或 virtual file path 重复时，初始化会失败。

## 3. 添加 game-loop node

把 `LunilGameLoop` 添加到 scene，分配 entry resource，并保留 **Start On Ready**。该 node 会：

1. 创建 Godot dispatcher、clock、console、resource 与 persistent-store adapter；
2. 选择可移植解释器；
3. 从 `_Ready` 启动 entry coroutine；
4. 从 `_Process` 驱动 Update work，从 `_PhysicsProcess` 驱动 FixedUpdate work；
5. 在 tree exit 和 predelete notification 时关闭。

`Pause With Tree` 会在 scene tree 暂停期间停止两个 tick phase。使用
`Maximum Dispatched Callbacks` 限制每次 tick 前的主线程 dispatcher drain。

## 4. 配置生成的 CLR binding

关闭 **Start On Ready**，再从父 scene script 配置并初始化 addon node：

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

使用 assembly-level `LuaClrGenerateBinding` attribute 声明所需 binding。详见
[AOT CLR binding](aot-bindings.zh-CN.pub.md)。

## 5. 连接 Godot signal

`LuaGodotSignalSubscription.Connect` 会 root Lua callback、通过 game-loop host 调度，并在
dispose 时断开。Typed overload 最多接收三个 signal value，且每个 Godot value 都需要显式转换为
`LuaValue`。连接到 `Node` 的 subscription 会在该 node 退出 tree 时自动断开。

## 6. 运行与导出

打开与 editor 匹配的项目并运行 main scene：

- [`samples/Lunil.Godot.4.4`](../samples/Lunil.Godot.4.4/) 使用 `Godot.NET.Sdk/4.4.1` 与 `net8.0`；
- [`samples/Lunil.Godot.4.6`](../samples/Lunil.Godot.4.6/) 使用 `Godot.NET.Sdk/4.6.3` 与 `net9.0`。

两个示例都会输出结果 `2` 的完成消息并成功退出。项目应保持分离，避免一个 editor 替换另一版本
生成的 `.godot` metadata。

桌面端与 Android 为稳定支持。Godot iOS 是 preview，因为官方 .NET exporter、Xcode build、签名与
设备运行需要 macOS。Godot Web 不在 0.14 支持矩阵内。

Lifecycle 与平台细节见 [Godot reference](godot-reference.zh-CN.pub.md)。
