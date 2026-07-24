using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Lunil.Core;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.StandardLibrary;

namespace Lunil.Hosting.Tests;

public sealed class LuaPatchMigrationTests
{
    [Fact]
    public void CanonicalSignedSchemaPreservesSelectedStateIntoReplacementModule()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "local function get() return 'old' end; " +
                "return {state={score=41},get=get}",
            ["mods/b.lua"] = "local a=require('a'); return {seen=a.state.score}",
        });
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; require('a'); require('b')").Succeeded);
        var schema = Schema(
            new LuaPatchModuleMigrationSchema
            {
                ModuleName = "a",
                State =
                [
                    new LuaPatchStateRule
                    {
                        TargetPath = "/state/score",
                        Kind = LuaPatchStateRuleKind.Preserve,
                    },
                ],
            },
            new LuaPatchModuleMigrationSchema { ModuleName = "b" });
        var bundle = CreateBundle(
            schema,
            Entry(
                "a",
                "local function get() return 'new' end; " +
                "return {state={score=0,level=2},get=get}"),
            Entry(
                "b",
                "local a=require('a'); return {seen=a.state.score}",
                "a"));

        var prepared = host.PreparePatch(bundle);
        Assert.True(prepared.Succeeded, prepared.Message);
        Assert.NotNull(prepared.PreparedPatch!.MigrationSchema);
        var commit = Commit(host, prepared.PreparedPatch);

        Assert.True(commit.Succeeded, commit.Message);
        Assert.True(host.TryGetPatchStateSchemaVersion("game-state", out var schemaVersion));
        Assert.Equal("3", schemaVersion);
        var values = host.RunUtf8(
            "local a=require('a'); return a.state.score,a.state.level,a.get(),require('b').seen")
            .Execution!.Values;
        Assert.Equal(41, values[0].AsInteger());
        Assert.Equal(2, values[1].AsInteger());
        Assert.Equal("new", values[2].AsString().ToString());
        Assert.Equal(41, values[3].AsInteger());
    }

    [Fact]
    public void MissingRequiredStatePathAbortsAndRestoresOldModule()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {state={score=7}}",
        });
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        Assert.True(host.State.TryGetModule("a", out var old));
        var schema = Schema(new LuaPatchModuleMigrationSchema
        {
            ModuleName = "a",
            State =
            [
                new LuaPatchStateRule
                {
                    TargetPath = "/state/missing",
                    Kind = LuaPatchStateRuleKind.Preserve,
                },
            ],
        });
        var prepared = host.PreparePatch(CreateBundle(
            schema,
            Entry("a", "return {state={missing=0}}")));
        Assert.True(prepared.Succeeded, prepared.Message);

        var commit = Commit(host, prepared.PreparedPatch!);

        Assert.Equal(LuaPatchCommitStatus.MigrationFailed, commit.Status);
        Assert.True(host.TryGetPatchStateSchemaVersion("game-state", out var schemaVersion));
        Assert.Equal("2", schemaVersion);
        Assert.True(host.State.TryGetModule("a", out var current));
        Assert.Equal(old!.Revision, current!.Revision);
        Assert.Equal(7, host.RunUtf8("return require('a').state.score")
            .Execution!.Values[0].AsInteger());
    }

    [Fact]
    public void UserdataAdapterMigratesPayloadAndIsRolledBackWhenLaterModuleFails()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {payload=old_payload}",
            ["mods/b.lua"] = "return {value=1}",
        });
        var oldPayload = new MutablePayload { Value = 17 };
        var newPayload = new MutablePayload { Value = 0 };
        host.State.SetGlobal(
            "old_payload",
            LuaValue.FromUserdata(host.State.CreateUserdata(oldPayload)));
        host.State.SetGlobal(
            "new_payload",
            LuaValue.FromUserdata(host.State.CreateUserdata(newPayload)));
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; require('a'); require('b')").Succeeded);
        var schema = Schema(
            new LuaPatchModuleMigrationSchema
            {
                ModuleName = "a",
                State =
                [
                    new LuaPatchStateRule
                    {
                        TargetPath = "/payload",
                        Kind = LuaPatchStateRuleKind.HostAdapter,
                        AdapterId = "payload-v1",
                    },
                ],
            },
            new LuaPatchModuleMigrationSchema { ModuleName = "b" });
        var prepared = host.PreparePatch(
            CreateBundle(
                schema,
                Entry("a", "return {payload=new_payload}"),
                Entry("b", "error('later candidate failed')", "a")),
            new LuaPatchPrepareOptions
            {
                StateMigrationAdapters = new Dictionary<string, ILuaPatchStateMigrationAdapter>
                {
                    ["payload-v1"] = new PayloadMigrationAdapter(),
                },
            });
        Assert.True(prepared.Succeeded, prepared.Message);

        var commit = Commit(host, prepared.PreparedPatch!);

        Assert.Equal(LuaPatchCommitStatus.ExecutionFailed, commit.Status);
        Assert.Equal(0, newPayload.Value);
        var retained = host.RunUtf8("return require('a').payload==old_payload")
            .Execution!.Values[0];
        Assert.True(retained.AsBoolean());
    }

    [Fact]
    public void ActiveCoroutineCanRejectPatchAtMigrationBoundary()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "return {co=coroutine.create(function() coroutine.yield('pause') end)}",
        });
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        Assert.True(host.State.TryGetModule("a", out var old));
        var schema = Schema(new LuaPatchModuleMigrationSchema
        {
            ModuleName = "a",
            Resources =
            [
                new LuaPatchResourceRule
                {
                    ResourceId = "worker",
                    Kind = LuaPatchResourceKind.Coroutine,
                    Disposition = LuaPatchResourceDisposition.RejectIfActive,
                    StatePath = "/co",
                },
            ],
        });
        var prepared = host.PreparePatch(CreateBundle(
            schema,
            Entry("a", "return {co=coroutine.create(function() return 2 end)}")));
        Assert.True(prepared.Succeeded, prepared.Message);

        var commit = Commit(host, prepared.PreparedPatch!);

        Assert.Equal(LuaPatchCommitStatus.MigrationFailed, commit.Status);
        Assert.Contains("worker", commit.Message, StringComparison.Ordinal);
        Assert.True(host.State.TryGetModule("a", out var current));
        Assert.Equal(old!.Revision, current!.Revision);
    }

    [Fact]
    public void ContinueCoroutinePreservesTheLiveThreadAcrossCommit()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "return {version='old',co=coroutine.create(function() " +
                "coroutine.yield('old-worker') end)}",
        });
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; old_worker=require('a').co").Succeeded);
        var schema = Schema(new LuaPatchModuleMigrationSchema
        {
            ModuleName = "a",
            Resources =
            [
                new LuaPatchResourceRule
                {
                    ResourceId = "worker",
                    Kind = LuaPatchResourceKind.Coroutine,
                    Disposition = LuaPatchResourceDisposition.Continue,
                    StatePath = "/co",
                },
            ],
        });
        var prepared = host.PreparePatch(CreateBundle(
            schema,
            Entry(
                "a",
                "return {version='new',co=coroutine.create(function() " +
                "coroutine.yield('new-worker') end)}")));
        Assert.True(prepared.Succeeded, prepared.Message);

        var commit = Commit(host, prepared.PreparedPatch!);

        Assert.True(commit.Succeeded, commit.Message);
        var values = host.RunUtf8("""
            local module = require('a')
            local same = module.co == old_worker
            local ok, value = coroutine.resume(module.co)
            return module.version, same, ok, value
            """).Execution!.Values;
        Assert.Equal("new", values[0].AsString().ToString());
        Assert.True(values[1].AsBoolean());
        Assert.True(values[2].AsBoolean());
        Assert.Equal("old-worker", values[3].AsString().ToString());
    }

    [Fact]
    public void ContinueCoroutinePreservesSuspendedNativeYieldInOldGeneration()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "local generation='old'; local function worker() " +
                "return patch_native_yield(generation) end; " +
                "return {co=coroutine.create(worker)}",
        });
        host.State.SetGlobal(
            "patch_native_yield",
            LuaValue.FromFunction(new LuaNativeFunction("patch_native_yield", NativeYieldStep)));
        var started = host.RunUtf8(
            "package.path='mods/?.lua'; old_worker=require('a').co; " +
            "return coroutine.resume(old_worker)").Execution!.Values;
        Assert.True(started[0].AsBoolean());
        Assert.Equal("native-pause", started[1].AsString().ToString());
        var oldThread = host.State.GetGlobal("old_worker").AsThread();
        Assert.True(oldThread.IsPatchGenerationActive);
        Assert.Equal(1, host.ActiveNativeContinuationCount);

        var schema = Schema(new LuaPatchModuleMigrationSchema
        {
            ModuleName = "a",
            Resources =
            [
                new LuaPatchResourceRule
                {
                    ResourceId = "native-worker",
                    Kind = LuaPatchResourceKind.Coroutine,
                    Disposition = LuaPatchResourceDisposition.Continue,
                    StatePath = "/co",
                },
            ],
        });
        var prepared = host.PreparePatch(CreateBundle(
            schema,
            Entry(
                "a",
                "local generation='new'; local function worker() " +
                "return patch_native_yield(generation) end; " +
                "return {co=coroutine.create(worker)}")));
        Assert.True(prepared.Succeeded, prepared.Message);

        var commit = Commit(host, prepared.PreparedPatch!);

        Assert.True(commit.Succeeded, commit.Message);
        Assert.True(oldThread.IsPatchGenerationActive);
        Assert.Same(oldThread, host.RunUtf8("return require('a').co").Execution!.Values[0].AsThread());
        var resumed = host.RunUtf8("return coroutine.resume(require('a').co)")
            .Execution!.Values;
        Assert.True(resumed[0].AsBoolean());
        Assert.Equal("old", resumed[1].AsString().ToString());
        Assert.Equal(0, host.ActiveNativeContinuationCount);
    }

    [Fact]
    public void CommittedPatchRejectsUnmigratedNativeContinuationAndPublishesCandidate()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "local function worker() return patch_native_yield('old') end; " +
                "return {co=coroutine.create(worker)}",
        });
        host.State.SetGlobal(
            "patch_native_yield",
            LuaValue.FromFunction(new LuaNativeFunction("patch_native_yield", NativeYieldStep)));
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; old_worker=require('a').co; " +
            "return coroutine.resume(old_worker)").Execution!.Values[0].AsBoolean());
        var oldThread = host.State.GetGlobal("old_worker").AsThread();
        var schema = Schema(new LuaPatchModuleMigrationSchema { ModuleName = "a" });
        var prepared = host.PreparePatch(CreateBundle(
            schema,
            Entry(
                "a",
                "local co=coroutine.create(function() return patch_native_yield('new') end); " +
                "assert(coroutine.resume(co)); return {co=co}")));
        Assert.True(prepared.Succeeded, prepared.Message);

        var commit = Commit(host, prepared.PreparedPatch!);

        Assert.True(commit.Succeeded, commit.Message);
        var candidateThread = host.RunUtf8("return require('a').co").Execution!.Values[0].AsThread();
        Assert.False(oldThread.IsPatchGenerationActive);
        Assert.True(candidateThread.IsPatchGenerationActive);
        Assert.Equal(1, host.ActiveNativeContinuationCount);
        Assert.Equal(1, host.StaleNativeContinuationCount);
        var stale = Assert.Throws<LuaRuntimeException>(() =>
            new LuaInterpreter().Resume(host.State, oldThread));
        Assert.Contains("inactive patch generation", stale.Message, StringComparison.Ordinal);
        Assert.Equal(LuaThreadStatus.Suspended, oldThread.Status);
        var closed = new LuaInterpreter().Close(host.State, oldThread);
        Assert.Equal(LuaVmSignal.Completed, closed.Signal);
        Assert.Equal(LuaThreadStatus.Dead, oldThread.Status);
        var resumed = host.RunUtf8("return coroutine.resume(require('a').co)")
            .Execution!.Values;
        Assert.True(resumed[0].AsBoolean());
        Assert.Equal("new", resumed[1].AsString().ToString());
    }

    [Fact]
    public void HostOwnedNativeContinuationIsNotFencedByModulePatch()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        var native = LuaValue.FromFunction(new LuaNativeFunction(
            "host_native_yield",
            NativeYieldStep));
        var thread = host.State.CreateThread(native);
        var hostValue = LuaValue.FromString(host.State.Strings.GetOrCreate("host"u8));
        var started = new LuaInterpreter().Start(host.State, thread, [hostValue]);
        Assert.Equal(LuaVmSignal.Yielded, started.Signal);
        Assert.Equal(0, host.ActiveNativeContinuationCount);
        var schema = Schema(new LuaPatchModuleMigrationSchema { ModuleName = "a" });
        var prepared = host.PreparePatch(CreateBundle(schema, Entry("a", "return {value=2}")));
        Assert.True(prepared.Succeeded, prepared.Message);

        var commit = Commit(host, prepared.PreparedPatch!);

        Assert.True(commit.Succeeded, commit.Message);
        Assert.True(thread.IsPatchGenerationActive);
        var resumed = new LuaInterpreter().Resume(host.State, thread);
        Assert.Equal(LuaVmSignal.Completed, resumed.Signal);
        Assert.Equal("host", resumed.Values[0].AsString().ToString());
        Assert.Equal(0, host.StaleNativeContinuationCount);
    }

    [Fact]
    public void ContinueCoroutinePreservesNativeCallLuaContinuationAndCallbackFrame()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "local generation='old'; local function callback() " +
                "coroutine.yield('callback-pause'); return generation end; " +
                "local function worker() return patch_native_call(callback,generation) end; " +
                "return {co=coroutine.create(worker)}",
        });
        host.State.SetGlobal(
            "patch_native_call",
            LuaValue.FromFunction(new LuaNativeFunction("patch_native_call", NativeCallLuaStep)));
        var started = host.RunUtf8(
            "package.path='mods/?.lua'; old_worker=require('a').co; " +
            "return coroutine.resume(old_worker)").Execution!.Values;
        Assert.True(started[0].AsBoolean());
        Assert.Equal("callback-pause", started[1].AsString().ToString());
        Assert.Equal(1, host.ActiveNativeContinuationCount);

        var schema = Schema(new LuaPatchModuleMigrationSchema
        {
            ModuleName = "a",
            Resources =
            [
                new LuaPatchResourceRule
                {
                    ResourceId = "native-callback-worker",
                    Kind = LuaPatchResourceKind.Coroutine,
                    Disposition = LuaPatchResourceDisposition.Continue,
                    StatePath = "/co",
                },
            ],
        });
        var prepared = host.PreparePatch(CreateBundle(
            schema,
            Entry(
                "a",
                "local generation='new'; local function callback() return generation end; " +
                "local function worker() return patch_native_call(callback,generation) end; " +
                "return {co=coroutine.create(worker)}")));
        Assert.True(prepared.Succeeded, prepared.Message);

        var commit = Commit(host, prepared.PreparedPatch!);

        Assert.True(commit.Succeeded, commit.Message);
        var resumed = host.RunUtf8("return coroutine.resume(require('a').co)")
            .Execution!.Values;
        Assert.True(resumed[0].AsBoolean());
        Assert.Equal("old", resumed[1].AsString().ToString());
        Assert.Equal("old", resumed[2].AsString().ToString());
    }

    [Fact]
    public void CandidateNativeContinuationIsPendingAndExecutionFailureRestoresOldGeneration()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "local function worker() return patch_native_yield('old') end; " +
                "return {co=coroutine.create(worker)}",
        });
        host.State.SetGlobal(
            "patch_native_yield",
            LuaValue.FromFunction(new LuaNativeFunction("patch_native_yield", NativeYieldStep)));
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; old_worker=require('a').co; " +
            "return coroutine.resume(old_worker)").Execution!.Values[0].AsBoolean());
        var oldThread = host.State.GetGlobal("old_worker").AsThread();
        LuaThread? candidateThread = null;
        host.State.SetGlobal(
            "capture_candidate",
            LuaValue.FromFunction(new LuaNativeFunction(
                "capture_candidate",
                (_, arguments) =>
                {
                    candidateThread = arguments[0].AsThread();
                    Assert.Equal(1, host.PendingNativeContinuationCount);
                    Assert.Equal(1, host.QuiescedNativeContinuationCount);
                    Assert.False(oldThread.IsPatchGenerationActive);
                    Assert.False(candidateThread.IsPatchGenerationActive);
                    return [];
                })));
        var schema = Schema(new LuaPatchModuleMigrationSchema { ModuleName = "a" });
        var prepared = host.PreparePatch(CreateBundle(
            schema,
            Entry(
                "a",
                "local co=coroutine.create(function() return patch_native_yield('new') end); " +
                "assert(coroutine.resume(co)); capture_candidate(co); error('candidate failed')")));
        Assert.True(prepared.Succeeded, prepared.Message);

        var commit = Commit(host, prepared.PreparedPatch!);

        Assert.Equal(LuaPatchCommitStatus.ExecutionFailed, commit.Status);
        Assert.True(oldThread.IsPatchGenerationActive);
        Assert.NotNull(candidateThread);
        Assert.False(candidateThread.IsPatchGenerationActive);
        Assert.Equal(1, host.ActiveNativeContinuationCount);
        Assert.Equal(1, host.StaleNativeContinuationCount);
        var resumed = host.RunUtf8("return coroutine.resume(old_worker)").Execution!.Values;
        Assert.True(resumed[0].AsBoolean());
        Assert.Equal("old", resumed[1].AsString().ToString());
    }

    [Fact]
    public void HealthRollbackRestoresTransferredNativeContinuationAndRejectsCandidate()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "local function worker() return patch_native_yield('old') end; " +
                "return {co=coroutine.create(worker)}",
        });
        host.State.SetGlobal(
            "patch_native_yield",
            LuaValue.FromFunction(new LuaNativeFunction("patch_native_yield", NativeYieldStep)));
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; old_worker=require('a').co; " +
            "return coroutine.resume(old_worker)").Execution!.Values[0].AsBoolean());
        var oldThread = host.State.GetGlobal("old_worker").AsThread();
        LuaThread? candidateThread = null;
        host.State.SetGlobal(
            "capture_candidate",
            LuaValue.FromFunction(new LuaNativeFunction(
                "capture_candidate",
                (_, arguments) =>
                {
                    candidateThread = arguments[0].AsThread();
                    return [];
                })));
        var schema = Schema(new LuaPatchModuleMigrationSchema
        {
            ModuleName = "a",
            Resources =
            [
                new LuaPatchResourceRule
                {
                    ResourceId = "native-worker",
                    Kind = LuaPatchResourceKind.Coroutine,
                    Disposition = LuaPatchResourceDisposition.Continue,
                    StatePath = "/co",
                },
            ],
        });
        var prepared = host.PreparePatch(CreateBundle(
            schema,
            Entry(
                "a",
                "local co=coroutine.create(function() return patch_native_yield('new') end); " +
                "assert(coroutine.resume(co)); capture_candidate(co); return {co=co}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var result = new LuaPatchCoordinator().CommitRing(
            "native-continuation-health-rollback",
            new LuaPatchRolloutRing
            {
                Name = "production",
                Targets =
                [
                    new LuaPatchDeploymentTarget(
                        "state-a",
                        host,
                        prepared.PreparedPatch!),
                ],
            },
            new LuaPatchCoordinatorOptions
            {
                HealthCheck = _ =>
                {
                    Assert.True(oldThread.IsPatchGenerationActive);
                    Assert.True(candidateThread!.IsPatchGenerationActive);
                    Assert.Equal(2, host.ActiveNativeContinuationCount);
                    return LuaPatchRingHealthDecision.Rollback;
                },
            });

        Assert.Equal(LuaPatchRingCommitStatus.HealthRejected, result.Status);
        Assert.True(oldThread.IsPatchGenerationActive);
        Assert.False(candidateThread!.IsPatchGenerationActive);
        Assert.Equal(1, host.ActiveNativeContinuationCount);
        Assert.Equal(1, host.StaleNativeContinuationCount);
        var resumed = host.RunUtf8("return coroutine.resume(old_worker)").Execution!.Values;
        Assert.True(resumed[0].AsBoolean());
        Assert.Equal("old", resumed[1].AsString().ToString());
    }

    private static LuaNativeStep NativeYieldStep(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values) => continuationId switch
        {
            0 => LuaNativeStep.Yielded(
                [LuaValue.FromString(context.State.Strings.GetOrCreate("native-pause"u8))],
                continuationId: 1,
                stateValues: [values[0]]),
            1 => LuaNativeStep.Completed(context.InvocationState[0]),
            _ => throw new InvalidOperationException("Unexpected native-yield continuation."),
        };

    private static LuaNativeStep NativeCallLuaStep(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values) => continuationId switch
        {
            0 => LuaNativeStep.CallLua(
                values[0],
                [],
                continuationId: 1,
                stateValues: [values[1]]),
            1 => LuaNativeStep.Completed(context.InvocationState[0], values[0]),
            _ => throw new InvalidOperationException("Unexpected native callback continuation."),
        };

    [Fact]
    public void StateSchemaRevisionIsRecheckedBeforeCandidateExecution()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        var schema = Schema(new LuaPatchModuleMigrationSchema { ModuleName = "a" });
        var prepared = host.PreparePatch(CreateBundle(
            schema,
            Entry("a", "schema_candidate_ran=true; return {value=2}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        host.SetPatchStateSchemaVersion("game-state", "external-change");

        var commit = Commit(host, prepared.PreparedPatch!);

        Assert.Equal(LuaPatchCommitStatus.RevisionConflict, commit.Status);
        Assert.False(commit.SideEffectsMayHaveOccurred);
        var values = host.RunUtf8("return require('a').value,schema_candidate_ran")
            .Execution!.Values;
        Assert.Equal(1, values[0].AsInteger());
        Assert.True(values[1].IsNil);
    }

    [Theory]
    [InlineData(LuaPatchResourceKind.Timer)]
    [InlineData(LuaPatchResourceKind.EventSubscription)]
    [InlineData(LuaPatchResourceKind.Task)]
    public void HostResourceDispositionIsReversibleAcrossAtomicFailure(
        LuaPatchResourceKind kind)
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
            ["mods/b.lua"] = "return {value=1}",
        });
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; require('a'); require('b')").Succeeded);
        var resource = new FakeResource();
        var schema = Schema(
            new LuaPatchModuleMigrationSchema
            {
                ModuleName = "a",
                Resources =
                [
                    new LuaPatchResourceRule
                    {
                        ResourceId = "game-resource",
                        Kind = kind,
                        Disposition = LuaPatchResourceDisposition.Cancel,
                        AdapterId = "resource-v1",
                    },
                ],
            },
            new LuaPatchModuleMigrationSchema { ModuleName = "b" });
        var prepared = host.PreparePatch(
            CreateBundle(
                schema,
                Entry("a", "return {value=2}"),
                Entry("b", "error('stop transaction')", "a")),
            new LuaPatchPrepareOptions
            {
                ResourceMigrationAdapters =
                    new Dictionary<string, ILuaPatchResourceMigrationAdapter>
                    {
                        ["resource-v1"] = new FakeResourceAdapter(resource),
                    },
            });
        Assert.True(prepared.Succeeded, prepared.Message);

        var commit = Commit(host, prepared.PreparedPatch!);

        Assert.Equal(LuaPatchCommitStatus.ExecutionFailed, commit.Status);
        Assert.False(resource.Cancelled);
        Assert.Equal(1, resource.ApplyCount);
        Assert.Equal(1, resource.RollbackCount);
    }

    [Fact]
    public void AdapterDisposeFailureDoesNotReplaceACommittedResultOrSkipCleanup()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        var resource = new FakeResource { ThrowOnDispose = true };
        var schema = Schema(new LuaPatchModuleMigrationSchema
        {
            ModuleName = "a",
            Resources =
            [
                new LuaPatchResourceRule
                {
                    ResourceId = "game-resource",
                    Kind = LuaPatchResourceKind.Timer,
                    Disposition = LuaPatchResourceDisposition.Cancel,
                    AdapterId = "resource-v1",
                },
            ],
        });
        var prepared = host.PreparePatch(
            CreateBundle(schema, Entry("a", "return {value=2}")),
            new LuaPatchPrepareOptions
            {
                ResourceMigrationAdapters =
                    new Dictionary<string, ILuaPatchResourceMigrationAdapter>
                    {
                        ["resource-v1"] = new FakeResourceAdapter(resource),
                    },
            });
        Assert.True(prepared.Succeeded, prepared.Message);

        var commit = Commit(host, prepared.PreparedPatch!);

        Assert.True(commit.Succeeded, commit.Message);
        Assert.True(resource.Cancelled);
        Assert.Equal(1, resource.DisposeCount);
        Assert.Equal(2, host.RunUtf8("return require('a').value")
            .Execution!.Values[0].AsInteger());
    }

    [Fact]
    public void SchemaReaderRejectsNonCanonicalJsonAndUnknownModules()
    {
        var schema = Schema(new LuaPatchModuleMigrationSchema { ModuleName = "z" });
        var canonical = LuaPatchMigrationSchemaSerializer.Serialize(schema);
        var nonCanonical = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(canonical).Replace(":", ": ", StringComparison.Ordinal));
        var exception = Assert.Throws<LuaPatchMigrationSchemaException>(() =>
            LuaPatchMigrationSchemaSerializer.Deserialize(nonCanonical));
        Assert.Equal(LuaPatchMigrationSchemaErrorCode.NonCanonicalJson, exception.Code);

        var bundleException = Assert.Throws<LuaPatchMigrationSchemaException>(() =>
            CreateBundle(schema, Entry("a", "return true")));
        Assert.Equal(LuaPatchMigrationSchemaErrorCode.UnknownModule, bundleException.Code);
    }

    private static LuaPatchCommitResult Commit(LuaHost host, LuaPreparedPatch patch)
    {
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);
        using var window = opened.Window!;
        return host.CommitPatch(patch, window);
    }

    private static LuaPatchMigrationSchema Schema(
        params LuaPatchModuleMigrationSchema[] modules) => new()
        {
            SchemaId = "game-state",
            BaseVersion = "2",
            TargetVersion = "3",
            Modules = modules.OrderBy(static module => module.ModuleName, StringComparer.Ordinal)
            .ToImmutableArray(),
        };

    private static LuaPatchEntry Entry(
        string moduleName,
        string source,
        params string[] dependencies) => new(
        $"modules/{moduleName}.lua",
        moduleName,
        LuaPatchEntryKind.Source,
        Encoding.UTF8.GetBytes(source),
        dependencies.Select(static dependency => new LuaPatchDependency(
            dependency,
            LuaPatchDependencyKind.Required)).ToImmutableArray());

    private static LuaPatchBundle CreateBundle(
        LuaPatchMigrationSchema schema,
        params LuaPatchEntry[] entries)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var schemaEntry = new LuaPatchEntry(
            LuaPatchMigrationSchemaFormat.BundleEntryName,
            null,
            LuaPatchEntryKind.CompanionData,
            LuaPatchMigrationSchemaSerializer.Serialize(schema));
        var bundle = LuaPatchBundle.Create(
            new LuaPatchManifest
            {
                PatchId = "alpha3-test",
                Channel = "test",
                TargetBuild = "test-3",
                BaseRevision = "test-2",
                TargetRevision = "test-3",
                LanguageVersion = LuaLanguageVersion.Lua54,
                RuntimeAbi = "lunil-0.12",
                CreatedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero),
                ExpiresAt = new DateTimeOffset(2099, 8, 22, 0, 0, 0, TimeSpan.Zero),
                Nonce = Guid.NewGuid().ToString("N"),
            },
            entries.Append(schemaEntry),
            new LuaPatchEcdsaSigner("test", key));
        _ = LuaPatchMigrationSchemaSerializer.ReadFromBundle(bundle);
        return bundle;
    }

    private static LuaHost CreateHost(IReadOnlyDictionary<string, string> files)
    {
        var host = new LuaHost(LuaHostOptions.Default with
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            StandardLibrary = LuaHostCapabilityProfiles.Create(LuaHostProfile.Restricted) with
            {
                FileSystem = new MemoryFileSystem(files),
            },
        });
        host.SetPatchStateSchemaVersion("game-state", "2");
        return host;
    }

    private sealed class MemoryFileSystem(IReadOnlyDictionary<string, string> files)
        : ILuaFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = files.ToDictionary(
            static pair => pair.Key,
            static pair => Encoding.UTF8.GetBytes(pair.Value),
            StringComparer.Ordinal);

        public byte[] ReadAllBytes(string path) => _files.TryGetValue(path, out var value)
            ? value.ToArray()
            : throw new FileNotFoundException(path);

        public bool FileExists(string path) => _files.ContainsKey(path);
    }

    private sealed class MutablePayload
    {
        public int Value { get; set; }
    }

    private sealed class PayloadMigrationAdapter : ILuaPatchStateMigrationAdapter
    {
        public string AdapterId => "payload-v1";

        public ILuaPatchStateMigrationOperation Prepare(LuaPatchStateMigrationContext context) =>
            new PayloadMigrationOperation(
                context.PreviousValue.AsUserdata().GetPayload<MutablePayload>(),
                context.CandidateValue.AsUserdata(),
                context.CandidateValue.AsUserdata().GetPayload<MutablePayload>());
    }

    private sealed class PayloadMigrationOperation(
        MutablePayload previous,
        LuaUserdata candidateUserdata,
        MutablePayload candidate) : ILuaPatchStateMigrationOperation
    {
        private readonly int _candidateBefore = candidate.Value;

        public LuaValue ResultValue => LuaValue.FromUserdata(candidateUserdata);

        public void Apply() => candidate.Value = previous.Value;

        public void Rollback() => candidate.Value = _candidateBefore;

        public void Dispose()
        {
        }
    }

    private sealed class FakeResource
    {
        public bool Cancelled { get; set; }

        public int ApplyCount { get; set; }

        public int RollbackCount { get; set; }

        public bool ThrowOnDispose { get; init; }

        public int DisposeCount { get; set; }
    }

    private sealed class FakeResourceAdapter(FakeResource resource)
        : ILuaPatchResourceMigrationAdapter
    {
        public string AdapterId => "resource-v1";

        public ILuaPatchResourceMigrationOperation Prepare(
            LuaPatchResourceMigrationContext context) => new FakeResourceOperation(resource);
    }

    private sealed class FakeResourceOperation(FakeResource resource)
        : ILuaPatchResourceMigrationOperation
    {
        private readonly bool _before = resource.Cancelled;

        public bool IsActive => true;

        public void Apply(LuaPatchResourceDisposition disposition)
        {
            Assert.Equal(LuaPatchResourceDisposition.Cancel, disposition);
            resource.Cancelled = true;
            resource.ApplyCount++;
        }

        public void Rollback()
        {
            resource.Cancelled = _before;
            resource.RollbackCount++;
        }

        public void Dispose()
        {
            resource.DisposeCount++;
            if (resource.ThrowOnDispose)
            {
                throw new InvalidOperationException("dispose failure");
            }
        }
    }
}
