#if NET10_0_OR_GREATER
using Lunil.CodeGen.Cil.Jit;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.Hosting;

internal sealed class LuaHostCilJitBackend : ILuaHostJitBackend
{
    private readonly LuaJitExecutor _executor;

    public LuaHostCilJitBackend(
        LuaHostJitOptions options,
        LuaInterpreterOptions interpreter)
    {
        _executor = new LuaJitExecutor(new LuaJitExecutorOptions
        {
            Policy = (LuaJitPolicy)options.Policy,
            FunctionEntryThreshold = options.FunctionEntryThreshold,
            BackedgeThreshold = options.BackedgeThreshold,
            SynchronousCompilation = options.SynchronousCompilation,
            CompilationQueueCapacity = options.CompilationQueueCapacity,
            MaximumConcurrentCompilations = options.MaximumConcurrentCompilations,
            MaximumCompilationAttempts = options.MaximumCompilationAttempts,
            MaximumPolymorphicShapes = options.MaximumPolymorphicShapes,
            EnableTier2 = options.EnableTier2,
            EnableTier2ManagedFallback = options.EnableTier2ManagedFallback,
            Tier2InvocationThreshold = options.Tier2InvocationThreshold,
            Tier2BackedgeThreshold = options.Tier2BackedgeThreshold,
            MaximumTier2GuardFailures = options.MaximumTier2GuardFailures,
            EnableLoopOsr = options.EnableLoopOsr,
            EnableLoopOsrManagedFallback = options.EnableLoopOsrManagedFallback,
            LoopOsrBackedgeThreshold = options.LoopOsrBackedgeThreshold,
            MaximumLoopOsrGuardFailures = options.MaximumLoopOsrGuardFailures,
            CompilationRetryBackoff = options.CompilationRetryBackoff,
            MaximumCodeCacheBytes = options.MaximumCodeCacheBytes,
            Interpreter = interpreter,
        });
    }

    public LuaHostJitStatistics Statistics => Convert(_executor.Statistics);

    public LuaExecutionResult Execute(
        LuaState state,
        LuaClosure closure,
        ReadOnlySpan<LuaValue> arguments) =>
        _executor.Execute(state, closure, arguments);

    public LuaExecutionResult ExecuteBinaryChunk(
        LuaState state,
        ReadOnlySpan<byte> binaryChunk,
        ReadOnlySpan<LuaValue> arguments,
        Lua54ChunkReaderOptions? readerOptions) =>
        _executor.ExecuteBinaryChunk(state, binaryChunk, arguments, readerOptions);

    public LuaExecutionResult Start(
        LuaState state,
        LuaThread thread,
        long maximumInstructionCount,
        ReadOnlySpan<LuaValue> arguments) =>
        _executor.Start(state, thread, maximumInstructionCount, arguments);

    public LuaExecutionResult Resume(
        LuaState state,
        LuaThread thread,
        long maximumInstructionCount,
        ReadOnlySpan<LuaValue> arguments) =>
        _executor.Resume(state, thread, maximumInstructionCount, arguments);

    public LuaExecutionResult Close(LuaState state, LuaThread thread) =>
        _executor.Close(state, thread);

    public void Invalidate(LuaIrModule module) => _executor.Invalidate(module);

    public byte[] ExportProfile(LuaIrModule module) => _executor.ExportProfile(module);

    public LuaHostJitProfileRemapResult RemapProfile(
        LuaIrModule sourceModule,
        LuaIrModule targetModule,
        ReadOnlySpan<byte> payload)
    {
        var result = LuaJitProfileRemapper.Remap(sourceModule, targetModule, payload);
        return new LuaHostJitProfileRemapResult(
            result.Succeeded,
            result.Payload,
            result.RemappedFunctionCount,
            result.IncompatibleFunctionCount,
            result.AddedFunctionCount,
            result.RemovedFunctionCount,
            result.DiagnosticCode,
            result.Message);
    }

    public LuaHostJitProfileImportResult ImportProfile(
        LuaIrModule module,
        ReadOnlySpan<byte> payload)
    {
        var result = _executor.ImportProfile(module, payload);
        return new LuaHostJitProfileImportResult(
            (LuaHostJitProfileImportStatus)result.Status,
            result.DiagnosticCode,
            result.Message);
    }

