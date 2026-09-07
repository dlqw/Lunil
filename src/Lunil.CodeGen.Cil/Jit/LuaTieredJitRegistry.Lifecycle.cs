using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Lunil.CodeGen.Cil.Emission;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;

namespace Lunil.CodeGen.Cil.Jit;

internal sealed partial class LuaTieredJitRegistry
{
    public void Invalidate(LuaIrModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        InvalidateModule(GetModuleContentId(module));
    }

    public void ClearCache()
    {
        foreach (var moduleContentId in _entries.Keys
            .Select(static key => key.ModuleContentId)
            .Distinct(StringComparer.Ordinal))
        {
            InvalidateModule(moduleContentId);
        }
    }

    public async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_entries.Values.All(static entry =>
            {
                lock (entry.Gate)
                {
                    return entry.State is not LuaJitFunctionState.Queued and
                        not LuaJitFunctionState.Compiling &&
                        entry.Tier2State is not LuaJitTier2State.Queued and
                        not LuaJitTier2State.Compiling &&
                        entry.LoopOsrEntries.Values.All(static loop =>
                            loop.State is not LuaJitOsrState.Queued and
                                not LuaJitOsrState.Compiling);
                }
            }))
            {
                return;
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        _disposeCancellation.Cancel();
        var calledFromWorker = _workerCallContext.Value;
        if (!calledFromWorker)
        {
            try
            {
                Task.WhenAll(_workers).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }

        var generations = _moduleGenerations.Values.ToArray();
        foreach (var generation in generations)
        {
            generation.BeginInvalidation();
        }

        try
        {
            foreach (var entry in _entries.Values)
            {
                lock (entry.Gate)
                {
                    entry.Method = null;
                    entry.Tier1Method = null;
                    entry.PlainTier1Method = null;
                    entry.Tier2Method = null;
                    entry.DirectCallMethod = null;
                    entry.Tier2Plan = null;
                    entry.Tier1EstimatedCodeBytes = 0;
                    entry.Tier2EstimatedCodeBytes = 0;
                    entry.EstimatedCodeBytes = 0;
                    entry.ActiveTier = LuaJitCompilationTier.Interpreter;
                    entry.State = LuaJitFunctionState.Invalidated;
                    entry.Tier2State = LuaJitTier2State.Invalidated;
                    Volatile.Write(ref entry.Tier2ProfilingActive, 0);
                    entry.Completion?.TrySetCanceled();
                    entry.Completion = null;
                    entry.Tier2Completion?.TrySetCanceled();
                    entry.Tier2Completion = null;
                    foreach (var loop in entry.LoopOsrEntries.Values)
                    {
                        loop.Method = null;
                        loop.EstimatedCodeBytes = 0;
                        loop.State = LuaJitOsrState.Invalidated;
                        loop.Completion?.TrySetCanceled();
                        loop.Completion = null;
                    }
                }
            }

            Interlocked.Exchange(ref _estimatedCodeBytes, 0);
            _entries.Clear();
        }
        finally
        {
            foreach (var generation in generations)
            {
                generation.CompleteInvalidation();
            }
        }
        _boundDirectCallCounters.Dispose();
        if (calledFromWorker)
        {
            _ = DisposeCancellationWhenWorkersCompleteAsync();
        }
        else
        {
            _disposeCancellation.Dispose();
        }
    }
}
