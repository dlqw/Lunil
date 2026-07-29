# Generate AOT-safe CLR bindings

[简体中文](aot-bindings.zh-CN.pub.md)

This how-to replaces runtime member discovery with exact C# bindings for NativeAOT, Unity IL2CPP,
trimming, and other hosts where reflection metadata is unavailable or intentionally disabled.

## 1. Declare exact binding requests

Add assembly-level attributes in the project that owns the CLR types:

```csharp
using Lunil.Hosting;

[assembly: LuaClrGenerateBinding(
    typeof(Game.Inventory),
    nameof(Game.Inventory.Add),
    nameof(Game.Inventory.Count))]
[assembly: LuaClrGenerateBinding(typeof(Func<int, int>))]
```

Names are ordinal and case-sensitive. An empty member list binds public constructors only. A
closed generic request such as `typeof(Game.Box<int>)` registers only that exact construction; it
does not enable arbitrary runtime generic instantiation.

`Lunil.Hosting` includes the generator as an analyzer asset. It emits
`Lunil.Generated.LuaClrGeneratedBindings`, using C# 9-compatible source.

## 2. Register the generated provider

```csharp
var registry = new LuaClrBindingRegistry();
new Lunil.Generated.LuaClrGeneratedBindings().RegisterBindings(registry);
```

Registration is deterministic. Conflicting type, signature, or closed-generic registrations fail
with `LuaClrErrorCode.BindingConflict` instead of choosing one entry.

## 3. Configure a registry-only bridge

Generated bindings do not grant access by themselves. Keep the capability and exact allowlist
policy alongside the registry:

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

`RegistryOnly` requires a registry whenever CLR interoperation is enabled. A missing type or member
fails closed; it never falls back to reflection. `RegistryThenReflection` preserves the exact-
allowlist reflection fallback for trusted .NET hosts, but it is not suitable as an AOT guarantee.

## 4. Preserve Unity types

`registry.CreateUnityLinkXml()` returns a linker descriptor for the exact registered types. The
Unity package also provides **Tools > Lunil > Generate AOT CLR Bindings**, which runs the generator
outside Unity's compiler and imports C# 9 output at
`Assets/LunilGenerated/LuaClrGeneratedBindings.g.cs` by default.

The Unity command requires the .NET SDK and at least one loaded assembly containing a binding
request. Run it again after changing requests, type signatures, or player stripping settings.

## 5. Validate unsupported shapes

The generator reports `LUNILBIND001` for unsupported members and `LUNILBIND002` for duplicate type
requests. Generic methods, by-ref or ref-like returns, pointer/function-pointer parameters,
ref-readonly parameters, and other ref-like shapes are rejected at build time. Bind closed generic
types instead of open generic definitions.

## Expected result

Construction, methods, properties, fields, indexers, delegate conversion, and event access use
static invokers from the registry. Runtime allowlists and conversion budgets still apply. See
[CLR interoperation](clr-interop.pub.md) for conversion and ownership rules and
[.NET NativeAOT and trimming](nativeaot-build-integration.pub.md) for publishing.
