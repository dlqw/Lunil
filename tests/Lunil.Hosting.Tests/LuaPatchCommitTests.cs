using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Lunil.CodeGen.Cil.Jit;
using Lunil.Core;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.StandardLibrary;

namespace Lunil.Hosting.Tests;

public sealed class LuaPatchCommitTests
{
    [Fact]
    public async Task BackgroundPrepareCapturesRevisionsWithoutMutatingLiveState()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
            ["mods/b.lua"] = "return {value=2}",
        });
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; require('a'); require('b')").Succeeded);
        Assert.True(host.State.TryGetModule("a", out var a));
        Assert.True(host.State.TryGetModule("b", out var b));
        var bundle = CreateBundle(
            Entry("a", "return {value=10}"),
            Entry("b", "return {value=20}"));

        var result = await host.PreparePatchAsync(bundle);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(LuaPatchPrepareStatus.Ready, result.Status);
        Assert.Equal(2, result.PreparedPatch!.Modules.Length);
        Assert.Equal(a!.Revision, result.PreparedPatch.Modules.Single(
            static module => module.ModuleName == "a").ExpectedRevision);
        Assert.Equal(b!.Revision, result.PreparedPatch.Modules.Single(
            static module => module.ModuleName == "b").ExpectedRevision);
        Assert.True(host.State.TryGetModule("a", out var afterA));
        Assert.True(host.State.TryGetModule("b", out var afterB));
        Assert.Equal(a.Revision, afterA!.Revision);
        Assert.Equal(b.Revision, afterB!.Revision);
        var values = host.RunUtf8("return require('a').value,require('b').value")
            .Execution!.Values;
        Assert.Equal(1, values[0].AsInteger());
        Assert.Equal(2, values[1].AsInteger());
    }

    [Fact]
    public void CommitPublishesDependencyFirstCandidatesAtomically()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/shared.lua"] = "return {value=1}",
            ["mods/main.lua"] = "local s=require('shared'); return {value=s.value+1}",
        }, LuaHostExecutionBackend.Jit);
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; require('shared'); require('main')").Succeeded);
        Assert.True(host.State.TryGetModule("shared", out var oldShared));
        Assert.True(host.State.TryGetModule("main", out var oldMain));
        var bundle = CreateBundle(
            Entry("shared", "return {value=20}"),
            Entry(
                "main",
                "local s=require('shared'); return {value=s.value+2}",
                "shared"));
        var prepared = host.PreparePatch(bundle);
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.True(commit.Succeeded, commit.Message);
        Assert.All(commit.Modules, static module =>
            Assert.Equal(LuaPatchModuleCommitStatus.Committed, module.Status));
        Assert.True(host.State.TryGetModule("shared", out var currentShared));
        Assert.True(host.State.TryGetModule("main", out var currentMain));
        Assert.True(currentShared!.Revision > oldShared!.Revision);
        Assert.True(currentMain!.Revision > oldMain!.Revision);
        var values = host.RunUtf8("return require('shared').value,require('main').value")
            .Execution!.Values;
        Assert.Equal(20, values[0].AsInteger());
        Assert.Equal(22, values[1].AsInteger());
    }

    [Fact]
    public void CommittedPatchFencesOldClrDelegatesAndPublishesCandidateDelegates()
    {
        var delegateName = typeof(Func<int, int>).FullName!;
        using var host = CreateClrCallbackHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "local function callback(value) return value+1 end; return {callback=callback}",
        });
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        var oldFunction = host.RunUtf8("return require('a').callback")
            .Execution!.Values[0];
        var oldCallback = (Func<int, int>)host.ClrBridge.CreateDelegate(
            oldFunction,
            delegateName);
        Assert.Equal(1, host.ClrBridge.ActiveCallbackCount);
        Func<int, int>? candidateCallback = null;
        var oldCallbackWasQuiesced = false;
        (int Active, int Pending, int Quiesced, int Stale)? stagedCounts = null;
        host.State.SetGlobal(
            "capture_callback",
            LuaValue.FromFunction(new LuaNativeFunction(
                "capture_callback",
                (_, arguments) =>
                {
                    candidateCallback = (Func<int, int>)host.ClrBridge.CreateDelegate(
                        arguments[0],
                        delegateName);
                    oldCallbackWasQuiesced = Assert.Throws<LuaClrException>(
                        () => oldCallback(40)).Code == LuaClrErrorCode.SubscriptionClosed;
                    stagedCounts = (
                        host.ClrBridge.ActiveCallbackCount,
                        host.ClrBridge.PendingCallbackCount,
                        host.ClrBridge.QuiescedCallbackCount,
                        host.ClrBridge.StaleCallbackCount);
                    return [];
                })));
        var prepared = host.PreparePatch(CreateBundle(Entry(
            "a",
            "local function callback(value) return value+2 end; " +
            "capture_callback(callback); return {callback=callback}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.True(commit.Succeeded, commit.Message);
        Assert.NotNull(candidateCallback);
        Assert.True(oldCallbackWasQuiesced);
        Assert.Equal((0, 1, 1, 0), stagedCounts!.Value);
        Assert.Equal(1, host.ClrBridge.ActiveCallbackCount);
        Assert.Equal(0, host.ClrBridge.PendingCallbackCount);
        Assert.Equal(1, host.ClrBridge.StaleCallbackCount);
        var stale = Assert.Throws<LuaClrException>(() => oldCallback(40));
        Assert.Equal(LuaClrErrorCode.SubscriptionClosed, stale.Code);
        Assert.Equal(42, candidateCallback(40));

        var next = host.PreparePatch(CreateBundle(Entry(
            "a",
            "local function callback(value) return value+3 end; return {callback=callback}")));
        Assert.True(next.Succeeded, next.Message);
        var nextWindow = host.TryOpenPatchUpdateWindow();
        Assert.True(nextWindow.Succeeded, nextWindow.Message);
        using (nextWindow.Window!)
        {
            Assert.True(host.CommitPatch(next.PreparedPatch!, nextWindow.Window!).Succeeded);
        }

        Assert.Throws<LuaClrException>(() => candidateCallback(40));
    }

    [Fact]
    public void FailedPatchRestoresOldClrDelegatesAndRejectsCandidateDelegates()
    {
        var delegateName = typeof(Func<int, int>).FullName!;
        using var host = CreateClrCallbackHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "local function callback(value) return value+1 end; return {callback=callback}",
            ["mods/b.lua"] = "return {value=1}",
        });
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; require('a'); require('b')").Succeeded);
        var oldCallback = (Func<int, int>)host.ClrBridge.CreateDelegate(
            host.RunUtf8("return require('a').callback").Execution!.Values[0],
            delegateName);
        Func<int, int>? candidateCallback = null;
        host.State.SetGlobal(
            "capture_callback",
            LuaValue.FromFunction(new LuaNativeFunction(
                "capture_callback",
                (_, arguments) =>
                {
                    candidateCallback = (Func<int, int>)host.ClrBridge.CreateDelegate(
                        arguments[0],
                        delegateName);
                    return [];
                })));
        var prepared = host.PreparePatch(CreateBundle(
            Entry(
                "a",
                "local function callback(value) return value+2 end; " +
                "capture_callback(callback); return {callback=callback}"),
            Entry("b", "error('candidate failed')", "a")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.Equal(LuaPatchCommitStatus.ExecutionFailed, commit.Status);
        Assert.Equal(1, host.ClrBridge.ActiveCallbackCount);
        Assert.Equal(0, host.ClrBridge.PendingCallbackCount);
        Assert.Equal(1, host.ClrBridge.StaleCallbackCount);
        Assert.Equal(41, oldCallback(40));
        Assert.NotNull(candidateCallback);
        var stale = Assert.Throws<LuaClrException>(() => candidateCallback(40));
        Assert.Equal(LuaClrErrorCode.SubscriptionClosed, stale.Code);
    }

    [Fact]
    public void CallbackGenerationSwitchesEventSubscriptionsAtomically()
    {
        using var host = CreateClrCallbackHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "local function callback(value) return value+1 end; return {callback=callback}",
        });
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        var source = new CallbackEventSource();
        var target = LuaValue.FromUserdata(host.State.CreateUserdata(new LuaClrObject(source)));
        using var oldSubscription = host.ClrBridge.Subscribe(
            target,
            nameof(CallbackEventSource.Changed),
            host.RunUtf8("return require('a').callback").Execution!.Values[0]);
        LuaClrSubscription? candidateSubscription = null;
        host.State.SetGlobal(
            "capture_subscription",
            LuaValue.FromFunction(new LuaNativeFunction(
                "capture_subscription",
                (_, arguments) =>
                {
                    candidateSubscription = host.ClrBridge.Subscribe(
                        target,
                        nameof(CallbackEventSource.Changed),
                        arguments[0]);
                    return [];
                })));
        Assert.True(oldSubscription.IsActive);
        Assert.Equal(41, source.Raise(40));
        var prepared = host.PreparePatch(CreateBundle(Entry(
            "a",
            "local function callback(value) return value+2 end; " +
            "capture_subscription(callback); return {callback=callback}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.True(commit.Succeeded, commit.Message);
        Assert.False(oldSubscription.IsActive);
        Assert.NotNull(candidateSubscription);
        Assert.True(candidateSubscription.IsActive);
        Assert.Equal(42, source.Raise(40));
        candidateSubscription.Dispose();
    }

    [Fact]
    public void CallbackGenerationRollbackRestoresEventSubscriptions()
    {
        using var host = CreateClrCallbackHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "local function callback(value) return value+1 end; return {callback=callback}",
            ["mods/b.lua"] = "return {value=1}",
        });
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; require('a'); require('b')").Succeeded);
        var source = new CallbackEventSource();
        var target = LuaValue.FromUserdata(host.State.CreateUserdata(new LuaClrObject(source)));
        using var oldSubscription = host.ClrBridge.Subscribe(
            target,
            nameof(CallbackEventSource.Changed),
            host.RunUtf8("return require('a').callback").Execution!.Values[0]);
        LuaClrSubscription? candidateSubscription = null;
        host.State.SetGlobal(
            "capture_subscription",
            LuaValue.FromFunction(new LuaNativeFunction(
                "capture_subscription",
                (_, arguments) =>
                {
                    candidateSubscription = host.ClrBridge.Subscribe(
                        target,
                        nameof(CallbackEventSource.Changed),
                        arguments[0]);
                    return [];
                })));
        var prepared = host.PreparePatch(CreateBundle(
            Entry(
                "a",
                "local function callback(value) return value+2 end; " +
                "capture_subscription(callback); return {callback=callback}"),
            Entry("b", "error('candidate failed')", "a")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.Equal(LuaPatchCommitStatus.ExecutionFailed, commit.Status);
        Assert.True(oldSubscription.IsActive);
        Assert.NotNull(candidateSubscription);
        Assert.False(candidateSubscription.IsActive);
        Assert.Equal(41, source.Raise(40));
        candidateSubscription.Dispose();
    }

    [Fact]
    public void CallbackGenerationFailsClosedWhenEventDetachmentThrows()
    {
        using var host = CreateClrCallbackHost(
            new Dictionary<string, string>
            {
                ["mods/a.lua"] =
                    "local function callback(value) return value+1 end; return {callback=callback}",
            },
            typeof(FaultingCallbackEventSource));
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        var source = new FaultingCallbackEventSource();
        var target = LuaValue.FromUserdata(host.State.CreateUserdata(new LuaClrObject(source)));
        var subscription = host.ClrBridge.Subscribe(
            target,
            nameof(FaultingCallbackEventSource.Changed),
            host.RunUtf8("return require('a').callback").Execution!.Values[0]);
        var prepared = host.PreparePatch(CreateBundle(Entry(
            "a",
            "local function callback(value) return value+2 end; return {callback=callback}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        source.ThrowOnRemove = true;
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.Equal(LuaPatchCommitStatus.PublicationFailed, commit.Status);
        Assert.False(subscription.IsActive);
        Assert.Equal(0, host.ClrBridge.ActiveCallbackCount);
        Assert.Equal(1, host.ClrBridge.StaleCallbackCount);
        var stale = Assert.Throws<LuaClrException>(() => source.Raise(40));
        Assert.Equal(LuaClrErrorCode.SubscriptionClosed, stale.Code);

        source.ThrowOnRemove = false;
        subscription.Dispose();
        var nextWindow = host.TryOpenPatchUpdateWindow();
        Assert.True(nextWindow.Succeeded, nextWindow.Message);
        using (nextWindow.Window!)
        {
            Assert.True(host.CommitPatch(prepared.PreparedPatch!, nextWindow.Window!).Succeeded);
        }
    }

    [Fact]
    public void BarrierRollbackAfterPublicationRestoresThePreviousCallbackGeneration()
    {
        var delegateName = typeof(Func<int, int>).FullName!;
        using var host = CreateClrCallbackHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "local function callback(value) return value+1 end; return {callback=callback}",
        });
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        var oldCallback = (Func<int, int>)host.ClrBridge.CreateDelegate(
            host.RunUtf8("return require('a').callback").Execution!.Values[0],
            delegateName);
        Func<int, int>? candidateCallback = null;
        host.State.SetGlobal(
            "capture_callback",
            LuaValue.FromFunction(new LuaNativeFunction(
                "capture_callback",
                (_, arguments) =>
                {
                    candidateCallback = (Func<int, int>)host.ClrBridge.CreateDelegate(
                        arguments[0],
                        delegateName);
                    return [];
                })));
        var prepared = host.PreparePatch(CreateBundle(Entry(
            "a",
            "local function callback(value) return value+2 end; " +
            "capture_callback(callback); return {callback=callback}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var result = new LuaPatchCoordinator().CommitRing(
            "callback-health-rollback",
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
                    Assert.Throws<LuaClrException>(() => oldCallback(40));
                    Assert.Equal(42, candidateCallback!(40));
                    return LuaPatchRingHealthDecision.Rollback;
                },
            });

        Assert.Equal(LuaPatchRingCommitStatus.HealthRejected, result.Status);
        Assert.Equal(41, oldCallback(40));
        Assert.Throws<LuaClrException>(() => candidateCallback!(40));
    }

    [Fact]
    public void CommittedPatchFencesOldClrTasksAndPublishesCandidateTasks()
    {
        var typeName = typeof(PatchTaskSource).FullName!;
        using var host = CreateClrTaskHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                $"local task=clr.call('{typeName}','Create',41); return {{task=task}}",
        });
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        var oldTaskValue = host.RunUtf8("return require('a').task").Execution!.Values[0];
        var oldTask = TaskPayload(oldTaskValue);
        LuaValue candidateTaskValue = LuaValue.Nil;
        LuaClrTask? candidateTask = null;
        (int Active, int Pending, int Quiesced, int Stale)? stagedCounts = null;
        host.State.SetGlobal(
            "capture_task",
            LuaValue.FromFunction(new LuaNativeFunction(
                "capture_task",
                (_, arguments) =>
                {
                    candidateTaskValue = arguments[0];
                    candidateTask = TaskPayload(candidateTaskValue);
                    Assert.Equal(
                        LuaClrErrorCode.AsyncGenerationClosed,
                        Assert.Throws<LuaClrException>(() =>
                            host.ClrBridge.Await(oldTaskValue)).Code);
                    Assert.Equal(42, host.ClrBridge.Await(candidateTaskValue).AsInteger());
                    stagedCounts = (
                        host.ClrBridge.ActiveTaskCount,
                        host.ClrBridge.PendingTaskCount,
                        host.ClrBridge.QuiescedTaskCount,
                        host.ClrBridge.StaleTaskCount);
                    return [];
                })));
        var prepared = host.PreparePatch(CreateBundle(Entry(
            "a",
            $"local task=clr.call('{typeName}','Create',42); " +
            "capture_task(task); return {task=task}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.True(commit.Succeeded, commit.Message);
        Assert.Equal((0, 1, 1, 0), stagedCounts!.Value);
        Assert.False(oldTask.IsActive);
        Assert.NotNull(candidateTask);
        Assert.True(candidateTask.IsActive);
        Assert.Equal(42, host.ClrBridge.Await(candidateTaskValue).AsInteger());
        Assert.Equal(
            LuaClrErrorCode.AsyncGenerationClosed,
            Assert.Throws<LuaClrException>(() => host.ClrBridge.Await(oldTaskValue)).Code);
        Assert.Equal(1, host.ClrBridge.ActiveTaskCount);
        Assert.Equal(1, host.ClrBridge.StaleTaskCount);
    }

    [Fact]
    public void CommittedPatchPausesOldTimerAndPublishesCandidateTimer()
    {
        var time = new ManualTimeProvider();
        using var host = CreateClrTimerHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "return {timer=clr.timer(function() timer_result='old' end,100)}",
        }, time);
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        var oldTimer = TimerPayload(host.RunUtf8("return require('a').timer")
            .Execution!.Values[0]);
        LuaClrTimer? candidateTimer = null;
        (int Active, int Pending, int Quiesced, int Stale)? staged = null;
        host.State.SetGlobal(
            "capture_timer",
            LuaValue.FromFunction(new LuaNativeFunction(
                "capture_timer",
                (_, arguments) =>
                {
                    candidateTimer = TimerPayload(arguments[0]);
                    Assert.False(candidateTimer.IsScheduled);
                    Assert.Equal(
                        LuaClrErrorCode.TimerGenerationClosed,
                        Assert.Throws<LuaClrException>(oldTimer.Cancel).Code);
                    Assert.False(oldTimer.IsDisposed);
                    staged = (
                        host.ClrBridge.ActiveTimerCount,
                        host.ClrBridge.PendingTimerCount,
                        host.ClrBridge.QuiescedTimerCount,
                        host.ClrBridge.StaleTimerCount);
                    return [];
                })));
        var prepared = host.PreparePatch(CreateBundle(Entry(
            "a",
            "local timer=clr.timer(function() timer_result='new' end,100); " +
            "capture_timer(timer); return {timer=timer}")));
        Assert.True(prepared.Succeeded, prepared.Message);

        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);
        using (opened.Window!)
        {
            Assert.True(host.CommitPatch(prepared.PreparedPatch!, opened.Window!).Succeeded);
        }

        Assert.Equal((0, 1, 1, 0), staged);
        Assert.False(oldTimer.IsActive);
        Assert.False(oldTimer.IsScheduled);
        Assert.True(candidateTimer!.IsActive);
        Assert.Equal(1, host.ClrBridge.ActiveTimerCount);
        Assert.Equal(1, host.ClrBridge.StaleTimerCount);
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, host.DispatchClrTimers());
        Assert.Equal("new", host.State.GetGlobal("timer_result").AsString().ToString());
    }

    [Fact]
    public void FailedPatchRestoresOldTimerAndRejectsCandidateTimer()
    {
        var time = new ManualTimeProvider();
        using var host = CreateClrTimerHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "return {timer=clr.timer(function() timer_result='old' end,100)}",
            ["mods/b.lua"] = "return {value=1}",
        }, time);
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; require('a'); require('b')").Succeeded);
        var oldTimer = TimerPayload(host.RunUtf8("return require('a').timer")
            .Execution!.Values[0]);
        LuaClrTimer? candidateTimer = null;
        host.State.SetGlobal(
            "capture_timer",
            LuaValue.FromFunction(new LuaNativeFunction(
                "capture_timer",
                (_, arguments) =>
                {
                    candidateTimer = TimerPayload(arguments[0]);
                    return [];
                })));
        var prepared = host.PreparePatch(CreateBundle(
            Entry(
                "a",
                "local timer=clr.timer(function() timer_result='new' end,100); " +
                "capture_timer(timer); return {timer=timer}"),
            Entry("b", "error('rollback')", "a")));
        Assert.True(prepared.Succeeded, prepared.Message);

        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);
        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.Equal(LuaPatchCommitStatus.ExecutionFailed, commit.Status);
        Assert.True(oldTimer.IsActive);
        Assert.True(oldTimer.IsScheduled);
        Assert.False(candidateTimer!.IsActive);
        Assert.False(candidateTimer.IsScheduled);
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, host.DispatchClrTimers());
        Assert.Equal("old", host.State.GetGlobal("timer_result").AsString().ToString());
    }

    [Fact]
    public void HealthRollbackRestoresPreviousTimerGeneration()
    {
        var time = new ManualTimeProvider();
        using var host = CreateClrTimerHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "return {timer=clr.timer(function() timer_result='old' end,100)}",
        }, time);
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        var oldTimer = TimerPayload(host.RunUtf8("return require('a').timer")
            .Execution!.Values[0]);
        LuaClrTimer? candidateTimer = null;
        host.State.SetGlobal(
            "capture_timer",
            LuaValue.FromFunction(new LuaNativeFunction(
                "capture_timer",
                (_, arguments) =>
                {
                    candidateTimer = TimerPayload(arguments[0]);
                    return [];
                })));
        var prepared = host.PreparePatch(CreateBundle(Entry(
            "a",
            "local timer=clr.timer(function() timer_result='new' end,100); " +
            "capture_timer(timer); return {timer=timer}")));
        Assert.True(prepared.Succeeded, prepared.Message);

        var result = new LuaPatchCoordinator().CommitRing(
            "timer-health-rollback",
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
                    Assert.False(oldTimer.IsActive);
                    Assert.True(candidateTimer!.IsActive);
                    return LuaPatchRingHealthDecision.Rollback;
                },
            });

        Assert.Equal(LuaPatchRingCommitStatus.HealthRejected, result.Status);
        Assert.True(oldTimer.IsActive);
        Assert.True(oldTimer.IsScheduled);
        Assert.False(candidateTimer!.IsActive);
        Assert.False(candidateTimer.IsScheduled);
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, host.DispatchClrTimers());
        Assert.Equal("old", host.State.GetGlobal("timer_result").AsString().ToString());
    }

    [Fact]
    public void HostOwnedTimerIsNotFencedByModulePatch()
    {
        var time = new ManualTimeProvider();
        using var host = CreateClrTimerHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        }, time);
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        var callback = host.RunUtf8(
            "return function() host_timer_result='host' end",
            "host-owned-timer")
            .Execution!.Values[0];
        using var timer = host.ClrBridge.ScheduleTimer(
            callback,
            new LuaClrTimerOptions { DueTime = TimeSpan.FromMilliseconds(100) });
        var prepared = host.PreparePatch(CreateBundle(Entry("a", "return {value=2}")));
        Assert.True(prepared.Succeeded, prepared.Message);

        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);
        using (opened.Window!)
        {
            Assert.True(host.CommitPatch(prepared.PreparedPatch!, opened.Window!).Succeeded);
        }

        Assert.True(timer.IsActive);
        Assert.True(timer.IsScheduled);
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, host.DispatchClrTimers());
        Assert.Equal("host", host.State.GetGlobal("host_timer_result").AsString().ToString());
    }

    [Fact]
    public void FailedPatchRestoresOldClrTasksAndRejectsCandidateTasks()
    {
        var typeName = typeof(PatchTaskSource).FullName!;
        using var host = CreateClrTaskHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                $"local task=clr.call('{typeName}','Create',41); return {{task=task}}",
            ["mods/b.lua"] = "return {value=1}",
        });
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; require('a'); require('b')").Succeeded);
        var oldTaskValue = host.RunUtf8("return require('a').task").Execution!.Values[0];
        LuaValue candidateTaskValue = LuaValue.Nil;
        host.State.SetGlobal(
            "capture_task",
            LuaValue.FromFunction(new LuaNativeFunction(
                "capture_task",
                (_, arguments) =>
                {
                    candidateTaskValue = arguments[0];
                    return [];
                })));
        var prepared = host.PreparePatch(CreateBundle(
            Entry(
                "a",
                $"local task=clr.call('{typeName}','Create',42); " +
                "capture_task(task); return {task=task}"),
            Entry("b", "error('candidate failed')", "a")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.Equal(LuaPatchCommitStatus.ExecutionFailed, commit.Status);
        Assert.Equal(41, host.ClrBridge.Await(oldTaskValue).AsInteger());
        Assert.Equal(
            LuaClrErrorCode.AsyncGenerationClosed,
            Assert.Throws<LuaClrException>(() => host.ClrBridge.Await(candidateTaskValue)).Code);
        Assert.Equal(1, host.ClrBridge.ActiveTaskCount);
        Assert.Equal(1, host.ClrBridge.StaleTaskCount);
    }

    [Fact]
    public void HealthRollbackRestoresThePreviousClrTaskGeneration()
    {
        var typeName = typeof(PatchTaskSource).FullName!;
        using var host = CreateClrTaskHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                $"local task=clr.call('{typeName}','Create',41); return {{task=task}}",
        });
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        var oldTaskValue = host.RunUtf8("return require('a').task").Execution!.Values[0];
        LuaValue candidateTaskValue = LuaValue.Nil;
        host.State.SetGlobal(
            "capture_task",
            LuaValue.FromFunction(new LuaNativeFunction(
                "capture_task",
                (_, arguments) =>
                {
                    candidateTaskValue = arguments[0];
                    return [];
                })));
        var prepared = host.PreparePatch(CreateBundle(Entry(
            "a",
            $"local task=clr.call('{typeName}','Create',42); " +
            "capture_task(task); return {task=task}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var result = new LuaPatchCoordinator().CommitRing(
            "task-health-rollback",
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
                    Assert.Equal(42, host.ClrBridge.Await(candidateTaskValue).AsInteger());
                    Assert.Equal(
                        LuaClrErrorCode.AsyncGenerationClosed,
                        Assert.Throws<LuaClrException>(() =>
                            host.ClrBridge.Await(oldTaskValue)).Code);
                    return LuaPatchRingHealthDecision.Rollback;
                },
            });

        Assert.Equal(LuaPatchRingCommitStatus.HealthRejected, result.Status);
        Assert.Equal(41, host.ClrBridge.Await(oldTaskValue).AsInteger());
        Assert.Equal(
            LuaClrErrorCode.AsyncGenerationClosed,
            Assert.Throws<LuaClrException>(() => host.ClrBridge.Await(candidateTaskValue)).Code);
    }

    [Fact]
    public void HostOwnedClrTasksAreNotFencedByModulePatches()
    {
        var typeName = typeof(PatchTaskSource).FullName!;
        using var host = CreateClrTaskHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        var taskValue = host.ClrBridge.InvokeStatic(
            typeName,
            nameof(PatchTaskSource.Create),
            [LuaValue.FromInteger(43)]).ReturnValue;
        var task = TaskPayload(taskValue);
        var prepared = host.PreparePatch(CreateBundle(Entry("a", "return {value=2}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        using (opened.Window!)
        {
            Assert.True(host.CommitPatch(prepared.PreparedPatch!, opened.Window!).Succeeded);
        }

        Assert.True(task.IsActive);
        Assert.Equal(43, host.ClrBridge.Await(taskValue).AsInteger());
        task.Dispose();
        Assert.True(task.IsDisposed);
        Assert.False(task.IsActive);
    }

    [Fact]
    public async Task AwaitRechecksTaskGenerationAfterLateCompletion()
    {
        const int taskId = 1701;
        var typeName = typeof(PatchTaskSource).FullName!;
        using var host = CreateClrTaskHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                $"local task=clr.call('{typeName}','CreatePending',{taskId}); " +
                "return {task=task}",
        });
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        var taskValue = host.RunUtf8("return require('a').task").Execution!.Values[0];
        using var started = new ManualResetEventSlim();
        var awaiting = Task.Run(() =>
        {
            started.Set();
            return Record.Exception(() => host.ClrBridge.Await(taskValue));
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        try
        {
            await Task.Delay(100);
            Assert.False(awaiting.IsCompleted);
            var prepared = host.PreparePatch(CreateBundle(Entry("a", "return {value=2}")));
            Assert.True(prepared.Succeeded, prepared.Message);
            var opened = host.TryOpenPatchUpdateWindow();
            Assert.True(opened.Succeeded, opened.Message);
            using (opened.Window!)
            {
                Assert.True(host.CommitPatch(prepared.PreparedPatch!, opened.Window!).Succeeded);
            }
        }
        finally
        {
            PatchTaskSource.Complete(taskId, 44);
        }

        var exception = Assert.IsType<LuaClrException>(
            await awaiting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(LuaClrErrorCode.AsyncGenerationClosed, exception.Code);
    }

    [Fact]
    public void RevisionConflictRejectsPatchBeforeCandidateExecution()
    {
        var files = new MutableMemoryFileSystem(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        using var host = CreateHost(files);
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        var prepared = host.PreparePatch(CreateBundle(Entry(
            "a",
            "patch_candidate_ran=true; return {value=9}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        files.Set("mods/a.lua", "return {value=2}");
        var reload = host.ReloadModule("a");
        Assert.True(reload.Succeeded, reload.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.Equal(LuaPatchCommitStatus.RevisionConflict, commit.Status);
        Assert.False(commit.SideEffectsMayHaveOccurred);
        var values = host.RunUtf8("return require('a').value,patch_candidate_ran")
            .Execution!.Values;
        Assert.Equal(2, values[0].AsInteger());
        Assert.True(values[1].IsNil);
    }

    [Fact]
    public void ExpiredPreparedPatchIsRejectedBeforeCandidateExecution()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        var prepared = host.PreparePatch(CreateBundle(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            Entry("a", "patch_candidate_ran=true; return {value=9}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.Equal(LuaPatchCommitStatus.Expired, commit.Status);
        Assert.False(commit.SideEffectsMayHaveOccurred);
        Assert.All(commit.Modules, static module =>
            Assert.Equal(LuaPatchModuleCommitStatus.NotExecuted, module.Status));
        var values = host.RunUtf8("return require('a').value,patch_candidate_ran")
            .Execution!.Values;
        Assert.Equal(1, values[0].AsInteger());
        Assert.True(values[1].IsNil);
    }

    [Fact]
    public void LaterExecutionFailureRestoresEveryTargetRevisionCacheAndClosureSlot()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "local n=0; local function next() n=n+1; return n end; return {next=next}",
            ["mods/b.lua"] = "return {value=1}",
        });
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; alias=require('a').next; require('b')").Succeeded);
        var alias = host.State.GetGlobal("alias").TryGetClosure()!;
        var oldVersion = alias.FunctionVersion;
        Assert.True(host.State.TryGetModule("a", out var oldA));
        Assert.True(host.State.TryGetModule("b", out var oldB));
        var prepared = host.PreparePatch(CreateBundle(
            Entry(
                "a",
                "local n=0; local function next() n=n+10; return n end; return {next=next}"),
            Entry("b", "error('candidate b failed')", "a")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.Equal(LuaPatchCommitStatus.ExecutionFailed, commit.Status);
        Assert.True(commit.SideEffectsMayHaveOccurred);
        Assert.Same(oldVersion, alias.FunctionVersion);
        Assert.True(host.State.TryGetModule("a", out var currentA));
        Assert.True(host.State.TryGetModule("b", out var currentB));
        Assert.Equal(oldA!.Revision, currentA!.Revision);
        Assert.Equal(oldB!.Revision, currentB!.Revision);
        var values = host.RunUtf8("return alias(),require('a').next(),require('b').value")
            .Execution!.Values;
        Assert.Equal(1, values[0].AsInteger());
        Assert.Equal(2, values[1].AsInteger());
        Assert.Equal(1, values[2].AsInteger());
    }

    [Fact]
    public void FunctionMigrationPreservesTheExistingLuaEnvCellAcrossHealthRollback()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "local env={value=41}; local _ENV=env; " +
                "local function read() return value end; return {read=read,env=env}",
        });
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; alias=require('a').read").Succeeded);
        var alias = host.State.GetGlobal("alias").TryGetClosure()!;
        var oldVersion = alias.FunctionVersion;
        var oldEnvironmentCell = alias.Upvalues.Single(static upvalue =>
            upvalue.Value.Kind == LuaValueKind.Table);
        var prepared = host.PreparePatch(CreateBundle(Entry(
            "a",
            "local env={value=99}; local _ENV=env; " +
            "local function read() return value+1 end; return {read=read,env=env}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var healthChecked = false;
        var result = new LuaPatchCoordinator().CommitRing(
            "lua-env-health-rollback",
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
                    Assert.Equal(42, host.RunUtf8("return alias()")
                        .Execution!.Values[0].AsInteger());
                    Assert.Same(oldEnvironmentCell, alias.Upvalues.Single(static upvalue =>
                        upvalue.Value.Kind == LuaValueKind.Table));
                    Assert.NotSame(oldVersion, alias.FunctionVersion);
                    healthChecked = true;
                    return LuaPatchRingHealthDecision.Rollback;
                },
            });

        Assert.Equal(LuaPatchRingCommitStatus.HealthRejected, result.Status);
        Assert.True(healthChecked);
        Assert.Same(oldVersion, alias.FunctionVersion);
        Assert.Same(oldEnvironmentCell, alias.Upvalues.Single(static upvalue =>
            upvalue.Value.Kind == LuaValueKind.Table));
        Assert.Equal(41, host.RunUtf8("return alias()")
            .Execution!.Values[0].AsInteger());
    }

    [Fact]
    public void FunctionMigrationPreservesLua51SetfenvAcrossHealthRollback()
    {
        using var host = CreateHost(
            new Dictionary<string, string>
            {
                ["mods/a.lua"] =
                    "local function read() return value end; return {read=read}",
            },
            LuaLanguageVersion.Lua51);
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; alias=require('a').read; " +
            "custom_env={value=41}; setfenv(alias,custom_env)").Succeeded);
        var alias = host.State.GetGlobal("alias").TryGetClosure()!;
        var oldVersion = alias.FunctionVersion;
        var prepared = host.PreparePatch(CreateBundle(
            LuaLanguageVersion.Lua51,
            Entry(
                "a",
                "local function read() return value+1 end; return {read=read}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var healthChecked = false;
        var result = new LuaPatchCoordinator().CommitRing(
            "lua51-environment-health-rollback",
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
                    var values = host.RunUtf8("return alias(),getfenv(alias)==custom_env")
                        .Execution!.Values;
                    Assert.Equal(42, values[0].AsFloat());
                    Assert.True(values[1].AsBoolean());
                    Assert.NotSame(oldVersion, alias.FunctionVersion);
                    healthChecked = true;
                    return LuaPatchRingHealthDecision.Rollback;
                },
            });

        Assert.Equal(LuaPatchRingCommitStatus.HealthRejected, result.Status);
        Assert.True(healthChecked);
        Assert.Same(oldVersion, alias.FunctionVersion);
        var restored = host.RunUtf8("return alias(),getfenv(alias)==custom_env")
            .Execution!.Values;
        Assert.Equal(41, restored[0].AsFloat());
        Assert.True(restored[1].AsBoolean());
    }

    [Fact]
    public void SuspendedFrameWithoutCoroutineMigrationIsFencedAfterCommit()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "local n=1; local function get() coroutine.yield('pause'); return n end; " +
                "return {get=get}",
        }, LuaHostExecutionBackend.Jit);
        var suspended = host.RunUtf8(
            "package.path='mods/?.lua'; alias=require('a').get; " +
            "co=coroutine.create(alias); return coroutine.resume(co)");
        Assert.True(suspended.Execution!.Values[0].AsBoolean());
        var alias = host.State.GetGlobal("alias").TryGetClosure()!;
        var oldVersion = alias.FunctionVersion;
        var prepared = host.PreparePatch(CreateBundle(Entry(
            "a",
            "local n=2; local function get() local _=coroutine; return n+10 end; " +
            "return {get=get}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.True(commit.Succeeded, commit.Message);
        Assert.NotSame(oldVersion, alias.FunctionVersion);
        Assert.Equal(oldVersion.Generation + 1, alias.FunctionVersion.Generation);
        var thread = host.State.GetGlobal("co").AsThread();
        Assert.False(thread.IsPatchGenerationActive);
        var resumed = host.RunUtf8("return coroutine.resume(co)").Execution!.Values;
        Assert.False(resumed[0].AsBoolean());
        Assert.Contains(
            "inactive patch generation",
            resumed[1].AsString().ToString(),
            StringComparison.Ordinal);
        var current = host.RunUtf8("return alias(),require('a').get()")
            .Execution!.Values;
        Assert.Equal(11, current[0].AsInteger());
        Assert.Equal(12, current[1].AsInteger());
    }

    [Fact]
    public void ZeroPauseBudgetDefersWithoutExecutingOrPublishing()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        Assert.True(host.State.TryGetModule("a", out var old));
        var prepared = host.PreparePatch(CreateBundle(Entry(
            "a",
            "budget_candidate_ran=true; return {value=2}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(
                prepared.PreparedPatch!,
                opened.Window!,
                new LuaPatchCommitOptions { MaximumPauseDuration = TimeSpan.Zero });
        }

        Assert.Equal(LuaPatchCommitStatus.Deferred, commit.Status);
        Assert.False(commit.SideEffectsMayHaveOccurred);
        Assert.True(host.State.TryGetModule("a", out var current));
        Assert.Equal(old!.Revision, current!.Revision);
        var values = host.RunUtf8("return require('a').value,budget_candidate_ran")
            .Execution!.Values;
        Assert.Equal(1, values[0].AsInteger());
        Assert.True(values[1].IsNil);
    }

    [Fact]
    public void ReentrantUpdateWindowRequestIsDeferredUntilTheNextTick()
    {
        using var host = CreateHost(new Dictionary<string, string>());
        host.State.SetGlobal(
            "open_window",
            LuaValue.FromFunction(new LuaNativeFunction(
                "open_window",
                (_, _) =>
                {
                    var opened = host.TryOpenPatchUpdateWindow();
                    opened.Window?.Dispose();
                    return [LuaValue.FromInteger((long)opened.Status)];
                })));

        var status = Assert.Single(host.RunUtf8("return open_window()")
            .Execution!.Values);

        Assert.Equal((long)LuaPatchUpdateWindowStatus.Deferred, status.AsInteger());
    }

    [Fact]
    public async Task ConcurrentExecutionMakesZeroWaitUpdateWindowDefer()
    {
        using var host = CreateHost(new Dictionary<string, string>());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        host.State.SetGlobal(
            "hold_frame",
            LuaValue.FromFunction(new LuaNativeFunction(
                "hold_frame",
                (_, _) =>
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                    return [];
                })));
        var executing = Task.Run(() => host.RunUtf8("hold_frame()"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        var opened = host.TryOpenPatchUpdateWindow();
        release.Set();
        var execution = await executing;

        Assert.Equal(LuaPatchUpdateWindowStatus.Deferred, opened.Status);
        Assert.Null(opened.Window);
        Assert.True(execution.Succeeded);
    }

    [Fact]
    public void CancelledCommitDoesNotRunCandidateCode()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        var prepared = host.PreparePatch(CreateBundle(Entry(
            "a",
            "cancel_candidate_ran=true; return {value=2}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(
                prepared.PreparedPatch!,
                opened.Window!,
                cancellationToken: cancellation.Token);
        }

        Assert.Equal(LuaPatchCommitStatus.Cancelled, commit.Status);
        Assert.False(commit.SideEffectsMayHaveOccurred);
        var values = host.RunUtf8("return require('a').value,cancel_candidate_ran")
            .Execution!.Values;
        Assert.Equal(1, values[0].AsInteger());
        Assert.True(values[1].IsNil);
    }

    [Fact]
    public void AtomicTablePolicyPreservesIdentityAndRejectsOpaqueCallbacksAtPrepareTime()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {keep=1,remove=2}",
        });
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; original=require('a')").Succeeded);
        var bundle = CreateBundle(Entry("a", "return {keep=3,added=4}"));
        var prepared = host.PreparePatch(bundle, new LuaPatchPrepareOptions
        {
            ModuleOptions = new Dictionary<string, LuaModuleReloadOptions>
            {
                ["a"] = new()
                {
                    CachePolicy = LuaModuleReloadCachePolicy.PatchExistingTable,
                },
            },
        });
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.True(commit.Succeeded, commit.Message);
        var module = Assert.Single(commit.Modules);
        Assert.Equal(2, module.PatchedExportCount);
        Assert.Equal(1, module.RemovedExportCount);
        var values = host.RunUtf8(
            "local a=require('a'); return a==original,a.keep,a.remove,a.added")
            .Execution!.Values;
        Assert.True(values[0].AsBoolean());
        Assert.Equal(3, values[1].AsInteger());
        Assert.True(values[2].IsNil);
        Assert.Equal(4, values[3].AsInteger());

        var unsupported = host.PreparePatch(bundle, new LuaPatchPrepareOptions
        {
            ModuleOptions = new Dictionary<string, LuaModuleReloadOptions>
            {
                ["a"] = new()
                {
                    CachePolicy = LuaModuleReloadCachePolicy.Custom,
                    CustomCachePolicy = static context => context.CandidateValue,
                },
            },
        });
        Assert.Equal(LuaPatchPrepareStatus.UnsupportedCachePolicy, unsupported.Status);
        Assert.Null(unsupported.PreparedPatch);
    }

    [Fact]
    public void AtomicTableHealthRollbackKeepsRemovedGraphRootedAcrossLuaCollection()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "local mt={marker={value='old-meta'}}; " +
                "return setmetatable({old={value=1}},mt)",
        });
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; original=require('a')").Succeeded);
        var original = host.State.GetGlobal("original").AsTable();
        var oldValue = original.Get(LuaValue.FromString(
            host.State.Strings.GetOrCreate("old"u8))).AsTable();
        var oldMetatable = original.Metatable!;
        var oldMetatableValue = oldMetatable.Get(LuaValue.FromString(
            host.State.Strings.GetOrCreate("marker"u8))).AsTable();
        var prepared = host.PreparePatch(
            CreateBundle(Entry(
                "a",
                "local mt={marker={value='new-meta'}}; " +
                "return setmetatable({added=2},mt)")),
            new LuaPatchPrepareOptions
            {
                ModuleOptions = new Dictionary<string, LuaModuleReloadOptions>
                {
                    ["a"] = new()
                    {
                        CachePolicy = LuaModuleReloadCachePolicy.PatchExistingTable,
                    },
                },
            });
        Assert.True(prepared.Succeeded, prepared.Message);

        var healthChecked = false;
        var result = new LuaPatchCoordinator().CommitRing(
            "atomic-table-gc-rollback",
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
                    host.State.Heap.CollectFull();
                    Assert.True(oldValue.IsAlive);
                    Assert.True(oldMetatable.IsAlive);
                    Assert.True(oldMetatableValue.IsAlive);
                    Assert.True(original.Get(LuaValue.FromString(
                        host.State.Strings.GetOrCreate("old"u8))).IsNil);
                    healthChecked = true;
                    return LuaPatchRingHealthDecision.Rollback;
                },
            });

        Assert.Equal(LuaPatchRingCommitStatus.HealthRejected, result.Status);
        Assert.True(healthChecked);
        var current = host.RunUtf8("return require('a')").Execution!.Values[0].AsTable();
        Assert.Same(original, current);
        Assert.Same(oldValue, current.Get(LuaValue.FromString(
            host.State.Strings.GetOrCreate("old"u8))).AsTable());
        Assert.Same(oldMetatable, current.Metatable);
        Assert.True(current.Get(LuaValue.FromString(
            host.State.Strings.GetOrCreate("added"u8))).IsNil);
    }

    [Fact]
    public void AtomicTablePolicyHonorsTheSharedJournalLimit()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {old=1}",
        });
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; original=require('a')").Succeeded);
        var original = host.State.GetGlobal("original").AsTable();
        var prepared = host.PreparePatch(
            CreateBundle(Entry("a", "return {added=2}")),
            new LuaPatchPrepareOptions
            {
                ModuleOptions = new Dictionary<string, LuaModuleReloadOptions>
                {
                    ["a"] = new()
                    {
                        CachePolicy = LuaModuleReloadCachePolicy.PatchExistingTable,
                    },
                },
                ResourceLimits = LuaPatchResourceLimits.Default with
                {
                    MaximumTablePatchEntryCount = 1,
                },
            });
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.Equal(LuaPatchCommitStatus.CachePolicyFailed, commit.Status);
        Assert.Contains(
            nameof(LuaPatchResourceLimits.MaximumTablePatchEntryCount),
            commit.Message,
            StringComparison.Ordinal);
        Assert.Same(
            original,
            host.RunUtf8("return require('a')").Execution!.Values[0].AsTable());
        Assert.Equal(1, host.RunUtf8("return require('a').old")
            .Execution!.Values[0].AsInteger());
    }

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

    private static LuaPatchBundle CreateBundle(params LuaPatchEntry[] entries) =>
        CreateBundle(
            LuaLanguageVersion.Lua54,
            new DateTimeOffset(2099, 8, 22, 0, 0, 0, TimeSpan.Zero),
            entries);

    private static LuaPatchBundle CreateBundle(
        LuaLanguageVersion languageVersion,
        params LuaPatchEntry[] entries) =>
        CreateBundle(
            languageVersion,
            new DateTimeOffset(2099, 8, 22, 0, 0, 0, TimeSpan.Zero),
            entries);

    private static LuaPatchBundle CreateBundle(
        DateTimeOffset expiresAt,
        params LuaPatchEntry[] entries) =>
        CreateBundle(LuaLanguageVersion.Lua54, expiresAt, entries);

    private static LuaPatchBundle CreateBundle(
        LuaLanguageVersion languageVersion,
        DateTimeOffset expiresAt,
        params LuaPatchEntry[] entries)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return LuaPatchBundle.Create(
            new LuaPatchManifest
            {
                PatchId = "alpha2-test",
                Channel = "test",
                TargetBuild = "test-2",
                BaseRevision = "test-1",
                TargetRevision = "test-2",
                LanguageVersion = languageVersion,
                RuntimeAbi = "lunil-0.12",
                CreatedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero),
                ExpiresAt = expiresAt,
                Nonce = Guid.NewGuid().ToString("N"),
            },
            entries,
            new LuaPatchEcdsaSigner("test", key));
    }

    private static LuaHost CreateHost(
        IReadOnlyDictionary<string, string> files,
        LuaHostExecutionBackend backend = LuaHostExecutionBackend.Interpreter) =>
        CreateHost(new MutableMemoryFileSystem(files), backend, LuaLanguageVersion.Lua54);

    private static LuaHost CreateHost(
        IReadOnlyDictionary<string, string> files,
        LuaLanguageVersion languageVersion) =>
        CreateHost(
            new MutableMemoryFileSystem(files),
            LuaHostExecutionBackend.Interpreter,
            languageVersion);

    private static LuaHost CreateHost(
        MutableMemoryFileSystem files,
        LuaHostExecutionBackend backend = LuaHostExecutionBackend.Interpreter,
        LuaLanguageVersion languageVersion = LuaLanguageVersion.Lua54) => new(
        LuaHostOptions.Default with
        {
            LanguageVersion = languageVersion,
            ExecutionBackend = backend,
            Jit = LuaJitExecutorOptions.Default with
            {
                FunctionEntryThreshold = 1,
                BackedgeThreshold = 1,
                SynchronousCompilation = true,
                EnableTier2 = false,
                EnableLoopOsr = false,
            },
            StandardLibrary = LuaHostCapabilityProfiles.Create(LuaHostProfile.Restricted) with
            {
                FileSystem = files,
            },
        });

    private static LuaHost CreateClrCallbackHost(
        IReadOnlyDictionary<string, string> files,
        Type? eventSourceType = null)
    {
        var delegateName = typeof(Func<int, int>).FullName!;
        eventSourceType ??= typeof(CallbackEventSource);
        return new LuaHost(LuaHostOptions.Default with
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            StandardLibrary = LuaHostCapabilityProfiles.Create(LuaHostProfile.Restricted) with
            {
                FileSystem = new MutableMemoryFileSystem(files),
            },
            Clr = new LuaClrOptions
            {
                Capabilities = LuaClrCapabilities.DelegateConversion |
                    LuaClrCapabilities.EventSubscription,
                AllowedAssemblyNames =
                [
                    typeof(Func<int, int>).Assembly.GetName().Name!,
                    eventSourceType.Assembly.GetName().Name!,
                ],
                AllowedTypeNames = [delegateName, eventSourceType.FullName!],
                AllowedMemberNames = [nameof(CallbackEventSource.Changed)],
                AllowedDelegateTypeNames = [delegateName],
                AllowedEventNames = [nameof(CallbackEventSource.Changed)],
            },
        });
    }

    private static LuaHost CreateClrTaskHost(IReadOnlyDictionary<string, string> files)
    {
        var typeName = typeof(PatchTaskSource).FullName!;
        return new LuaHost(LuaHostOptions.Default with
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            StandardLibrary = LuaHostCapabilityProfiles.Create(LuaHostProfile.Restricted) with
            {
                FileSystem = new MutableMemoryFileSystem(files),
            },
            Clr = new LuaClrOptions
            {
                Capabilities = LuaClrCapabilities.MemberAccess | LuaClrCapabilities.Async,
                AllowedAssemblyNames = [typeof(PatchTaskSource).Assembly.GetName().Name!],
                AllowedTypeNames = [typeName],
                AllowedMemberNames =
                [
                    nameof(PatchTaskSource.Create),
                    nameof(PatchTaskSource.CreatePending),
                ],
                InstallGlobalModule = true,
            },
        });
    }

    private static LuaHost CreateClrTimerHost(
        IReadOnlyDictionary<string, string> files,
        ManualTimeProvider timeProvider) => new(LuaHostOptions.Default with
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            StandardLibrary = LuaHostCapabilityProfiles.Create(LuaHostProfile.Restricted) with
            {
                FileSystem = new MutableMemoryFileSystem(files),
            },
            Clr = new LuaClrOptions
            {
                Capabilities = LuaClrCapabilities.Timers,
                InstallGlobalModule = true,
                TimeProvider = timeProvider,
            },
        });

    private static LuaClrTask TaskPayload(LuaValue value) =>
        Assert.IsType<LuaClrTask>(value.AsUserdata().Payload);

    private static LuaClrTimer TimerPayload(LuaValue value) =>
        Assert.IsType<LuaClrTimer>(value.AsUserdata().Payload);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);
        private long _timestamp;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan value)
        {
            _now += value;
            _timestamp += value.Ticks;
        }
    }

    public sealed class CallbackEventSource
    {
        public event Func<int, int>? Changed;

        public int Raise(int value) => Changed?.Invoke(value) ?? -1;
    }

    public sealed class FaultingCallbackEventSource
    {
        private Func<int, int>? _changed;

        public bool ThrowOnRemove { get; set; }

        public event Func<int, int> Changed
        {
            add => _changed += value;
            remove
            {
                if (ThrowOnRemove)
                {
                    throw new InvalidOperationException("Event detachment failed.");
                }

                _changed -= value;
            }
        }

        public int Raise(int value) => _changed?.Invoke(value) ?? -1;
    }

    public static class PatchTaskSource
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<
            int,
            TaskCompletionSource<int>> Pending = [];

        public static Task<int> Create(int value) => Task.FromResult(value);

        public static Task<int> CreatePending(int id) => Pending.GetOrAdd(
            id,
            static _ => new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public static void Complete(int id, int value)
        {
            if (Pending.TryRemove(id, out var completion))
            {
                completion.TrySetResult(value);
            }
        }
    }

    private sealed class MutableMemoryFileSystem(IReadOnlyDictionary<string, string> files)
        : ILuaFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = files.ToDictionary(
            static pair => pair.Key,
            static pair => Encoding.UTF8.GetBytes(pair.Value),
            StringComparer.Ordinal);

        public byte[] ReadAllBytes(string path) => _files.TryGetValue(path, out var bytes)
            ? bytes.ToArray()
            : throw new FileNotFoundException(path);

        public bool FileExists(string path) => _files.ContainsKey(path);

        public void Set(string path, string source) =>
            _files[path] = Encoding.UTF8.GetBytes(source);
    }
}
