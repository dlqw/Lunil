# CLR interoperation

Lunil 0.11 adds an opt-in CLR bridge to `Lunil.Hosting`. The bridge is disabled by default and
never loads an assembly by name. A host must allow both an already loaded assembly and each CLR
type that Lua may discover or construct.

## Configure a host

The assembly and type allowlists use exact, case-sensitive names. `InstallGlobalModule` controls
whether the host publishes the Lua `clr` table; the same bridge remains available to the embedding
application through `LuaHost.ClrBridge` when the global table is not installed.

```csharp
using Lunil.Hosting;

var options = LuaHostOptions.Restricted with
{
    Clr = new LuaClrOptions
    {
        Capabilities = LuaClrCapabilities.TypeDiscovery |
            LuaClrCapabilities.Construction,
        AllowedAssemblyNames = ["Example.Contracts"],
        AllowedTypeNames = ["Example.Contracts.Point"],
        InstallGlobalModule = true,
        OwnConstructedObjects = true,
    },
};

using var host = new LuaHost(options);
var run = host.RunUtf8("""
    local info = clr.type("Example.Contracts.Point")
    local point = clr.new("Example.Contracts.Point", 10, 20)
    return info.name, info.assembly, info.constructible, type(point)
    """);
```

`LuaClrOptions.Disabled` grants no CLR capability. Enabling discovery or construction without an
assembly allowlist is rejected when the bridge is created. Type names are bounded by
`MaximumTypeNameLength`, whose accepted range is 1 through 4096 characters.

## Lua module

When installed, the global `clr` table has two functions:

- `clr.type(fullName)` returns `name`, `assembly`, `value_type`, `constructible`, and a
  `constructors` array. Each constructor entry has a `parameters` array of CLR parameter type
  names.
- `clr.new(fullName, ...)` selects a public constructor and returns a full userdata whose payload
  is a `LuaClrObject`.

The module does not expose members, reflection objects, assembly loading, or unrestricted type
lookup. Member access and callbacks are outside the alpha.1 contract.

## Constructor selection and conversion

Only public instance constructors with the same arity as the Lua argument list participate.
Lunil converts every argument, chooses the candidate with the lowest total conversion cost, and
uses the ordinal CLR parameter-type signature to break a tie. The result therefore does not depend
on reflection enumeration order.

The alpha.1 conversion surface is:

| Lua value | CLR target |
| --- | --- |
| `nil` | Reference types and `Nullable<T>` |
| Boolean | `bool` |
| String | `string`, one-character `char`, or an exact enum name |
| Integer or float | CLR numeric types; integers may also initialize an enum |
| CLR userdata | The wrapped instance when it is assignable to the parameter type |
| Any Lua value | `LuaValue`; primitive values also have an `object` fallback |

Unsupported values, overflow, and constructors with no fully convertible argument list produce
`LuaClrErrorCode.NoMatchingConstructor`. A constructor exception produces
`LuaClrErrorCode.ConstructionFailed` and retains the original exception as `InnerException` for the
embedding application. Calls made through the Lua module surface the error code in a catchable Lua
error message.

## Ownership

`clr.new` wraps the constructed instance in `LuaClrObject`. With the default
`OwnConstructedObjects = true`, collection or explicit payload disposal calls `IDisposable.Dispose`
at most once. Set the option to `false` when the embedding application owns the instance. The
wrapper's `Dispose` method is idempotent in both modes.

The bridge and its userdata belong to one `LuaState`; normal owner validation prevents a Lua value
from being installed in another state.

## Deployment constraints

The bridge only searches `AppDomain.CurrentDomain.GetAssemblies()` and matches assembly simple
names plus type full names exactly. Applications published with trimming or NativeAOT must preserve
constructors for every allowlisted type through their own linker metadata. The bridge does not add
dynamic dependencies or substitute another type when metadata is absent.

For a statically known type, a rooted method can declare the dependency explicitly:

```csharp
using System.Diagnostics.CodeAnalysis;

[DynamicDependency(
    DynamicallyAccessedMemberTypes.PublicConstructors,
    typeof(Example.Contracts.Point))]
static void PreserveClrBridgeTypes()
{
}
```
