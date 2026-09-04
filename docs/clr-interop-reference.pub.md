# CLR interoperation reference

[简体中文](clr-interop-reference.zh-CN.pub.md)

This reference lists the callable Lua surface, conversion rules, timer policies, lifecycle gauges,
and ownership contracts of Lunil's CLR bridge. For setup steps, see
[How to configure CLR interoperation](clr-interop.pub.md).

## Global `clr` functions

| Function | Contract |
| --- | --- |
| `clr.type(fullName)` | Returns allowlisted type metadata and public constructor descriptions. |
| `clr.new(fullName, ...)` | Selects a constructor deterministically and returns owned userdata. |
| `clr.members(fullName)` | Returns metadata for allowlisted members. |
| `clr.get(target, name [, index...])` | Reads an allowlisted property, field, or indexer. |
| `clr.set(target, name, value)` | Writes an allowlisted property or field. |
| `clr.call(target, name, ...)` | Invokes an instance method/operator; a type name target selects a static member. |
| `clr.on(target, event, callback)` | Returns a disposable `LuaClrSubscription`. |
| `clr.await(task)` | Synchronously waits for `Task`/`ValueTask` userdata and converts the result. |
| `clr.cancellation()` | Creates a bridge-owned cancellation token source. |
| `clr.cancel(value)` | Signals a bridge-owned cancellation token source. |
| `clr.timer(callback, dueMs [, periodMs [, policy [, maxCatchUp]]])` | Creates a host-polled timer. |
| `clr.cancel_timer(timer)` | Cancels a timer without the general disposal capability. |
| `clr.dispose(value)` | Idempotently disposes bridge userdata or a subscription. |
| `clr.generic(definitionName, typeArgumentNames...)` | Resolves an explicitly registered closed generic type. |
| `clr.next(iterator)` | Advances one bounded projected iterator. |
| `clr.link_iterator(iterator, cancellation)` | Links iterator disposal to bridge cancellation. |

Constructed userdata also exposes allowlisted properties, fields, methods, indexers, and CLR
operators through ordinary Lua indexing and calls. Method lookup returns a bound function;
`object.method(x)` and `object:method(x)` are both accepted.

## Allowlist matching and limits

- Assembly, type, member, event, and delegate names use ordinal, case-sensitive matching.
- The bridge never loads an assembly by name; it searches already-loaded assemblies.
- A capability that requires an allowlist fails closed when that list is empty.
- A bare member entry applies to every allowlisted type; `Full.Type.Name.Member` scopes it to one
  type.
- If one type exceeds `MaximumCachedMembers` across allowlisted members and overload candidates,
  discovery and access fail with `MemberNotFound`; candidates are not truncated.

## Conversion and overload selection

Candidates are filtered by arity, optional/default parameters, and named host-side arguments. The
lowest total conversion cost wins, with ordinal parameter signatures as the tie-breaker.

Supported inputs include nil to reference/nullable types, booleans, strings/chars, exact enum names
and integer values, CLR numeric types with overflow checks, arrays and `ValueTuple` values represented
by Lua tables, `LuaValue`, compatible CLR userdata, and primitive `object` fallback. Rectangular CLR
arrays and jagged arrays become recursively nested one-based tables. Unsupported values produce
`NoMatchingConstructor` or `NoMatchingMember`. Lua floats bound to integral CLR parameters must
carry an exact integer representation; fractional values fail overload resolution instead of
rounding. Instance members of boxed value-type userdata reject writes with `MemberNotAllowed`
because the mutation would only reach a copy.

`EnumRepresentation` defaults to exact name strings and can instead use underlying integers or
`{ name, value }` tables. `DecimalRepresentation` defaults to invariant exact strings;
`ExactInteger` accepts only integral values representable by Lua integers, while `LossyFloat`
explicitly permits precision loss. `ulong` through `long.MaxValue` becomes a Lua integer; larger
values fail explicitly.

