using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Lunil.Hosting;
using Lunil.Runtime.Values;

namespace Lunil.Gameplay.Fixture
{
    public sealed class SharedEngineSoakSession
    {
        private const string SoakSource =
            "soak_state={tick=0,checksum=17,items={}};" +
            "while not soak_stop do " +
            "soak_state.tick=soak_state.tick+1;local tick=soak_state.tick;" +
            "local slot=1+(tick%128);local previous=soak_state.items[slot];" +
            "local value=((previous and previous.value or 0)+tick*17+slot)%2147483647;" +
            "soak_state.items[slot]={value=value,tick=tick,payload={slot,tick%97,value%193}};" +
            "soak_state.checksum=(soak_state.checksum*48271+value+slot)%2147483647;" +
            "if tick%64==0 then collectgarbage('step',32) end;coroutine.yield() end;" +
            "return soak_state.tick,soak_state.checksum";

        private readonly LuaGameLoopHost _gameLoop;
        private readonly Func<LuaGameLoopTickResult?> _tick;
        private readonly string _hostIdentity;
        private readonly TimeSpan _duration;
        private readonly TimeSpan _warmup;
        private readonly TimeSpan _sampleInterval;
        private readonly Stopwatch _stopwatch;
        private readonly List<SharedEngineSoakSample> _samples =
            new List<SharedEngineSoakSample>();
        private readonly LuaGameLoopOperation _operation;
        private TimeSpan _nextSample;
        private bool _completed;

        public SharedEngineSoakSession(
            LuaGameLoopHost gameLoop,
            Func<LuaGameLoopTickResult?> tick,
            string hostIdentity,
            TimeSpan duration,
            TimeSpan warmup,
            TimeSpan sampleInterval)
        {
#pragma warning disable CA1510 // Shared source targets the Unity 2022.3 API surface.
            if (gameLoop == null) throw new ArgumentNullException(nameof(gameLoop));
            if (tick == null) throw new ArgumentNullException(nameof(tick));
#pragma warning restore CA1510
            if (string.IsNullOrWhiteSpace(hostIdentity))
                throw new ArgumentException("A soak host identity is required.", nameof(hostIdentity));
#pragma warning disable CA1512 // Shared source targets the Unity 2022.3 API surface.
            if (duration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration));
            if (warmup < TimeSpan.Zero || warmup >= duration)
                throw new ArgumentOutOfRangeException(nameof(warmup));
            if (sampleInterval <= TimeSpan.Zero || sampleInterval > duration - warmup)
                throw new ArgumentOutOfRangeException(nameof(sampleInterval));
#pragma warning restore CA1512

            _gameLoop = gameLoop;
            _tick = tick;
            _hostIdentity = hostIdentity;
            _duration = duration;
            _warmup = warmup;
            _sampleInterval = sampleInterval;
            var compilation = gameLoop.Host.CompileUtf8(SoakSource, "=engine-soak");
            Require(compilation.Succeeded, "The shared engine soak source did not compile.");
            _operation = gameLoop.Start(compilation);
            _stopwatch = Stopwatch.StartNew();
            _nextSample = warmup;
        }

        public bool IsComplete { get { return _completed; } }
        public long TickCount { get; private set; }

        public SharedEngineSoakResult? Tick()
        {
            if (_completed) throw new InvalidOperationException("The soak session already completed.");
            var result = _tick();
            Require(result != null && result.Succeeded,
                "A shared engine soak tick failed: " + DescribeFailure(result));
            TickCount++;

            var elapsed = _stopwatch.Elapsed;
            if (elapsed >= _nextSample)
            {
                _samples.Add(CaptureSample(elapsed));
                do { _nextSample += _sampleInterval; }
                while (_nextSample <= elapsed);
            }
            if (elapsed < _duration) return null;
            return Complete();
        }

