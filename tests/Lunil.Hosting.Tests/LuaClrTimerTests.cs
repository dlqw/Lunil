using Lunil.Runtime.Values;
using Lunil.Runtime.Execution;

namespace Lunil.Hosting.Tests;

public sealed class LuaClrTimerTests
{
    [Fact]
    public void OneShotTimerRunsOnlyWhenOwnerPollsAfterDueTime()
    {
        var time = new ManualTimeProvider();
        using var host = CreateHost(time);
        Assert.True(host.RunUtf8(
            "ticks=0; timer=clr.timer(function(tick,missed) ticks=tick*10+missed end,100)")
            .Succeeded);
        var timer = TimerPayload(host.State.GetGlobal("timer"));

        time.Advance(TimeSpan.FromMilliseconds(99));
        Assert.Equal(0, host.DispatchClrTimers());
        Assert.Equal(0, host.State.GetGlobal("ticks").AsInteger());

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, host.DispatchClrTimers());
        Assert.Equal(10, host.State.GetGlobal("ticks").AsInteger());
        Assert.False(timer.IsScheduled);
        Assert.Equal(0, host.DispatchClrTimers());
    }

    [Fact]
    public void CoalescingPeriodicTimerPreservesPhaseAndReportsMissedTicks()
    {
        var time = new ManualTimeProvider();
        using var host = CreateHost(time);
        Assert.True(host.RunUtf8(
            "seen_tick=0; seen_missed=0; " +
            "timer=clr.timer(function(tick,missed) seen_tick=tick; seen_missed=missed end," +
            "5,10,'coalesce')").Succeeded);

        time.Advance(TimeSpan.FromMilliseconds(45));

        Assert.Equal(1, host.DispatchClrTimers());
        Assert.Equal(1, host.State.GetGlobal("seen_tick").AsInteger());
        Assert.Equal(4, host.State.GetGlobal("seen_missed").AsInteger());
        var timer = TimerPayload(host.State.GetGlobal("timer"));
        Assert.Equal(4, timer.MissedTickCount);
        Assert.Equal(0, host.DispatchClrTimers());

        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(1, host.DispatchClrTimers());
        Assert.Equal(2, host.State.GetGlobal("seen_tick").AsInteger());
    }

    [Fact]
    public void SkippingPeriodicTimerSchedulesFromPollTimeAndReportsMissedTicks()
    {
        var time = new ManualTimeProvider();
        using var host = CreateHost(time);
        Assert.True(host.RunUtf8(
            "seen_missed=0; " +
            "timer=clr.timer(function(_,missed) seen_missed=missed end,5,10,'skip')")
            .Succeeded);

        time.Advance(TimeSpan.FromMilliseconds(45));

        Assert.Equal(1, host.DispatchClrTimers());
        Assert.Equal(4, host.State.GetGlobal("seen_missed").AsInteger());
        time.Advance(TimeSpan.FromMilliseconds(9));
        Assert.Equal(0, host.DispatchClrTimers());
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, host.DispatchClrTimers());
    }

    [Fact]
    public void WallClockChangesDoNotAdvanceMonotonicTimers()
    {
        var time = new ManualTimeProvider();
        using var host = CreateHost(time);
        Assert.True(host.RunUtf8(
            "ticks=0; timer=clr.timer(function() ticks=ticks+1 end,100)").Succeeded);
        time.Advance(TimeSpan.FromMilliseconds(50));
        time.JumpUtc(TimeSpan.FromDays(30));

        Assert.Equal(0, host.DispatchClrTimers());
        time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(1, host.DispatchClrTimers());
    }

    [Fact]
    public void CatchUpPolicyIsFairAndBoundedPerDispatch()
    {
        var time = new ManualTimeProvider();
        using var host = CreateHost(time, maximumDispatch: 10);
        Assert.True(host.RunUtf8(
            "ticks=0; timer=clr.timer(function() ticks=ticks+1 end,5,10,'catch_up',3)")
            .Succeeded);
        time.Advance(TimeSpan.FromMilliseconds(45));

        Assert.Equal(3, host.DispatchClrTimers());
        Assert.Equal(3, host.State.GetGlobal("ticks").AsInteger());
        Assert.Equal(2, host.DispatchClrTimers());
        Assert.Equal(5, host.State.GetGlobal("ticks").AsInteger());
    }

    [Fact]
    public void CancelTimerAndHostDisposalAreIdempotent()
    {
        var time = new ManualTimeProvider();
        var host = CreateHost(time);
        Assert.True(host.RunUtf8(
            "ticks=0; timer=clr.timer(function() ticks=ticks+1 end,0,10); " +
            "clr.cancel_timer(timer); clr.cancel_timer(timer)").Succeeded);
        var timer = TimerPayload(host.State.GetGlobal("timer"));

        Assert.True(timer.IsDisposed);
        Assert.Equal(0, host.DispatchClrTimers());
        host.Dispose();
        host.Dispose();
    }

    [Fact]
    public void HostDisposalCancelsOutstandingTimerResources()
    {
        var time = new ManualTimeProvider();
        var host = CreateHost(time);
        var callback = host.RunUtf8("return function() end").Execution!.Values[0];
        var timer = host.ClrBridge.ScheduleTimer(
            callback,
            new LuaClrTimerOptions { DueTime = TimeSpan.FromDays(1) });

        host.Dispose();

        Assert.True(timer.IsDisposed);
        Assert.False(timer.IsActive);
        Assert.False(timer.IsScheduled);
    }

    [Fact]
    public void CollectedTimerUserdataReleasesSchedulerRegistration()
    {
        var time = new ManualTimeProvider();
        using var host = CreateHost(time);
        LuaClrTimer? captured = null;
        host.State.SetGlobal(
            "capture_timer",
            LuaValue.FromFunction(new LuaNativeFunction(
                "capture_timer",
                (_, values) =>
                {
                    captured = TimerPayload(values[0]);
                    return [];
                })));

        Assert.True(host.RunUtf8(
            "local timer=clr.timer(function() end,1000); capture_timer(timer); " +
            "timer=nil; collectgarbage('collect'); collectgarbage('collect')").Succeeded);

        Assert.True(captured!.IsDisposed);
        Assert.Equal(0, host.ClrBridge.ActiveTimerCount);
    }

    [Fact]
    public void TimerCountAndDispatchBoundsFailClosed()
    {
        var time = new ManualTimeProvider();
        using var host = CreateHost(time, maximumTimers: 1, maximumDispatch: 2);
        var callback = host.RunUtf8("return function() end").Execution!.Values[0];
        using var timer = host.ClrBridge.ScheduleTimer(callback, new LuaClrTimerOptions());

        var limit = Assert.Throws<LuaClrException>(() =>
            host.ClrBridge.ScheduleTimer(callback, new LuaClrTimerOptions()));
        Assert.Equal(LuaClrErrorCode.InvocationFailed, limit.Code);
        Assert.Throws<ArgumentOutOfRangeException>(() => host.DispatchClrTimers(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => host.ClrBridge.ScheduleTimer(
            callback,
            new LuaClrTimerOptions { Period = TimeSpan.Zero }));
    }

    [Fact]
    public void TimerDurationPolicyAndCatchUpBoundsFailClosed()
    {
        var time = new ManualTimeProvider();
        using var host = CreateHost(time);
        var callback = host.RunUtf8("return function() end").Execution!.Values[0];

        Assert.Throws<ArgumentOutOfRangeException>(() => host.ClrBridge.ScheduleTimer(
            callback,
            new LuaClrTimerOptions { DueTime = TimeSpan.FromTicks(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => host.ClrBridge.ScheduleTimer(
            callback,
            new LuaClrTimerOptions { DueTime = TimeSpan.FromDays(3651) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => host.ClrBridge.ScheduleTimer(
            callback,
            new LuaClrTimerOptions { Period = TimeSpan.FromDays(3651) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => host.ClrBridge.ScheduleTimer(
            callback,
            new LuaClrTimerOptions { MaximumCatchUpTicks = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => host.ClrBridge.ScheduleTimer(
            callback,
            new LuaClrTimerOptions { MaximumCatchUpTicks = 10_001 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => host.ClrBridge.ScheduleTimer(
            callback,
            new LuaClrTimerOptions { CatchUpPolicy = (LuaClrTimerCatchUpPolicy)byte.MaxValue }));
    }

    [Fact]
    public void HostOwnedTimerIsNotStagedByUnrelatedModulePatchGeneration()
    {
        var time = new ManualTimeProvider();
        using var host = CreateHost(time);
        var callback = host.RunUtf8("return function() host_ticks=(host_ticks or 0)+1 end")
            .Execution!.Values[0];
        using var timer = host.ClrBridge.ScheduleTimer(
            callback,
            new LuaClrTimerOptions { DueTime = TimeSpan.Zero });

        Assert.True(timer.IsActive);
        Assert.Equal(1, host.ClrBridge.ActiveTimerCount);
        Assert.Equal(1, host.DispatchClrTimers());
        Assert.Equal(1, host.State.GetGlobal("host_ticks").AsInteger());
    }

    [Fact]
    public void DispatchRejectsReentryWhileLuaStateIsRunning()
    {
        var time = new ManualTimeProvider();
        using var host = CreateHost(time);
        LuaClrErrorCode? code = null;
        host.State.SetGlobal(
            "poll_from_lua",
            LuaValue.FromFunction(new LuaNativeFunction(
                "poll_from_lua",
                (_, _) =>
                {
                    code = Assert.Throws<LuaClrException>(
                        () => host.ClrBridge.DispatchTimers()).Code;
                    return [];
                })));

        Assert.True(host.RunUtf8("poll_from_lua()").Succeeded);
        Assert.Equal(LuaClrErrorCode.ThreadDenied, code);
    }

    [Fact]
    public async Task DispatchRejectsNonOwnerThread()
    {
        var time = new ManualTimeProvider();
        using var host = CreateHost(time);
        var completion = new TaskCompletionSource<LuaClrException>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                host.DispatchClrTimers();
                completion.SetException(new InvalidOperationException(
                    "Timer dispatch unexpectedly accepted a non-owner thread."));
            }
            catch (LuaClrException exception)
            {
                completion.SetResult(exception);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });

        thread.Start();
        var error = await completion.Task;

        Assert.Equal(LuaClrErrorCode.ThreadDenied, error.Code);
    }

    [Fact]
    public void CallbackFailureConsumesOnlyItsSelectedTick()
    {
        var time = new ManualTimeProvider();
        using var host = CreateHost(time);
        Assert.True(host.RunUtf8(
            "timer=clr.timer(function() error('timer failed') end,0,10)").Succeeded);

        var error = Assert.Throws<LuaClrException>(() => host.DispatchClrTimers());
        Assert.Equal(LuaClrErrorCode.InvocationFailed, error.Code);
        var timer = TimerPayload(host.State.GetGlobal("timer"));
        Assert.Equal(1, timer.DispatchedTickCount);
        Assert.True(timer.IsScheduled);
        Assert.Equal(0, host.DispatchClrTimers());
    }

    private static LuaHost CreateHost(
        ManualTimeProvider timeProvider,
        int maximumTimers = 32,
        int maximumDispatch = 16) => new(LuaHostOptions.Default with
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            Clr = new LuaClrOptions
            {
                Capabilities = LuaClrCapabilities.Timers,
                InstallGlobalModule = true,
                TimeProvider = timeProvider,
                MaximumTimerCount = maximumTimers,
                MaximumTimerDispatchCount = maximumDispatch,
            },
        });

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

        public void JumpUtc(TimeSpan value) => _now += value;
    }
}
