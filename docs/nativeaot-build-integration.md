# .NET NativeAOT and trimming

[简体中文](nativeaot-build-integration.zh-CN.md)

Lunil supports source and verified PUC chunk compilation with interpreter execution when dynamic code is unavailable. This guide covers application publishing, fallback behavior, and compatibility boundaries.

## Publish an application

Reference the Lunil packages your application needs and use normal .NET publish properties. Lua source or `.luac` files can be shipped as application content or resources and passed to `LuaCompiler` or the chunk reader at runtime.

```powershell
dotnet publish app.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishAot=true -p:PublishTrimmed=true
```

When `RuntimeFeature.IsDynamicCodeSupported` is `false`, `LuaJitExecutor` uses the canonical interpreter path. This fallback is deterministic and does not initialize dynamic-code emitters or treat a Lua artifact as ahead-of-time compiled.

## Compatibility boundaries

.NET NativeAOT is a deployment mode for the managed host; it is not a Lua AOT backend. Lunil does not expose the Lua C ABI, so native Lua C modules are unsupported. Reflection-based host extensions must preserve their own reachable members with linker metadata. See the Chinese guide for detailed publishing examples and CLR bridge trimming requirements.
