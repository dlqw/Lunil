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
    private async Task WorkerAsync()
    {
        _workerCallContext.Value = true;
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(
                _disposeCancellation.Token).ConfigureAwait(false))
            {
                Compile(request);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _workerCallContext.Value = false;
        }
    }

    private async Task DisposeCancellationWhenWorkersCompleteAsync()
    {
        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _disposeCancellation.Dispose();
        }
    }

    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "Queue admission rejects tier compilation when dynamic code is unavailable.")]
    private void Compile(CompilationRequest request)
    {
        if (_disposeCancellation.IsCancellationRequested)
        {
            request.Completion.TrySetCanceled(_disposeCancellation.Token);
            return;
        }

        if (request.Tier == LuaJitCompilationTier.Tier2)
        {
            CompileTier2(request);
            return;
        }

        if (request.Tier == LuaJitCompilationTier.LoopOsr)
        {
            CompileLoopOsr(request);
            return;
        }

        lock (request.Entry.Gate)
        {
            if (request.Entry.State != LuaJitFunctionState.Queued ||
                !ReferenceEquals(request.Entry.Completion, request.Completion))
            {
                request.Completion.TrySetResult(false);
                return;
            }

            request.Entry.State = LuaJitFunctionState.Compiling;
        }

        var queueLatency = Stopwatch.GetElapsedTime(request.EnqueuedTimestamp);
        Interlocked.Add(ref _totalQueueLatencyTicks, queueLatency.Ticks);
        Interlocked.Increment(ref _compilationStarted);
        RaiseEvent(new LuaJitEvent(
            LuaJitEventKind.CompilationStarted,
            request.Entry.Key.ModuleContentId,
            request.Entry.Key.FunctionId,
            LuaJitFunctionState.Compiling,
            Duration: queueLatency));

        var started = Stopwatch.GetTimestamp();
        LuaTier1CompilationResult result;
        CancellationTokenSource? linkedCancellation = null;
        var compilationCancellation = request.CancellationToken.CanBeCanceled
            ? (linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _disposeCancellation.Token,
                request.CancellationToken)).Token
            : _disposeCancellation.Token;
        try
        {
            result = _compiler.Compile(
                request.Module,
                request.Entry.Key.FunctionId,
                IsTier2Enabled || IsLoopOsrEnabled,
                compilationCancellation);
            compilationCancellation.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
        {
            lock (request.Entry.Gate)
            {
                request.Entry.Method = null;
                request.Entry.EstimatedCodeBytes = 0;
                request.Entry.State = LuaJitFunctionState.Invalidated;
                request.Entry.Completion?.TrySetCanceled(_disposeCancellation.Token);
            }

            return;
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
            lock (request.Entry.Gate)
            {
                if (ReferenceEquals(request.Entry.Completion, request.Completion) &&
                    request.Entry.State == LuaJitFunctionState.Compiling)
                {
                    request.Entry.State = LuaJitFunctionState.Cold;
                    request.Entry.CompilationAttempts--;
                }

                request.Completion.TrySetCanceled(request.CancellationToken);
            }

            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            result = new LuaTier1CompilationResult(
                null,
                0,
                [$"{exception.GetType().Name}: {exception.Message}"]);
        }
        finally
        {
            linkedCancellation?.Dispose();
        }

        var compileDuration = Stopwatch.GetElapsedTime(started);
        Interlocked.Add(ref _totalCompilationTicks, compileDuration.Ticks);
        AccumulateTier1CompilationMetrics(result.Metrics);
        if (result.Succeeded && TryInstallCompiledMethod(request, result))
        {
            Interlocked.Increment(ref _compilationCompleted);
            RaiseEvent(new LuaJitEvent(
                LuaJitEventKind.CompilationCompleted,
                request.Entry.Key.ModuleContentId,
                request.Entry.Key.FunctionId,
                LuaJitFunctionState.Ready,
                result.EstimatedCodeBytes,
                compileDuration,
                CompilationMetrics: result.Metrics));
            return;
        }

        FailCompilation(
            request,
            result.Succeeded ? "JIT1004" : "JIT1003",
            compileDuration,
            result.Metrics);
    }

    private void CompileTier2(CompilationRequest request)
    {
        lock (request.Entry.Gate)
        {
            if (request.Entry.Tier2State != LuaJitTier2State.Queued ||
                !ReferenceEquals(request.Entry.Tier2Completion, request.Completion))
            {
                request.Completion.TrySetResult(false);
                return;
            }

            request.Entry.Tier2State = LuaJitTier2State.Compiling;
        }

        var queueLatency = Stopwatch.GetElapsedTime(request.EnqueuedTimestamp);
        Interlocked.Add(ref _totalQueueLatencyTicks, queueLatency.Ticks);
        Interlocked.Increment(ref _tier2CompilationStarted);
        RaiseEvent(new LuaJitEvent(
            LuaJitEventKind.Tier2CompilationStarted,
            request.Entry.Key.ModuleContentId,
            request.Entry.Key.FunctionId,
            LuaJitFunctionState.Ready,
            Duration: queueLatency,
            Tier: LuaJitCompilationTier.Tier2));

        var started = Stopwatch.GetTimestamp();
        LuaTier2CompilationResult result;
        CancellationTokenSource? linkedCancellation = null;
        var compilationCancellation = request.CancellationToken.CanBeCanceled
            ? (linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _disposeCancellation.Token,
                request.CancellationToken)).Token
            : _disposeCancellation.Token;
        try
        {
            result = _tier2Compiler.Compile(
                request.Module,
                request.Entry.Key.FunctionId,
                request.Profile!,
                compilationCancellation);
            compilationCancellation.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
        {
            lock (request.Entry.Gate)
            {
                request.Entry.Tier2State = LuaJitTier2State.Invalidated;
                request.Entry.Tier2Completion?.TrySetCanceled(_disposeCancellation.Token);
            }

            return;
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
            lock (request.Entry.Gate)
            {
                if (ReferenceEquals(request.Entry.Tier2Completion, request.Completion) &&
                    request.Entry.Tier2State == LuaJitTier2State.Compiling)
                {
                    request.Entry.Tier2State = LuaJitTier2State.Profiling;
                    request.Entry.Tier2CompilationAttempts--;
                }

                request.Completion.TrySetCanceled(request.CancellationToken);
            }

            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            result = new LuaTier2CompilationResult(
                null,
                null,
                0,
                [$"{exception.GetType().Name}: {exception.Message}"]);
        }
        finally
        {
            linkedCancellation?.Dispose();
        }

        var compileDuration = Stopwatch.GetElapsedTime(started);
        Interlocked.Add(ref _totalCompilationTicks, compileDuration.Ticks);
        var codeKindAllowed = IsTier2CodeKindAllowed(result);
        if (result.Succeeded && codeKindAllowed && TryInstallTier2Method(request, result))
        {
            Interlocked.Increment(ref _tier2CompilationCompleted);
            RaiseEvent(new LuaJitEvent(
                LuaJitEventKind.Tier2CompilationCompleted,
                request.Entry.Key.ModuleContentId,
                request.Entry.Key.FunctionId,
                LuaJitFunctionState.Ready,
                result.EstimatedCodeBytes,
                compileDuration,
                Tier: LuaJitCompilationTier.Tier2,
                Tier2CompilationMetrics: result.Metrics));
            return;
        }

        FailTier2Compilation(
            request,
            result.Succeeded
                ? codeKindAllowed
                    ? "JIT2002"
                    : LuaJitTier2DiagnosticCodes.UnexpectedCodeKind
                : "JIT2001",
            compileDuration,
            result.Metrics);
    }

    private bool IsTier2CodeKindAllowed(LuaTier2CompilationResult result) =>
        _options.EnableTier2ManagedFallback ||
        result.Plan?.CodeKind is LuaJitTier2CodeKind.ExactNumericSpecializedCil or
            LuaJitTier2CodeKind.GuardedSpecializedCil;

    private void CompileLoopOsr(CompilationRequest request)
    {
        var loop = request.LoopOsr ??
            throw new InvalidOperationException("A loop OSR request requires a loop entry.");
        lock (request.Entry.Gate)
        {
            if (loop.State != LuaJitOsrState.Queued ||
                !ReferenceEquals(loop.Completion, request.Completion))
            {
                request.Completion.TrySetResult(false);
                return;
            }

            loop.State = LuaJitOsrState.Compiling;
        }

        var queueLatency = Stopwatch.GetElapsedTime(request.EnqueuedTimestamp);
        Interlocked.Add(ref _totalQueueLatencyTicks, queueLatency.Ticks);
        Interlocked.Increment(ref _loopOsrCompilationStarted);
        RaiseEvent(new LuaJitEvent(
            LuaJitEventKind.LoopOsrCompilationStarted,
            request.Entry.Key.ModuleContentId,
            request.Entry.Key.FunctionId,
            ReadState(request.Entry),
            Duration: queueLatency,
            Tier: LuaJitCompilationTier.LoopOsr));

        var started = Stopwatch.GetTimestamp();
        LuaLoopOsrCompilationResult result;
        try
        {
            result = _loopOsrCompiler.Compile(
                request.Module,
                loop.Plan,
                !loop.PreferManagedFallback,
                _disposeCancellation.Token);
            _disposeCancellation.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (_disposeCancellation.IsCancellationRequested)
        {
            lock (request.Entry.Gate)
            {
                loop.State = LuaJitOsrState.Invalidated;
                loop.Completion?.TrySetCanceled(_disposeCancellation.Token);
            }

            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            result = new LuaLoopOsrCompilationResult(
                null,
                null,
                0,
                [$"{exception.GetType().Name}: {exception.Message}"]);
        }

        var compileDuration = Stopwatch.GetElapsedTime(started);
        Interlocked.Add(ref _totalCompilationTicks, compileDuration.Ticks);
        var codeKindAllowed = IsLoopOsrCodeKindAllowed(result);
        if (result.Succeeded && codeKindAllowed &&
            TryInstallLoopOsrMethod(request, loop, result))
        {
            Interlocked.Increment(ref _loopOsrCompilationCompleted);
            RaiseEvent(new LuaJitEvent(
                LuaJitEventKind.LoopOsrCompilationCompleted,
                request.Entry.Key.ModuleContentId,
                request.Entry.Key.FunctionId,
                ReadState(request.Entry),
                result.EstimatedCodeBytes,
                compileDuration,
                Tier: LuaJitCompilationTier.LoopOsr,
                LoopOsrCompilationMetrics: result.Metrics));
            return;
        }

        var failed = false;
        lock (request.Entry.Gate)
        {
            if (loop.State == LuaJitOsrState.Compiling &&
                ReferenceEquals(loop.Completion, request.Completion))
            {
                loop.State = LuaJitOsrState.Failed;
                loop.RetryAfterTimestamp = CalculateRetryAfterTimestamp();
                request.Completion.TrySetResult(false);
                failed = true;
            }
        }

        if (!failed)
        {
            return;
        }

        Interlocked.Increment(ref _loopOsrCompilationFailed);
        RaiseEvent(new LuaJitEvent(
            LuaJitEventKind.LoopOsrCompilationFailed,
            request.Entry.Key.ModuleContentId,
            request.Entry.Key.FunctionId,
            ReadState(request.Entry),
            Duration: compileDuration,
            DiagnosticCode: result.Succeeded
                ? codeKindAllowed
                    ? "JIT3002"
                    : LuaJitLoopOsrDiagnosticCodes.UnexpectedCodeKind
                : "JIT3001",
            Tier: LuaJitCompilationTier.LoopOsr,
            LoopOsrCompilationMetrics: result.Metrics));
    }

    private bool IsLoopOsrCodeKindAllowed(LuaLoopOsrCompilationResult result) =>
        _options.EnableLoopOsrManagedFallback ||
        result.Plan?.CodeKind == LuaJitLoopOsrCodeKind.GuardedExactNumericCil;

    private bool TryInstallLoopOsrMethod(
        CompilationRequest request,
        LoopOsrEntry loop,
        LuaLoopOsrCompilationResult result)
    {
        if (result.EstimatedCodeBytes <= 0 ||
            result.EstimatedCodeBytes > _options.MaximumCodeCacheBytes)
        {
            return false;
        }

        var evictionEvents = new List<LuaJitEvent>();
        lock (_cacheGate)
        {
            lock (request.Entry.Gate)
            {
                if (loop.State != LuaJitOsrState.Compiling ||
                    !ReferenceEquals(loop.Completion, request.Completion))
                {
                    request.Completion.TrySetResult(false);
                    return false;
                }
            }

            while (Interlocked.Read(ref _estimatedCodeBytes) >
                _options.MaximumCodeCacheBytes - result.EstimatedCodeBytes)
            {
                var candidate = FindEvictionCandidate(request.Entry);
                if (candidate is null)
                {
                    return false;
                }

                var eviction = EvictEntry(candidate);
                if (eviction is not null)
                {
                    evictionEvents.Add(eviction);
                }
            }

            lock (request.Entry.Gate)
            {
                if (loop.State != LuaJitOsrState.Compiling ||
                    !ReferenceEquals(loop.Completion, request.Completion))
                {
                    request.Completion.TrySetResult(false);
                    return false;
                }

                loop.Method = result.Method;
                request.Entry.InstalledGeneration = GetBackendGeneration(
                    request.Entry.Key.ModuleContentId).Current;
                loop.Plan = result.Plan ?? loop.Plan;
                loop.EstimatedCodeBytes = result.EstimatedCodeBytes;
                loop.State = LuaJitOsrState.Ready;
                loop.GuardFailures = 0;
                request.Entry.EstimatedCodeBytes = checked(
                    request.Entry.EstimatedCodeBytes + result.EstimatedCodeBytes);
                Interlocked.Add(ref _estimatedCodeBytes, result.EstimatedCodeBytes);
                request.Completion.TrySetResult(true);
            }
        }

        foreach (var jitEvent in evictionEvents)
        {
            RaiseEvent(jitEvent);
        }

        return true;
    }

    private bool TryInstallTier2Method(
        CompilationRequest request,
        LuaTier2CompilationResult result)
    {
        if (result.EstimatedCodeBytes <= 0 ||
            result.EstimatedCodeBytes > _options.MaximumCodeCacheBytes)
        {
            return false;
        }

        var evictionEvents = new List<LuaJitEvent>();
        lock (_cacheGate)
        {
            lock (request.Entry.Gate)
            {
                if (request.Entry.Tier2State != LuaJitTier2State.Compiling ||
                    !ReferenceEquals(request.Entry.Tier2Completion, request.Completion))
                {
                    request.Completion.TrySetResult(false);
                    return false;
                }
            }

            while (Interlocked.Read(ref _estimatedCodeBytes) >
                _options.MaximumCodeCacheBytes - result.EstimatedCodeBytes)
            {
                var candidate = FindEvictionCandidate(request.Entry);
                if (candidate is null)
                {
                    return false;
                }

                var eviction = EvictEntry(candidate);
                if (eviction is not null)
                {
                    evictionEvents.Add(eviction);
                }
            }

            lock (request.Entry.Gate)
            {
                if (request.Entry.Tier2State != LuaJitTier2State.Compiling ||
                    !ReferenceEquals(request.Entry.Tier2Completion, request.Completion))
                {
                    request.Completion.TrySetResult(false);
                    return false;
                }

                request.Entry.Tier2Method = result.Method;
                request.Entry.DirectCallMethod = result.DirectCallMethod;
                result.RuntimeSites?.BindDirectCallCounters(_boundDirectCallCounters);
                result.RuntimeSites?.BindTablePicCounters(_tablePicCounters);
                request.Entry.Tier2Plan = result.Plan;
                Volatile.Write(ref request.Entry.DirectCallEligibility, 0);
                request.Entry.Tier2EstimatedCodeBytes = result.EstimatedCodeBytes;
                request.Entry.Method = result.Method;
                request.Entry.InstalledGeneration = GetBackendGeneration(
                    request.Entry.Key.ModuleContentId).Current;
                request.Entry.ActiveTier = LuaJitCompilationTier.Tier2;
                request.Entry.Tier2State = LuaJitTier2State.Ready;
                request.Entry.Tier2GuardFailures = 0;
                Volatile.Write(ref request.Entry.Tier2ProfilingActive, 0);
                request.Entry.EstimatedCodeBytes = checked(
                    request.Entry.Tier1EstimatedCodeBytes + result.EstimatedCodeBytes +
                    GetLoopOsrCodeBytes(request.Entry));
                Interlocked.Add(ref _estimatedCodeBytes, result.EstimatedCodeBytes);
                request.Completion.TrySetResult(true);
            }
        }

        foreach (var jitEvent in evictionEvents)
        {
            RaiseEvent(jitEvent);
        }

        return true;
    }

    private void FailTier2Compilation(
        CompilationRequest request,
        string diagnosticCode,
        TimeSpan duration,
        LuaJitTier2CompilationMetrics? metrics)
    {
        lock (request.Entry.Gate)
        {
            if (request.Entry.Tier2State != LuaJitTier2State.Compiling ||
                !ReferenceEquals(request.Entry.Tier2Completion, request.Completion))
            {
                request.Completion.TrySetResult(false);
                return;
            }

            request.Entry.Tier2State = LuaJitTier2State.Failed;
            request.Entry.Tier2RetryAfterTimestamp = CalculateRetryAfterTimestamp();
            if (request.Entry.Tier2CompilationAttempts >= _options.MaximumCompilationAttempts)
            {
                DeactivateTier2ProfilingLocked(request.Entry);
            }
            request.Completion.TrySetResult(false);
        }

        Interlocked.Increment(ref _tier2CompilationFailed);
        RaiseEvent(new LuaJitEvent(
            LuaJitEventKind.Tier2CompilationFailed,
            request.Entry.Key.ModuleContentId,
            request.Entry.Key.FunctionId,
            LuaJitFunctionState.Ready,
            Duration: duration,
            DiagnosticCode: diagnosticCode,
            Tier: LuaJitCompilationTier.Tier2,
            Tier2CompilationMetrics: metrics));
    }

    private bool TryInstallCompiledMethod(
        CompilationRequest request,
        LuaTier1CompilationResult result)
    {
        if (result.EstimatedCodeBytes <= 0 ||
            result.EstimatedCodeBytes > _options.MaximumCodeCacheBytes)
        {
            return false;
        }

        var evictionEvents = new List<LuaJitEvent>();
        lock (_cacheGate)
        {
            lock (request.Entry.Gate)
            {
                if (request.Entry.State != LuaJitFunctionState.Compiling ||
                    !ReferenceEquals(request.Entry.Completion, request.Completion))
                {
                    request.Completion.TrySetResult(false);
                    return false;
                }
            }

            while (Interlocked.Read(ref _estimatedCodeBytes) >
                _options.MaximumCodeCacheBytes - result.EstimatedCodeBytes)
            {
                var candidate = FindEvictionCandidate(request.Entry);
                if (candidate is null)
                {
                    return false;
                }

                var eviction = EvictEntry(candidate);
                if (eviction is not null)
                {
                    evictionEvents.Add(eviction);
                }
            }

            lock (request.Entry.Gate)
            {
                if (request.Entry.State != LuaJitFunctionState.Compiling ||
                    !ReferenceEquals(request.Entry.Completion, request.Completion))
                {
                    request.Completion.TrySetResult(false);
                    return false;
                }

                request.Entry.Tier1Method = result.Method;
                request.Entry.PlainTier1Method = result.PlainMethod;
                request.Entry.Tier1EstimatedCodeBytes = result.EstimatedCodeBytes;
                request.Entry.Method = result.Method;
                request.Entry.InstalledGeneration = GetBackendGeneration(
                    request.Entry.Key.ModuleContentId).Current;
                request.Entry.ActiveTier = LuaJitCompilationTier.Tier1;
                request.Entry.EstimatedCodeBytes = checked(
                    result.EstimatedCodeBytes + GetLoopOsrCodeBytes(request.Entry));
                request.Entry.State = LuaJitFunctionState.Ready;
                request.Entry.Tier2State = IsTier2Enabled
                    ? LuaJitTier2State.Profiling
                    : LuaJitTier2State.Disabled;
                Volatile.Write(
                    ref request.Entry.Tier2ProfilingActive,
                    IsTier2Enabled ? 1 : 0);
                request.Entry.FailureCode = null;
                Interlocked.Add(ref _estimatedCodeBytes, result.EstimatedCodeBytes);
                request.Completion.TrySetResult(true);
            }
        }

        foreach (var jitEvent in evictionEvents)
        {
            RaiseEvent(jitEvent);
        }

        return true;
    }

    private void FailCompilation(
        CompilationRequest request,
        string diagnosticCode,
        TimeSpan duration,
        LuaJitCompilationMetrics? metrics)
    {
        lock (request.Entry.Gate)
        {
            if (request.Entry.State != LuaJitFunctionState.Compiling ||
                !ReferenceEquals(request.Entry.Completion, request.Completion))
            {
                request.Completion.TrySetResult(false);
                return;
            }

            request.Entry.Method = null;
            request.Entry.EstimatedCodeBytes = 0;
            request.Entry.State = LuaJitFunctionState.Failed;
            request.Entry.FailureCode = diagnosticCode;
            request.Entry.RetryAfterTimestamp = CalculateRetryAfterTimestamp();
            request.Completion.TrySetResult(false);
        }

        Interlocked.Increment(ref _compilationFailed);
        RaiseEvent(new LuaJitEvent(
            LuaJitEventKind.CompilationFailed,
            request.Entry.Key.ModuleContentId,
            request.Entry.Key.FunctionId,
            LuaJitFunctionState.Failed,
            Duration: duration,
            DiagnosticCode: diagnosticCode,
            CompilationMetrics: metrics));
    }
}
