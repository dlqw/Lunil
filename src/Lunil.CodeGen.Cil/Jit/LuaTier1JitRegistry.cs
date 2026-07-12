using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Lunil.CodeGen.Cil.Emission;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;

namespace Lunil.CodeGen.Cil.Jit;

internal sealed class LuaTier1JitRegistry : ILuaInstructionExecutor, IDisposable
{
    private const int CodegenVersion = 1;
    private readonly LuaJitExecutorOptions _options;
    private readonly ILuaDynamicCodeCapabilities _capabilities;
    private readonly ILuaTier1Compiler _compiler;
    private readonly ConcurrentDictionary<FunctionKey, FunctionEntry> _entries = [];
    private readonly ConditionalWeakTable<LuaIrModule, ModuleIdentity> _moduleIdentities = new();
    private readonly ConditionalWeakTable<LuaFrame, FunctionEntryObservation> _observedFrames =
        new();
    private readonly Channel<CompilationRequest> _queue;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly AsyncLocal<bool> _workerCallContext = new();
    private readonly Task[] _workers;
    private readonly Lock _cacheGate = new();
    private long _accessStamp;
    private long _estimatedCodeBytes;
    private long _functionEntries;
    private long _backedges;
    private long _compilationQueued;
    private long _compilationStarted;
    private long _compilationCompleted;
    private long _compilationFailed;
    private long _queueRejected;
    private long _compiledInvocations;
    private long _interpreterFallbacks;
    private long _deoptimizations;
    private long _cacheEvictions;
    private long _invalidations;
    private long _totalQueueLatencyTicks;
    private long _totalCompilationTicks;
    private int _disposed;

    public LuaTier1JitRegistry(
        LuaJitExecutorOptions options,
        ILuaDynamicCodeCapabilities capabilities,
        ILuaTier1Compiler compiler)
    {
        _options = options;
        _capabilities = capabilities;
        _compiler = compiler;
        _queue = Channel.CreateBounded<CompilationRequest>(new BoundedChannelOptions(
            options.CompilationQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = options.MaximumConcurrentCompilations == 1,
            SingleWriter = false,
        });
        _workers = options.SynchronousCompilation ||
            options.Policy is LuaJitPolicy.InterpreterOnly or LuaJitPolicy.RequireJit
            ? []
            : Enumerable.Range(0, options.MaximumConcurrentCompilations)
                .Select(_ => Task.Run(WorkerAsync))
                .ToArray();
    }

    public event EventHandler<LuaJitEvent>? EventOccurred;

    public LuaCompiledExit Execute(
        LuaExecutionEngine engine,
        LuaExecutionContext context,
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_options.Policy == LuaJitPolicy.InterpreterOnly)
        {
            return Fallback(
                string.Empty,
                frame.Closure.Function.Id,
                frame.ProgramCounter,
                LuaJitFunctionState.Cold,
                LuaCompiledExitReason.BackendInvalidated);
        }

        var module = frame.Closure.Module;
        var moduleContentId = GetModuleContentId(module);
        var key = new FunctionKey(
            moduleContentId,
            frame.Closure.Function.Id,
            LuaCodegenAbiV1.RuntimeAbiVersion,
            CodegenVersion);
        var entry = _entries.GetOrAdd(key, static key => new FunctionEntry(key));
        Interlocked.Exchange(
            ref entry.LastAccessStamp,
            Interlocked.Increment(ref _accessStamp));

        if (context.HasExactDebugHooks || !context.IsDebugModeCurrent())
        {
            return Fallback(
                moduleContentId,
                key.FunctionId,
                frame.ProgramCounter,
                ReadState(entry),
                LuaCompiledExitReason.DebugModeChanged);
        }

        var method = ReadReadyMethod(entry);
        if (method is not null)
        {
            return InvokeCompiled(entry, method, context, thread, frame);
        }

