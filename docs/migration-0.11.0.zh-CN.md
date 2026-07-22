# 迁移到 Lunil 0.11.0

0.11 在 `Lunil.Hosting` 增加 additive、opt-in、受能力控制的 CLR bridge。由于
`LuaHostOptions.Clr` 默认是 `LuaClrOptions.Disabled`，现有 Host 的 compiler、语言版本、runtime、
标准库和执行行为保持不变。

## 现有 Host 无需调整

现有 Host 不会获得 `clr` 全局变量，也不会授予发现、构造、member 访问、callback、event、异步等待
或 disposal 能力。稳定版 0.10 API 在 0.11 线上仍然有效。

## 使用精确 allowlist 启用

```csharp
using var host = new LuaHost(LuaHostOptions.Restricted with
{
    Clr = new LuaClrOptions
    {
        Capabilities = LuaClrCapabilities.TypeDiscovery |
            LuaClrCapabilities.Construction | LuaClrCapabilities.MemberAccess,
        AllowedAssemblyNames = ["Example.Contracts"],
        AllowedTypeNames = ["Example.Contracts.Point"],
        AllowedMemberNames = ["Value", "Translate"],
        InstallGlobalModule = true,
    },
});
```

`AllowedMemberNames` 接受精确 member 名或带类型限定的 entry。Delegate 与 event capability 需要
独立的精确列表（`AllowedDelegateTypeNames`、`AllowedEventNames`）。通过 `ThreadPolicy` 设置
callback 线程策略，通过 `OwnConstructedObjects` 设置 disposal ownership。Bridge 只搜索已经加载的
assembly，不会 fallback 到不受限制的 reflection。

转换、callback、异步、ownership、错误与部署规则见[CLR 互操作](clr-interop.zh-CN.md)。