`CollectionProjection` defaults to `TablesAndIterators`: lists and dictionaries become tables and
other enumerables become bounded iterators. `Userdata` disables projection. `ConversionLimits`
bounds recursive depth, total items, estimated bytes, and rejects cycles.

Methods with `ref`/`out` parameters use `RefOutRepresentation`. The default
`PositionalAndNamedTable` returns positional values plus a table keyed by stable CLR parameter names;
`Positional` and `NamedTable` select one representation. `Task` and `ValueTask` results become
`LuaClrTask`. `LuaClrCancellation` converts to `CancellationToken`; nil maps to
`CancellationToken.None`. CLR exceptions become `LuaClrException`/catchable Lua errors.
`IncludeExceptionMessages` controls whether host exception messages are exposed.

## Callback and task contracts

- `LuaClrBridge.CreateDelegate` validates every parameter and return type before creating a delegate.
- `LuaClrSubscription.Dispose` is idempotent, detaches the handler, and releases the Lua callback.
- A callback invoked directly through the bridge cannot yield. A void delegate callback or
  host-polled timer attached to `LuaGameLoopHost` can yield because the game-loop host runs it as a
  scheduled coroutine and resumes it on a later matching tick. Entry still follows `ThreadPolicy`
  and the owning `LuaState` execution boundary.
- `clr.await` rejects an incomplete task with `AsyncFailed` when the calling thread has a
  `SynchronizationContext`.
- Inactive generation-owned tasks fail with `AsyncGenerationClosed`; the underlying CLR `Task` is
  not cancelled.

Lifecycle properties and gauges are:

| Resource | Instance state | Bridge gauges |
| --- | --- | --- |
| Callback/subscription | `LuaClrSubscription.IsActive` | `ActiveCallbackCount`, `PendingCallbackCount`, `QuiescedCallbackCount`, `StaleCallbackCount` |
| Task | `LuaClrTask.IsActive` | `ActiveTaskCount`, `PendingTaskCount`, `QuiescedTaskCount`, `StaleTaskCount` |
| Timer | `LuaClrTimer.IsActive` | `ActiveTimerCount`, `PendingTimerCount`, `QuiescedTimerCount`, `StaleTimerCount` |

## Timer policies

The callback receives a one-based dispatched tick and the number of elapsed ticks omitted by that
dispatch. Omit `periodMs` for a one-shot timer.

| Policy | Behavior |
| --- | --- |
| `skip` | Schedules the next period from the current poll time. |
| `coalesce` | Preserves the original phase and reports omitted ticks. |
| `catch_up` | Dispatches elapsed ticks individually, bounded by `MaximumCatchUpTicks` per poll. |

Timer count, per-poll dispatch, duration, and catch-up limits are validated before scheduling.
Scheduling uses the configured `TimeProvider` monotonic timestamp. Dispatch from a busy state or a
non-owner thread fails closed, and callbacks use the host's interpreter budgets.

## Ownership and binding modes

`LuaClrObject` owns constructed `IDisposable` instances by default and calls `Dispose` at most once.
Set `OwnConstructedObjects=false` for host-owned instances. Userdata, callbacks, subscriptions,
tasks, timers, and stable-resource userdata belong to one `LuaState` and cannot move to another.

`LuaPatchStableResourceHandle.AcquireLease()` protects host-side in-flight work. Member calls hold a
lease for the invocation; event subscriptions retain one until unsubscribe. Disposal rejects new
access. An owned `IDisposable` or `IAsyncDisposable` resource is released after its final lease;
non-owning handles only close access.

`RegistryThenReflection` is the compatibility default for trusted .NET hosts and still requires
exact allowlists. `RegistryOnly` requires `LuaClrOptions.BindingRegistry` and never falls back to
reflection; it is the binding mode for NativeAOT, Unity IL2CPP, strict trimming, and deterministic
hosts. Generated bindings do not grant access without matching capabilities and allowlists.
