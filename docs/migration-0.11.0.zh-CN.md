# 迁移到 Lunil 0.11.0

0.11 在 `Lunil.Hosting` 中增加 CLR 互操作。这是一项 additive、opt-in Hosting 功能：
`LuaHostOptions.Clr` 默认使用 `LuaClrOptions.Disabled`，因此现有 Host 的 compiler、语言版本、
runtime、标准库和执行行为保持不变。

## 现有 Host 无需调整

使用原有 option 创建 `LuaHost` 的代码不会获得 `clr` 全局变量，也不会授予类型发现或构造能力。
稳定版 0.10 API 在 0.11 线上仍然有效。

## 使用精确 allowlist 启用

需要对象构造的应用必须显式配置 assembly 与类型边界：

```csharp
using Lunil.Hosting;

using var host = new LuaHost(LuaHostOptions.Restricted with
{
    Clr = new LuaClrOptions
    {
        Capabilities = LuaClrCapabilities.TypeDiscovery |
            LuaClrCapabilities.Construction,
        AllowedAssemblyNames = ["Example.Contracts"],
        AllowedTypeNames = ["Example.Contracts.Point"],
        InstallGlobalModule = true,
    },
});
```

Bridge 只使用已经加载的 assembly。此前通过自定义 native function 构造 CLR 对象的应用可以保留
原有函数，也可以改用 `clr.new`；0.11 不会自动增加全局变量或 reflection fallback。

转换、ownership、错误与发布规则见 [CLR 互操作](clr-interop.zh-CN.md)。
