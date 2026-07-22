# Migrating to Lunil 0.11.0

The 0.11 line adds an additive, opt-in, capability-controlled CLR bridge to `Lunil.Hosting`.
Existing hosts keep the same compiler, language-version, runtime, standard-library, and execution
behavior because `LuaHostOptions.Clr` defaults to `LuaClrOptions.Disabled`.

## No action for existing hosts

Existing hosts do not receive a `clr` global and do not grant discovery, construction, member access,
callbacks, events, async waiting, or disposal. The stable 0.10 API remains valid on 0.11.

## Opt in with exact allowlists

```csharp
using var host = new LuaHost(LuaHostOptions.Restricted with
{
    Clr = new LuaClrOptions
    {
        Capabilities = LuaClrCapabilities.TypeDiscovery |
            LuaClrCapabilities.Construction | LuaClrCapabilities.MemberAccess,
        AllowedAssemblyNames = ["Example.Contracts"],
        AllowedTypeNames = ["Example.Contracts.Point"],
        AllowedMemberNames = ["Value", "Translate"],
        InstallGlobalModule = true,
    },
});
```

`AllowedMemberNames` accepts exact member names or type-qualified entries. Delegate and event
capabilities require their own exact lists (`AllowedDelegateTypeNames`, `AllowedEventNames`).
Use `ThreadPolicy` to choose callback admission and `OwnConstructedObjects` to choose disposal
ownership. The bridge searches only already-loaded assemblies and never falls back to unrestricted
reflection.

See [CLR interoperation](clr-interop.md) for conversion, callbacks, async, ownership, errors, and
deployment rules.
