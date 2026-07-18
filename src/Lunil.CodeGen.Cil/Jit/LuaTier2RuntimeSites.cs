using System.Runtime.CompilerServices;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;

namespace Lunil.CodeGen.Cil.Jit;

/// <summary>
/// Per-compiled-method mutable inline-cache state. It is held by the generated delegate and
/// uses weak Runtime cache entries, so compiled code does not keep Lua objects alive.
/// </summary>
internal sealed class LuaTier2RuntimeSites
{
    private readonly LuaCodegenTableSiteCache?[] _tableSites;
    private readonly LuaCodegenCallSiteCache?[] _callSites;
    private readonly LuaDirectCompiledMethod?[] _directCallSites;
    private readonly List<LuaTier2RuntimeSites> _counterChildren = [];
    private LuaDirectCallCounterSink? _directCallCounters;
    private LuaTablePicCounterSink? _tablePicCounters;

    public LuaTier2RuntimeSites(
        int instructionCount,
        IReadOnlyDictionary<int, LuaBoundDirectCall>? directCallSites = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(instructionCount);
        _tableSites = new LuaCodegenTableSiteCache?[instructionCount];
        _callSites = new LuaCodegenCallSiteCache?[instructionCount];
        _directCallSites = new LuaDirectCompiledMethod?[instructionCount];
        if (directCallSites is not null)
        {
            foreach (var (programCounter, directCall) in directCallSites)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(programCounter);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                    programCounter,
                    instructionCount);
                ArgumentNullException.ThrowIfNull(directCall);
                _directCallSites[programCounter] = directCall.Method;
            }
        }
    }

    internal void BindDirectCallCounters(LuaDirectCallCounterSink counters)
    {
        ArgumentNullException.ThrowIfNull(counters);
        Volatile.Write(ref _directCallCounters, counters);
        foreach (var child in _counterChildren)
        {
            child.BindDirectCallCounters(counters);
        }
    }

    internal void BindTablePicCounters(LuaTablePicCounterSink counters)
    {
        ArgumentNullException.ThrowIfNull(counters);
        Volatile.Write(ref _tablePicCounters, counters);
        foreach (var site in _tableSites)
        {
            site?.BindCounters(counters);
        }

        foreach (var child in _counterChildren)
        {
            child.BindTablePicCounters(counters);
        }
    }

    internal void AttachCounterChild(LuaTier2RuntimeSites child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (ReferenceEquals(this, child))
        {
            throw new ArgumentException("A Runtime site set cannot contain itself.", nameof(child));
        }

        _counterChildren.Add(child);
        if (Volatile.Read(ref _directCallCounters) is { } counters)
        {
            child.BindDirectCallCounters(counters);
        }

        if (Volatile.Read(ref _tablePicCounters) is { } tablePicCounters)
        {
            child.BindTablePicCounters(tablePicCounters);
        }
    }

    internal bool TryExecuteBoundDirectCall(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame caller,
        int programCounter,
        int functionRegister,
        int argumentCount,
        int expectedResults)
    {
        var method = Volatile.Read(ref _directCallSites[programCounter]);
        if (method is null)
        {
            return false;
        }

        if (context.State.Heap.PendingFinalizerCount != 0)
        {
            return false;
        }

        var completed = method(
            context,
            thread,
            caller,
            functionRegister,
            argumentCount,
            expectedResults);
        if (completed)
        {
            _directCallCounters?.RecordCompletion();
        }
        else
        {
            _directCallCounters?.RecordFallback(!context.IsBackendGenerationCurrent());
        }

        return completed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordInlineDirectCallCompletion() =>
        _directCallCounters?.RecordCompletion();

    internal void RecordInlineDirectCallFallback() =>
        _directCallCounters?.RecordFallback(invalidated: false);

    internal LuaCodegenTableSiteCache GetTableSite(int programCounter)
    {
        var site = Volatile.Read(ref _tableSites[programCounter]);
        if (site is not null)
        {
            return site;
        }

        var created = Volatile.Read(ref _tablePicCounters) is { } counters
            ? new LuaCodegenTableSiteCache(counters)
            : new LuaCodegenTableSiteCache();
        return Interlocked.CompareExchange(
            ref _tableSites[programCounter],
            created,
            null) ?? created;
    }

    internal LuaCodegenCallSiteCache GetCallSite(
        int programCounter,
        string expectedModuleContentId)
    {
        var site = Volatile.Read(ref _callSites[programCounter]);
        if (site is not null)
        {
            return site;
        }

        var created = new LuaCodegenCallSiteCache(
            expectedModuleContentId,
            LuaJitModuleIdentity.Create);
        return Interlocked.CompareExchange(
            ref _callSites[programCounter],
            created,
            null) ?? created;
    }
}

