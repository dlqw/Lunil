# Generate AOT-safe CLR bindings

[简体中文](aot-bindings.zh-CN.pub.md)

This how-to declares the exact C# bindings that CLR interoperation requires on every runtime.
NativeAOT, Unity IL2CPP, trimming, and deterministic hosts get the same registry dispatch as
trusted .NET hosts; no runtime falls back to reflection.

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

## 3. Configure the bridge with a registry

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
        InstallGlobalModule = true,
    },
};
```

A registry is required whenever CLR interoperation is enabled. A missing type or member
fails closed; dispatch never falls back to reflection on any runtime, and generated bindings
need no runtime reflection metadata on AOT and trimmed hosts.

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

Construction, methods, properties, fields, delegate conversion, and event access use
static invokers from the registry; indexers cannot be bound and fail closed. Runtime
allowlists and conversion budgets still apply. See
[CLR interoperation](clr-interop.pub.md) for conversion and ownership rules and
[.NET NativeAOT and trimming](nativeaot-build-integration.pub.md) for publishing.
