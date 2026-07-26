# How CLR bridge lifecycles work

[简体中文](clr-interop-lifecycle.zh-CN.pub.md)

Lunil treats CLR access as an explicit host boundary rather than unrestricted reflection. This
page explains why allowlists, state ownership, callback admission, and generation fencing are part
of one lifecycle model. For configuration steps and exact API contracts, use the
[how-to guide](clr-interop.pub.md) and [reference](clr-interop-reference.pub.md).

## Capability and allowlist layers

A capability answers which class of operation may occur; an allowlist answers which application
identity may participate. Requiring both prevents enabling construction, member access, delegates,
events, or timers from implicitly exposing every loaded type. Searching only loaded assemblies also
keeps assembly loading under the application's control.

Fully qualified member names reduce future privilege expansion: adding a second allowlisted type
cannot make an existing bare member entry apply unexpectedly. Resource limits such as
`MaximumCachedMembers` fail the whole lookup instead of truncating candidates, preserving
deterministic overload selection.

## Conversion and asynchronous boundaries

Overload selection assigns stable costs to supported Lua-to-CLR conversions and uses ordinal
signatures to break ties. Values that cannot preserve the declared CLR contract fail explicitly;
for example, a `ulong` beyond `long.MaxValue` does not silently become unrelated userdata.

`clr.await` is synchronous by design, but it refuses to block an incomplete task on a thread with a
`SynchronizationContext`. That boundary avoids deadlocking a single-thread game loop. An
asynchronous host should consume `LuaClrTask.Task` through its scheduler and resume Lua at an
explicit host-controlled boundary.

## One execution owner per state

CLR userdata, callbacks, tasks, subscriptions, and timers retain their originating `LuaState`.
Callbacks may enter only through the same per-state execution boundary used by the interpreter and
JIT. `AnyThreadWhenIdle` permits a non-owner thread to claim an idle state atomically, but it does
not permit concurrent entry, re-entry of a busy state, or yielding through a CLR callback.

Timers follow the same rule by avoiding worker threads entirely. The host polls them while the
state is idle, making scheduling cost and callback entry part of the game loop's explicit budget.

## Generation fencing during hot update

When a module frame creates a delegate, subscription, task, or timer, the resource is associated
with that module generation. Patch preparation quiesces previous-generation resources and keeps
candidate resources pending. Publication activates only candidate resources; execution,
migration, barrier, or health rollback rejects candidates and restores only the previous
generation.

This transaction prevents callbacks or task results from crossing into code whose state contract
has already been replaced. `clr.await` checks admission both before waiting and before converting a
result. Candidate loaders may await their own staged tasks, but inactive external consumers fail
closed. The underlying CLR task is not cancelled because generation admission and application work
cancellation are separate concerns.

Event handlers are detached and reattached with the same publication transaction. Timers retain
their remaining delay while quiesced. A signed `Timer + Continue` migration transfers remaining
delay and counters to the candidate timer at the same state path, so its next tick uses candidate
code and policy.

## Stable resource identity and leases

A native or host resource often must outlive Lua module generations without gaining two owners.
`LuaPatchStableResourceHandle` separates the stable identity from the generation-specific userdata
placeholder. A `HostResource + Continue` rule transfers that identity instead of constructing a
second native object.

Leases keep in-flight host work valid while the handle closes. Disposal rejects new access, then
waits for the final member-call, event-subscription, or explicit host lease before releasing an
owned resource. A non-owning handle closes access without disposing the application object. This
provides one identity, one owner, and rollback-safe admission across patch generations.