        ObserveHotness(entry, frame, frame.ProgramCounter, instruction);
        if (ShouldCompile(entry))
        {
            var waitForCompilation = _options.SynchronousCompilation ||
                _options.Policy == LuaJitPolicy.RequireJit;
            var completion = RequestCompilation(entry, module, waitForCompilation);
            if (waitForCompilation && completion is not null)
            {
                _ = completion.GetAwaiter().GetResult();
            }

            method = ReadReadyMethod(entry);
            if (method is not null)
            {
                return InvokeCompiled(entry, method, context, thread, frame);
            }
        }

        if (_options.Policy == LuaJitPolicy.RequireJit)
        {
            throw CreateRequiredJitException(entry);
        }

        return Fallback(
            moduleContentId,
            key.FunctionId,
            frame.ProgramCounter,
            ReadState(entry),
            LuaCompiledExitReason.BackendInvalidated);
    }

    public LuaJitStatistics GetStatistics() => new(
        Interlocked.Read(ref _functionEntries),
        Interlocked.Read(ref _backedges),
        Interlocked.Read(ref _compilationQueued),
        Interlocked.Read(ref _compilationStarted),
        Interlocked.Read(ref _compilationCompleted),
        Interlocked.Read(ref _compilationFailed),
        Interlocked.Read(ref _queueRejected),
        Interlocked.Read(ref _compiledInvocations),
        Interlocked.Read(ref _interpreterFallbacks),
        Interlocked.Read(ref _deoptimizations),
        Interlocked.Read(ref _cacheEvictions),
        Interlocked.Read(ref _invalidations),
        Interlocked.Read(ref _estimatedCodeBytes),
        Interlocked.Read(ref _totalQueueLatencyTicks),
        Interlocked.Read(ref _totalCompilationTicks));

    public LuaJitFunctionState GetFunctionState(LuaIrModule module, int functionId)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (functionId < 0 || functionId >= module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        var key = new FunctionKey(
            GetModuleContentId(module),
            functionId,
            LuaCodegenAbiV1.RuntimeAbiVersion,
            CodegenVersion);
        if (!_entries.TryGetValue(key, out var entry))
        {
            return LuaJitFunctionState.Cold;
        }

        lock (entry.Gate)
        {
            return entry.State;
        }
    }

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
                        not LuaJitFunctionState.Compiling;
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

        foreach (var entry in _entries.Values)
        {
            lock (entry.Gate)
            {
                entry.Method = null;
                entry.EstimatedCodeBytes = 0;
                entry.State = LuaJitFunctionState.Invalidated;
                entry.Completion?.TrySetCanceled();
                entry.Completion = null;
            }
        }

        Interlocked.Exchange(ref _estimatedCodeBytes, 0);
        _entries.Clear();
        if (calledFromWorker)
        {
            _ = DisposeCancellationWhenWorkersCompleteAsync();
        }
        else
        {
            _disposeCancellation.Dispose();
        }
    }

    private static LuaCompiledMethod? ReadReadyMethod(FunctionEntry entry)
    {
        lock (entry.Gate)
        {
            return entry.State == LuaJitFunctionState.Ready ? entry.Method : null;
        }
    }

    private static LuaJitFunctionState ReadState(FunctionEntry entry)
    {
        lock (entry.Gate)
        {
            return entry.State;
        }
    }

    private LuaCompiledExit InvokeCompiled(
        FunctionEntry entry,
        LuaCompiledMethod method,
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame)
    {
        Interlocked.Increment(ref _compiledInvocations);
        Interlocked.Exchange(
            ref entry.LastAccessStamp,
            Interlocked.Increment(ref _accessStamp));
        var exit = method(context, thread, frame);
        if (exit.Kind == LuaCompiledExitKind.Deopt)
        {
            Interlocked.Increment(ref _deoptimizations);
            RaiseEvent(new LuaJitEvent(
                LuaJitEventKind.Deoptimized,
                entry.Key.ModuleContentId,
                entry.Key.FunctionId,
                LuaJitFunctionState.Ready,
                DiagnosticCode: exit.Reason.ToString()));
        }

        return exit;
    }

    private void ObserveHotness(
        FunctionEntry entry,
        LuaFrame frame,
        int programCounter,
        LuaIrInstruction instruction)
    {
        if (programCounter == 0)
        {
            var observation = _observedFrames.GetValue(
                frame,
                static _ => new FunctionEntryObservation());
            if (Interlocked.Exchange(ref observation.Counted, 1) == 0)
            {
                Interlocked.Increment(ref entry.FunctionEntries);
                Interlocked.Increment(ref _functionEntries);
            }
        }

        if (IsBackedge(programCounter, instruction))
        {
            Interlocked.Increment(ref entry.Backedges);
            Interlocked.Increment(ref _backedges);
        }
    }

    private bool ShouldCompile(FunctionEntry entry) => _options.Policy switch
    {
        LuaJitPolicy.PreferJit or LuaJitPolicy.RequireJit => true,
        LuaJitPolicy.Auto =>
            Interlocked.Read(ref entry.FunctionEntries) >= _options.FunctionEntryThreshold ||
            Interlocked.Read(ref entry.Backedges) >= _options.BackedgeThreshold,
        _ => false,
    };

    private Task<bool>? RequestCompilation(
        FunctionEntry entry,
        LuaIrModule module,
        bool compileSynchronously)
    {
        if (!_capabilities.IsDynamicCodeSupported || !_capabilities.IsDynamicCodeCompiled)
        {
            MarkUnavailable(entry);
            return entry.Completion?.Task;
        }

        CompilationRequest request;
        lock (entry.Gate)
        {
            if (entry.State == LuaJitFunctionState.Ready)
            {
                return Task.FromResult(true);
            }

            if (entry.State is LuaJitFunctionState.Queued or LuaJitFunctionState.Compiling)
            {
                return entry.Completion?.Task;
            }

            if (entry.State == LuaJitFunctionState.Failed &&
                (entry.CompilationAttempts >= _options.MaximumCompilationAttempts ||
                 Stopwatch.GetTimestamp() < entry.RetryAfterTimestamp))
            {
                return entry.Completion?.Task;
            }

            entry.State = LuaJitFunctionState.Queued;
            entry.CompilationAttempts++;
            entry.FailureCode = null;
            entry.Completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            request = new CompilationRequest(
                entry,
                module,
                Stopwatch.GetTimestamp(),
                entry.Completion);
        }

        Interlocked.Increment(ref _compilationQueued);
        RaiseEvent(new LuaJitEvent(
            LuaJitEventKind.Queued,
            entry.Key.ModuleContentId,
            entry.Key.FunctionId,
            LuaJitFunctionState.Queued));

        if (compileSynchronously)
        {
            Compile(request);
            return request.Completion.Task;
        }

        if (_queue.Writer.TryWrite(request))
        {
            return request.Completion.Task;
        }

        lock (entry.Gate)
        {
            if (ReferenceEquals(entry.Completion, request.Completion) &&
                entry.State == LuaJitFunctionState.Queued)
            {
                entry.State = LuaJitFunctionState.Cold;
                entry.CompilationAttempts--;
                entry.Completion = null;
            }
        }

        Interlocked.Increment(ref _queueRejected);
        request.Completion.TrySetResult(false);
        return request.Completion.Task;
    }

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

    private void Compile(CompilationRequest request)
    {
        if (_disposeCancellation.IsCancellationRequested)
        {
            request.Completion.TrySetCanceled(_disposeCancellation.Token);
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
        try
        {
            result = _compiler.Compile(
                request.Module,
                request.Entry.Key.FunctionId,
                _disposeCancellation.Token);
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
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            result = new LuaTier1CompilationResult(
                null,
                0,
                [$"{exception.GetType().Name}: {exception.Message}"]);
        }

        var compileDuration = Stopwatch.GetElapsedTime(started);
        Interlocked.Add(ref _totalCompilationTicks, compileDuration.Ticks);
        if (result.Succeeded && TryInstallCompiledMethod(request, result))
        {
            Interlocked.Increment(ref _compilationCompleted);
            RaiseEvent(new LuaJitEvent(
                LuaJitEventKind.CompilationCompleted,
                request.Entry.Key.ModuleContentId,
                request.Entry.Key.FunctionId,
                LuaJitFunctionState.Ready,
                result.EstimatedCodeBytes,
                compileDuration));
            return;
        }

        FailCompilation(
            request,
            result.Succeeded ? "JIT1004" : "JIT1003",
            compileDuration);
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

                LuaJitEvent? eviction = null;
                lock (candidate.Gate)
                {
                    if (candidate.State == LuaJitFunctionState.Ready &&
                        candidate.Method is not null)
                    {
                        var released = candidate.EstimatedCodeBytes;
                        Interlocked.Add(ref _estimatedCodeBytes, -released);
                        candidate.Method = null;
                        candidate.EstimatedCodeBytes = 0;
                        candidate.State = LuaJitFunctionState.Invalidated;
                        Interlocked.Increment(ref _cacheEvictions);
                        eviction = new LuaJitEvent(
                            LuaJitEventKind.Evicted,
                            candidate.Key.ModuleContentId,
                            candidate.Key.FunctionId,
                            LuaJitFunctionState.Invalidated,
                            released);
                    }
                }

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

                request.Entry.Method = result.Method;
                request.Entry.EstimatedCodeBytes = result.EstimatedCodeBytes;
                request.Entry.State = LuaJitFunctionState.Ready;
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
        TimeSpan duration)
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
            DiagnosticCode: diagnosticCode));
    }

    private void MarkUnavailable(FunctionEntry entry)
    {
        var changed = false;
        lock (entry.Gate)
        {
            if (entry.State != LuaJitFunctionState.Failed || entry.FailureCode != "JIT1001")
            {
                entry.State = LuaJitFunctionState.Failed;
                entry.FailureCode = "JIT1001";
                entry.Completion ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                entry.Completion.TrySetResult(false);
                changed = true;
            }
        }

        if (changed)
        {
            RaiseEvent(new LuaJitEvent(
                LuaJitEventKind.CompilationFailed,
                entry.Key.ModuleContentId,
                entry.Key.FunctionId,
                LuaJitFunctionState.Failed,
                DiagnosticCode: "JIT1001"));
        }
    }

    private FunctionEntry? FindEvictionCandidate(FunctionEntry excluded)
    {
        FunctionEntry? result = null;
        var oldest = long.MaxValue;
        foreach (var entry in _entries.Values)
        {
            if (ReferenceEquals(entry, excluded))
            {
                continue;
            }

            lock (entry.Gate)
            {
                if (entry.State == LuaJitFunctionState.Ready &&
                    Interlocked.Read(ref entry.LastAccessStamp) < oldest)
                {
                    result = entry;
                    oldest = Interlocked.Read(ref entry.LastAccessStamp);
                }
            }
        }

        return result;
    }

    private void InvalidateModule(string moduleContentId)
    {
        var events = new List<LuaJitEvent>();
        lock (_cacheGate)
        {
            foreach (var entry in _entries.Where(pair => string.Equals(
                pair.Key.ModuleContentId,
                moduleContentId,
                StringComparison.Ordinal)).Select(static pair => pair.Value))
            {
                lock (entry.Gate)
                {
                    if (entry.EstimatedCodeBytes != 0)
                    {
                        Interlocked.Add(ref _estimatedCodeBytes, -entry.EstimatedCodeBytes);
                    }

                    entry.Method = null;
                    entry.EstimatedCodeBytes = 0;
                    entry.State = LuaJitFunctionState.Invalidated;
                    entry.FailureCode = null;
                    entry.Completion?.TrySetResult(false);
                    entry.Completion = null;
                    Interlocked.Increment(ref _invalidations);
                    events.Add(new LuaJitEvent(
                        LuaJitEventKind.Invalidated,
                        entry.Key.ModuleContentId,
                        entry.Key.FunctionId,
                        LuaJitFunctionState.Invalidated));
                }
            }
        }

        foreach (var jitEvent in events)
        {
            RaiseEvent(jitEvent);
        }
    }

    private LuaCompiledExit Fallback(
        string moduleContentId,
        int functionId,
        int programCounter,
        LuaJitFunctionState state,
        LuaCompiledExitReason reason)
    {
        Interlocked.Increment(ref _interpreterFallbacks);
        RaiseEvent(new LuaJitEvent(
            LuaJitEventKind.Fallback,
            moduleContentId,
            functionId,
            state,
            DiagnosticCode: reason.ToString()));
        return LuaCompiledExit.Deopt(programCounter, instructionsConsumed: 0, reason);
    }

    private static LuaJitException CreateRequiredJitException(FunctionEntry entry)
    {
        lock (entry.Gate)
        {
            var code = entry.FailureCode ?? "JIT1002";
            return new LuaJitException(
                code,
                code == "JIT1001"
                    ? "Tier 1 JIT is required, but dynamic code is unavailable."
                    : "Tier 1 JIT is required, but the function could not be compiled.");
        }
    }

    private string GetModuleContentId(LuaIrModule module) =>
        _moduleIdentities.GetValue(
            module,
            static module => new ModuleIdentity(LuaJitModuleIdentity.Create(module)))
        .ContentId;

    private long CalculateRetryAfterTimestamp()
    {
        var now = Stopwatch.GetTimestamp();
        var delay = _options.CompilationRetryBackoff.TotalSeconds * Stopwatch.Frequency;
        if (double.IsPositiveInfinity(delay) || delay >= long.MaxValue - now)
        {
            return long.MaxValue;
        }

        return now + (long)Math.Ceiling(delay);
    }

    private void RaiseEvent(LuaJitEvent jitEvent)
    {
        var handlers = EventOccurred?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.Cast<EventHandler<LuaJitEvent>>())
        {
            try
            {
                handler(this, jitEvent);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and
                not StackOverflowException and not AccessViolationException)
            {
            }
        }
    }

    private static bool IsBackedge(int programCounter, LuaIrInstruction instruction) =>
        instruction.B <= programCounter && instruction.Opcode is
            LuaIrOpcode.Jump or LuaIrOpcode.JumpIfFalse or LuaIrOpcode.JumpIfTrue or
            LuaIrOpcode.NumericForPrepare or LuaIrOpcode.NumericForLoop;

    private readonly record struct FunctionKey(
        string ModuleContentId,
        int FunctionId,
        int RuntimeAbiVersion,
        int CodegenVersion);

    private sealed class FunctionEntry(FunctionKey key)
    {
        public FunctionKey Key { get; } = key;

        public Lock Gate { get; } = new();

        public LuaJitFunctionState State { get; set; }

        public LuaCompiledMethod? Method { get; set; }

        public TaskCompletionSource<bool>? Completion { get; set; }

        public long EstimatedCodeBytes { get; set; }

        public long LastAccessStamp;

        public long FunctionEntries;

        public long Backedges;

        public int CompilationAttempts { get; set; }

        public long RetryAfterTimestamp { get; set; }

        public string? FailureCode { get; set; }
    }

    private sealed record CompilationRequest(
        FunctionEntry Entry,
        LuaIrModule Module,
        long EnqueuedTimestamp,
        TaskCompletionSource<bool> Completion);

    private sealed record ModuleIdentity(string ContentId);

    private sealed class FunctionEntryObservation
    {
        public int Counted;
    }
}
