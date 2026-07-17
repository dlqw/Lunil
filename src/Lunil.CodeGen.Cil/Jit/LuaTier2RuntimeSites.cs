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

        var created = new LuaCodegenTableSiteCache();
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

internal sealed class LuaDirectCallCounterSink
{
    private long _completions;
    private long _fallbacks;
    private long _invalidations;

    public long Entries => checked(Completions + Fallbacks);

    public long Completions => Interlocked.Read(ref _completions);

    public long Fallbacks => Interlocked.Read(ref _fallbacks);

    public long Invalidations => Interlocked.Read(ref _invalidations);

    public long SchedulerExitsAvoided => checked(Completions * 2);

    public void RecordCompletion() => Interlocked.Increment(ref _completions);

    public void RecordFallback(bool invalidated)
    {
        Interlocked.Increment(ref _fallbacks);
        if (invalidated)
        {
            Interlocked.Increment(ref _invalidations);
        }
    }
}