    public LuaHostJitWarmupResult Warmup(
        LuaIrModule module,
        LuaHostJitWarmupOptions options,
        CancellationToken cancellationToken)
    {
        var result = _executor.Warmup(
            module,
            new LuaJitWarmupOptions
            {
                MaximumFunctions = options.MaximumFunctions,
                MaximumDuration = options.MaximumDuration,
                IncludeTier2 = options.IncludeTier2,
                ProfiledFunctionsOnly = options.ProfiledFunctionsOnly,
            },
            cancellationToken);
        return new LuaHostJitWarmupResult(
            (LuaHostJitWarmupStatus)result.Status,
            result.CandidateFunctionCount,
            result.SelectedFunctionCount,
            result.ReadyFunctionCount,
            result.IneligibleFunctionCount,
            result.FailedFunctionCount,
            result.SkippedFunctionCount,
            result.Duration,
            [.. result.Functions.Select(static function => new LuaHostJitWarmupFunctionResult(
                function.FunctionId,
                function.ProfileSamples,
                (LuaHostJitWarmupFunctionStatus)function.Status,
                (LuaHostJitCompilationTier)function.Tier,
                function.DiagnosticCode))]);
    }

    public void Dispose() => _executor.Dispose();

    private static LuaHostJitStatistics Convert(LuaJitStatistics value) => new(
        value.FunctionEntries,
        value.Backedges,
        value.CompilationQueued,
        value.CompilationStarted,
        value.CompilationCompleted,
        value.CompilationFailed,
        value.QueueRejected,
        value.CompiledInvocations,
        value.InterpreterFallbacks,
        value.Deoptimizations,
        value.CacheEvictions,
        value.Invalidations,
        value.EstimatedCodeBytes,
        value.TotalQueueLatencyTicks,
        value.TotalCompilationTicks,
        value.Tier2CompilationQueued,
        value.Tier2CompilationStarted,
        value.Tier2CompilationCompleted,
        value.Tier2CompilationFailed,
        value.Tier2Invocations,
        value.Tier2GuardFailures,
        value.Tier2Invalidations,
        value.LoopOsrRequests,
        value.LoopOsrCompilationQueued,
        value.LoopOsrCompilationStarted,
        value.LoopOsrCompilationCompleted,
        value.LoopOsrCompilationFailed,
        value.LoopOsrEntries,
        value.LoopOsrExits,
        value.LoopOsrGuardFailures,
        value.LoopOsrInvalidations,
        value.CompiledCanonicalInstructions,
        value.SchedulerExits,
        value.ContinueExits,
        value.PollExits,
        value.CallExits,
        value.TailCallExits,
        value.ReturnExits,
        value.InstructionBudgetPolls,
        value.GarbageCollectionPolls,
        value.DebugModeDeoptimizations,
        value.Tier1CompileAllocatedBytes,
        value.Tier1DirectCanonicalInstructions,
        value.Tier1SlowPathCanonicalInstructions,
        value.Tier1PlanInstructions,
        value.TotalCanonicalVerificationTicks,
        value.TotalControlFlowAnalysisTicks,
        value.TotalMethodPlanBuildTicks,
        value.TotalPlanVerificationTicks,
        value.TotalReflectionEmitTicks,
        value.TotalDelegateCreationTicks,
        value.EligibilityEvaluated,
        value.EligibilityAccepted,
        value.EligibilityRejected,
        value.Tier2EligibilityEvaluated,
        value.Tier2EligibilityAccepted,
        value.Tier2EligibilityRejected,
        value.LoopOsrEligibilityEvaluated,
        value.LoopOsrEligibilityAccepted,
        value.LoopOsrEligibilityRejected)
    {
        Tier2MethodEntries = value.Tier2MethodEntries,
        Tier2CompletedInvocations = value.Tier2CompletedInvocations,
        Tier2UnsupportedExits = value.Tier2UnsupportedExits,
        DirectCallEntries = value.DirectCallEntries,
        DirectCallCompletions = value.DirectCallCompletions,
        DirectCallFallbacks = value.DirectCallFallbacks,
        DirectCallInvalidations = value.DirectCallInvalidations,
        SchedulerExitsAvoided = value.SchedulerExitsAvoided,
        TablePicHits = value.TablePicHits,
        TablePicMisses = value.TablePicMisses,
        TablePicInvalidations = value.TablePicInvalidations,
    };
}
#endif
