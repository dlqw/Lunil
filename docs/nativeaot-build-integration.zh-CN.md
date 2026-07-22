# .NET NativeAOT 与 trimming 兼容

[English](nativeaot-build-integration.md)

本指南说明如何在 .NET NativeAOT、trimming 和其他动态代码不可用的部署环境中运行 Lunil。它描述的是托管宿主的发布方式，不是 Lua AOT 后端。

## 可用功能与回退

动态代码不可用时，`LuaCompiler`、`LuaWorkspace`、`LuaInterpreter`、运行时、标准库、Hosting 解释器路径，以及 CLI 的 `run`、`check`、`build --target chunk` 和 `dump` 均可使用。PUC Lua chunk 在导入前仍会经过格式和结构验证。

`LuaJitExecutor` 检查 `RuntimeFeature.IsDynamicCodeSupported`。当结果为 `false` 时，`Auto` 与 `PreferJit` 使用 canonical interpreter；不会初始化 `Reflection.Emit` 或把 Lua 输入误认为预编译产物。Lua persisted/static AOT、`Lunil.Build`、静态 registry 和 CIL artifact loader 不属于当前产品接口。

## 发布应用

应用直接引用所需 Lunil package，并使用标准 SDK 属性发布：

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <PublishTrimmed>true</PublishTrimmed>
  <TrimMode>full</TrimMode>
  <InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup>
```

```powershell
# .NET NativeAOT
dotnet publish app.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishAot=true -p:PublishTrimmed=true

# Trimmed self-contained single file
dotnet publish app.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishTrimmed=true -p:PublishSingleFile=true

# ReadyToRun/CoreCLR（动态代码仍可用）
dotnet publish app.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishReadyToRun=true
```

把 Lua source 或 `.luac` 作为普通 content/resource 随应用分发，再在运行时交给 `LuaCompiler` 或 chunk reader。无需额外 MSBuild task 或静态 Lua registry。

## 兼容边界

- Lunil 不暴露 Lua C ABI，因此不支持 native Lua C module。
- 动态代码不可用时，JIT 专用遥测不会表示为编译成功；应用应把执行视为 interpreter 路径。
- 反射型 host extension 必须自行用 linker metadata 保留可访问成员。
- 使用 CLR bridge 时，必须保留 allowlist 中 type 的 public constructor、member 和 delegate signature；可使用 `DynamicDependency`。缺少 metadata 时 bridge 会以诊断拒绝访问。
