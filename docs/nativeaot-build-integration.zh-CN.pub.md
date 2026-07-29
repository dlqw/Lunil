# 使用 .NET NativeAOT 与 trimming 发布 Lunil

[English](nativeaot-build-integration.pub.md)

本 how-to 在动态代码不可用时发布托管 Lunil host。NativeAOT 是应用部署模式，不是 Lua AOT
backend：Lua source 与已验证 PUC chunk 仍会编译为 canonical IR，并由解释器执行。

## 1. 配置 host

使用 `Lunil.Hosting`。当同一配置需要同时适用于 CoreCLR 与 NativeAOT 时，显式选择解释器：

```csharp
using var host = new LuaHost(new LuaHostOptions
{
    ExecutionBackend = LuaHostExecutionBackend.Interpreter,
});
```

动态代码不可用时，`Auto` 也会选择解释器。显式 `Jit` 需要 .NET 10 dynamic-code backend，并会在
NativeAOT 中抛出 `PlatformNotSupportedException`。

## 2. 生成 CLR binding

Reflection-based discovery 不是 AOT 契约。请求准确 binding，并使用
`LuaClrBindingMode.RegistryOnly` 配置 bridge：

```csharp
using Lunil.Hosting;

[assembly: LuaClrGenerateBinding(
    typeof(App.ScoreService),
    nameof(App.ScoreService.Add))]

var registry = new LuaClrBindingRegistry();
new Lunil.Generated.LuaClrGeneratedBindings().RegisterBindings(registry);

var typeName = typeof(App.ScoreService).FullName!;
var assemblyName = typeof(App.ScoreService).Assembly.GetName().Name!;

using var host = new LuaHost(LuaHostOptions.Restricted with
{
    ExecutionBackend = LuaHostExecutionBackend.Interpreter,
    Clr = new LuaClrOptions
    {
        Capabilities = LuaClrCapabilities.TypeDiscovery |
            LuaClrCapabilities.MemberAccess,
        AllowedAssemblyNames = [assemblyName],
        AllowedTypeNames = [typeName],
        AllowedMemberNames = [$"{typeName}.Add"],
        BindingRegistry = registry,
        BindingMode = LuaClrBindingMode.RegistryOnly,
        InstallGlobalModule = true,
    },
});
```

`Lunil.Hosting` package 包含 generator analyzer。生成的 invoker 无需应用自行提供 runtime
reflection annotation，即可覆盖所请求的 constructor 与 member。Capability 和准确 assembly、type、
member、delegate、event allowlist 仍然生效。详见
[AOT CLR binding](aot-bindings.zh-CN.pub.md)。

## 3. 启用 NativeAOT publish

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <PublishTrimmed>true</PublishTrimmed>
  <TrimMode>full</TrimMode>
  <InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup>
```

把 `.lua` 或 `.luac` 作为普通 application content/resource 分发。无需 Lunil MSBuild task 或 static
Lua artifact registry。

## 4. 发布目标 RID

```powershell
dotnet publish app.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishAot=true -p:PublishTrimmed=true
```

Trimmed CoreCLR 部署使用普通 SDK mode：

```powershell
dotnet publish app.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishTrimmed=true -p:PublishSingleFile=true
```

ReadyToRun 仍是 CoreCLR 部署；动态代码可用时可以使用 JIT。

## 兼容边界

- Lunil 不暴露 Lua C ABI，因此不支持 native Lua C module。
- Portable Hosting 不会加载或探测 `Lunil.CodeGen.Cil`。
- 生成 binding 之外的应用 reflection 必须自行保留可达 metadata。
- Closed generic、delegate、event、collection projection 与 ref/out shape 必须显式请求并允许；
  generator 不支持的形状会在构建时失败。
- 缺少生成 binding 或 allowlist entry 时，runtime 会 fail closed。

应在每个实际发布 RID 上验证可执行文件，包括 CLR call、game-loop scheduling、签名 patch
publication、trimming 和 application content loading。
