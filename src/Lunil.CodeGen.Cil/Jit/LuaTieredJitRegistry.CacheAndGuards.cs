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
            entry.DirectCallMethod = null;
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

                var planFingerprint = CreateTier2GuardPlanFingerprint(
                    entry.Tier2Plan ?? throw new InvalidOperationException(
                        "A ready Tier 2 entry has no specialization plan."));
                released = entry.Tier2EstimatedCodeBytes;
                entry.Tier2Method = null;
                entry.DirectCallMethod = null;
                entry.Tier2Plan = null;
                entry.Tier2EstimatedCodeBytes = 0;
                entry.EstimatedCodeBytes = checked(
                    entry.Tier1EstimatedCodeBytes + GetLoopOsrCodeBytes(entry));
                entry.Method = entry.Tier1Method;
                entry.ActiveTier = LuaJitCompilationTier.Tier1;
                // Deopt feedback can legitimately change the next specialization plan. Bound
                // retries per invalidated plan rather than globally so progressive profiling can
                // discard unstable guards without allowing one unchanged plan to churn methods.
                var invalidations = entry.Tier2GuardInvalidationsByPlan.GetValueOrDefault(
                    planFingerprint);
                invalidations = invalidations == int.MaxValue
                    ? int.MaxValue
                    : invalidations + 1;
                entry.Tier2GuardInvalidationsByPlan[planFingerprint] = invalidations;
                var canReprofile = invalidations <= _options.MaximumCompilationAttempts;
                entry.Tier2State = canReprofile
                    ? LuaJitTier2State.Profiling
                    : LuaJitTier2State.Failed;
                entry.Tier2CompilationAttempts = canReprofile
                    ? 0
                    : _options.MaximumCompilationAttempts;
                entry.Tier2GuardFailures = 0;
                entry.CompletedTier1Invocations = 0;
                entry.Tier2Eligibility = null;
                entry.NextTier2EligibilitySample = 0;
                entry.NoNumericTier2EligibilityEvaluations = 0;
                if (canReprofile)
                {
                    Volatile.Write(ref entry.Tier2ProfilingActive, 1);
                }
                else
                {
                    DeactivateTier2ProfilingLocked(entry);
                }
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

    private static void RecordTier2GuardFailureProfile(
        FunctionEntry entry,
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int programCounter)
    {
        if (!ReferenceEquals(thread.CurrentFrame, frame) ||
            (uint)programCounter >= (uint)frame.Function.Instructions.Length)
        {
            return;
        }

        entry.Profile.Observe(
            context,
            thread,
            frame,
            programCounter,
            frame.Function.Instructions[programCounter],
            GetModuleContentId);
    }

    private static string CreateTier2GuardPlanFingerprint(LuaJitTier2Plan plan)
    {
        var builder = new StringBuilder();
        builder.Append((int)plan.CodeKind).Append('|')
            .Append(plan.NumericRegionCount).Append('|')
            .Append(plan.UnboxedNumericLocalCount).Append('|')
            .Append(plan.DirectNumericInstructionCount).Append('|');
        foreach (var optimization in plan.Optimizations)
        {
            builder.Append(optimization.ProgramCounter).Append(':')
                .Append((int)optimization.Kind).Append(':')
                .Append(optimization.CanonicalInstructionCount).Append(':')
                .Append(optimization.Guard.Length).Append(':')
                .Append(optimization.Guard).Append('|');
        }

        return builder.ToString();
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
                entry.DirectCallMethod = null;
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

    private void SweepInvalidatedEntries()
    {
        // Candidates are read without the per-entry gate and revalidated under it: only
        // tombstones (State == Invalidated) are eligible, oldest invalidation first.
        List<KeyValuePair<FunctionKey, FunctionEntry>>? candidates = null;
        foreach (var pair in _entries)
        {
            if (pair.Value.State == LuaJitFunctionState.Invalidated)
            {
                candidates ??= [];
                candidates.Add(pair);
            }
        }

        if (candidates is null)
        {
            return;
        }

        candidates.Sort(static (left, right) =>
            left.Value.InvalidatedStamp.CompareTo(right.Value.InvalidatedStamp));
        var removalTarget = _entries.Count - _maximumTrackedFunctionEntries;
        var removed = 0;
        foreach (var pair in candidates)
        {
            if (removed >= removalTarget)
            {
                break;
            }

            var entry = pair.Value;
            lock (entry.Gate)
            {
                if (entry.State != LuaJitFunctionState.Invalidated)
                {
                    continue;
                }

                if (_entries.TryGetValue(pair.Key, out var current) &&
                    ReferenceEquals(current, entry) &&
                    _entries.TryRemove(pair.Key, out _))
                {
                    removed++;
                }
            }
        }
    }

    private void InvalidateModule(string moduleContentId)
    {
        var generation = GetBackendGeneration(moduleContentId);
        generation.BeginInvalidation();
        var events = new List<LuaJitEvent>();
        try
        {
            lock (_cacheGate)
            {
                foreach (var pair in _entries.Where(pair => string.Equals(
                    pair.Key.ModuleContentId,
                    moduleContentId,
                    StringComparison.Ordinal)))
                {
                    var entry = pair.Value;
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
                        entry.DirectCallMethod = null;
                        entry.Tier2Plan = null;
                        entry.Tier1EstimatedCodeBytes = 0;
                        entry.Tier2EstimatedCodeBytes = 0;
                        entry.EstimatedCodeBytes = 0;
                        entry.ActiveTier = LuaJitCompilationTier.Interpreter;
                        entry.State = LuaJitFunctionState.Invalidated;
                        entry.InvalidatedStamp = ++_invalidationStamp;
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

                // Tombstones keep the observable Invalidated state (function-state queries,
                // terminal routes, and cross-invalidation publication guards all read it),
                // so they are retained but bounded: once the table exceeds its cap, the
                // oldest invalidated entries are reclaimed.
                if (_entries.Count > _maximumTrackedFunctionEntries)
                {
                    SweepInvalidatedEntries();
                }

                // Reclaim generations whose content id no longer has tracked entries, so
                // the generation table also stays bounded across hot reloads. In-flight
                // compiled delegates hold their generation object directly and are unaffected.
                var liveContentIds = _entries.Keys
                    .Select(static key => key.ModuleContentId)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var trackedContentId in _moduleGenerations.Keys)
                {
                    if (!liveContentIds.Contains(trackedContentId))
                    {
                        _ = _moduleGenerations.TryRemove(trackedContentId, out _);
                    }
                }
            }
        }
        finally
        {
            generation.CompleteInvalidation();
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
        frame.BackendBackedgeProbeCountdown = frame.InstructionRoute ==
            LuaFrameInstructionRoute.InterpreterWithBackedgeProbes
                ? GetBackedgeProbeCountdown(entry)
                : 1;
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
                    Volatile.Read(ref entry.LoopOsrObservationState) < 0;
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
            LuaJitTier2EligibilityReason.UnsupportedInstruction or
            LuaJitTier2EligibilityReason.InsufficientTier2Work or
            LuaJitTier2EligibilityReason.HotLoopCallBoundary;

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
        entry.Tier2GuardInvalidationsByPlan.Clear();
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

    private int GetBackedgeProbeCountdown(FunctionEntry entry)
    {
        var observedBackedges = Interlocked.Read(ref entry.Backedges);
        var remaining = long.MaxValue;
        lock (entry.Gate)
        {
            if (_options.Policy == LuaJitPolicy.Auto && entry.Eligibility is null)
            {
                remaining = _options.BackedgeThreshold - observedBackedges;
            }
        }

        var loopOsrObservationState = Volatile.Read(ref entry.LoopOsrObservationState);
        if (loopOsrObservationState == 0)
        {
            remaining = Math.Min(
                remaining,
                _options.LoopOsrBackedgeThreshold - observedBackedges);
        }
        else if (loopOsrObservationState > 0)
        {
            // Once concrete loop plans exist, each loop owns its own backedge counter. Keep
            // per-backedge routing for this short qualification/installation phase so backedges
            // from distinct natural loops are never attributed to the wrong plan.
            remaining = 1;
        }

        return remaining switch
        {
            <= 1 => 1,
            > int.MaxValue => int.MaxValue,
            _ => (int)remaining,
        };
    }

    private static void RejectLoopOsrWhenNoBackedge(
        FunctionEntry entry,
        LuaIrFunction function)
    {
        if (Volatile.Read(ref entry.LoopOsrObservationState) != 0 ||
            HasLoopOsrBackedge(function))
        {
            return;
        }

        lock (entry.Gate)
        {
            if (entry.LoopOsrAnalyzed)
            {
                return;
            }

            entry.LoopOsrAnalyzed = true;
            Volatile.Write(ref entry.LoopOsrObservationState, -1);
        }
    }

    private static bool HasLoopOsrBackedge(LuaIrFunction function)
    {
        for (var programCounter = 0;
             programCounter < function.Instructions.Length;
             programCounter++)
        {
            if (LuaLoopOsrAnalyzer.IsOsrBackedgeInstruction(
                    function.Instructions[programCounter],
                    programCounter))
            {
                return true;
            }
        }

        return false;
    }

    private void TryRejectLoopOsrStructurally(FunctionEntry entry, LuaIrModule module)
    {
        if (Volatile.Read(ref entry.LoopOsrObservationState) != 0 ||
            Volatile.Read(ref entry.LoopOsrStructuralRejectionPreflightState) != 0 ||
            Interlocked.Read(ref entry.FunctionEntries) < 4 ||
            Interlocked.Read(ref entry.Backedges) <
                Math.Min(_options.LoopOsrBackedgeThreshold, 64))
        {
            return;
        }

        if (Interlocked.CompareExchange(
                ref entry.LoopOsrStructuralRejectionPreflightState,
                1,
                0) != 0)
        {
            return;
        }

        try
        {
            var function = module.Functions[entry.Key.FunctionId];
            var plans = LuaLoopOsrAnalyzer.Analyze(module, entry.Key.FunctionId).ToArray();
            if (plans.Length == 0 || plans.Any(plan =>
                    LuaLoopOsrEligibilityEvaluator.Evaluate(function, plan).IsAutoEligible))
            {
                return;
            }

            EnsureLoopOsrEntries(entry, module);
        }
        finally
        {
            Volatile.Write(ref entry.LoopOsrStructuralRejectionPreflightState, -1);
        }
    }

    private int CommitPendingBackedges(FunctionEntry entry, LuaFrame frame)
    {
        var reportedBackedgeCount = frame.UnreportedBackendBackedgeCount;
        if (reportedBackedgeCount == 0)
        {
            return 0;
        }

        frame.UnreportedBackendBackedgeCount = 0;
        Interlocked.Add(ref entry.Backedges, reportedBackedgeCount);
        Interlocked.Add(ref _backedges, reportedBackedgeCount);
        return reportedBackedgeCount;
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
            LuaCodegenAbiV3.RuntimeAbiVersion,
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
}
