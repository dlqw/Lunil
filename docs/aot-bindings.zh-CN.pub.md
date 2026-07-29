# 生成 AOT-safe CLR binding

[English](aot-bindings.pub.md)

本 how-to 使用准确的 C# binding 替代 runtime member discovery，适用于 NativeAOT、Unity
IL2CPP、trimming，以及 reflection metadata 不可用或被主动关闭的宿主。

## 1. 声明准确 binding request

在拥有 CLR type 的项目中添加 assembly-level attribute：

```csharp
using Lunil.Hosting;

[assembly: LuaClrGenerateBinding(
    typeof(Game.Inventory),
    nameof(Game.Inventory.Add),
    nameof(Game.Inventory.Count))]
[assembly: LuaClrGenerateBinding(typeof(Func<int, int>))]
```

名称按 ordinal 且大小写敏感匹配。Member list 为空时只绑定 public constructor。像
`typeof(Game.Box<int>)` 这样的 closed generic request 只注册该准确构造，不会开放任意 runtime
generic 实例化。

`Lunil.Hosting` 把 generator 作为 analyzer asset 提供。它生成兼容 C# 9 的
`Lunil.Generated.LuaClrGeneratedBindings`。

## 2. 注册生成的 provider

```csharp
var registry = new LuaClrBindingRegistry();
new Lunil.Generated.LuaClrGeneratedBindings().RegisterBindings(registry);
```

注册是确定性的。Type、signature 或 closed-generic registration 冲突时会返回
`LuaClrErrorCode.BindingConflict`，不会选择其中一个。

## 3. 配置 registry-only bridge

生成 binding 本身不会授予访问权限。Capability 与准确 allowlist policy 必须和 registry 一起配置：

```csharp
var typeName = typeof(Game.Inventory).FullName!;
var assemblyName = typeof(Game.Inventory).Assembly.GetName().Name!;

var hostOptions = LuaHostOptions.Restricted with
{
    Clr = new LuaClrOptions
    {
        Capabilities = LuaClrCapabilities.TypeDiscovery |
            LuaClrCapabilities.Construction |
            LuaClrCapabilities.MemberAccess,
        AllowedAssemblyNames = [assemblyName],
        AllowedTypeNames = [typeName],
        AllowedMemberNames = [$"{typeName}.Add", $"{typeName}.Count"],
        BindingRegistry = registry,
        BindingMode = LuaClrBindingMode.RegistryOnly,
        InstallGlobalModule = true,
    },
};
```

CLR 互操作启用时，`RegistryOnly` 要求提供 registry。缺少 type 或 member 时会 fail closed，绝不
fallback 到 reflection。`RegistryThenReflection` 为 trusted .NET host 保留准确 allowlist 的
reflection fallback，但不能作为 AOT 保证。

## 4. 保留 Unity type

`registry.CreateUnityLinkXml()` 会为准确注册的 type 返回 linker descriptor。Unity package 还提供
**Tools > Lunil > Generate AOT CLR Bindings**，会在 Unity compiler 外运行 generator，并默认把
兼容 C# 9 的输出导入到 `Assets/LunilGenerated/LuaClrGeneratedBindings.g.cs`。

Unity 命令需要 .NET SDK，且至少一个已加载 assembly 包含 binding request。Request、type signature
或 player stripping setting 变化后应重新运行。

## 5. 校验不支持的形状

Generator 对不支持的 member 报告 `LUNILBIND001`，对重复 type request 报告
`LUNILBIND002`。Generic method、by-ref/ref-like return、pointer/function-pointer parameter、
ref-readonly parameter 和其他 ref-like 形状会在构建时被拒绝。应绑定 closed generic type，而不是
open generic definition。

## 预期结果

Constructor、method、property、field、indexer、delegate conversion 与 event access 会使用
registry 中的 static invoker。Runtime allowlist 和 conversion budget 仍然生效。转换与 ownership
规则见 [CLR 互操作](clr-interop.zh-CN.pub.md)，发布方法见
[.NET NativeAOT 与 trimming](nativeaot-build-integration.zh-CN.pub.md)。
