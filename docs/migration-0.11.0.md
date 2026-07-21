# Migrating to Lunil 0.11.0

The 0.11 line adds CLR interoperation to `Lunil.Hosting`. It is an additive, opt-in hosting
feature: existing hosts keep the same compiler, language-version, runtime, standard-library, and
execution behavior because `LuaHostOptions.Clr` defaults to `LuaClrOptions.Disabled`.

## No action for existing hosts

Code that creates `LuaHost` with existing options does not receive a `clr` global and does not
grant type discovery or construction. The stable 0.10 API remains valid on the 0.11 line.

## Opt in with exact allowlists

Applications that need construction configure the assembly and type boundaries explicitly:

```csharp
using Lunil.Hosting;

using var host = new LuaHost(LuaHostOptions.Restricted with
{
    Clr = new LuaClrOptions
    {
        Capabilities = LuaClrCapabilities.TypeDiscovery |
            LuaClrCapabilities.Construction,
        AllowedAssemblyNames = ["Example.Contracts"],
        AllowedTypeNames = ["Example.Contracts.Point"],
        InstallGlobalModule = true,
    },
});
```

The bridge uses already loaded assemblies only. Applications that previously used their own native
functions for CLR construction can keep those functions or replace them with `clr.new`; no
automatic global or reflection fallback is introduced.

See [CLR interoperation](clr-interop.md) for conversion, ownership, error, and deployment rules.
