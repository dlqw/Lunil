# How to configure CLR interoperation

[简体中文](clr-interop.zh-CN.pub.md)

This guide enables Lunil's opt-in CLR bridge in an existing `Lunil.Hosting` application. Before
starting, list the exact loaded assemblies, CLR types, members, and operations that Lua needs. The
default host does not install a `clr` global or expose reflection.

For lookup details, use the [CLR interoperation reference](clr-interop-reference.pub.md). For the
reasoning behind state ownership, callback admission, and hot-update fencing, read
[How CLR bridge lifecycles work](clr-interop-lifecycle.pub.md).

## 1. Configure the host

Start from a restricted host, grant only the required capabilities, and use fully qualified
allowlist entries:

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
        AllowedMemberNames =
        [
            "Example.Contracts.Point.Value",
            "Example.Contracts.Point.Translate",
        ],
        InstallGlobalModule = true,
    },
};
using var host = new LuaHost(options);
var run = host.RunUtf8("local p=clr.new('Example.Contracts.Point', 1, 2); return p:Translate(3)");
```

Names are ordinal and case-sensitive, and the bridge searches only assemblies already loaded by
the application. Prefer `Full.Type.Name.Member` over a bare member name, which applies to every
allowlisted type. A capability that requires an empty allowlist fails closed.

## 2. Add delegates and events when required

Add `DelegateConversion` and list exact delegate types in `AllowedDelegateTypeNames`. Add
`EventSubscription` and list exact events in `AllowedEventNames`. Dispose every returned
`LuaClrSubscription`; disposal is idempotent and releases the rooted Lua callback.

Choose a `ThreadPolicy` that matches the host's execution model. Under `AnyThreadWhenIdle`, a
non-owner callback enters only when it atomically claims an idle state. A callback that yields,
re-enters a busy state, or arrives through a disallowed thread fails closed.

## 3. Drive timers from the game loop

Grant `Timers`, configure resource bounds, and call `DispatchClrTimers` only while the state is idle:

```csharp
var options = LuaHostOptions.Restricted with
{
    Clr = new LuaClrOptions
    {
        Capabilities = LuaClrCapabilities.Timers,
        InstallGlobalModule = true,
        TimeProvider = TimeProvider.System,
        MaximumTimerCount = 4096,
        MaximumTimerDispatchCount = 256,
    },
};
using var host = new LuaHost(options);
host.RunUtf8("heartbeat=clr.timer(function(tick,missed) last_tick=tick; missed_ticks=missed end,0,50,'coalesce')");

while (running)
{
    host.DispatchClrTimers(256);
    RunGameFrame();
}
```

Timers do not own workers and never enter Lua from a thread-pool callback. Select `skip`,
`coalesce`, or `catch_up` according to the required missed-tick behavior, and keep the configured
dispatch and catch-up limits appropriate for one frame.

## 4. Preserve one host resource across patches

Create a stable handle for a native or host object that must retain one identity and owner:

```csharp
var userdata = host.ClrBridge.CreateStableResource(
    "world-session",
    worldSession,
    ownsResource: true);
host.State.SetGlobal("world_session", LuaValue.FromUserdata(userdata));
```

Keep the resource's runtime type and accessed members allowlisted. In the signed patch manifest,
use a `HostResource + Continue` migration rule for its module-cache path; the candidate module must
place a placeholder at that path rather than constructing another native resource. See the
[hot-update lifecycle explanation](hot-update-lifecycle.pub.md) and
[patch manifest reference](hot-update-reference.pub.md) for publication and rollback behavior.

## 5. Prepare trimming and NativeAOT publication

Preserve the public constructors, members, and delegate signatures of every allowlisted application
type with linker metadata such as `DynamicDependency`. Lunil preserves its delegate callback
adapter and `Task<TResult>.Result` metadata, but missing application metadata still fails closed.
Follow [How to publish with .NET NativeAOT and trimming](nativeaot-build-integration.pub.md) for the
complete publish procedure.

The configured bridge now exposes only the requested CLR surface. `NoMatchingConstructor`,
`NoMatchingMember`, `ThreadDenied`, and generation-closed errors identify the boundary that rejected
an operation; consult the [reference](clr-interop-reference.pub.md) for the applicable rule.
