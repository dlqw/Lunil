# CLR interoperation

Lunil 0.11 provides an opt-in, capability-controlled CLR bridge. It only searches already-loaded
assemblies and exact allowlists; reflection is never exposed as an unrestricted escape hatch.
The default host remains unchanged and does not install a `clr` global.

## Configure a host

```csharp
var options = LuaHostOptions.Restricted with
{
    Clr = new LuaClrOptions
    {
        Capabilities = LuaClrCapabilities.TypeDiscovery |
            LuaClrCapabilities.Construction | LuaClrCapabilities.MemberAccess |
            LuaClrCapabilities.Async,
        AllowedAssemblyNames = ["Example.Contracts"],
        AllowedTypeNames = ["Example.Contracts.Point"],
        AllowedMemberNames = ["Value", "Translate"],
        InstallGlobalModule = true,
    },
};
using var host = new LuaHost(options);
var run = host.RunUtf8("local p=clr.new('Example.Contracts.Point', 1, 2); return p:Translate(3)");
```

Assembly names, type names, and member names are ordinal and case-sensitive. The bridge does not
load an assembly by name. A capability that needs an allowlist fails closed when its list is empty.
Restricted, NativeAOT, trimming, and deterministic hosts use the same policy.

## Lua module

When installed, the global `clr` table contains:

- `clr.type(fullName)` — metadata and public constructor descriptions.
- `clr.new(fullName, ...)` — deterministic constructor selection and owned userdata creation.
- `clr.members(fullName)` — allowlisted member metadata.
- `clr.get(target, name [, index...])`, `clr.set(target, name, value)` — explicit member access.
- `clr.call(target, name, ...)` — method/operator invocation. A type name as the first argument
  invokes a static member.
- `clr.on(target, event, callback)` — disposable event subscription.
- `clr.await(task)` — waits for a `Task`/`ValueTask` userdata and converts its result.
- `clr.cancellation()`, `clr.cancel(value)` — create and signal a bridge-owned cancellation token source.
- `clr.dispose(value)` — idempotent disposal of bridge userdata or subscriptions.

Constructed userdata also supports allowlisted properties, fields, methods, indexers, and CLR
operators through normal Lua indexing and calls. A method lookup returns a bound function; both
`object.method(x)` and `object:method(x)` are accepted.

## Conversion and overload rules

Candidates are filtered by arity, optional/default parameters, and named host-side arguments. The
lowest total conversion cost wins; ordinal parameter signatures break ties. Supported conversions
include nil to references/nullable, booleans, strings/chars, exact enum names and integer values,
all CLR numeric types with overflow checks, arrays and `ValueTuple` values represented by Lua tables,
`LuaValue`, compatible CLR userdata, and primitive `object` fallback. Unsupported values produce a
stable `NoMatchingConstructor` or `NoMatchingMember` error.

Methods with `ref`/`out` parameters return the ordinary result followed by ref/out values in
parameter order. Task and `ValueTask` results become `LuaClrTask` userdata and are consumed by
`clr.await`. `LuaClrCancellation` userdata converts to `CancellationToken`; nil maps to `CancellationToken.None`. CLR exceptions are translated to `LuaClrException`/catchable Lua errors; set
`IncludeExceptionMessages` only when exposing messages is appropriate for the host.

## Delegates and events

Grant `DelegateConversion` and list exact delegate type names in `AllowedDelegateTypeNames`.
`LuaClrBridge.CreateDelegate` validates every parameter and return type before creating a delegate.
Grant `EventSubscription` and list event names in `AllowedEventNames`; `Subscribe` returns an
idempotent `LuaClrSubscription`. The subscription userdata roots the Lua callback and releases it
when disposed. Callback entry obeys `ThreadPolicy`, preserves Lua state ownership, and rejects
callbacks that yield or re-enter a busy state from an unsupported thread.

Hot-update publication fences delegates by the `LuaIrModule` that owns their closure. Delegates from
the previous module generation become fail-closed with `SubscriptionClosed`; candidate delegates
created while loaders execute remain pending until the whole patch barrier publishes. A failed
candidate or ring health rollback rejects candidate delegates and restores the previous generation.
Event subscriptions follow the same transaction: old handlers are detached on publication and
reattached on rollback, while failed candidate handlers are detached. Use `IsActive` on
`LuaClrSubscription` and export `ActiveCallbackCount`, `PendingCallbackCount`,
`QuiescedCallbackCount`, and `StaleCallbackCount` from `LuaClrBridge` as lifecycle gauges.

## Ownership and deployment

`LuaClrObject` owns constructed `IDisposable` instances by default and forwards `Dispose` at most once;
set `OwnConstructedObjects=false` for host-owned instances. Userdata, callbacks, subscriptions, and
tasks belong to one `LuaState` and cannot be transferred to another state.

Trimming and NativeAOT applications must preserve public constructors, members, and delegate
signatures for every allowlisted type with linker metadata such as `DynamicDependency`. Missing
metadata fails closed with a stable bridge diagnostic. Interpreter and dynamic JIT share the same
bridge implementation and conversion rules.
