using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
    ILuaFrameInstructionRouter,
    ILuaInstructionObserver,
    ILuaLoopOsrObserver,
    IDisposable
{
    private const int CodegenVersion = LuaJitProfileCodec.CurrentCodegenVersion;
    private const int MaximumNoNumericTier2EligibilityEvaluations = 2;
    private readonly LuaJitExecutorOptions _options;
    private readonly ILuaDynamicCodeCapabilities _capabilities;
    private readonly ILuaTier1Compiler _compiler;
    private readonly ILuaTier2Compiler _tier2Compiler;
    private readonly ILuaLoopOsrCompiler _loopOsrCompiler;
    private readonly ConcurrentDictionary<FunctionKey, FunctionEntry> _entries = [];
    private readonly ConditionalWeakTable<LuaIrModule, ModuleRouteCache> _moduleRoutes = new();
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
    private long _tier2CompletedInvocations;
    private long _tier2UnsupportedExits;
    private long _tier2GuardFailures;
    private long _tier2Invalidations;
    private long _loopOsrRequests;
    private long _loopOsrCompilationQueued;
    private long _loopOsrCompilationStarted;
    private long _loopOsrCompilationCompleted;
    private long _loopOsrCompilationFailed;
    private long _loopOsrEntries;
    private long _loopOsrExits;
    private long _loopOsrGuardFailures;
    private long _loopOsrInvalidations;
    private long _compiledCanonicalInstructions;
    private long _schedulerExits;
    private long _continueExits;
    private long _pollExits;
    private long _callExits;
    private long _tailCallExits;
    private long _returnExits;
    private long _instructionBudgetPolls;
    private long _garbageCollectionPolls;
    private long _debugModeDeoptimizations;
    private long _tier1CompileAllocatedBytes;
    private long _tier1DirectCanonicalInstructions;
    private long _tier1SlowPathCanonicalInstructions;
    private long _tier1PlanInstructions;
    private long _totalCanonicalVerificationTicks;
    private long _totalControlFlowAnalysisTicks;
    private long _totalMethodPlanBuildTicks;
    private long _totalPlanVerificationTicks;
    private long _totalReflectionEmitTicks;
    private long _totalDelegateCreationTicks;
    private long _eligibilityEvaluated;
    private long _eligibilityAccepted;
    private long _eligibilityRejected;
    private long _tier2EligibilityEvaluated;
    private long _tier2EligibilityAccepted;
    private long _tier2EligibilityRejected;
    private long _loopOsrEligibilityEvaluated;
    private long _loopOsrEligibilityAccepted;
    private long _loopOsrEligibilityRejected;
    private int _loopOsrCompilerPreparationState;
    private int _disposed;

    public LuaTieredJitRegistry(
        LuaJitExecutorOptions options,
        ILuaDynamicCodeCapabilities capabilities,
        ILuaTier1Compiler compiler,
        ILuaTier2Compiler tier2Compiler,
        ILuaLoopOsrCompiler loopOsrCompiler)
    {
        _options = options;
        _capabilities = capabilities;
        _compiler = compiler;
        _tier2Compiler = tier2Compiler;
        _loopOsrCompiler = loopOsrCompiler;
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

    private bool IsLoopOsrEnabled => _options.EnableLoopOsr &&
        _capabilities.IsDynamicCodeSupported && _capabilities.IsDynamicCodeCompiled;

    private bool IsTier2Enabled => _options.EnableTier2 &&
        _capabilities.IsDynamicCodeSupported && _capabilities.IsDynamicCodeCompiled;

    public LuaFrameInstructionRoute GetInitialFrameInstructionRoute(LuaClosure closure)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return LuaFrameInstructionRoute.Interpreter;
        }

        var route = GetFunctionRoute(closure.Module, closure.Function.Id);
        return Volatile.Read(ref route.TerminalInterpreterRoute) != 0
            ? LuaFrameInstructionRoute.Interpreter
            : LuaFrameInstructionRoute.Backend;
    }

    public LuaCompiledExit Execute(
        LuaExecutionEngine engine,
        LuaExecutionContext context,
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        var module = frame.Closure.Module;
        var functionRoute = GetFunctionRoute(module, frame.Closure.Function.Id);
        var entry = GetOrCreateEntry(functionRoute, frame.Closure.Function.ParameterCount);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return ExecuteReferenceInstruction(
                engine,
                context,
                thread,
                frame,
                functionRoute,
                entry,
                LuaCompiledExitReason.BackendInvalidated);
        }
        if (context.TryBeginInstructionObservation(frame.ProgramCounter))
        {
            ObserveInstructionCore(
                context,
                thread,
                frame,
                frame.ProgramCounter,
                instruction,
                entry,
                functionRoute);
        }
        if (_options.Policy == LuaJitPolicy.InterpreterOnly)
        {
            MarkTerminalInterpreterRoute(functionRoute);
            return ExecuteReferenceInstruction(
                engine,
                context,
                thread,
                frame,
                functionRoute,
                entry,
                LuaCompiledExitReason.BackendInvalidated);
        }
        FunctionEntryObservation? frameObservation = null;
        if ((IsTier2Enabled && Volatile.Read(ref entry.Tier2ProfilingActive) != 0) ||
            (IsLoopOsrEnabled && Volatile.Read(ref entry.LoopOsrObservationState) >= 0))
        {
            frameObservation = _observedFrames.GetValue(
                frame,
                static _ => new FunctionEntryObservation());
            frameObservation.Entry = entry;
            frameObservation.Route = functionRoute;
        }
        Interlocked.Exchange(
            ref entry.LastAccessStamp,
            Interlocked.Increment(ref _accessStamp));

        if (context.HasExactDebugHooks || !context.IsDebugModeCurrent())
        {
            return ExecuteReferenceInstruction(
                engine,
                context,
                thread,
                frame,
                functionRoute,
                entry,
                LuaCompiledExitReason.DebugModeChanged);
        }

        if (frameObservation is not null &&
            Volatile.Read(ref entry.LoopOsrObservationState) > 0 &&
            Volatile.Read(ref frameObservation.HasPendingLoop) != 0 &&
            TryInvokeLoopOsr(
                entry,
                module,
                context,
                thread,
                frame,
                frameObservation) is { } osrExit)
        {
            return osrExit;
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
                frame.InstructionRoute = LuaFrameInstructionRoute.Backend;
                return InvokeCompiled(entry, entryPoint.Value, context, thread, frame);
            }
        }

        if (ShouldConsiderCompilation(entry))
        {
            if (!_capabilities.IsDynamicCodeSupported || !_capabilities.IsDynamicCodeCompiled)
            {
                _ = RequestCompilation(entry, module, compileSynchronously: false);
            }
            else
            {
                var policyAllowsCompilation = _options.Policy == LuaJitPolicy.PreferJit;
                if (!policyAllowsCompilation)
                {
                    var invocationHot = Interlocked.Read(ref entry.FunctionEntries) >=
                        _options.FunctionEntryThreshold;
                    var eligibility = EnsureEligibility(entry, module, invocationHot);
                    policyAllowsCompilation = eligibility.IsCompilable &&
                        (_options.Policy != LuaJitPolicy.Auto || eligibility.IsAutoEligible);
                    if (_options.Policy == LuaJitPolicy.Auto &&
                        !eligibility.IsAutoEligible)
                    {
                        EnsureLoopOsrEntries(entry, module);
                        TryMarkTerminalInterpreterRoute(functionRoute, entry);
                    }
                }

                if (policyAllowsCompilation)
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
                        frame.InstructionRoute = LuaFrameInstructionRoute.Backend;
                        return InvokeCompiled(entry, entryPoint.Value, context, thread, frame);
                    }
                }
            }
        }

        if (_options.Policy == LuaJitPolicy.RequireJit)
        {
            throw CreateRequiredJitException(entry);
        }

        return ExecuteReferenceInstruction(
            engine,
            context,
            thread,
            frame,
            functionRoute,
            entry,
            LuaCompiledExitReason.BackendInvalidated,
            forceBackendProbe: frameObservation is not null &&
                Volatile.Read(ref frameObservation.HasPendingLoop) != 0);
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

        FunctionEntry entry;
        FunctionRoute functionRoute;
        if (!_observedFrames.TryGetValue(frame, out var frameObservation) ||
            frameObservation.Entry is not { } observedEntry ||
            frameObservation.Route is not { } observedRoute)
        {
            functionRoute = GetFunctionRoute(
                frame.Closure.Module,
                frame.Closure.Function.Id);
            entry = GetOrCreateEntry(
                functionRoute,
                frame.Closure.Function.ParameterCount);
            frameObservation = _observedFrames.GetValue(
                frame,
                static _ => new FunctionEntryObservation());
            frameObservation.Entry = entry;
            frameObservation.Route = functionRoute;
        }
        else
        {
            entry = observedEntry;
            functionRoute = observedRoute;
        }

        ObserveInstructionCore(
            context,
            thread,
            frame,
            programCounter,
            instruction,
            entry,
            functionRoute);
    }

    private void ObserveInstructionCore(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int programCounter,
        LuaIrInstruction instruction,
        FunctionEntry entry,
        FunctionRoute functionRoute)
    {
        if (_options.Policy == LuaJitPolicy.InterpreterOnly ||
            Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (Volatile.Read(ref entry.LoopOsrRuntimeQualificationPendingCount) != 0)
        {
            ObserveLoopOsrRuntimeQualification(
                entry,
                thread,
                frame,
                programCounter,
                instruction);
            TryMarkTerminalInterpreterRoute(functionRoute, entry);
        }

        ObserveHotness(entry, frame, programCounter, instruction);
        if (Volatile.Read(ref entry.Tier2ProfilingActive) != 0)
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

    public void ObserveLoopOsrBackedge(LuaFrame frame, int programCounter)
    {
        if (_observedFrames.TryGetValue(frame, out var observation) &&
            observation.Entry is { } entry)
        {
            Interlocked.Increment(ref entry.Backedges);
            Interlocked.Increment(ref _backedges);
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
        Interlocked.Read(ref _tier2Invalidations),
        Interlocked.Read(ref _loopOsrRequests),
        Interlocked.Read(ref _loopOsrCompilationQueued),
        Interlocked.Read(ref _loopOsrCompilationStarted),
        Interlocked.Read(ref _loopOsrCompilationCompleted),
        Interlocked.Read(ref _loopOsrCompilationFailed),
        Interlocked.Read(ref _loopOsrEntries),
        Interlocked.Read(ref _loopOsrExits),
        Interlocked.Read(ref _loopOsrGuardFailures),
        Interlocked.Read(ref _loopOsrInvalidations),
        Interlocked.Read(ref _compiledCanonicalInstructions),
        Interlocked.Read(ref _schedulerExits),
        Interlocked.Read(ref _continueExits),
        Interlocked.Read(ref _pollExits),
        Interlocked.Read(ref _callExits),
        Interlocked.Read(ref _tailCallExits),
        Interlocked.Read(ref _returnExits),
        Interlocked.Read(ref _instructionBudgetPolls),
        Interlocked.Read(ref _garbageCollectionPolls),
        Interlocked.Read(ref _debugModeDeoptimizations),
        Interlocked.Read(ref _tier1CompileAllocatedBytes),
        Interlocked.Read(ref _tier1DirectCanonicalInstructions),
        Interlocked.Read(ref _tier1SlowPathCanonicalInstructions),
        Interlocked.Read(ref _tier1PlanInstructions),
        Interlocked.Read(ref _totalCanonicalVerificationTicks),
        Interlocked.Read(ref _totalControlFlowAnalysisTicks),
        Interlocked.Read(ref _totalMethodPlanBuildTicks),
        Interlocked.Read(ref _totalPlanVerificationTicks),
        Interlocked.Read(ref _totalReflectionEmitTicks),
        Interlocked.Read(ref _totalDelegateCreationTicks),
        Interlocked.Read(ref _eligibilityEvaluated),
        Interlocked.Read(ref _eligibilityAccepted),
        Interlocked.Read(ref _eligibilityRejected),
        Interlocked.Read(ref _tier2EligibilityEvaluated),
        Interlocked.Read(ref _tier2EligibilityAccepted),
        Interlocked.Read(ref _tier2EligibilityRejected),
        Interlocked.Read(ref _loopOsrEligibilityEvaluated),
        Interlocked.Read(ref _loopOsrEligibilityAccepted),
        Interlocked.Read(ref _loopOsrEligibilityRejected))
    {
        Tier2MethodEntries = Interlocked.Read(ref _tier2Invocations),
        Tier2CompletedInvocations = Interlocked.Read(ref _tier2CompletedInvocations),
        Tier2UnsupportedExits = Interlocked.Read(ref _tier2UnsupportedExits),
    };

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
            LuaCodegenAbiV2.RuntimeAbiVersion,
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

    public LuaJitFunctionEligibility GetFunctionEligibility(
        LuaIrModule module,
        int functionId)
    {
        ArgumentNullException.ThrowIfNull(module);
        if ((uint)functionId >= (uint)module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        var key = new FunctionKey(
            GetModuleContentId(module),
            functionId,
            LuaCodegenAbiV2.RuntimeAbiVersion,
            CodegenVersion);
        var entry = GetOrCreateEntry(key, module.Functions[functionId].ParameterCount);
        return EnsureEligibility(entry, module);
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
            LuaCodegenAbiV2.RuntimeAbiVersion,
            CodegenVersion);
        return _entries.TryGetValue(key, out var entry)
            ? entry.Profile.Snapshot()
            : new LuaJitFunctionProfile(
                0,
                [.. Enumerable.Repeat(LuaJitValueKinds.None, function.ParameterCount)],
                []);
    }

    public LuaJitTier2Eligibility GetTier2PromotionEligibility(
        LuaIrModule module,
        int functionId)
    {
        ArgumentNullException.ThrowIfNull(module);
        if ((uint)functionId >= (uint)module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        var key = new FunctionKey(
            GetModuleContentId(module),
            functionId,
            LuaCodegenAbiV2.RuntimeAbiVersion,
            CodegenVersion);
        var entry = GetOrCreateEntry(key, module.Functions[functionId].ParameterCount);
        LuaJitFunctionProfile profile;
        lock (entry.Gate)
        {
            if (entry.Tier2Eligibility is { } cached &&
                cached.ProfileSamples == entry.Profile.Samples)
            {
                return cached;
            }

            profile = entry.Profile.Snapshot();
        }

        return ProfileGuidedLuaTier2Compiler.EvaluateAutoPromotionEligibility(
            module,
            functionId,
            profile,
            CancellationToken.None);
    }

    public void ImportProfile(LuaIrModule module, LuaJitModuleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(profile);
        foreach (var imported in profile.Functions)
        {
            var function = module.Functions[imported.FunctionId];
            var key = new FunctionKey(
                profile.ModuleContentId,
                imported.FunctionId,
                LuaCodegenAbiV2.RuntimeAbiVersion,
                CodegenVersion);
            var entry = GetOrCreateEntry(key, function.ParameterCount);
            entry.Profile.Merge(imported.Profile);
            lock (entry.Gate)
            {
                entry.Tier2Eligibility = null;
                entry.NextTier2EligibilitySample = 0;
                entry.NoNumericTier2EligibilityEvaluations = 0;
                if (IsTier2Enabled && entry.Tier2State == LuaJitTier2State.Ineligible)
                {
                    entry.Tier2State = LuaJitTier2State.Profiling;
                    Volatile.Write(ref entry.Tier2ProfilingActive, 1);
                    if (entry.ActiveTier == LuaJitCompilationTier.Tier1 &&
                        entry.Tier1Method is not null)
                    {
                        entry.Method = entry.Tier1Method;
                    }
                }
            }
            var entrySamples = imported.Profile.Sites
                .FirstOrDefault(static site => site.ProgramCounter == 0)?
                .Samples ?? 0;
            if (entrySamples > 0)
            {
                SetMaximum(ref entry.FunctionEntries, _options.FunctionEntryThreshold);
                SetMaximum(
                    ref entry.CompletedTier1Invocations,
                    Math.Min(entrySamples, _options.Tier2InvocationThreshold));
            }
        }
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
            return IsTier2Enabled
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

    public IReadOnlyList<LuaJitLoopOsrPlan> GetLoopOsrPlans(
        LuaIrModule module,
        int functionId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(module);
        if ((uint)functionId >= (uint)module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        var function = module.Functions[functionId];
        return LuaLoopOsrAnalyzer.Analyze(module, functionId)
            .Select(plan => plan with
            {
                CodeKind = LuaLoopOsrEligibilityEvaluator.Evaluate(
                    function,
                    plan).ExpectedCodeKind,
            })
            .ToArray();
    }

    public LuaJitOsrState GetLoopOsrState(
        LuaIrModule module,
        int functionId,
        int headerProgramCounter,
        int backedgeProgramCounter)
    {
        ArgumentNullException.ThrowIfNull(module);
        if ((uint)functionId >= (uint)module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        if (!IsLoopOsrEnabled)
        {
            return LuaJitOsrState.Disabled;
        }

        var key = new FunctionKey(
            GetModuleContentId(module),
            functionId,
            LuaCodegenAbiV2.RuntimeAbiVersion,
            CodegenVersion);
        var entry = GetOrCreateEntry(key, module.Functions[functionId].ParameterCount);
        EnsureLoopOsrEntries(entry, module);
        lock (entry.Gate)
        {
            return entry.LoopOsrEntries.TryGetValue(
                new LoopKey(headerProgramCounter, backedgeProgramCounter),
                out var loop)
                ? loop.State
                : IsLoopOsrEnabled
                    ? LuaJitOsrState.Profiling
                    : LuaJitOsrState.Disabled;
        }
    }

    public LuaJitLoopOsrEligibility GetLoopOsrEligibility(
        LuaIrModule module,
        int functionId,
        int headerProgramCounter,
        int backedgeProgramCounter)
    {
        ArgumentNullException.ThrowIfNull(module);
        if ((uint)functionId >= (uint)module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        if (!IsLoopOsrEnabled)
        {
            var plan = LuaLoopOsrAnalyzer.Analyze(module, functionId).FirstOrDefault(candidate =>
                candidate.HeaderProgramCounter == headerProgramCounter &&
                candidate.BackedgeProgramCounter == backedgeProgramCounter) ??
                throw new ArgumentException(
                    "The requested edge is not a verified natural-loop backedge.",
                    nameof(backedgeProgramCounter));
            return LuaLoopOsrEligibilityEvaluator.Evaluate(
                module.Functions[functionId],
                plan);
        }

        var key = new FunctionKey(
            GetModuleContentId(module),
            functionId,
            LuaCodegenAbiV2.RuntimeAbiVersion,
            CodegenVersion);
        var entry = GetOrCreateEntry(key, module.Functions[functionId].ParameterCount);
        EnsureLoopOsrEntries(entry, module);
        lock (entry.Gate)
        {
            if (entry.LoopOsrEntries.TryGetValue(
                    new LoopKey(headerProgramCounter, backedgeProgramCounter),
                    out var loop))
            {
                return loop.Eligibility;
            }
        }

        throw new ArgumentException(
            "The requested edge is not a verified natural-loop backedge.",
            nameof(backedgeProgramCounter));
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

        foreach (var entry in _entries.Values)
        {
            lock (entry.Gate)
            {
                entry.Method = null;
                entry.Tier1Method = null;
                entry.PlainTier1Method = null;
                entry.Tier2Method = null;
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

    private FunctionEntry GetOrCreateEntry(FunctionKey key, int parameterCount) =>
        _entries.GetOrAdd(
            key,
            static (entryKey, state) => new FunctionEntry(
                entryKey,
                state.ParameterCount,
                state.Registry._options.MaximumPolymorphicShapes,
                state.Registry.IsTier2Enabled,
                state.Registry.IsLoopOsrEnabled),
            new FunctionEntryFactoryState(this, parameterCount));

    private FunctionEntry GetOrCreateEntry(FunctionRoute route, int parameterCount)
    {
        if (Volatile.Read(ref route.Entry) is { } cached)
        {
            return cached;
        }

        var key = new FunctionKey(
            route.ModuleContentId,
            route.FunctionId,
            LuaCodegenAbiV2.RuntimeAbiVersion,
            CodegenVersion);
        var entry = GetOrCreateEntry(key, parameterCount);
        return Interlocked.CompareExchange(ref route.Entry, entry, null) ?? entry;
    }

    private FunctionRoute GetFunctionRoute(LuaIrModule module, int functionId)
    {
        var cache = _moduleRoutes.GetValue(
            module,
            static module => new ModuleRouteCache(
                LuaJitModuleIdentity.Create(module),
                module.Functions.Length));
        return cache.GetFunctionRoute(functionId);
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
        if (entryPoint.Tier == LuaJitCompilationTier.Tier2)
        {
            if (exit.Kind is LuaCompiledExitKind.Return or LuaCompiledExitKind.TailCall)
            {
                Interlocked.Increment(ref _tier2CompletedInvocations);
            }

            if (exit.Reason == LuaCompiledExitReason.UnsupportedInstruction)
            {
                Interlocked.Increment(ref _tier2UnsupportedExits);
            }
        }
        Interlocked.Add(ref _compiledCanonicalInstructions, exit.InstructionsConsumed);
        Interlocked.Increment(ref _schedulerExits);
        switch (exit.Kind)
        {
            case LuaCompiledExitKind.Continue:
                Interlocked.Increment(ref _continueExits);
                break;
            case LuaCompiledExitKind.Poll:
                Interlocked.Increment(ref _pollExits);
                if (exit.Reason == LuaCompiledExitReason.InstructionBudget)
                {
                    Interlocked.Increment(ref _instructionBudgetPolls);
                }
                else if (exit.Reason == LuaCompiledExitReason.GarbageCollection)
                {
                    Interlocked.Increment(ref _garbageCollectionPolls);
                }

                break;
            case LuaCompiledExitKind.Call:
                Interlocked.Increment(ref _callExits);
                break;
            case LuaCompiledExitKind.TailCall:
                Interlocked.Increment(ref _tailCallExits);
                break;
            case LuaCompiledExitKind.Return:
                Interlocked.Increment(ref _returnExits);
                break;
            case LuaCompiledExitKind.Deopt:
                if (exit.Reason == LuaCompiledExitReason.DebugModeChanged)
                {
                    Interlocked.Increment(ref _debugModeDeoptimizations);
                }

                break;
        }

        if (exit.Kind == LuaCompiledExitKind.Deopt)
        {
            Interlocked.Increment(ref _deoptimizations);
            if (entryPoint.Tier == LuaJitCompilationTier.Tier2 &&
                exit.Reason == LuaCompiledExitReason.GuardFailure)
            {
                HandleTier2GuardFailure(entry);
            }
            else if (entryPoint.Tier == LuaJitCompilationTier.Tier2 &&
                exit.Reason == LuaCompiledExitReason.UnsupportedInstruction)
            {
                HandleTier2UnsupportedExit(entry);
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
            if (!frame.BackendEntryObserved)
            {
                frame.BackendEntryObserved = true;
                Interlocked.Increment(ref entry.FunctionEntries);
                Interlocked.Increment(ref _functionEntries);
            }
        }

        if (LuaInstructionRouting.IsBackedge(programCounter, instruction))
        {
            Interlocked.Increment(ref entry.Backedges);
            Interlocked.Increment(ref _backedges);
        }

        var observationState = Volatile.Read(ref entry.LoopOsrObservationState);
        if (observationState < 0 ||
            !IsLoopOsrEnabled ||
            !LuaLoopOsrAnalyzer.IsOsrBackedgeInstruction(instruction, programCounter))
        {
            return;
        }

        var analyzeAtHotBackedge = observationState == 0;
        if (analyzeAtHotBackedge &&
            Interlocked.Read(ref entry.Backedges) < _options.LoopOsrBackedgeThreshold)
        {
            return;
        }

        EnsureLoopOsrEntries(entry, frame.Closure.Module);
        if (Volatile.Read(ref entry.LoopOsrObservationState) < 0)
        {
            return;
        }
        LoopOsrEntry? loop;
        var canProfile = false;
        lock (entry.Gate)
        {
            entry.LoopOsrEntries.TryGetValue(
                new LoopKey(instruction.B, programCounter),
                out loop);
            canProfile = loop is
            {
                ExactNumericQualificationState: > 0,
                State: not (
                    LuaJitOsrState.Disabled or
                    LuaJitOsrState.Ineligible or
                    LuaJitOsrState.Invalidated),
            };
        }

        if (loop is null)
        {
            return;
        }

        if (analyzeAtHotBackedge)
        {
            Interlocked.Exchange(
                ref loop.Backedges,
                _options.LoopOsrBackedgeThreshold - 1L);
        }

        if (!canProfile)
        {
            return;
        }

        var observedBackedges = Interlocked.Increment(ref loop.Backedges);
        if (observedBackedges < _options.LoopOsrBackedgeThreshold)
        {
            return;
        }

        var frameObservation = _observedFrames.GetValue(
            frame,
            static _ => new FunctionEntryObservation());
        lock (frameObservation.Gate)
        {
            frameObservation.PendingLoop = loop.Key;
            Volatile.Write(ref frameObservation.HasPendingLoop, 1);
        }
    }

    private LuaCompiledExit? TryInvokeLoopOsr(
        FunctionEntry entry,
        LuaIrModule module,
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        FunctionEntryObservation observation)
    {
        if (!IsLoopOsrEnabled)
        {
            return null;
        }

        LoopKey? pending;
        lock (observation.Gate)
        {
            pending = observation.PendingLoop;
            if (pending is { } observed &&
                frame.ProgramCounter == observed.BackedgeProgramCounter)
            {
                return null;
            }

            observation.PendingLoop = null;
            Volatile.Write(ref observation.HasPendingLoop, 0);
        }

        if (pending is not { } key || frame.ProgramCounter != key.HeaderProgramCounter)
        {
            return null;
        }

        EnsureLoopOsrEntries(entry, module);
        LoopOsrEntry? loop;
        lock (entry.Gate)
        {
            entry.LoopOsrEntries.TryGetValue(key, out loop);
        }

        if (loop is null)
        {
            return null;
        }

        Interlocked.Increment(ref _loopOsrRequests);
        var compileSynchronously = _options.SynchronousCompilation;
        var completion = RequestLoopOsrCompilation(
            entry,
            loop,
            module,
            compileSynchronously);
        if (compileSynchronously && completion is not null)
        {
            _ = completion.GetAwaiter().GetResult();
        }

        LuaCompiledMethod? method;
        lock (entry.Gate)
        {
            method = loop.State == LuaJitOsrState.Ready ? loop.Method : null;
        }

        if (method is null)
        {
            return null;
        }

        Interlocked.Increment(ref _loopOsrEntries);
        Interlocked.Exchange(
            ref entry.LastAccessStamp,
            Interlocked.Increment(ref _accessStamp));
        RaiseEvent(new LuaJitEvent(
            LuaJitEventKind.LoopOsrEntered,
            entry.Key.ModuleContentId,
            entry.Key.FunctionId,
            ReadState(entry),
            Tier: LuaJitCompilationTier.LoopOsr));
        var exit = method(context, thread, frame);
        Interlocked.Increment(ref _loopOsrExits);
        RaiseEvent(new LuaJitEvent(
            LuaJitEventKind.LoopOsrExited,
            entry.Key.ModuleContentId,
            entry.Key.FunctionId,
            ReadState(entry),
            DiagnosticCode: exit.Reason == LuaCompiledExitReason.None
                ? exit.Kind.ToString()
                : exit.Reason.ToString(),
            Tier: LuaJitCompilationTier.LoopOsr));
        if (exit.Kind == LuaCompiledExitKind.Deopt &&
            exit.Reason == LuaCompiledExitReason.GuardFailure)
        {
            HandleLoopOsrGuardFailure(entry, loop);
        }

        return exit;
    }

    private void EnsureLoopOsrEntries(FunctionEntry entry, LuaIrModule module)
    {
        if (!IsLoopOsrEnabled)
        {
            return;
        }

        List<(LoopOsrEntry Loop, LuaJitLoopOsrEligibility Eligibility)>? evaluated = null;
        var runtimeQualificationPendingCount = 0;
        lock (entry.Gate)
        {
            if (entry.LoopOsrAnalyzed)
            {
                return;
            }

            foreach (var plan in LuaLoopOsrAnalyzer.Analyze(module, entry.Key.FunctionId))
            {
                var key = new LoopKey(
                    plan.HeaderProgramCounter,
                    plan.BackedgeProgramCounter);
                var eligibility = LuaLoopOsrEligibilityEvaluator.Evaluate(
                    module.Functions[entry.Key.FunctionId],
                    plan);
                var loop = new LoopOsrEntry(
                    key,
                    plan,
                    eligibility,
                    _options.EnableLoopOsrManagedFallback);
                if (entry.LoopOsrEntries.TryAdd(key, loop))
                {
                    if (eligibility.IsAutoEligible &&
                        !_options.EnableLoopOsrManagedFallback)
                    {
                        foreach (var pc in plan.ProgramCounters)
                        {
                            if (!LuaLoopOsrRuntimeEligibilityEvaluator
                                .RequiresExactNumericObservation(
                                    module.Functions[entry.Key.FunctionId].Instructions[pc]))
                            {
                                continue;
                            }

                            loop.PendingExactNumericGuardSites.Add(pc);
                            if (!entry.LoopOsrGuardSites.TryGetValue(pc, out var guardedLoops))
                            {
                                guardedLoops = [];
                                entry.LoopOsrGuardSites.Add(pc, guardedLoops);
                            }

                            guardedLoops.Add(loop);
                        }

                        if (loop.PendingExactNumericGuardSites.Count == 0)
                        {
                            loop.ExactNumericQualificationState = 1;
                            loop.Eligibility = eligibility;
                            (evaluated ??= []).Add((loop, eligibility));
                        }
                        else
                        {
                            runtimeQualificationPendingCount++;
                        }
                    }
                    else
                    {
                        (evaluated ??= []).Add((loop, eligibility));
                    }
                }
            }

            entry.LoopOsrAnalyzed = true;
            Volatile.Write(
                ref entry.LoopOsrRuntimeQualificationPendingCount,
                runtimeQualificationPendingCount);
            Volatile.Write(
                ref entry.LoopOsrObservationState,
                entry.LoopOsrEntries.Values.Any(static loop =>
                    loop.State != LuaJitOsrState.Ineligible) ? 1 : -1);
        }

        if (evaluated is null)
        {
            return;
        }

        foreach (var evaluation in evaluated)
        {
            RecordLoopOsrEligibility(entry, evaluation.Eligibility);
        }
    }

    private void ObserveLoopOsrRuntimeQualification(
        FunctionEntry entry,
        LuaThread thread,
        LuaFrame frame,
        int programCounter,
        LuaIrInstruction instruction)
    {
        if (_options.EnableLoopOsrManagedFallback ||
            !entry.LoopOsrGuardSites.TryGetValue(programCounter, out var guardedLoops))
        {
            return;
        }

        var exactNumeric = LuaLoopOsrRuntimeEligibilityEvaluator.HasExactNumericOperands(
            thread,
            frame,
            instruction);
        List<LuaJitLoopOsrEligibility>? completed = null;
        lock (entry.Gate)
        {
            foreach (var loop in guardedLoops)
            {
                if (loop.ExactNumericQualificationState != 0)
                {
                    continue;
                }

                if (!exactNumeric)
                {
                    loop.ExactNumericQualificationState = -1;
                    loop.State = LuaJitOsrState.Ineligible;
                    loop.PendingExactNumericGuardSites.Clear();
                    loop.Eligibility = loop.Eligibility with
                    {
                        IsAutoEligible = false,
                        Reason = LuaJitLoopOsrEligibilityReason.NonExactNumericProfile,
                        DiagnosticCode = LuaJitLoopOsrDiagnosticCodes.NonExactNumericProfile,
                        ExpectedCodeKind = LuaJitLoopOsrCodeKind.ManagedCanonicalProgram,
                    };
                    Interlocked.Decrement(
                        ref entry.LoopOsrRuntimeQualificationPendingCount);
                    (completed ??= []).Add(loop.Eligibility);
                    continue;
                }

                loop.PendingExactNumericGuardSites.Remove(programCounter);
                if (loop.PendingExactNumericGuardSites.Count != 0)
                {
                    continue;
                }

                loop.ExactNumericQualificationState = 1;
                loop.Eligibility = loop.StructuralEligibility;
                Interlocked.Decrement(ref entry.LoopOsrRuntimeQualificationPendingCount);
                (completed ??= []).Add(loop.Eligibility);
            }

            if (entry.LoopOsrEntries.Values.All(static loop =>
                    loop.State == LuaJitOsrState.Ineligible))
            {
                Volatile.Write(ref entry.LoopOsrObservationState, -1);
            }
        }

        if (completed is null)
        {
            return;
        }

        foreach (var eligibility in completed)
        {
            RecordLoopOsrEligibility(entry, eligibility);
        }
    }

    private void RecordLoopOsrEligibility(
        FunctionEntry entry,
        LuaJitLoopOsrEligibility eligibility)
    {
        Interlocked.Increment(ref _loopOsrEligibilityEvaluated);
        if (eligibility.IsAutoEligible)
        {
            Interlocked.Increment(ref _loopOsrEligibilityAccepted);
        }
        else
        {
            Interlocked.Increment(ref _loopOsrEligibilityRejected);
        }

        RaiseEvent(new LuaJitEvent(
            eligibility.IsAutoEligible
                ? LuaJitEventKind.LoopOsrEligibilityAccepted
                : LuaJitEventKind.LoopOsrEligibilityRejected,
            entry.Key.ModuleContentId,
            entry.Key.FunctionId,
            ReadState(entry),
            DiagnosticCode: eligibility.DiagnosticCode,
            Tier: LuaJitCompilationTier.LoopOsr,
            LoopOsrEligibility: eligibility));

        if (eligibility.IsAutoEligible)
        {
            EnsureLoopOsrCompilerPrepared(entry);
        }
    }

    private void EnsureLoopOsrCompilerPrepared(FunctionEntry entry)
    {
        if (_loopOsrCompiler is not CanonicalLuaLoopOsrCompiler ||
            Volatile.Read(ref _loopOsrCompilerPreparationState) == 2)
        {
            return;
        }

        if (Interlocked.CompareExchange(
                ref _loopOsrCompilerPreparationState,
                1,
                0) != 0)
        {
            CanonicalLuaLoopOsrCompiler.PrepareCompiler();
            return;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            CanonicalLuaLoopOsrCompiler.PrepareCompiler();
        }
        catch
        {
            Volatile.Write(ref _loopOsrCompilerPreparationState, 0);
            throw;
        }

        var duration = Stopwatch.GetElapsedTime(started);
        Volatile.Write(ref _loopOsrCompilerPreparationState, 2);
        RaiseEvent(new LuaJitEvent(
            LuaJitEventKind.LoopOsrCompilerPrepared,
            entry.Key.ModuleContentId,
            entry.Key.FunctionId,
            ReadState(entry),
            Duration: duration,
            Tier: LuaJitCompilationTier.LoopOsr));
    }

    private bool ShouldConsiderCompilation(FunctionEntry entry) => _options.Policy switch
    {
        LuaJitPolicy.PreferJit or LuaJitPolicy.RequireJit => true,
        LuaJitPolicy.Auto =>
            Interlocked.Read(ref entry.FunctionEntries) >= _options.FunctionEntryThreshold ||
            Interlocked.Read(ref entry.Backedges) >= _options.BackedgeThreshold,
        _ => false,
    };

    private LuaJitFunctionEligibility EnsureEligibility(
        FunctionEntry entry,
        LuaIrModule module,
        bool repeatedInvocationObserved = false)
    {
        LuaJitFunctionEligibility eligibility;
        var evaluated = false;
        lock (entry.Gate)
        {
            if (entry.Eligibility is { } cached &&
                !(repeatedInvocationObserved &&
                  cached.Reason == LuaJitEligibilityReason.NoRepeatedWork))
            {
                return cached;
            }

            eligibility = LuaTier1EligibilityEvaluator.Evaluate(
                module,
                entry.Key.FunctionId,
                IsTier2Enabled || IsLoopOsrEnabled,
                repeatedInvocationObserved);
            entry.Eligibility = eligibility;
            evaluated = true;
        }

        if (evaluated)
        {
            Interlocked.Increment(ref _eligibilityEvaluated);
            if (eligibility.IsAutoEligible)
            {
                Interlocked.Increment(ref _eligibilityAccepted);
            }
            else
            {
                Interlocked.Increment(ref _eligibilityRejected);
            }

            RaiseEvent(new LuaJitEvent(
                eligibility.IsAutoEligible
                    ? LuaJitEventKind.EligibilityAccepted
                    : LuaJitEventKind.EligibilityRejected,
                entry.Key.ModuleContentId,
                entry.Key.FunctionId,
                ReadState(entry),
                eligibility.EstimatedCodeBytes,
                DiagnosticCode: eligibility.DiagnosticCode,
                Eligibility: eligibility));
        }

        return eligibility;
    }

    private bool ShouldPromoteToTier2(FunctionEntry entry, int programCounter)
    {
        if (!IsTier2Enabled)
        {
            return false;
        }

        lock (entry.Gate)
        {
            var promotionStateReady = entry.Tier2State == LuaJitTier2State.Profiling ||
                entry.Tier2State == LuaJitTier2State.Failed &&
                entry.Tier2CompilationAttempts < _options.MaximumCompilationAttempts &&
                Stopwatch.GetTimestamp() >= entry.Tier2RetryAfterTimestamp;
            return entry.ActiveTier == LuaJitCompilationTier.Tier1 &&
                promotionStateReady &&
                !entry.Tier2EligibilityEvaluationInProgress &&
                (entry.Tier2Eligibility is not { IsAutoEligible: false } ||
                 entry.Profile.Samples >= entry.NextTier2EligibilitySample) &&
                (programCounter == 0 &&
                 Interlocked.Read(ref entry.CompletedTier1Invocations) >=
                    _options.Tier2InvocationThreshold ||
                 Interlocked.Read(ref entry.Backedges) >= _options.Tier2BackedgeThreshold);
        }
    }

    private void RecordTier2Eligibility(
        FunctionEntry entry,
        LuaJitTier2Eligibility eligibility)
    {
        Interlocked.Increment(ref _tier2EligibilityEvaluated);
        if (eligibility.IsAutoEligible)
        {
            Interlocked.Increment(ref _tier2EligibilityAccepted);
        }
        else
        {
            Interlocked.Increment(ref _tier2EligibilityRejected);
        }

        RaiseEvent(new LuaJitEvent(
            eligibility.IsAutoEligible
                ? LuaJitEventKind.Tier2EligibilityAccepted
                : LuaJitEventKind.Tier2EligibilityRejected,
            entry.Key.ModuleContentId,
            entry.Key.FunctionId,
            LuaJitFunctionState.Ready,
            DiagnosticCode: eligibility.DiagnosticCode,
            Tier: LuaJitCompilationTier.Tier2,
            Tier2Eligibility: eligibility));
    }

    private static long CalculateNextTier2EligibilitySample(long profileSamples)
    {
        if (profileSamples >= long.MaxValue / 2)
        {
            return long.MaxValue;
        }

        return Math.Max(profileSamples + 1, profileSamples * 2);
    }

    private Task<bool>? RequestLoopOsrCompilation(
        FunctionEntry entry,
        LoopOsrEntry loop,
        LuaIrModule module,
        bool compileSynchronously)
    {
        if (loop.Eligibility.IsAutoEligible)
        {
            EnsureLoopOsrCompilerPrepared(entry);
        }

        CompilationRequest request;
        lock (entry.Gate)
        {
            if (loop.State == LuaJitOsrState.Ineligible)
            {
                return null;
            }

            if (loop.State == LuaJitOsrState.Ready)
            {
                return Task.FromResult(true);
            }

            if (loop.State is LuaJitOsrState.Queued or LuaJitOsrState.Compiling)
            {
                return loop.Completion?.Task;
            }

            if (loop.State == LuaJitOsrState.Failed &&
                (loop.CompilationAttempts >= _options.MaximumCompilationAttempts ||
                 Stopwatch.GetTimestamp() < loop.RetryAfterTimestamp))
            {
                return loop.Completion?.Task;
            }

            loop.State = LuaJitOsrState.Queued;
            loop.CompilationAttempts++;
            loop.Completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            request = new CompilationRequest(
                entry,
                module,
                Stopwatch.GetTimestamp(),
                loop.Completion,
                LuaJitCompilationTier.LoopOsr,
                null,
                loop);
        }

        Interlocked.Increment(ref _loopOsrCompilationQueued);
        RaiseEvent(new LuaJitEvent(
            LuaJitEventKind.LoopOsrQueued,
            entry.Key.ModuleContentId,
            entry.Key.FunctionId,
            ReadState(entry),
            Tier: LuaJitCompilationTier.LoopOsr));
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
            if (ReferenceEquals(loop.Completion, request.Completion) &&
                loop.State == LuaJitOsrState.Queued)
            {
                loop.State = LuaJitOsrState.Profiling;
                loop.CompilationAttempts--;
                loop.Completion = null;
            }
        }

        Interlocked.Increment(ref _queueRejected);
        request.Completion.TrySetResult(false);
        return request.Completion.Task;
    }

    private Task<bool>? RequestTier2Compilation(
        FunctionEntry entry,
        LuaIrModule module,
        bool compileSynchronously)
    {
        LuaJitFunctionProfile profile;
        var evaluateEligibility = !_options.EnableTier2ManagedFallback;
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

            if (evaluateEligibility)
            {
                if (entry.Tier2EligibilityEvaluationInProgress)
                {
                    return null;
                }

                if (entry.Tier2Eligibility is { IsAutoEligible: false } &&
                    entry.Profile.Samples < entry.NextTier2EligibilitySample)
                {
                    return null;
                }

                entry.Tier2EligibilityEvaluationInProgress = true;
            }

            profile = entry.Profile.Snapshot();
        }

        if (evaluateEligibility)
        {
            LuaJitTier2Eligibility eligibility;
            try
            {
                eligibility = ProfileGuidedLuaTier2Compiler.EvaluateAutoPromotionEligibility(
                    module,
                    entry.Key.FunctionId,
                    profile,
                    _disposeCancellation.Token);
            }
            catch
            {
                lock (entry.Gate)
                {
                    entry.Tier2EligibilityEvaluationInProgress = false;
                }

                throw;
            }

            lock (entry.Gate)
            {
                entry.Tier2EligibilityEvaluationInProgress = false;
                entry.Tier2Eligibility = eligibility;
                var terminalRejection = false;
                if (eligibility.IsAutoEligible)
                {
                    entry.NoNumericTier2EligibilityEvaluations = 0;
                    entry.NextTier2EligibilitySample = 0;
                }
                else if (eligibility.Reason ==
                    LuaJitTier2EligibilityReason.NoNumericHotspot)
                {
                    entry.NoNumericTier2EligibilityEvaluations++;
                    terminalRejection = entry.NoNumericTier2EligibilityEvaluations >=
                        MaximumNoNumericTier2EligibilityEvaluations;
                    entry.NextTier2EligibilitySample = terminalRejection
                        ? long.MaxValue
                        : CalculateNextTier2EligibilitySample(profile.Samples);
                }
                else
                {
                    entry.NoNumericTier2EligibilityEvaluations = 0;
                    entry.NextTier2EligibilitySample = long.MaxValue;
                    terminalRejection = IsTerminalTier2Rejection(eligibility.Reason);
                }

                if (!eligibility.IsAutoEligible &&
                    terminalRejection &&
                    entry.ActiveTier == LuaJitCompilationTier.Tier1 &&
                    entry.Tier2State is LuaJitTier2State.Profiling or
                        LuaJitTier2State.Failed)
                {
                    entry.Tier2State = LuaJitTier2State.Ineligible;
                    DeactivateTier2ProfilingLocked(entry);
                }
            }

            RecordTier2Eligibility(entry, eligibility);
            if (!eligibility.IsAutoEligible)
            {
                return null;
            }
        }

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
                profile,
                null);
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
                null,
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
        try
        {
            result = _compiler.Compile(
                request.Module,
                request.Entry.Key.FunctionId,
                IsTier2Enabled || IsLoopOsrEnabled,
                _disposeCancellation.Token);
            _disposeCancellation.Token.ThrowIfCancellationRequested();
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
        result.Plan?.CodeKind == LuaJitTier2CodeKind.ExactNumericSpecializedCil;

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
                request.Entry.Tier2Plan = result.Plan;
                request.Entry.Tier2EstimatedCodeBytes = result.EstimatedCodeBytes;
                request.Entry.Method = result.Method;
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

    private void AccumulateTier1CompilationMetrics(LuaJitCompilationMetrics? metrics)
    {
        if (metrics is not { } value)
        {
            return;
        }

        Interlocked.Add(ref _tier1CompileAllocatedBytes, value.AllocatedBytes);
        Interlocked.Add(
            ref _tier1DirectCanonicalInstructions,
            value.DirectCanonicalInstructionCount);
        Interlocked.Add(
            ref _tier1SlowPathCanonicalInstructions,
            value.SlowPathCanonicalInstructionCount);
        Interlocked.Add(ref _tier1PlanInstructions, value.PlanInstructionCount);
        Interlocked.Add(
            ref _totalCanonicalVerificationTicks,
            value.CanonicalVerificationDuration.Ticks);
        Interlocked.Add(
            ref _totalControlFlowAnalysisTicks,
            value.ControlFlowAnalysisDuration.Ticks);
        Interlocked.Add(
            ref _totalMethodPlanBuildTicks,
            value.MethodPlanBuildDuration.Ticks);
        Interlocked.Add(
            ref _totalPlanVerificationTicks,
            value.PlanVerificationDuration.Ticks);
        Interlocked.Add(
            ref _totalReflectionEmitTicks,
            value.ReflectionEmitDuration.Ticks);
        Interlocked.Add(
            ref _totalDelegateCreationTicks,
            value.DelegateCreationDuration.Ticks);
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
                if (entry.EstimatedCodeBytes > 0 &&
                    Interlocked.Read(ref entry.LastAccessStamp) < oldest)
                {
                    result = entry;
                    oldest = Interlocked.Read(ref entry.LastAccessStamp);
                }
            }
        }

        return result;
    }

    private static long GetLoopOsrCodeBytes(FunctionEntry entry) =>
        entry.LoopOsrEntries.Values.Sum(static loop => loop.EstimatedCodeBytes);

    private LuaJitEvent? EvictEntry(FunctionEntry entry)
    {
        lock (entry.Gate)
        {
            if (entry.EstimatedCodeBytes == 0)
            {
                return null;
            }

            var released = entry.EstimatedCodeBytes;
            Interlocked.Add(ref _estimatedCodeBytes, -released);
            entry.Method = null;
            entry.Tier1Method = null;
            entry.PlainTier1Method = null;
            entry.Tier2Method = null;
            entry.Tier2Plan = null;
            entry.Tier1EstimatedCodeBytes = 0;
            entry.Tier2EstimatedCodeBytes = 0;
            entry.EstimatedCodeBytes = 0;
            entry.ActiveTier = LuaJitCompilationTier.Interpreter;
            entry.State = LuaJitFunctionState.Invalidated;
            entry.Tier2State = LuaJitTier2State.Invalidated;
            Volatile.Write(ref entry.Tier2ProfilingActive, 0);
            ResetTier2PromotionStateLocked(entry);
            entry.Tier2Completion?.TrySetResult(false);
            entry.Tier2Completion = null;
            var invalidatedLoops = entry.LoopOsrEntries.Values.Count(static loop =>
                loop.Method is not null);
            foreach (var loop in entry.LoopOsrEntries.Values)
            {
                loop.Method = null;
                loop.EstimatedCodeBytes = 0;
                loop.State = LuaJitOsrState.Invalidated;
                loop.Completion?.TrySetResult(false);
                loop.Completion = null;
            }
            Interlocked.Add(ref _loopOsrInvalidations, invalidatedLoops);
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
                entry.EstimatedCodeBytes = checked(
                    entry.Tier1EstimatedCodeBytes + GetLoopOsrCodeBytes(entry));
                entry.Method = entry.Tier1Method;
                entry.ActiveTier = LuaJitCompilationTier.Tier1;
                entry.Tier2State = LuaJitTier2State.Profiling;
                entry.Tier2CompilationAttempts = 0;
                entry.Tier2GuardFailures = 0;
                entry.CompletedTier1Invocations = 0;
                entry.Tier2Eligibility = null;
                entry.NextTier2EligibilitySample = 0;
                entry.NoNumericTier2EligibilityEvaluations = 0;
                Volatile.Write(ref entry.Tier2ProfilingActive, 1);
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

    private void HandleTier2UnsupportedExit(FunctionEntry entry)
    {
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
                entry.EstimatedCodeBytes = checked(
                    entry.Tier1EstimatedCodeBytes + GetLoopOsrCodeBytes(entry));
                entry.Method = entry.Tier1Method;
                entry.ActiveTier = LuaJitCompilationTier.Tier1;
                entry.Tier2State = LuaJitTier2State.Failed;
                entry.Tier2CompilationAttempts = _options.MaximumCompilationAttempts;
                DeactivateTier2ProfilingLocked(entry);
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
            DiagnosticCode: LuaJitTier2DiagnosticCodes.UnsupportedInstruction,
            Tier: LuaJitCompilationTier.Tier2));
    }

    private void HandleLoopOsrGuardFailure(FunctionEntry entry, LoopOsrEntry loop)
    {
        Interlocked.Increment(ref _loopOsrGuardFailures);
        var failures = Interlocked.Increment(ref loop.GuardFailures);
        RaiseEvent(new LuaJitEvent(
            LuaJitEventKind.LoopOsrGuardFailed,
            entry.Key.ModuleContentId,
            entry.Key.FunctionId,
            ReadState(entry),
            DiagnosticCode: LuaCompiledExitReason.GuardFailure.ToString(),
            Tier: LuaJitCompilationTier.LoopOsr));
        if (failures < _options.MaximumLoopOsrGuardFailures)
        {
            return;
        }

        long released;
        lock (_cacheGate)
        {
            lock (entry.Gate)
            {
                if (loop.State != LuaJitOsrState.Ready || loop.Method is null)
                {
                    return;
                }

                released = loop.EstimatedCodeBytes;
                var specialized = loop.Plan.CodeKind ==
                    LuaJitLoopOsrCodeKind.GuardedExactNumericCil;
                loop.Method = null;
                loop.EstimatedCodeBytes = 0;
                loop.State = specialized && !_options.EnableLoopOsrManagedFallback
                    ? LuaJitOsrState.Ineligible
                    : LuaJitOsrState.Profiling;
                loop.CompilationAttempts = 0;
                loop.GuardFailures = 0;
                loop.PreferManagedFallback = specialized &&
                    _options.EnableLoopOsrManagedFallback;
                if (loop.State == LuaJitOsrState.Ineligible &&
                    entry.LoopOsrEntries.Values.All(static candidate =>
                        candidate.State == LuaJitOsrState.Ineligible))
                {
                    Volatile.Write(ref entry.LoopOsrObservationState, -1);
                }
                entry.EstimatedCodeBytes -= released;
                Interlocked.Add(ref _estimatedCodeBytes, -released);
            }
        }

        Interlocked.Increment(ref _loopOsrInvalidations);
        RaiseEvent(new LuaJitEvent(
            LuaJitEventKind.LoopOsrInvalidated,
            entry.Key.ModuleContentId,
            entry.Key.FunctionId,
            ReadState(entry),
            released,
            DiagnosticCode: "JIT3003",
            Tier: LuaJitCompilationTier.LoopOsr));
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
                    entry.PlainTier1Method = null;
                    entry.Tier2Method = null;
                    entry.Tier2Plan = null;
                    entry.Tier1EstimatedCodeBytes = 0;
                    entry.Tier2EstimatedCodeBytes = 0;
                    entry.EstimatedCodeBytes = 0;
                    entry.ActiveTier = LuaJitCompilationTier.Interpreter;
                    entry.State = LuaJitFunctionState.Invalidated;
                    entry.Tier2State = LuaJitTier2State.Invalidated;
                    Volatile.Write(ref entry.Tier2ProfilingActive, 0);
                    ResetTier2PromotionStateLocked(entry);
                    entry.FailureCode = null;
                    entry.Completion?.TrySetResult(false);
                    entry.Completion = null;
                    entry.Tier2Completion?.TrySetResult(false);
                    entry.Tier2Completion = null;
                    foreach (var loop in entry.LoopOsrEntries.Values)
                    {
                        if (loop.Method is not null)
                        {
                            Interlocked.Increment(ref _loopOsrInvalidations);
                            events.Add(new LuaJitEvent(
                                LuaJitEventKind.LoopOsrInvalidated,
                                entry.Key.ModuleContentId,
                                entry.Key.FunctionId,
                                LuaJitFunctionState.Invalidated,
                                loop.EstimatedCodeBytes,
                                DiagnosticCode: "JIT3004",
                                Tier: LuaJitCompilationTier.LoopOsr));
                        }

                        loop.Method = null;
                        loop.EstimatedCodeBytes = 0;
                        loop.State = LuaJitOsrState.Invalidated;
                        loop.Completion?.TrySetResult(false);
                        loop.Completion = null;
                    }
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

    private LuaCompiledExit ExecuteReferenceInstruction(
        LuaExecutionEngine engine,
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        FunctionRoute route,
        FunctionEntry entry,
        LuaCompiledExitReason reason,
        bool forceBackendProbe = false)
    {
        var state = ReadState(entry);
        var terminal = TryMarkTerminalInterpreterRoute(route, entry);
        frame.InstructionRoute = terminal
            ? LuaFrameInstructionRoute.Interpreter
            : forceBackendProbe || RequiresPerInstructionBackendProbe(entry)
                ? LuaFrameInstructionRoute.Backend
                : LuaFrameInstructionRoute.InterpreterWithBackedgeProbes;
        RecordFallbackTransition(entry, state, reason);
        return engine.ExecuteCodegenSlowPath(
            context,
            thread,
            frame,
            frame.ProgramCounter);
    }

    private bool TryMarkTerminalInterpreterRoute(
        FunctionRoute route,
        FunctionEntry entry)
    {
        if (Volatile.Read(ref route.TerminalInterpreterRoute) != 0)
        {
            return true;
        }

        var terminal = _options.Policy == LuaJitPolicy.InterpreterOnly ||
            !_capabilities.IsDynamicCodeSupported ||
            !_capabilities.IsDynamicCodeCompiled;
        if (!terminal && _options.Policy == LuaJitPolicy.Auto)
        {
            lock (entry.Gate)
            {
                terminal = entry.Eligibility is
                {
                    IsAutoEligible: false,
                    Reason: not LuaJitEligibilityReason.NoRepeatedWork,
                } &&
                    (!IsLoopOsrEnabled ||
                     Volatile.Read(ref entry.LoopOsrObservationState) < 0);
            }
        }

        if (terminal)
        {
            MarkTerminalInterpreterRoute(route);
        }

        return terminal;
    }

    private static void MarkTerminalInterpreterRoute(FunctionRoute route) =>
        Volatile.Write(ref route.TerminalInterpreterRoute, 1);

    private static bool IsTerminalTier2Rejection(LuaJitTier2EligibilityReason reason) =>
        reason is LuaJitTier2EligibilityReason.PolymorphicNumericProfile or
            LuaJitTier2EligibilityReason.ManagedOptimizationRequired or
            LuaJitTier2EligibilityReason.ManagedSemanticBoundary or
            LuaJitTier2EligibilityReason.UnsupportedInstruction;

    // The caller holds entry.Gate. Keeping both delegates in the entry makes this a single
    // publication point: concurrent readers either execute the profiled method they already
    // captured or observe the plain method on their next scheduler entry.
    private static void DeactivateTier2ProfilingLocked(FunctionEntry entry)
    {
        Volatile.Write(ref entry.Tier2ProfilingActive, 0);
        if (entry.ActiveTier == LuaJitCompilationTier.Tier1 &&
            entry.PlainTier1Method is not null)
        {
            entry.Method = entry.PlainTier1Method;
        }
    }

    private static void ResetTier2PromotionStateLocked(FunctionEntry entry)
    {
        entry.Tier2Eligibility = null;
        entry.Tier2EligibilityEvaluationInProgress = false;
        entry.NextTier2EligibilitySample = 0;
        entry.NoNumericTier2EligibilityEvaluations = 0;
        entry.Tier2CompilationAttempts = 0;
        entry.Tier2RetryAfterTimestamp = 0;
        entry.Tier2GuardFailures = 0;
        entry.CompletedTier1Invocations = 0;
    }

    private bool RequiresPerInstructionBackendProbe(FunctionEntry entry)
    {
        if (Volatile.Read(ref entry.LoopOsrRuntimeQualificationPendingCount) != 0)
        {
            return true;
        }

        lock (entry.Gate)
        {
            return entry.State == LuaJitFunctionState.Failed &&
                entry.CompilationAttempts < _options.MaximumCompilationAttempts;
        }
    }

    private void RecordFallbackTransition(
        FunctionEntry entry,
        LuaJitFunctionState state,
        LuaCompiledExitReason reason)
    {
        var transition = ((int)state << 8) | (int)reason;
        lock (entry.Gate)
        {
            if (entry.LastFallbackTransition == transition)
            {
                return;
            }

            entry.LastFallbackTransition = transition;
        }

        Interlocked.Increment(ref _interpreterFallbacks);
        if (EventOccurred is null)
        {
            return;
        }

        RaiseEvent(new LuaJitEvent(
            LuaJitEventKind.Fallback,
            entry.Key.ModuleContentId,
            entry.Key.FunctionId,
            state,
            DiagnosticCode: reason.ToString()));
    }

    private static LuaJitException CreateRequiredJitException(FunctionEntry entry)
    {
        lock (entry.Gate)
        {
            var ineligible = entry.Eligibility is { IsCompilable: false }
                ? entry.Eligibility
                : null;
            var code = ineligible?.DiagnosticCode ?? entry.FailureCode ?? "JIT1002";
            return new LuaJitException(
                code,
                code == "JIT1001"
                    ? "Tier 1 JIT is required, but dynamic code is unavailable."
                    : ineligible is not null
                        ? $"Tier 1 JIT is required, but eligibility rejected the function: " +
                            $"{ineligible.Reason}."
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
            LuaCodegenAbiV2.RuntimeAbiVersion,
            CodegenVersion);
        return _entries.GetValueOrDefault(key);
    }

    private static void SetMaximum(ref long target, long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref target);
            if (current >= value ||
                Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
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

    private readonly record struct FunctionKey(
        string ModuleContentId,
        int FunctionId,
        int RuntimeAbiVersion,
        int CodegenVersion);

    private readonly record struct CompiledEntryPoint(
        LuaCompiledMethod Method,
        LuaJitCompilationTier Tier);

    private readonly record struct FunctionEntryFactoryState(
        LuaTieredJitRegistry Registry,
        int ParameterCount);

    private readonly record struct LoopKey(
        int HeaderProgramCounter,
        int BackedgeProgramCounter);

    private sealed class FunctionEntry(
        FunctionKey key,
        int parameterCount,
        int maximumPolymorphicShapes,
        bool enableTier2,
        bool enableLoopOsr)
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

        public Dictionary<LoopKey, LoopOsrEntry> LoopOsrEntries { get; } = [];

        public Dictionary<int, List<LoopOsrEntry>> LoopOsrGuardSites { get; } = [];

        public bool LoopOsrAnalyzed { get; set; } = !enableLoopOsr;

        public int LoopOsrObservationState = enableLoopOsr ? 0 : -1;

        public int LoopOsrRuntimeQualificationPendingCount;

        public LuaCompiledMethod? Method { get; set; }

        public LuaCompiledMethod? Tier1Method { get; set; }

        public LuaCompiledMethod? PlainTier1Method { get; set; }

        public LuaCompiledMethod? Tier2Method { get; set; }

        public LuaJitTier2Plan? Tier2Plan { get; set; }

        public LuaJitTier2Eligibility? Tier2Eligibility { get; set; }

        public bool Tier2EligibilityEvaluationInProgress { get; set; }

        public long NextTier2EligibilitySample { get; set; }

        public int NoNumericTier2EligibilityEvaluations { get; set; }

        public LuaJitFunctionEligibility? Eligibility { get; set; }

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

        public int Tier2ProfilingActive;

        public int CompilationAttempts { get; set; }

        public long RetryAfterTimestamp { get; set; }

        public int Tier2CompilationAttempts { get; set; }

        public long Tier2RetryAfterTimestamp { get; set; }

        public string? FailureCode { get; set; }

        public int LastFallbackTransition { get; set; } = -1;
    }

    private sealed class ModuleRouteCache(string moduleContentId, int functionCount)
    {
        private readonly FunctionRoute?[] _functions = new FunctionRoute[functionCount];

        public FunctionRoute GetFunctionRoute(int functionId)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(functionId);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                functionId,
                _functions.Length);
            if (Volatile.Read(ref _functions[functionId]) is { } cached)
            {
                return cached;
            }

            var route = new FunctionRoute(moduleContentId, functionId);
            return Interlocked.CompareExchange(
                ref _functions[functionId],
                route,
                null) ?? route;
        }
    }

    private sealed class FunctionRoute(string moduleContentId, int functionId)
    {
        public string ModuleContentId { get; } = moduleContentId;

        public int FunctionId { get; } = functionId;

        public FunctionEntry? Entry;

        public int TerminalInterpreterRoute;
    }

    private sealed record CompilationRequest(
        FunctionEntry Entry,
        LuaIrModule Module,
        long EnqueuedTimestamp,
        TaskCompletionSource<bool> Completion,
        LuaJitCompilationTier Tier,
        LuaJitFunctionProfile? Profile,
        LoopOsrEntry? LoopOsr);

    private sealed class LoopOsrEntry(
        LoopKey key,
        LuaJitLoopOsrPlan plan,
        LuaJitLoopOsrEligibility eligibility,
        bool enableManagedFallback)
    {
        public LoopKey Key { get; } = key;

        public LuaJitLoopOsrPlan Plan { get; set; } = plan;

        public LuaJitLoopOsrEligibility StructuralEligibility { get; } = eligibility;

        public LuaJitLoopOsrEligibility Eligibility { get; set; } =
            eligibility.IsAutoEligible && !enableManagedFallback
                ? eligibility with
                {
                    IsAutoEligible = false,
                    Reason = LuaJitLoopOsrEligibilityReason.AwaitingExactNumericProfile,
                }
                : eligibility;

        public LuaJitOsrState State { get; set; } =
            eligibility.IsAutoEligible || enableManagedFallback
                ? LuaJitOsrState.Profiling
                : LuaJitOsrState.Ineligible;

        public LuaCompiledMethod? Method { get; set; }

        public TaskCompletionSource<bool>? Completion { get; set; }

        public long EstimatedCodeBytes { get; set; }

        public long Backedges;

        public long GuardFailures;

        public HashSet<int> PendingExactNumericGuardSites { get; } = [];

        public int ExactNumericQualificationState { get; set; } =
            eligibility.IsAutoEligible && !enableManagedFallback ? 0 : 1;

        public bool PreferManagedFallback { get; set; }

        public int CompilationAttempts { get; set; }

        public long RetryAfterTimestamp { get; set; }
    }

    private sealed class FunctionEntryObservation
    {
        public Lock Gate { get; } = new();

        public int HasPendingLoop;

        public LoopKey? PendingLoop { get; set; }

        public FunctionEntry? Entry { get; set; }

        public FunctionRoute? Route { get; set; }
    }
}
