# Publish Lunil with .NET NativeAOT and trimming

[简体中文](nativeaot-build-integration.zh-CN.pub.md)

This how-to publishes a managed Lunil host when dynamic code is unavailable. NativeAOT is an
application deployment mode, not a Lua AOT backend: Lua source and verified PUC chunks still compile
to canonical IR and run through the interpreter.

## 1. Configure the host

Use `Lunil.Hosting` and select the interpreter explicitly when one configuration must work across
CoreCLR and NativeAOT:

```csharp
using var host = new LuaHost(new LuaHostOptions
{
    ExecutionBackend = LuaHostExecutionBackend.Interpreter,
});
```

`Auto` also selects the interpreter when dynamic code is unavailable. Explicit `Jit` requires the
.NET 10 dynamic-code backend and throws `PlatformNotSupportedException` in NativeAOT.

## 2. Generate CLR bindings

Reflection-based discovery is not an AOT contract. Request exact bindings and configure the bridge
with `LuaClrBindingMode.RegistryOnly`:

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

The `Lunil.Hosting` package includes the generator analyzer. Generated invokers cover the requested
constructors and members without application-owned runtime reflection annotations. Capabilities and
exact assembly, type, member, delegate, and event allowlists still apply. See
[AOT CLR bindings](aot-bindings.pub.md).

## 3. Enable NativeAOT publishing

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <PublishTrimmed>true</PublishTrimmed>
  <TrimMode>full</TrimMode>
  <InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup>
```

Ship `.lua` or `.luac` inputs as ordinary application content or resources. No Lunil MSBuild task or
static Lua artifact registry is required.

## 4. Publish the target RID

```powershell
dotnet publish app.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishAot=true -p:PublishTrimmed=true
```

For trimmed CoreCLR deployments, use normal SDK modes:

```powershell
dotnet publish app.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishTrimmed=true -p:PublishSingleFile=true
```

ReadyToRun remains a CoreCLR deployment and may use the JIT when dynamic code is available.

## Compatibility boundaries

- Lunil does not expose the Lua C ABI; native Lua C modules are unsupported.
- Portable Hosting does not load or probe `Lunil.CodeGen.Cil`.
- Application reflection outside generated bindings must preserve its own reachable metadata.
- Closed generics, delegates, events, collection projection, and ref/out shapes must be requested and
  allowed explicitly; unsupported generator shapes fail at build time.
- Missing generated bindings or allowlist entries fail closed at runtime.

Validate the published executable on every RID it ships, including CLR calls, game-loop scheduling,
signed patch publication, trimming, and application content loading.
