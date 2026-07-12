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

internal sealed class LuaTieredJitRegistry :
    ILuaInstructionExecutor,
    ILuaInstructionObserver,
    IDisposable
{
    private const int CodegenVersion = 1;
    private readonly LuaJitExecutorOptions _options;
    private readonly ILuaDynamicCodeCapabilities _capabilities;
    private readonly ILuaTier1Compiler _compiler;
    private readonly ILuaTier2Compiler _tier2Compiler;
    private readonly ConcurrentDictionary<FunctionKey, FunctionEntry> _entries = [];
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
    private long _tier2CompilationQueued;
    private long _tier2CompilationStarted;
    private long _tier2CompilationCompleted;
    private long _tier2CompilationFailed;
    private long _tier2Invocations;
    private long _tier2GuardFailures;
    private long _tier2Invalidations;
    private int _disposed;

    public LuaTieredJitRegistry(
        LuaJitExecutorOptions options,
        ILuaDynamicCodeCapabilities capabilities,
        ILuaTier1Compiler compiler,
        ILuaTier2Compiler tier2Compiler)
    {
        _options = options;
        _capabilities = capabilities;
        _compiler = compiler;
        _tier2Compiler = tier2Compiler;
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
        engine.ObserveCodegenInstruction(
            context,
            thread,
            frame,
            frame.ProgramCounter);
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
        var entry = _entries.GetOrAdd(
            key,
            key => new FunctionEntry(
                key,
                frame.Closure.Function.ParameterCount,
                _options.MaximumPolymorphicShapes,
                _options.EnableTier2));
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

        var entryPoint = ReadReadyMethod(entry);
        if (entryPoint is not null)
        {
            if (ShouldPromoteToTier2(entry, frame.ProgramCounter))
            {
                _ = RequestTier2Compilation(
                    entry,
                    module,
                    _options.SynchronousCompilation);
                entryPoint = ReadReadyMethod(entry);
            }

            if (entryPoint is not null)
            {
                return InvokeCompiled(entry, entryPoint.Value, context, thread, frame);
            }
        }

        if (ShouldCompile(entry))
        {
            var waitForCompilation = _options.SynchronousCompilation ||
                _options.Policy == LuaJitPolicy.RequireJit;
            var completion = RequestCompilation(entry, module, waitForCompilation);
            if (waitForCompilation && completion is not null)
            {
                _ = completion.GetAwaiter().GetResult();
            }

            entryPoint = ReadReadyMethod(entry);
            if (entryPoint is not null)
            {
                return InvokeCompiled(entry, entryPoint.Value, context, thread, frame);
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

    public void ObserveInstruction(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int programCounter,
        LuaIrInstruction instruction)
    {
        if (_options.Policy == LuaJitPolicy.InterpreterOnly ||
            Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var key = new FunctionKey(
            GetModuleContentId(frame.Closure.Module),
            frame.Closure.Function.Id,
            LuaCodegenAbiV1.RuntimeAbiVersion,
            CodegenVersion);
        var entry = _entries.GetOrAdd(
            key,
            key => new FunctionEntry(
                key,
                frame.Closure.Function.ParameterCount,
                _options.MaximumPolymorphicShapes,
                _options.EnableTier2));
        ObserveHotness(entry, frame, programCounter, instruction);
        if (_options.EnableTier2)
        {
            entry.Profile.Observe(
                context,
                thread,
                frame,
                programCounter,
                instruction,
                GetModuleContentId);
        }
        if (instruction.Opcode is LuaIrOpcode.Return or LuaIrOpcode.TailCall)
        {
            lock (entry.Gate)
            {
                if (entry.ActiveTier == LuaJitCompilationTier.Tier1)
                {
                    Interlocked.Increment(ref entry.CompletedTier1Invocations);
                }
            }
        }
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
        Interlocked.Read(ref _totalCompilationTicks),
        Interlocked.Read(ref _tier2CompilationQueued),
        Interlocked.Read(ref _tier2CompilationStarted),
        Interlocked.Read(ref _tier2CompilationCompleted),
        Interlocked.Read(ref _tier2CompilationFailed),
        Interlocked.Read(ref _tier2Invocations),
        Interlocked.Read(ref _tier2GuardFailures),
        Interlocked.Read(ref _tier2Invalidations));

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

    public LuaJitFunctionProfile GetFunctionProfile(LuaIrModule module, int functionId)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (functionId < 0 || functionId >= module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        var function = module.Functions[functionId];
        var key = new FunctionKey(
            GetModuleContentId(module),
            functionId,
            LuaCodegenAbiV1.RuntimeAbiVersion,
            CodegenVersion);
        return _entries.TryGetValue(key, out var entry)
            ? entry.Profile.Snapshot()
            : new LuaJitFunctionProfile(
                0,
                [.. Enumerable.Repeat(LuaJitValueKinds.None, function.ParameterCount)],
                []);
    }

    public LuaJitCompilationTier GetFunctionTier(LuaIrModule module, int functionId)
    {
        var entry = FindEntry(module, functionId);
        if (entry is null)
        {
            return LuaJitCompilationTier.Interpreter;
        }

        lock (entry.Gate)
        {
            return entry.ActiveTier;
        }
    }

    public LuaJitTier2State GetTier2State(LuaIrModule module, int functionId)
    {
        var entry = FindEntry(module, functionId);
        if (entry is null)
        {
            return _options.EnableTier2
                ? LuaJitTier2State.Profiling
                : LuaJitTier2State.Disabled;
        }

        lock (entry.Gate)
        {
            return entry.Tier2State;
        }
    }

    public LuaJitTier2Plan? GetTier2Plan(LuaIrModule module, int functionId)
    {
        var entry = FindEntry(module, functionId);
        if (entry is null)
        {
            return null;
        }

        lock (entry.Gate)
        {
            return entry.Tier2Plan;
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
                        not LuaJitFunctionState.Compiling &&
                        entry.Tier2State is not LuaJitTier2State.Queued and
                        not LuaJitTier2State.Compiling;
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
                entry.Tier1Method = null;
                entry.Tier2Method = null;
                entry.Tier2Plan = null;
                entry.Tier1EstimatedCodeBytes = 0;
                entry.Tier2EstimatedCodeBytes = 0;
                entry.EstimatedCodeBytes = 0;
                entry.ActiveTier = LuaJitCompilationTier.Interpreter;
                entry.State = LuaJitFunctionState.Invalidated;
                entry.Tier2State = LuaJitTier2State.Invalidated;
                entry.Completion?.TrySetCanceled();
                entry.Completion = null;
                entry.Tier2Completion?.TrySetCanceled();
                entry.Tier2Completion = null;
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

    private static CompiledEntryPoint? ReadReadyMethod(FunctionEntry entry)
    {
        lock (entry.Gate)
        {
            return entry.State == LuaJitFunctionState.Ready && entry.Method is not null
                ? new CompiledEntryPoint(entry.Method, entry.ActiveTier)
                : null;
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
        CompiledEntryPoint entryPoint,
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame)
    {
        Interlocked.Increment(ref _compiledInvocations);
        if (entryPoint.Tier == LuaJitCompilationTier.Tier2)
        {
            Interlocked.Increment(ref _tier2Invocations);
        }
        else
        {
            Interlocked.Increment(ref entry.Tier1Invocations);
        }
        Interlocked.Exchange(
            ref entry.LastAccessStamp,
            Interlocked.Increment(ref _accessStamp));
        var exit = entryPoint.Method(context, thread, frame);
        if (exit.Kind == LuaCompiledExitKind.Deopt)
        {
            Interlocked.Increment(ref _deoptimizations);
            if (entryPoint.Tier == LuaJitCompilationTier.Tier2 &&
                exit.Reason == LuaCompiledExitReason.GuardFailure)
            {
                HandleTier2GuardFailure(entry);
            }

            RaiseEvent(new LuaJitEvent(
                LuaJitEventKind.Deoptimized,
                entry.Key.ModuleContentId,
                entry.Key.FunctionId,
                LuaJitFunctionState.Ready,
                DiagnosticCode: exit.Reason.ToString(),
                Tier: entryPoint.Tier));
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

    private bool ShouldPromoteToTier2(FunctionEntry entry, int programCounter)
    {
        if (!_options.EnableTier2)
        {
            return false;
        }

        lock (entry.Gate)
        {
            return entry.ActiveTier == LuaJitCompilationTier.Tier1 &&
                entry.Tier2State == LuaJitTier2State.Profiling &&
                (programCounter == 0 &&
                 Interlocked.Read(ref entry.CompletedTier1Invocations) >=
                    _options.Tier2InvocationThreshold ||
                 Interlocked.Read(ref entry.Backedges) >= _options.Tier2BackedgeThreshold);
        }
    }

    private Task<bool>? RequestTier2Compilation(
        FunctionEntry entry,
        LuaIrModule module,
        bool compileSynchronously)
    {
        CompilationRequest request;
        lock (entry.Gate)
        {
            if (entry.ActiveTier == LuaJitCompilationTier.Tier2)
            {
                return Task.FromResult(true);
            }

            if (entry.State != LuaJitFunctionState.Ready || entry.Tier1Method is null ||
                entry.Tier2State is LuaJitTier2State.Queued or LuaJitTier2State.Compiling)
            {
                return entry.Tier2Completion?.Task;
            }

            if (entry.Tier2State == LuaJitTier2State.Failed &&
                (entry.Tier2CompilationAttempts >= _options.MaximumCompilationAttempts ||
                 Stopwatch.GetTimestamp() < entry.Tier2RetryAfterTimestamp))
            {
                return entry.Tier2Completion?.Task;
            }

            entry.Tier2State = LuaJitTier2State.Queued;
            entry.Tier2CompilationAttempts++;
            entry.Tier2Completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            request = new CompilationRequest(
                entry,
                module,
                Stopwatch.GetTimestamp(),
                entry.Tier2Completion,
                LuaJitCompilationTier.Tier2,
                entry.Profile.Snapshot());
        }

        Interlocked.Increment(ref _tier2CompilationQueued);
        RaiseEvent(new LuaJitEvent(
            LuaJitEventKind.Tier2Queued,
            entry.Key.ModuleContentId,
            entry.Key.FunctionId,
            LuaJitFunctionState.Ready,
            Tier: LuaJitCompilationTier.Tier2));

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
            if (ReferenceEquals(entry.Tier2Completion, request.Completion) &&
                entry.Tier2State == LuaJitTier2State.Queued)
            {
                entry.Tier2State = LuaJitTier2State.Profiling;
                entry.Tier2CompilationAttempts--;
                entry.Tier2Completion = null;
            }
        }

        Interlocked.Increment(ref _queueRejected);
        request.Completion.TrySetResult(false);
        return request.Completion.Task;
    }

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
                entry.Completion,
                LuaJitCompilationTier.Tier1,
                null);
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

        if (request.Tier == LuaJitCompilationTier.Tier2)
        {
            CompileTier2(request);
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
        try
        {
            result = _tier2Compiler.Compile(
                request.Module,
                request.Entry.Key.FunctionId,
                request.Profile!,
                _disposeCancellation.Token);
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
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            result = new LuaTier2CompilationResult(
                null,
                null,
                0,
                [$"{exception.GetType().Name}: {exception.Message}"]);
        }

        var compileDuration = Stopwatch.GetElapsedTime(started);
        Interlocked.Add(ref _totalCompilationTicks, compileDuration.Ticks);
        if (result.Succeeded && TryInstallTier2Method(request, result))
        {
            Interlocked.Increment(ref _tier2CompilationCompleted);
            RaiseEvent(new LuaJitEvent(
                LuaJitEventKind.Tier2CompilationCompleted,
                request.Entry.Key.ModuleContentId,
                request.Entry.Key.FunctionId,
                LuaJitFunctionState.Ready,
                result.EstimatedCodeBytes,
                compileDuration,
                Tier: LuaJitCompilationTier.Tier2));
            return;
        }

        FailTier2Compilation(
            request,
            result.Succeeded ? "JIT2002" : "JIT2001",
            compileDuration);
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
                request.Entry.Tier2Plan = result.Plan;
                request.Entry.Tier2EstimatedCodeBytes = result.EstimatedCodeBytes;
                request.Entry.Method = result.Method;
                request.Entry.ActiveTier = LuaJitCompilationTier.Tier2;
                request.Entry.Tier2State = LuaJitTier2State.Ready;
                request.Entry.Tier2GuardFailures = 0;
                request.Entry.EstimatedCodeBytes = checked(
                    request.Entry.Tier1EstimatedCodeBytes + result.EstimatedCodeBytes);
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
        TimeSpan duration)
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
            Tier: LuaJitCompilationTier.Tier2));
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
                request.Entry.Tier1EstimatedCodeBytes = result.EstimatedCodeBytes;
                request.Entry.Method = result.Method;
                request.Entry.ActiveTier = LuaJitCompilationTier.Tier1;
                request.Entry.EstimatedCodeBytes = result.EstimatedCodeBytes;
                request.Entry.State = LuaJitFunctionState.Ready;
                request.Entry.Tier2State = _options.EnableTier2
                    ? LuaJitTier2State.Profiling
                    : LuaJitTier2State.Disabled;
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

    private LuaJitEvent? EvictEntry(FunctionEntry entry)
    {
        lock (entry.Gate)
        {
            if (entry.State != LuaJitFunctionState.Ready || entry.Method is null)
            {
                return null;
            }

            var released = entry.EstimatedCodeBytes;
            Interlocked.Add(ref _estimatedCodeBytes, -released);
            entry.Method = null;
            entry.Tier1Method = null;
            entry.Tier2Method = null;
            entry.Tier2Plan = null;
            entry.Tier1EstimatedCodeBytes = 0;
            entry.Tier2EstimatedCodeBytes = 0;
            entry.EstimatedCodeBytes = 0;
            entry.ActiveTier = LuaJitCompilationTier.Interpreter;
            entry.State = LuaJitFunctionState.Invalidated;
            entry.Tier2State = LuaJitTier2State.Invalidated;
            entry.Tier2Completion?.TrySetResult(false);
            entry.Tier2Completion = null;
            Interlocked.Increment(ref _cacheEvictions);
            return new LuaJitEvent(
                LuaJitEventKind.Evicted,
                entry.Key.ModuleContentId,
                entry.Key.FunctionId,
                LuaJitFunctionState.Invalidated,
                released,
                Tier: LuaJitCompilationTier.Interpreter);
        }
    }

    private void HandleTier2GuardFailure(FunctionEntry entry)
    {
        Interlocked.Increment(ref _tier2GuardFailures);
        var failures = Interlocked.Increment(ref entry.Tier2GuardFailures);
        RaiseEvent(new LuaJitEvent(
            LuaJitEventKind.Tier2GuardFailed,
            entry.Key.ModuleContentId,
            entry.Key.FunctionId,
            LuaJitFunctionState.Ready,
            DiagnosticCode: LuaCompiledExitReason.GuardFailure.ToString(),
            Tier: LuaJitCompilationTier.Tier2));
        if (failures < _options.MaximumTier2GuardFailures)
        {
            return;
        }

        long released;
        lock (_cacheGate)
        {
            lock (entry.Gate)
            {
                if (entry.ActiveTier != LuaJitCompilationTier.Tier2 ||
                    entry.Tier1Method is null)
                {
                    return;
                }

                released = entry.Tier2EstimatedCodeBytes;
                entry.Tier2Method = null;
                entry.Tier2Plan = null;
                entry.Tier2EstimatedCodeBytes = 0;
                entry.EstimatedCodeBytes = entry.Tier1EstimatedCodeBytes;
                entry.Method = entry.Tier1Method;
                entry.ActiveTier = LuaJitCompilationTier.Tier1;
                entry.Tier2State = LuaJitTier2State.Profiling;
                entry.Tier2CompilationAttempts = 0;
                entry.Tier2GuardFailures = 0;
                entry.CompletedTier1Invocations = 0;
                Interlocked.Add(ref _estimatedCodeBytes, -released);
            }
        }

        Interlocked.Increment(ref _tier2Invalidations);
        RaiseEvent(new LuaJitEvent(
            LuaJitEventKind.Tier2Invalidated,
            entry.Key.ModuleContentId,
            entry.Key.FunctionId,
            LuaJitFunctionState.Ready,
            released,
            DiagnosticCode: "JIT2003",
            Tier: LuaJitCompilationTier.Tier2));
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
                    entry.Tier1Method = null;
                    entry.Tier2Method = null;
                    entry.Tier2Plan = null;
                    entry.Tier1EstimatedCodeBytes = 0;
                    entry.Tier2EstimatedCodeBytes = 0;
                    entry.EstimatedCodeBytes = 0;
                    entry.ActiveTier = LuaJitCompilationTier.Interpreter;
                    entry.State = LuaJitFunctionState.Invalidated;
                    entry.Tier2State = LuaJitTier2State.Invalidated;
                    entry.FailureCode = null;
                    entry.Completion?.TrySetResult(false);
                    entry.Completion = null;
                    entry.Tier2Completion?.TrySetResult(false);
                    entry.Tier2Completion = null;
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

    private static string GetModuleContentId(LuaIrModule module) =>
        LuaJitModuleIdentity.Create(module);

    private FunctionEntry? FindEntry(LuaIrModule module, int functionId)
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
        return _entries.GetValueOrDefault(key);
    }

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

    private readonly record struct CompiledEntryPoint(
        LuaCompiledMethod Method,
        LuaJitCompilationTier Tier);

    private sealed class FunctionEntry(
        FunctionKey key,
        int parameterCount,
        int maximumPolymorphicShapes,
        bool enableTier2)
    {
        public FunctionKey Key { get; } = key;

        public Lock Gate { get; } = new();

        public LuaJitFunctionState State { get; set; }

        public LuaJitCompilationTier ActiveTier { get; set; }

        public LuaJitTier2State Tier2State { get; set; } = enableTier2
            ? LuaJitTier2State.Profiling
            : LuaJitTier2State.Disabled;

        public LuaJitProfileAccumulator Profile { get; } = new(
            parameterCount,
            maximumPolymorphicShapes);

        public LuaCompiledMethod? Method { get; set; }

        public LuaCompiledMethod? Tier1Method { get; set; }

        public LuaCompiledMethod? Tier2Method { get; set; }

        public LuaJitTier2Plan? Tier2Plan { get; set; }

        public TaskCompletionSource<bool>? Completion { get; set; }

        public TaskCompletionSource<bool>? Tier2Completion { get; set; }

        public long EstimatedCodeBytes { get; set; }

        public long Tier1EstimatedCodeBytes { get; set; }

        public long Tier2EstimatedCodeBytes { get; set; }

        public long LastAccessStamp;

        public long FunctionEntries;

        public long Backedges;

        public long Tier1Invocations;

        public long CompletedTier1Invocations;

        public long Tier2GuardFailures;

        public int CompilationAttempts { get; set; }

        public long RetryAfterTimestamp { get; set; }

        public int Tier2CompilationAttempts { get; set; }

        public long Tier2RetryAfterTimestamp { get; set; }

        public string? FailureCode { get; set; }
    }

    private sealed record CompilationRequest(
        FunctionEntry Entry,
        LuaIrModule Module,
        long EnqueuedTimestamp,
        TaskCompletionSource<bool> Completion,
        LuaJitCompilationTier Tier,
        LuaJitFunctionProfile? Profile);

    private sealed class FunctionEntryObservation
    {
        public int Counted;
    }
}
