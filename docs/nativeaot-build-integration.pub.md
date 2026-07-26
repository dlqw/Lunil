# How to publish with .NET NativeAOT and trimming

[简体中文](nativeaot-build-integration.zh-CN.pub.md)

This guide shows how to run Lunil in .NET NativeAOT, trimmed, and other deployments where dynamic
code is unavailable. It describes publishing a managed host, not a Lua AOT backend.

## 1. Select the available execution path

When dynamic code is unavailable, `LuaCompiler`, `LuaWorkspace`, `LuaInterpreter`, the runtime,
standard libraries, the hosting interpreter path, and the CLI `run`, `check`, `build --target
chunk`, and `dump` commands remain available. PUC Lua chunks still pass format and structural
verification before import.

`LuaJitExecutor` checks `RuntimeFeature.IsDynamicCodeSupported`. When it is `false`, `Auto` and
`PreferJit` use the canonical interpreter; they do not initialize `Reflection.Emit` or treat Lua
input as a precompiled artifact. Persisted/static Lua AOT, `Lunil.Build`, static registries, and CIL
artifact loaders are not current product interfaces.

## 2. Publish the application

Reference the Lunil packages your application needs and use standard SDK properties:

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

# ReadyToRun/CoreCLR (dynamic code remains available)
dotnet publish app.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishReadyToRun=true
```

Ship Lua source or `.luac` files as ordinary application content or resources, then pass them to
`LuaCompiler` or the chunk reader at runtime. No additional MSBuild task or static Lua registry is
required.

## 3. Preserve required application metadata

- Lunil does not expose the Lua C ABI, so native Lua C modules are unsupported.
- When dynamic code is unavailable, JIT-only telemetry does not report successful compilation;
  treat execution as the interpreter path.
- Reflection-based host extensions must preserve their own reachable members with linker metadata.
- CLR bridge consumers must preserve public constructors, members, and delegate signatures for
  every allowlisted application type, for example with `DynamicDependency`. The bridge preserves
  its interpreted delegate callback adapter and generic task-result metadata. Missing application
  metadata still causes bridge access to fail closed.
