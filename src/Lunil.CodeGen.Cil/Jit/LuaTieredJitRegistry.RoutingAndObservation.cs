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
    private static CompiledEntryPoint? ReadReadyMethod(FunctionEntry entry)
    {
        lock (entry.Gate)
        {
            return entry.State == LuaJitFunctionState.Ready && entry.Method is not null
                ? new CompiledEntryPoint(
                    entry.Method,
                    entry.ActiveTier,
                    entry.InstalledGeneration)
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
            LuaCodegenAbiV3.RuntimeAbiVersion,
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

    private LuaBackendGeneration GetBackendGeneration(string moduleContentId) =>
        _moduleGenerations.GetOrAdd(
            moduleContentId,
            static _ => new LuaBackendGeneration());

    private static LuaJitFunctionState ReadState(FunctionEntry entry)
    {
        lock (entry.Gate)
        {
            return entry.State;
        }
    }

    private LuaCompiledExit InvokeCompiled(
        LuaExecutionEngine engine,
        LuaState state,
        FunctionEntry entry,
        CompiledEntryPoint entryPoint,
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame)
    {
        var scheduler = context.Scheduler ?? throw new InvalidOperationException(
            "A compiled runtime entry requires the shared scheduler context.");
        var currentEntry = entry;
        var currentEntryPoint = entryPoint;
        var currentFrame = frame;
        var directCallDepth = 0;
        var directChainStarted = false;

        while (true)
        {
            context.SetExitFrame(currentFrame);
            var generation = GetBackendGeneration(currentEntry.Key.ModuleContentId);
            if (!context.TryEnterBackendGeneration(generation, currentEntryPoint.Generation))
            {
                if (directChainStarted)
                {
                    Interlocked.Increment(ref _directCallInvalidations);
                    Interlocked.Increment(ref _directCallFallbacks);
                }

                var invalidated = LuaCompiledExit.Deopt(
                    currentFrame.ProgramCounter,
                    context.InstructionsConsumed,
                    LuaCompiledExitReason.BackendInvalidated);
                RecordSchedulerExit(
                    currentEntry,
                    currentEntryPoint,
                    context,
                    thread,
                    currentFrame,
                    invalidated);
                return invalidated;
            }

            var frameCountBefore = thread.FrameCount;
            var consumedBefore = context.InstructionsConsumed;
            Interlocked.Increment(ref _compiledInvocations);
            if (currentEntryPoint.Tier == LuaJitCompilationTier.Tier2)
            {
                Interlocked.Increment(ref _tier2Invocations);
            }
            else
            {
                Interlocked.Increment(ref currentEntry.Tier1Invocations);
            }
            Interlocked.Exchange(
                ref currentEntry.LastAccessStamp,
                Interlocked.Increment(ref _accessStamp));

            LuaCompiledExit exit;
            try
            {
                exit = currentEntryPoint.Method(context, thread, currentFrame);
            }
            finally
            {
                context.ExitBackendGeneration();
            }

            RecordCompiledInvocation(
                currentEntry,
                currentEntryPoint,
                exit,
                checked(exit.InstructionsConsumed - consumedBefore));

            if (TryGetDirectCallee(
                    state,
                    context,
                    thread,
                    currentFrame,
                    frameCountBefore,
                    exit,
                    out var calleeFrame,
                    out var calleeEntry,
                    out var calleeEntryPoint))
            {
                // Preserve the logical scheduler boundary before entering the callee. Pending
                // finalizers remain on the shared path so their protected frame semantics stay
                // centralized in LuaExecutionEngine.
                state.Heap.SafePoint();
                if (state.Heap.PendingFinalizerCount != 0)
                {
                    Interlocked.Increment(ref _directCallFallbacks);
                    RecordSchedulerExit(
                        currentEntry,
                        currentEntryPoint,
                        context,
                        thread,
                        currentFrame,
                        exit);
                    return exit;
                }

                LuaCodegenAbiV1.CommitProgramCounter(currentFrame, exit.ProgramCounter);
                Interlocked.Increment(ref _directCallEntries);
                Interlocked.Increment(ref _schedulerExitsAvoided);
                directCallDepth++;
                directChainStarted = true;
                currentFrame = calleeFrame;
                currentEntry = calleeEntry;
                currentEntryPoint = calleeEntryPoint;
                continue;
            }

            if (directCallDepth > 0 &&
                exit.Kind == LuaCompiledExitKind.Return &&
                ReferenceEquals(thread.CurrentFrame, currentFrame) &&
                (uint)exit.ProgramCounter < (uint)currentFrame.Function.Instructions.Length &&
                currentFrame.Function.Instructions[exit.ProgramCounter].Opcode ==
                    LuaIrOpcode.Return)
            {
                LuaCodegenAbiV1.CommitProgramCounter(currentFrame, exit.ProgramCounter);
                var returnInstruction = currentFrame.Function.Instructions[exit.ProgramCounter];
                var result = engine.ExecuteReturn(
                    state,
                    scheduler,
                    thread,
                    currentFrame,
                    returnInstruction);
                if (result is not null || thread.FrameCount == 0)
                {
                    throw new InvalidOperationException(
                        "A direct compiled leaf escaped its owning caller frame.");
                }

                directCallDepth--;
                Interlocked.Increment(ref _directCallCompletions);
                Interlocked.Increment(ref _schedulerExitsAvoided);
                thread.AdvanceFramePoolEpoch();
                state.Heap.SafePoint();

                var callerFrame = thread.CurrentFrame;
                if (state.Heap.PendingFinalizerCount != 0 ||
                    scheduler.Transfer != LuaSchedulerTransfer.None ||
                    !context.IsDebugModeCurrent() ||
                    !TryGetReadyEntry(callerFrame, out var callerEntry, out var callerEntryPoint))
                {
                    var schedulerExit = LuaCompiledExit.Continue(
                        callerFrame.ProgramCounter,
                        context.InstructionsConsumed);
                    context.SetExitFrame(callerFrame);
                    RecordSchedulerExit(
                        currentEntry,
                        currentEntryPoint,
                        context,
                        thread,
                        callerFrame,
                        schedulerExit);
                    return schedulerExit;
                }

                currentFrame = callerFrame;
                currentEntry = callerEntry;
                currentEntryPoint = callerEntryPoint;
                continue;
            }

            if (directCallDepth > 0)
            {
                Interlocked.Increment(ref _directCallFallbacks);
            }
            context.SetExitFrame(currentFrame);
            RecordSchedulerExit(
                currentEntry,
                currentEntryPoint,
                context,
                thread,
                currentFrame,
                exit);
            return exit;
        }
    }

    private bool TryGetDirectCallee(
        LuaState state,
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame callerFrame,
        int frameCountBefore,
        LuaCompiledExit exit,
        out LuaFrame calleeFrame,
        out FunctionEntry calleeEntry,
        out CompiledEntryPoint calleeEntryPoint)
    {
        calleeFrame = null!;
        calleeEntry = null!;
        calleeEntryPoint = default;
        if (exit.Kind != LuaCompiledExitKind.Continue ||
            context.HasExactDebugHooks ||
            !context.IsDebugModeCurrent() ||
            state.IsRunningFinalizer ||
            thread.UnwindState is not null ||
            schedulerHasTransfer(context) ||
            thread.FrameCount != frameCountBefore + 1 ||
            ReferenceEquals(thread.CurrentFrame, callerFrame) ||
            callerFrame.ProgramCounter != exit.ProgramCounter ||
            exit.ProgramCounter <= 0)
        {
            return false;
        }

        var callInstruction = callerFrame.Function.Instructions[exit.ProgramCounter - 1];
        if (callInstruction.Opcode != LuaIrOpcode.Call ||
            callInstruction.B < 0 ||
            callInstruction.C < 0 ||
            callerFrame.Continuation.Kind != LuaContinuationKind.None)
        {
            return false;
        }

        calleeFrame = thread.CurrentFrame;
        if (calleeFrame.ProgramCounter != 0 || calleeFrame.IsDebugHook || calleeFrame.IsHidden ||
            calleeFrame.PendingDebugHookEvent is not null ||
            calleeFrame.Continuation.Kind != LuaContinuationKind.None ||
            calleeFrame.ToBeClosedSlots.Count != 0 ||
            !ReferenceEquals(calleeFrame.FunctionVersion, calleeFrame.Closure.FunctionVersion))
        {
            return false;
        }

        var route = GetFunctionRoute(calleeFrame.Module, calleeFrame.Function.Id);
        calleeEntry = GetOrCreateEntry(route, calleeFrame.Function.ParameterCount);
        if (!TryReadDirectLeafEntry(calleeEntry, calleeFrame.Function, out calleeEntryPoint))
        {
            return false;
        }

        ObserveHotness(
            calleeEntry,
            calleeFrame,
            programCounter: 0,
            calleeFrame.Function.Instructions[0]);
        calleeFrame.InstructionRoute = LuaFrameInstructionRoute.Backend;
        return true;

        static bool schedulerHasTransfer(LuaExecutionContext executionContext) =>
            executionContext.Scheduler?.Transfer != LuaSchedulerTransfer.None;
    }

    private static bool TryReadDirectLeafEntry(
        FunctionEntry entry,
        LuaIrFunction function,
        out CompiledEntryPoint entryPoint)
    {
        lock (entry.Gate)
        {
            if (entry.State != LuaJitFunctionState.Ready ||
                entry.ActiveTier != LuaJitCompilationTier.Tier2 ||
                entry.Method is null ||
                entry.Tier2Plan is null)
            {
                entryPoint = default;
                return false;
            }

            var eligibility = Volatile.Read(ref entry.DirectCallEligibility);
            if (eligibility == 0)
            {
                eligibility = IsDirectLeafEligible(function, entry.Tier2Plan) ? 1 : -1;
                Volatile.Write(ref entry.DirectCallEligibility, eligibility);
            }
            if (eligibility < 0)
            {
                entryPoint = default;
                return false;
            }

            entryPoint = new CompiledEntryPoint(
                entry.Method,
                entry.ActiveTier,
                entry.InstalledGeneration);
            return true;
        }
    }

    private static bool IsDirectLeafEligible(
        LuaIrFunction function,
        LuaJitTier2Plan plan)
    {
        const int maximumInstructions = 512;
        if (function.IsVarArg || function.Upvalues.Length != 0 ||
            function.Instructions.Length is 0 or > maximumInstructions)
        {
            return false;
        }

        var optimizedNumericSites = plan.Optimizations
            .Where(static optimization => optimization.Kind is
                LuaJitOptimizationKind.NumericUnary or LuaJitOptimizationKind.NumericBinary)
            .Select(static optimization => optimization.ProgramCounter)
            .ToHashSet();
        for (var programCounter = 0;
             programCounter < function.Instructions.Length;
             programCounter++)
        {
            var instruction = function.Instructions[programCounter];
            switch (instruction.Opcode)
            {
                case LuaIrOpcode.LoadConstant:
                case LuaIrOpcode.LoadNil:
                case LuaIrOpcode.Move:
                case LuaIrOpcode.SetTop:
                case LuaIrOpcode.Jump:
                case LuaIrOpcode.JumpIfFalse:
                case LuaIrOpcode.JumpIfTrue:
                case LuaIrOpcode.NumericForPrepare:
                case LuaIrOpcode.NumericForLoop:
                    break;
                case LuaIrOpcode.Unary:
                case LuaIrOpcode.Binary:
                    if (!optimizedNumericSites.Contains(programCounter))
                    {
                        return false;
                    }

                    break;
                case LuaIrOpcode.Return when instruction.B >= 0:
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private bool TryGetReadyEntry(
        LuaFrame frame,
        out FunctionEntry entry,
        out CompiledEntryPoint entryPoint)
    {
        var route = GetFunctionRoute(frame.Module, frame.Function.Id);
        entry = GetOrCreateEntry(route, frame.Function.ParameterCount);
        if (ReadReadyMethod(entry) is not { } ready)
        {
            entryPoint = default;
            return false;
        }

        frame.InstructionRoute = LuaFrameInstructionRoute.Backend;
        entryPoint = ready;
        return true;
    }

    private void RecordCompiledInvocation(
        FunctionEntry entry,
        CompiledEntryPoint entryPoint,
        LuaCompiledExit exit,
        long instructionsConsumed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(instructionsConsumed);
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

        Interlocked.Add(ref _compiledCanonicalInstructions, instructionsConsumed);
    }

    private void RecordSchedulerExit(
        FunctionEntry entry,
        CompiledEntryPoint entryPoint,
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        LuaCompiledExit exit)
    {
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

        if (exit.Kind != LuaCompiledExitKind.Deopt)
        {
            return;
        }

        Interlocked.Increment(ref _deoptimizations);
        if (entryPoint.Tier == LuaJitCompilationTier.Tier2 &&
            exit.Reason == LuaCompiledExitReason.GuardFailure)
        {
            RecordTier2GuardFailureProfile(
                entry,
                context,
                thread,
                frame,
                exit.ProgramCounter);
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

        var reportedBackedgeCount = CommitPendingBackedges(entry, frame);
        if (reportedBackedgeCount == 0 &&
            LuaInstructionRouting.IsBackedge(programCounter, instruction))
        {
            reportedBackedgeCount = 1;
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

        EnsureLoopOsrEntries(entry, frame.Module);
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
        var generation = GetBackendGeneration(entry.Key.ModuleContentId);
        var installedGeneration = Volatile.Read(ref entry.InstalledGeneration);
        if (!context.TryEnterBackendGeneration(generation, installedGeneration))
        {
            return LuaCompiledExit.Deopt(
                frame.ProgramCounter,
                instructionsConsumed: 0,
                LuaCompiledExitReason.BackendInvalidated);
        }

        LuaCompiledExit exit;
        try
        {
            exit = method(context, thread, frame);
        }
        finally
        {
            context.ExitBackendGeneration();
        }
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

        var numericTypes = LuaLoopOsrRuntimeEligibilityEvaluator.CaptureExactNumericTypes(
            thread,
            frame,
            programCounter,
            instruction);
        var exactNumeric = !numericTypes.IsDefault;
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

                foreach (var type in numericTypes)
                {
                    var key = (type.ProgramCounter, type.Register);
                    if (loop.ExactNumericTypes.TryGetValue(key, out var existing))
                    {
                        loop.ExactNumericTypes[key] = existing | type.Kinds;
                    }
                    else
                    {
                        loop.ExactNumericTypes.Add(key, type.Kinds);
                    }
                }

                loop.PendingExactNumericGuardSites.Remove(programCounter);
                if (loop.PendingExactNumericGuardSites.Count != 0)
                {
                    continue;
                }

                loop.ExactNumericQualificationState = 1;
                loop.Plan = loop.Plan with
                {
                    NumericTypes =
                    [
                        .. loop.ExactNumericTypes
                            .OrderBy(static pair => pair.Key.ProgramCounter)
                            .ThenBy(static pair => pair.Key.Register)
                            .Select(static pair => new LuaJitOsrRegisterTypeProfile(
                                pair.Key.ProgramCounter,
                                pair.Key.Register,
                                pair.Value)),
                    ],
                };
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
                Interlocked.Read(ref entry.CompletedTier1Invocations) > 0 &&
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
        bool compileSynchronously,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            using var eligibilityCancellation = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(
                    _disposeCancellation.Token,
                    cancellationToken)
                : null;
            try
            {
                eligibility = ProfileGuidedLuaTier2Compiler.EvaluateAutoPromotionEligibility(
                    module,
                    entry.Key.FunctionId,
                    profile,
                    eligibilityCancellation?.Token ?? _disposeCancellation.Token);
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
                null,
                cancellationToken);
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
        bool compileSynchronously,
        CancellationToken cancellationToken = default)
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
                null,
                cancellationToken);
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
}
