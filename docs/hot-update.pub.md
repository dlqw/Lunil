# How to deploy signed patch bundles

[简体中文](hot-update.zh-CN.pub.md)

This guide deploys Lunil module replacements through a signed patch bundle. It assumes an existing
`LuaHost`, a deployment control plane, an application release ledger, and ECDSA P-256 signing keys.
The procedure validates and stages every candidate before opening a game-loop update window.

Use the [patch reference](hot-update-reference.pub.md) for exact fields, limits, statuses, and
telemetry names. Read [How hot-update publication works](hot-update-lifecycle.pub.md) for transaction,
generation, rollback, and distributed-barrier semantics.

## 1. Create and inspect the bundle

Put replacement payloads under one root and describe them with a canonical manifest. Declare the
current and target revision, update intent, runtime contract, target labels, requested admission
capabilities, dependencies, expiry, and nonce. Pack with a protected private key, then inspect and
dry-run the result with public trust material:

```text
lunil patch pack manifest.json payload --output update.lpatch --private-key private.pem --key-id release-2026
lunil patch verify update.lpatch --trust-store patch-trust.json
lunil patch inspect update.lpatch --trust-store patch-trust.json
lunil patch dry-run update.lpatch --trust-store patch-trust.json
```

Use one stable target-label snapshot throughout preparation and commit. If environment, region,
shard, platform, or ring assignment changes, discard the prepared patch and prepare it again.

## 2. Configure trust, acceptance, and replay protection

Build a trust store from current P-256 public keys and configure activation, retirement, or
revocation instants as required. Then bind preparation to the host's current build, revision,
runtime ABI, channel, granted admission capabilities, target labels, release ledger, and rollback
authorization:

```csharp
var trustStore = new LuaPatchEcdsaTrustStore([
    new LuaPatchTrustedEcdsaKey("release-2026-q3", q3PublicKey)
    {
        ValidFrom = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        ValidUntil = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero),
    },
]);

using var stream = File.OpenRead("update.lpatch");
var bundle = LuaPatchBundle.Read(stream, trustStore);
var replayStore = new LuaPatchFileReplayStore("state/accepted-patches.ndjson");
var prepareOptions = new LuaPatchPrepareOptions
{
    AcceptancePolicy = new LuaPatchAcceptancePolicy
    {
        TargetBuild = currentBuild,
        CurrentRevision = currentRevision,
        RuntimeAbi = "lunil-0.12",
        AllowedChannels = ["production"],
        GrantedCapabilities = hostPatchCapabilities,
        TargetLabels = targetLabels,
        RevisionClassifier = releaseLedger.Classify,
        RollbackAuthorizer = (manifest, signer) =>
            rollbackKeyIds.Contains(signer.KeyId) &&
            approvedRollbackTargets.Contains(manifest.TargetRevision),
    },
    ReplayStore = replayStore,
    ReplayScope = "zone-01",
};
```

Use a stable deployment-target identity for `ReplayScope`, not a process id. Keep the replay store
on lock-correct local storage shared by processes that may operate the same target, or implement
the same reservation and exclusive commit-lease state machine in a transactional database.

## 3. Prepare candidates outside the update window

Share one limiter across hosts so compilation fan-out has bounded concurrency and queue depth:

```csharp
var limiter = new LuaPatchPreparationLimiter(
    maximumConcurrency: Math.Max(1, Environment.ProcessorCount / 2),
    maximumQueueLength: 64);

prepareOptions = prepareOptions with
{
    PreparationLimiter = limiter,
    PreparationWaitTimeout = TimeSpan.FromMilliseconds(250),
    StateMigrationAdapters = stateAdapters,
    ResourceMigrationAdapters = resourceAdapters,
};

var preparation = await host.PreparePatchAsync(bundle, prepareOptions, stoppingToken);
if (preparation.Status == LuaPatchPrepareStatus.Deferred)
{
    ScheduleRetry(preparation.AdmissionStatus);
    return;
}
if (!preparation.Succeeded)
{
    ReportPreparationFailure(preparation);
    return;
}
```

Preparation compiles and verifies candidates in isolation, captures expected live module revisions,
and reserves replay identity without running candidate loaders. A migration schema requires the
matching live schema version and every named adapter before preparation can succeed.

## 4. Commit at a game-loop safe point

Between frames, open a bounded update window and commit on the same thread:

```csharp
var opened = host.TryOpenPatchUpdateWindow(new LuaPatchUpdateWindowOptions
{
    WaitTimeout = TimeSpan.Zero,
    MaximumDuration = TimeSpan.FromMilliseconds(8),
}, stoppingToken);
if (!opened.Succeeded)
{
    ScheduleForLaterFrame(preparation.PreparedPatch!);
    return;
}

using var window = opened.Window!;
var commit = host.CommitPatch(
    preparation.PreparedPatch!,
    window,
    new LuaPatchCommitOptions
    {
        MaximumPauseDuration = TimeSpan.FromMilliseconds(4),
    },
    stoppingToken);
```

