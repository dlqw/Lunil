# Integrate Lunil with a game-engine loop

[简体中文](game-engine-hosting.zh-CN.pub.md)

This how-to adapts an engine's main thread, clocks, assets, storage, and frame phases to
`LuaGameLoopHost`. Unity and Godot packages provide these adapters; custom engines implement the
same contracts.

## 1. Create engine services

Provide only the services your host needs:

- `ILuaGameLoopDispatcher` — checks main-thread access and queues callbacks.
- `TimeProvider` — supplies the engine clock used by CLR timers.
- `ILuaGameLoopAssetResolver` — reads immutable asset bytes.
- `ILuaModuleResolver` — resolves Lua modules for compilation and `require`.
- `ILuaGameLoopPersistentStore` — reads and writes exact byte snapshots.
- `ILuaConsole` — routes Lua output to the engine console.

The dispatcher must report access from the construction thread. Invalid budgets, a foreign
dispatcher owner, reentrant ticks, or disposal inside a tick fail immediately.

## 2. Compose the host

```csharp
var options = new LuaGameLoopHostOptions
{
    HostOptions = LuaHostOptions.Restricted with
    {
        ExecutionBackend = LuaHostExecutionBackend.Interpreter,
        ModuleResolver = moduleResolver,
    },
    Dispatcher = dispatcher,
    TimeProvider = engineTime,
    Console = console,
    ModuleResolver = moduleResolver,
    AssetResolver = assets,
    PersistentStore = store,
    MaximumCallbacksPerTick = 1_024,
    MaximumInstructionsPerTick = 1_000_000,
    MaximumQueuedWork = 65_536,
};

using var loop = new LuaGameLoopHost(options);
```

## 3. Schedule by phase

`Start` defaults to `Update` and resumes a yielded operation on its next matching tick.

```csharp
var update = loop.Start(updateCompilation);
var physics = loop.Start(physicsCompilation, options: new LuaGameLoopStartOptions
{
    Phase = LuaGameLoopPhase.FixedUpdate,
    ResumePolicy = LuaGameLoopResumePolicy.NextTick,
});

LuaGameLoopTickResult updateResult = loop.Tick();
LuaGameLoopTickResult fixedResult = loop.TickFixed();
```

Inspect `Failures` on every result. One callback failure is recorded without silently aborting the
remaining queue. `ExecutedInstructionCount`, completed/suspended/cancelled counts, and remaining
work are available for engine telemetry.

## 4. Publish patches at a frame boundary

Prepare and validate a signed patch before entering the frame boundary, then commit it there.
`productionPrepareOptions` must include `AcceptancePolicy`, `ReplayStore`, and a stable `ReplayScope`
for the target:

```csharp
var prepared = loop.Host.PreparePatch(bundle, productionPrepareOptions);
if (!prepared.Succeeded) throw new InvalidOperationException(prepared.Message);

loop.PublishAtFrameBoundary(host =>
{
    var opened = host.TryOpenPatchUpdateWindow();
    if (!opened.Succeeded) throw new InvalidOperationException(opened.Message);
    using (opened.Window)
    {
        var committed = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        if (!committed.Succeeded) throw new InvalidOperationException(committed.Message);
    }
});
```

The publication becomes visible before scheduled work in the next tick. Work queued for a disposed
generation is rejected rather than applied to a replacement host.

Build the trust, target/revision/channel/capability/rollback, replay, and migration policy in
`productionPrepareOptions` as shown in
[Deploy signed patch bundles](deploy-signed-patch-bundles.pub.md#1-configure-trust-admission-replay-and-migration).

## 5. Shut down on the owner thread

Cancel outstanding operations, stop accepting callbacks, disconnect engine events, and call
`Dispose` on the construction thread. Engine adapters expose `Shutdown` for this sequence.

See [Unity hosting](unity-hosting.pub.md), [Godot hosting](godot-hosting.pub.md), and the
[signed patch reference](signed-patch-bundles.pub.md).
