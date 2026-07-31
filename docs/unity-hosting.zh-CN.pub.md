# 把 Lunil 添加到 Unity 项目

[English](unity-hosting.pub.md)

本 how-to 安装 Lunil UPM package，并从 Unity Update/FixedUpdate lifecycle 启动 Lua coroutine。
Unity 2022.3 LTS 与 Unity 6 都是一等支持目标。

## 前置条件

- Unity 2022.3 LTS 或 Unity 6。
- Release asset `com.dlqw.lunil-0.14.0.tgz`。
- 仅在 Editor 中预生成 CLR binding 时需要 .NET SDK 10。

## 1. 安装 package

在 Package Manager 中选择 **Add package from tarball**，再选择
`com.dlqw.lunil-0.14.0.tgz`。Tarball 包含可移植解释器 assembly，不包含 CoreCLR JIT。

不要先用 Unity 6 打开再降级到 2022.3；请直接使用独立的
[`samples/Lunil.Unity.2022.3`](../samples/Lunil.Unity.2022.3/) 项目。

## 2. 导入 Lua asset

添加 `Assets/Scripts/main.lua`：

```lua
counter = 1
coroutine.yield()
counter = counter + 1
return counter
```

Scripted importer 会创建 binary-safe `LuaScriptAsset`。Asset ID 是规范化的 `@Assets/...`
路径。Module name 会先移除前导 `Assets/`，再去掉扩展名并把 `/` 替换为 `.`；因此
`Assets/Scripts/main.lua` 对应 `Scripts.main`。

## 3. 添加 game-loop component

把 `LuaUnityGameLoop` 添加到 GameObject，并将 `main.lua` 指定给 **Entry Script**；保留
**Start On Enable**。Component 会：

1. 创建 Unity dispatcher、clock、console、asset resolver 与 persistent-store adapter；
2. 选择解释器；
3. 在 enable 时启动 entry operation；
4. 从 Unity lifecycle 调用 `TickUpdate` 和 `TickFixed`；
5. 在 disable、destroy、assembly reload 与 play-mode transition 时关闭。

如果从代码创建 component，请在 `Initialize` 前订阅 `TickCompleted` 或 `HostFailed`。

## 4. 配置精确 CLR binding

声明 Lua 可以使用的 member：

```csharp
[assembly: LuaClrGenerateBinding(
    typeof(Game.Inventory),
    nameof(Game.Inventory.Add))]
```

选择 **Tools > Lunil > Generate AOT CLR Bindings**。Unity 会把兼容 C# 9 的输出写入
`Assets/LunilGenerated/`。使用生成 registry、精确 allowlist 与
`LuaClrBindingMode.RegistryOnly` 配置 `LuaClrOptions`；详见 [AOT CLR binding](aot-bindings.zh-CN.pub.md)。

## 5. 构建

- Mono 与 IL2CPP 使用相同的可移植解释器语义。
- 只有在生成 binding 和 preservation metadata 就绪后再启用 **High** managed stripping。
- WebGL callback 由主线程驱动，不能阻塞等待 task。
- 支持 iOS generated-player 编译；最终签名与执行需要 macOS/Xcode。

打开对应 sample 并按 Play，Console 会输出结果 `2` 的完成消息。

准确 lifecycle 与平台矩阵见 [Unity reference](unity-reference.zh-CN.pub.md)。
