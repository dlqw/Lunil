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
    public LuaCompiledExit Execute(
        LuaExecutionEngine engine,
        LuaExecutionContext context,
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        in LuaIrInstruction instruction)
    {
        var module = frame.Module;
        var functionRoute = GetFunctionRoute(module, frame.Function.Id);
        var entry = GetOrCreateEntry(functionRoute, frame.Function.ParameterCount);
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
        TryRejectLoopOsrStructurally(entry, module);
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
        if (Volatile.Read(ref entry.LoopOsrObservationState) == 0)
        {
            RejectLoopOsrWhenNoBackedge(entry, frame.Function);
        }
        FunctionEntryObservation? frameObservation = null;
        if ((IsTier2Enabled && Volatile.Read(ref entry.Tier2ProfilingActive) != 0) ||
            Volatile.Read(ref entry.LoopOsrObservationState) >= 0)
        {
            frameObservation = _observedFrames.GetValue(
                frame,
                static _ => new FunctionEntryObservation());
            frameObservation.Bind(frame.Closure, entry, functionRoute);
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
                return InvokeCompiled(
                    engine,
                    state,
                    entry,
                    entryPoint.Value,
                    context,
                    thread,
                    frame);
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
                        return InvokeCompiled(
                            engine,
                            state,
                            entry,
                            entryPoint.Value,
                            context,
                            thread,
                            frame);
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

    bool ILuaDirectCallExecutor.TryExecuteDirectCall(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame caller,
        LuaCodegenCallSiteCache cache,
        int functionRegister,
        int expectedFunctionId,
        int argumentCount,
        int expectedResults)
    {
        if (Volatile.Read(ref _disposed) != 0 || context.HasExactDebugHooks ||
            !context.IsDebugModeCurrent() || !ReferenceEquals(context.Thread, thread) ||
            !ReferenceEquals(thread.CurrentFrame, caller) || thread.UnwindState is not null ||
            thread.IsClosing || caller.Continuation.Kind != LuaContinuationKind.None ||
            context.Scheduler?.Transfer != LuaSchedulerTransfer.None ||
            context.State.IsRunningFinalizer)
        {
            return false;
        }

        var closure = LuaCodegenAbiV2.ReadRegisterUnchecked(
            thread,
            caller,
            functionRegister).TryGetClosure();
        if (closure is null || closure.Function.Id != expectedFunctionId ||
            !ReferenceEquals(closure.Module, caller.Module) || closure.Function.IsVarArg ||
            closure.Function.Upvalues.Length != 0)
        {
            return false;
        }

        var functionVersion = closure.FunctionVersion;
        FunctionEntry entry;
        if (cache.TryGetDirectBackendEntry(functionVersion.Generation, out var cached) &&
            cached is FunctionEntry cachedEntry &&
            cachedEntry.Key.FunctionId == expectedFunctionId)
        {
            entry = cachedEntry;
        }
        else
        {
            var route = GetFunctionRoute(closure.Module, expectedFunctionId);
            entry = GetOrCreateEntry(route, closure.Function.ParameterCount);
            cache.SetDirectBackendEntry(entry, functionVersion.Generation);
        }

        LuaDirectCompiledMethod directMethod;
        long installedGeneration;
        lock (entry.Gate)
        {
            if (entry.State != LuaJitFunctionState.Ready ||
                entry.ActiveTier != LuaJitCompilationTier.Tier2 ||
                entry.DirectCallMethod is null)
            {
                return false;
            }

            directMethod = entry.DirectCallMethod;
            installedGeneration = entry.InstalledGeneration;
        }

        var backendGeneration = GetBackendGeneration(entry.Key.ModuleContentId);
        if (!context.OwnsBackendGeneration(backendGeneration, installedGeneration))
        {
            Interlocked.Increment(ref _directCallInvalidations);
            Interlocked.Increment(ref _directCallFallbacks);
            return false;
        }

        // Qualified direct leaves cannot allocate or mutate heap state. Reuse the compiled
        // caller's bounded backedge safepoint quantum instead of polling once per leaf call;
        // already queued finalizers still force frame materialization immediately.
        if (context.State.Heap.PendingFinalizerCount != 0)
        {
            Interlocked.Increment(ref _directCallFallbacks);
            return false;
        }

        Interlocked.Increment(ref _directCallEntries);
        if (!directMethod(
                context,
                thread,
                caller,
                functionRegister,
                argumentCount,
                expectedResults))
        {
            Interlocked.Increment(ref _directCallFallbacks);
            if (!context.OwnsBackendGeneration(backendGeneration, installedGeneration))
            {
                Interlocked.Increment(ref _directCallInvalidations);
            }

            return false;
        }

        Interlocked.Increment(ref _directCallCompletions);
        Interlocked.Add(ref _schedulerExitsAvoided, 2);
        return true;
    }

    public void ObserveInstruction(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int programCounter,
        in LuaIrInstruction instruction)
    {
        if (_options.Policy == LuaJitPolicy.InterpreterOnly ||
            Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        FunctionEntry entry;
        FunctionRoute functionRoute;
        if (!_observedFrames.TryGetValue(frame, out var frameObservation) ||
            !frameObservation.IsBoundTo(frame.Closure) ||
            frameObservation.Entry is not { } observedEntry ||
            frameObservation.Route is not { } observedRoute)
        {
            functionRoute = GetFunctionRoute(
                frame.Module,
                frame.Function.Id);
            entry = GetOrCreateEntry(
                functionRoute,
                frame.Function.ParameterCount);
            frameObservation = _observedFrames.GetValue(
                frame,
                static _ => new FunctionEntryObservation());
            frameObservation.Bind(frame.Closure, entry, functionRoute);
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

    public void ObserveLoopOsrBackedges(
        LuaFrame frame,
        int programCounter,
        int backedgeCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backedgeCount);
        if (_observedFrames.TryGetValue(frame, out var observation) &&
            observation.Entry is { } entry)
        {
            Interlocked.Add(ref entry.Backedges, backedgeCount);
            Interlocked.Add(ref _backedges, backedgeCount);
        }
    }

    internal int TrackedEntryCount => _entries.Count;

    internal int TrackedModuleGenerationCount => _moduleGenerations.Count;

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
        DirectCallEntries = checked(
            Interlocked.Read(ref _directCallEntries) + _boundDirectCallCounters.Entries),
        DirectCallCompletions = checked(
            Interlocked.Read(ref _directCallCompletions) +
            _boundDirectCallCounters.Completions),
        DirectCallFallbacks = checked(
            Interlocked.Read(ref _directCallFallbacks) + _boundDirectCallCounters.Fallbacks),
        DirectCallInvalidations = checked(
            Interlocked.Read(ref _directCallInvalidations) +
            _boundDirectCallCounters.Invalidations),
        SchedulerExitsAvoided = checked(
            Interlocked.Read(ref _schedulerExitsAvoided) +
            _boundDirectCallCounters.SchedulerExitsAvoided),
        TablePicHits = _tablePicCounters.Hits,
        TablePicMisses = _tablePicCounters.Misses,
        TablePicInvalidations = _tablePicCounters.Invalidations,
    };
}
