# .NET NativeAOT 与 trimming 兼容

从 `0.8.0-alpha.12` 起，Lunil 不再提供 Lua persisted/static AOT、`Lunil.Build`、静态
registry 或 CIL artifact 加载器。本文件只描述 **.NET NativeAOT/trimming** 兼容性；它们是
宿主应用的 .NET 发布方式，不是 Lua AOT 后端。

## 支持范围

在动态代码不可用的进程中，以下产品面继续可用：

- `LuaCompiler` 的源码编译、注解与语义分析；
- `LuaWorkspace` 的多模块图、类型与增量分析；
- `LuaInterpreter`、runtime、标准库与 Hosting interpreter 路径；
- `Lunil.Cli` 的 `run`、`check`、`build --target chunk` 与 `dump`；
- PUC Lua 5.4 chunk 的读取、验证、执行与构建。

`LuaJitExecutor` 会检查 `RuntimeFeature.IsDynamicCodeSupported`。当结果为 `false` 时，`Auto`
与 `PreferJit` 均确定性回退 reference interpreter；不会初始化 Reflection.Emit、采集 Tier 2
profile 或排队 Tier 1/Tier 2/Loop OSR 编译。旧 Lua AOT CLI/config/environment target 返回
`LUNIL0006`，不会因为 .NET NativeAOT 发布而恢复。

## 发布普通 consumer

Consumer 直接引用所需的 Lunil package，再使用标准 SDK 属性发布，无需额外 MSBuild task：

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

NativeAOT consumer 若需在运行时载入 Lua，只需把源码或 `.luac` 作为普通 content/resource
随应用分发，再交给 `LuaCompiler` 或 chunk reader；Lunil 不在 build 阶段生成静态 Lua registry。

## 仓库验证

六个发布 RID 共用同一个 fixture：

```powershell
./scripts/Test-NativeAotFixture.ps1 -RuntimeIdentifier win-x64
./scripts/Test-LunilCliPublish.ps1 -RuntimeIdentifier win-x64 -Modes NativeAot
./scripts/Test-LunilPublishModes.ps1 -RuntimeIdentifier win-x64
```

`Test-NativeAotFixture.ps1` 以 `PublishAot=true`、`PublishTrimmed=true`、
`TreatWarningsAsErrors=true` 发布 `tests/Lunil.NativeAot.Fixture`，并验证：

1. 从随包 Lua source 编译并通过解释器执行；
2. 动态 source 与静态分析结果可用；
3. `LuaWorkspace` 的跨模块分析正确；
4. 默认 JIT policy 保持一致；
5. 动态代码不可用时 JIT executor 不采样、不编译且精确回退；
6. fixture 输出 `LUNIL_NATIVEAOT_OK`。

CI 在 `win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`
执行 fixture 与 NativeAOT CLI；Windows x64 另外覆盖 trimmed single-file 与 ReadyToRun。

## 兼容边界

- Native Lua C module 不受支持，因为 Lunil 不暴露 Lua C ABI。
- JIT-only telemetry 在动态代码不可用时保持空值或 disabled state；consumer 不应把 fallback
  解释为编译成功。
- 反射型 host extension 必须自行声明 trimming metadata；Lunil 内建 compiler/runtime 路径以
  IL2026/IL3050 warning-as-error fixture 为门禁。
- 旧 `Lunil.Build` package、`LunilCompile` item、Lua AOT artifact/manifest/loader API 已删除；
  迁移到运行时编译/解释执行或 portable Lua chunk。
