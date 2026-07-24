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
- `clr.timer(callback, dueMs [, periodMs [, policy [, maxCatchUp]]])` — create a host-polled timer.
- `clr.cancel_timer(timer)` — cancel a timer without requiring the general disposal capability.
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

`LuaClrTask` wrappers created while a module frame calls CLR code use the same generation barrier.
`clr.await` checks admission before waiting and again before converting the result: previous tasks
become stale after publication, candidate tasks remain externally pending until publication, and
rollback restores only the previous generation. The owning candidate loader may await its own task
while staging state. Other inactive wrappers fail with `AsyncGenerationClosed`; the underlying CLR
`Task` is not cancelled. Host-side CLR calls made without a running module frame remain host-owned
and are not fenced. Use `LuaClrTask.IsActive` and the bridge's `ActiveTaskCount`,
`PendingTaskCount`, `QuiescedTaskCount`, and `StaleTaskCount` gauges before consuming `Task` directly
from host integration code.

## Game-loop timers

Grant `Timers` to create `LuaClrTimer` instances. Timers do not own a worker and never enter Lua from
a thread-pool callback. The game loop advances them explicitly while the state is idle:

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

The callback receives its one-based dispatched tick and the number of elapsed ticks omitted by that
dispatch. `skip` schedules the next period from the poll time, `coalesce` preserves the original
phase and reports omitted ticks, and `catch_up` dispatches elapsed ticks individually up to
`MaximumCatchUpTicks` per poll. One-shot timers omit `periodMs`. Timer count, per-poll dispatch,
duration, and catch-up bounds are validated before scheduling. Scheduling uses the configured
`TimeProvider` monotonic timestamp, so wall-clock corrections do not move due ticks. Dispatch from a
busy state or a non-owner thread fails closed; callback execution uses the host's interpreter budgets.

Module-owned timers join the patch generation barrier. Previous timers are paused with their
remaining delay, candidate timers are unscheduled while pending, publication makes only candidates
active, and execution/migration/barrier/health rollback restores only previous timers. A quiesced
timer cannot be cancelled before the outcome is known. Monitor `LuaClrTimer.IsActive` together with
`ActiveTimerCount`, `PendingTimerCount`, `QuiescedTimerCount`, and `StaleTimerCount`. A signed
`Timer + Continue` migration rule transfers remaining delay and counters to the candidate timer at
the same state path, so the next tick uses the candidate callback and scheduling policy.

## Ownership and deployment

`LuaClrObject` owns constructed `IDisposable` instances by default and forwards `Dispose` at most once;
set `OwnConstructedObjects=false` for host-owned instances. Userdata, callbacks, subscriptions,
tasks, and timers belong to one `LuaState` and cannot be transferred to another state.

Use a stable resource handle when a host or native object must keep one identity and one owner across
patch generations:

```csharp
var userdata = host.ClrBridge.CreateStableResource(
    "world-session",
    worldSession,
    ownsResource: true);
host.State.SetGlobal("world_session", LuaValue.FromUserdata(userdata));
```

With `MemberAccess` enabled, the resource's exact runtime type, assembly, and accessed members must
still be allowlisted. Host-side in-flight work can call
`LuaPatchStableResourceHandle.AcquireLease()`; CLR member calls acquire a lease for the invocation,
and event subscriptions retain one until unsubscribe. Disposing the handle rejects new access. An
owned `IDisposable` or `IAsyncDisposable` resource is released after its final lease closes; a
non-owning handle only closes access. The userdata remains bound to its original `LuaState`.

To carry this identity through a patch, declare a `HostResource + Continue` rule for its module-cache
path. The candidate module should put only a placeholder at that path, not construct a second native
resource. See [Production hot update](hot-update.md#state-schema-and-resource-migration) for migration,
rollback, and `RejectIfActive` behavior.

Trimming and NativeAOT applications must preserve public constructors, members, and delegate
signatures for every allowlisted type with linker metadata such as `DynamicDependency`. Missing
metadata fails closed with a stable bridge diagnostic. Interpreter and dynamic JIT share the same
bridge implementation and conversion rules.