        private SharedEngineSoakResult Complete()
        {
            _gameLoop.Host.State.SetGlobal("soak_stop", LuaValue.FromBoolean(true));
            var finalTick = _tick();
            Require(finalTick != null && finalTick.Succeeded,
                "The shared engine soak final tick failed: " + DescribeFailure(finalTick));
            TickCount++;
            Require(_operation.Status == LuaGameLoopOperationStatus.Completed,
                "The shared engine soak coroutine did not complete.");
            _gameLoop.Host.State.Heap.CollectFull();
            _samples.Add(CaptureSample(_stopwatch.Elapsed));
            Require(_gameLoop.ActiveOperationCount == 0 && _gameLoop.PendingWorkCount == 0,
                "The shared engine soak retained active work after completion.");

            var stable = _samples.Where(sample => sample.Elapsed >= _warmup).ToArray();
            Require(stable.Length >= 4,
                "The shared engine soak needs at least four stable-window samples.");
            var windowSize = Math.Min(3, stable.Length / 2);
            var first = stable.Take(windowSize).ToArray();
            var last = stable.Skip(stable.Length - windowSize).ToArray();
            var managedGrowth = GrowthRatio(
                first.Average(sample => (double)sample.ManagedBytes),
                last.Average(sample => (double)sample.ManagedBytes));
            var logicalGrowth = GrowthRatio(
                first.Average(sample => (double)sample.LogicalBytes),
                last.Average(sample => (double)sample.LogicalBytes));
            var objectGrowth = GrowthRatio(
                first.Average(sample => (double)sample.ObjectCount),
                last.Average(sample => (double)sample.ObjectCount));
            var maximumGrowth = Math.Max(managedGrowth, Math.Max(logicalGrowth, objectGrowth));
            Require(maximumGrowth <= 0.05,
                "The shared engine soak retained-state growth exceeded 5%: " +
                maximumGrowth.ToString("P3", CultureInfo.InvariantCulture));
            _completed = true;
            return new SharedEngineSoakResult(
                _hostIdentity,
                _stopwatch.Elapsed,
                TickCount,
                managedGrowth,
                logicalGrowth,
                objectGrowth,
                maximumGrowth,
                stable.Length,
                _gameLoop.ActiveOperationCount,
                _gameLoop.PendingWorkCount);
        }

        private SharedEngineSoakSample CaptureSample(TimeSpan elapsed)
        {
            _gameLoop.Host.State.Heap.CollectFull();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return new SharedEngineSoakSample(
                elapsed,
                GC.GetTotalMemory(true),
                _gameLoop.Host.State.Heap.LogicalBytes,
                _gameLoop.Host.State.Heap.ObjectCount);
        }

        private static double GrowthRatio(double first, double last)
        {
            if (first <= 0.0) return last <= 0.0 ? 0.0 : double.PositiveInfinity;
            return Math.Max(0.0, (last - first) / first);
        }

        private static string DescribeFailure(LuaGameLoopTickResult? result)
        {
            if (result == null) return "tick returned null";
            return result.Failures.Length == 0
                ? "unknown failure"
                : string.Join(" | ", result.Failures);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    public sealed class SharedEngineSoakSample
    {
        public SharedEngineSoakSample(
            TimeSpan elapsed,
            long managedBytes,
            long logicalBytes,
            int objectCount)
        {
            Elapsed = elapsed;
            ManagedBytes = managedBytes;
            LogicalBytes = logicalBytes;
            ObjectCount = objectCount;
        }

        public TimeSpan Elapsed { get; private set; }
        public long ManagedBytes { get; private set; }
        public long LogicalBytes { get; private set; }
        public int ObjectCount { get; private set; }
    }

    public sealed class SharedEngineSoakResult
    {
        public SharedEngineSoakResult(
            string hostIdentity,
            TimeSpan elapsed,
            long tickCount,
            double managedGrowth,
            double logicalGrowth,
            double objectGrowth,
            double maximumGrowth,
            int sampleCount,
            int activeOperationCount,
            int pendingWorkCount)
        {
            HostIdentity = hostIdentity;
            Elapsed = elapsed;
            TickCount = tickCount;
            ManagedGrowth = managedGrowth;
            LogicalGrowth = logicalGrowth;
            ObjectGrowth = objectGrowth;
            MaximumGrowth = maximumGrowth;
            SampleCount = sampleCount;
            ActiveOperationCount = activeOperationCount;
            PendingWorkCount = pendingWorkCount;
        }

        public string HostIdentity { get; private set; }
        public TimeSpan Elapsed { get; private set; }
        public long TickCount { get; private set; }
        public double ManagedGrowth { get; private set; }
        public double LogicalGrowth { get; private set; }
        public double ObjectGrowth { get; private set; }
        public double MaximumGrowth { get; private set; }
        public int SampleCount { get; private set; }
        public int ActiveOperationCount { get; private set; }
        public int PendingWorkCount { get; private set; }

        public string ToMarker()
        {
            return "LUNIL_ENGINE_SOAK_RESULT host=" + HostIdentity +
                " seconds=" + Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture) +
                " ticks=" + TickCount +
                " managed_growth=" + ManagedGrowth.ToString("F6", CultureInfo.InvariantCulture) +
                " logical_growth=" + LogicalGrowth.ToString("F6", CultureInfo.InvariantCulture) +
                " object_growth=" + ObjectGrowth.ToString("F6", CultureInfo.InvariantCulture) +
                " max_growth=" + MaximumGrowth.ToString("F6", CultureInfo.InvariantCulture) +
                " samples=" + SampleCount +
                " active=" + ActiveOperationCount +
                " pending=" + PendingWorkCount;
        }
    }
}