internal sealed class LuaTablePicCounterSink : ILuaCodegenTablePicCounterSink
{
    private long _hits;
    private long _misses;
    private long _invalidations;

    public bool SupportsWeightedSampling => true;

    public long Hits => Interlocked.Read(ref _hits);

    public long Misses => Interlocked.Read(ref _misses);

    public long Invalidations => Interlocked.Read(ref _invalidations);

    public void RecordHit() => Interlocked.Increment(ref _hits);

    public void RecordMiss() => Interlocked.Increment(ref _misses);

    public void RecordInvalidation() => Interlocked.Increment(ref _invalidations);

    public void RecordHits(int count) => Interlocked.Add(ref _hits, count);

    public void RecordMisses(int count) => Interlocked.Add(ref _misses, count);
}

internal sealed class LuaDirectCallCounterSink : IDisposable
{
    private sealed class CounterShard
    {
        public long Completions;
        public long Fallbacks;
        public long Invalidations;
    }

    private static int s_nextId;

    [ThreadStatic]
    private static int t_cachedId;

    [ThreadStatic]
    private static CounterShard? t_cachedShard;

    private readonly int _id = Interlocked.Increment(ref s_nextId);
    private readonly ThreadLocal<CounterShard> _shards = new(
        static () => new CounterShard(),
        trackAllValues: true);
    private readonly Lock _disposeGate = new();
    private long _finalCompletions;
    private long _finalFallbacks;
    private long _finalInvalidations;
    private bool _disposed;

    public long Entries => checked(Completions + Fallbacks);

    public long Completions => Sum(
        static shard => shard.Completions,
        ref _finalCompletions);

    public long Fallbacks => Sum(
        static shard => shard.Fallbacks,
        ref _finalFallbacks);

    public long Invalidations => Sum(
        static shard => shard.Invalidations,
        ref _finalInvalidations);

    public long SchedulerExitsAvoided => checked(Completions * 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordCompletion()
    {
        var shard = CurrentShard();
        shard.Completions++;
    }

    public void RecordFallback(bool invalidated)
    {
        var shard = CurrentShard();
        shard.Fallbacks++;
        if (invalidated)
        {
            shard.Invalidations++;
        }
    }

    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var shard in _shards.Values)
            {
                _finalCompletions = checked(_finalCompletions + shard.Completions);
                _finalFallbacks = checked(_finalFallbacks + shard.Fallbacks);
                _finalInvalidations = checked(_finalInvalidations + shard.Invalidations);
            }

            _shards.Dispose();
            _disposed = true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private CounterShard CurrentShard()
    {
        if (t_cachedId == _id && t_cachedShard is { } cached)
        {
            return cached;
        }

        var shard = _shards.Value!;
        t_cachedId = _id;
        t_cachedShard = shard;
        return shard;
    }

    private long Sum(Func<CounterShard, long> selector, ref long finalValue)
    {
        lock (_disposeGate)
        {
            if (_disposed)
            {
                return finalValue;
            }

            long total = 0;
            foreach (var shard in _shards.Values)
            {
                total = checked(total + selector(shard));
            }

            return total;
        }
    }
}