Handle every non-success status. Expiry, revision drift, migration failure, pause-budget exhaustion,
or cancellation leaves the old module graph active. Because candidate code can call host-visible
services, treat `SideEffectsMayHaveOccurred` as a requirement for application-level reconciliation.

## 5. Declare state and resource migration

Register the live schema version and use disjoint RFC 6901 state paths. When creating a bundle
through the API, serialize the complete schema and append it as the canonical companion entry:

```csharp
host.SetPatchStateSchemaVersion("game-state", "42");

var schema = new LuaPatchMigrationSchema
{
    SchemaId = "game-state",
    BaseVersion = "42",
    TargetVersion = "43",
    Modules =
    [
        new LuaPatchModuleMigrationSchema
        {
            ModuleName = "game.match",
            State =
            [
                new LuaPatchStateRule
                {
                    TargetPath = "/match/state",
                    Kind = LuaPatchStateRuleKind.PatchTable,
                },
            ],
            Resources =
            [
                new LuaPatchResourceRule
                {
                    ResourceId = "world-session",
                    Kind = LuaPatchResourceKind.HostResource,
                    Disposition = LuaPatchResourceDisposition.Continue,
                    StatePath = "/session",
                },
            ],
        },
    ],
};

var schemaEntry = new LuaPatchEntry(
    LuaPatchMigrationSchemaFormat.BundleEntryName,
    moduleName: null,
    LuaPatchEntryKind.CompanionData,
    LuaPatchMigrationSchemaSerializer.Serialize(schema));

var bundle = LuaPatchBundle.Create(
    manifest,
    replacementEntries.Append(schemaEntry),
    signer);
```

For `lunil patch pack`, write the serialized bytes to
`<payload-root>/migration/schema.json` and include that path in the input manifest as a
`CompanionData` entry with no module name. The pack command reconstructs and signs the canonical
bundle from those descriptors and files.

Use `PatchTable` when external aliases require one table identity. Use `HostResource + Continue`
for one native-resource identity, `Coroutine + Continue` for an admitted suspended thread, and
`Timer + Continue` to transfer remaining delay into the candidate timer. Do not create a duplicate
native resource in the candidate loader. Application-defined cancellation, restart, drain, or
transformation requires reversible adapters.

## 6. Roll out through isolated rings

Prepare every target from the same canonical manifest. Isolate traffic, wait for quiescence, and
deploy canary and production rings in order:

```csharp
using var journal = new LuaPatchFileJournal("state/hot-update/deploy.ndjson");
var plan = new LuaPatchRolloutPlan
{
    RolloutId = "game-2026-07-22-01",
    Rings =
    [
        new LuaPatchRolloutRing { Name = "canary", Targets = canaryTargets },
        new LuaPatchRolloutRing { Name = "production", Targets = productionTargets },
    ],
};

var result = new LuaPatchCoordinator().Deploy(plan, new LuaPatchCoordinatorOptions
{
    RequireTargetIsolation = true,
    Journal = journal,
    TargetLifecycle = lifecycleOptions,
    UpdateWindow = updateWindowOptions,
    Commit = commitOptions,
    GenerationGuard = generationGuard,
    HealthCheck = CheckRingHealth,
}, stoppingToken);
```

Do not route a target after a restoration failure. For a ring spanning processes, configure one
`DistributedBarrier` membership and quorum in every participant, use the same rollout id and ring
name, and retain terminal barrier state until every process and operator can observe the decision.

## 7. Recover incomplete deployment transactions

At service startup, reconcile incomplete journal transactions with application-owned durable state
and routing state:

```csharp
using var journal = new LuaPatchFileJournal("state/hot-update/deploy.ndjson");
var pending = journal.GetIncompleteTransactions();
var recovered = journal.RecoverIncomplete(recoveryHandler);
```

Return `Committed` or `RolledBack` only after the application establishes the authoritative outcome.
Return `Manual` when automated reconciliation is insufficient. Keep the journal, replay store, and
distributed-barrier store under appropriate operating-system permissions and storage durability.

## 8. Export health and telemetry

Export preparation-limiter gauges, generation snapshots, bounded rollout history, activity traces,
and patch metrics. Alert on transition residue, stale-resource growth, replay or journal corruption,
history recording failures, recovery backlog, and any target left isolated. The exact counters,
activity names, metric names, and default resource limits are listed in the
[patch reference](hot-update-reference.pub.md).
