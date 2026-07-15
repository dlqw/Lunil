using Lunil.Runtime.Execution;

namespace Lunil.CodeGen.Cil.Jit;

public enum LuaJitPolicy : byte
{
    InterpreterOnly,
    Auto,
    PreferJit,
    RequireJit,
}

public enum LuaJitFunctionState : byte
{
    Cold,
    Queued,
    Compiling,
    Ready,
    Failed,
    Invalidated,
}

public enum LuaJitCompilationTier : byte
{
    Interpreter,
    Tier1,
    Tier2,
    LoopOsr,
}

public enum LuaJitTier2State : byte
{
    Disabled,
    Profiling,
    Queued,
    Compiling,
    Ready,
    Failed,
    Invalidated,
    Ineligible,
}

public enum LuaJitTier2EligibilityReason : byte
{
    Eligible,
    NoNumericHotspot,
    PolymorphicNumericProfile,
    ManagedOptimizationRequired,
    ManagedSemanticBoundary,
    UnsupportedInstruction,
}

public static class LuaJitTier2DiagnosticCodes
{
    public const string NoNumericHotspot = "JIT2101";
    public const string PolymorphicNumericProfile = "JIT2102";
    public const string ManagedOptimizationRequired = "JIT2103";
    public const string UnexpectedCodeKind = "JIT2104";
    public const string ManagedSemanticBoundary = "JIT2105";
    public const string UnsupportedInstruction = "JIT2106";
}

public enum LuaJitOsrState : byte
{
    Disabled,
    Ineligible,
    Profiling,
    Queued,
    Compiling,
    Ready,
    Failed,
    Invalidated,
}

public enum LuaJitLoopOsrCodeKind : byte
{
    ManagedCanonicalProgram,
    GuardedExactNumericCil,
}

public enum LuaJitLoopOsrEligibilityReason : byte
{
    Eligible,
    NoNumericHotspot,
    ManagedSemanticBoundary,
    UnsupportedInstruction,
    AwaitingExactNumericProfile,
    NonExactNumericProfile,
}

public static class LuaJitLoopOsrDiagnosticCodes
{
    public const string NoNumericHotspot = "JIT3101";
    public const string ManagedSemanticBoundary = "JIT3102";
    public const string UnsupportedInstruction = "JIT3103";
    public const string UnexpectedCodeKind = "JIT3104";
    public const string NonExactNumericProfile = "JIT3105";
}

public enum LuaJitEventKind : byte
{
    Queued,
    CompilationStarted,
    CompilationCompleted,
    CompilationFailed,
    Fallback,
    Deoptimized,
    Evicted,
    Invalidated,
    Tier2Queued,
    Tier2CompilationStarted,
    Tier2CompilationCompleted,
    Tier2CompilationFailed,
    Tier2GuardFailed,
    Tier2Invalidated,
    LoopOsrQueued,
    LoopOsrCompilationStarted,
    LoopOsrCompilationCompleted,
    LoopOsrCompilationFailed,
    LoopOsrEntered,
    LoopOsrExited,
    LoopOsrGuardFailed,
    LoopOsrInvalidated,
    EligibilityAccepted,
    EligibilityRejected,
    Tier2EligibilityAccepted,
    Tier2EligibilityRejected,
    LoopOsrEligibilityAccepted,
    LoopOsrEligibilityRejected,
    LoopOsrCompilerPrepared,
}

public sealed record LuaJitExecutorOptions
{
    public static LuaJitExecutorOptions Default { get; } = new();

    /// <summary>
    /// Selects the dynamic compilation policy. The release default enables qualified Tier 1
    /// compilation while retaining deterministic eligibility checks and interpreter fallback.
    /// </summary>
    public LuaJitPolicy Policy { get; init; } = LuaJitPolicy.Auto;

    public int FunctionEntryThreshold { get; init; } = 32;

    public int BackedgeThreshold { get; init; } = 64;

    public bool SynchronousCompilation { get; init; }

    public int CompilationQueueCapacity { get; init; } = 1_024;

    public int MaximumConcurrentCompilations { get; init; } = 1;

    public int MaximumCompilationAttempts { get; init; } = 1;

    public int MaximumPolymorphicShapes { get; init; } = 4;

    /// <summary>
    /// Enables profile-guided Tier 2 promotion. The release default admits only profiles that
    /// deterministically produce exact-numeric specialized CIL and retains Tier 1 otherwise.
    /// </summary>
    public bool EnableTier2 { get; init; } = true;

    /// <summary>
    /// Allows Tier 2 profiles that require the managed profile-program fallback. This path remains
    /// an explicit opt-in because table, call, metamethod, and coroutine workloads have not passed
    /// the default-rollout performance gates.
    /// </summary>
    public bool EnableTier2ManagedFallback { get; init; }

    public int Tier2InvocationThreshold { get; init; } = 128;

    public int Tier2BackedgeThreshold { get; init; } = 1_024;

    public int MaximumTier2GuardFailures { get; init; } = 16;

    /// <summary>
    /// Enables qualified loop on-stack replacement. The release default admits only verified loops
    /// that pass exact-numeric runtime qualification and produce guarded specialized CIL.
    /// </summary>
    public bool EnableLoopOsr { get; init; } = true;

    /// <summary>
    /// Allows natural loops that cannot produce guarded exact-numeric CIL to use the managed
    /// canonical loop program. This experimental path remains an explicit opt-in.
    /// </summary>
    public bool EnableLoopOsrManagedFallback { get; init; }

    public int LoopOsrBackedgeThreshold { get; init; } = 1_024;

    public int MaximumLoopOsrGuardFailures { get; init; } = 16;

    public TimeSpan CompilationRetryBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public long MaximumCodeCacheBytes { get; init; } = 64 * 1024 * 1024;

    public LuaInterpreterOptions Interpreter { get; init; } = LuaInterpreterOptions.Default;
}

/// <summary>Process-local counters for the tiered execution registry.</summary>
/// <param name="Tier2Invocations">
/// Compatibility alias for <see cref="LuaJitStatistics.Tier2MethodEntries"/>. It counts generated method
/// entries, not completed Lua invocations or canonical loop iterations.
/// </param>
public sealed record LuaJitStatistics(
    long FunctionEntries,
    long Backedges,
    long CompilationQueued,
    long CompilationStarted,
    long CompilationCompleted,
    long CompilationFailed,
    long QueueRejected,
    long CompiledInvocations,
    long InterpreterFallbacks,
    long Deoptimizations,
    long CacheEvictions,
    long Invalidations,
    long EstimatedCodeBytes,
    long TotalQueueLatencyTicks,
    long TotalCompilationTicks,
    long Tier2CompilationQueued,
    long Tier2CompilationStarted,
    long Tier2CompilationCompleted,
    long Tier2CompilationFailed,
    long Tier2Invocations,
    long Tier2GuardFailures,
    long Tier2Invalidations,
    long LoopOsrRequests,
    long LoopOsrCompilationQueued,
    long LoopOsrCompilationStarted,
    long LoopOsrCompilationCompleted,
    long LoopOsrCompilationFailed,
    long LoopOsrEntries,
    long LoopOsrExits,
    long LoopOsrGuardFailures,
    long LoopOsrInvalidations,
    long CompiledCanonicalInstructions,
    long SchedulerExits,
    long ContinueExits,
    long PollExits,
    long CallExits,
    long TailCallExits,
    long ReturnExits,
    long InstructionBudgetPolls,
    long GarbageCollectionPolls,
    long DebugModeDeoptimizations,
    long Tier1CompileAllocatedBytes,
    long Tier1DirectCanonicalInstructions,
    long Tier1SlowPathCanonicalInstructions,
    long Tier1PlanInstructions,
    long TotalCanonicalVerificationTicks,
    long TotalControlFlowAnalysisTicks,
    long TotalMethodPlanBuildTicks,
    long TotalPlanVerificationTicks,
    long TotalReflectionEmitTicks,
    long TotalDelegateCreationTicks,
    long EligibilityEvaluated,
    long EligibilityAccepted,
    long EligibilityRejected,
    long Tier2EligibilityEvaluated,
    long Tier2EligibilityAccepted,
    long Tier2EligibilityRejected,
    long LoopOsrEligibilityEvaluated,
    long LoopOsrEligibilityAccepted,
    long LoopOsrEligibilityRejected)
{
    /// <summary>Number of times a Tier 2 delegate was entered.</summary>
    public long Tier2MethodEntries { get; init; }

    /// <summary>Tier 2 entries that completed the current Lua invocation.</summary>
    public long Tier2CompletedInvocations { get; init; }

    /// <summary>Defensive exits attributed to unsupported Tier 2 coverage.</summary>
    public long Tier2UnsupportedExits { get; init; }
}

public sealed record LuaJitTier2Eligibility(
    bool IsAutoEligible,
    LuaJitTier2EligibilityReason Reason,
    string? DiagnosticCode,
    long ProfileSamples,
    int OptimizationCount,
    int NumericOptimizationCount,
    LuaJitTier2CodeKind ExpectedCodeKind);

public sealed record LuaJitLoopOsrEligibility(
    bool IsAutoEligible,
    LuaJitLoopOsrEligibilityReason Reason,
    string? DiagnosticCode,
    int LoopInstructionCount,
    int SpecializedInstructionCount,
    int GuardCount,
    LuaJitLoopOsrCodeKind ExpectedCodeKind);

public readonly record struct LuaJitCompilationMetrics(
    TimeSpan CanonicalVerificationDuration,
    TimeSpan ControlFlowAnalysisDuration,
    TimeSpan MethodPlanBuildDuration,
    TimeSpan PlanVerificationDuration,
    TimeSpan ReflectionEmitDuration,
    TimeSpan DelegateCreationDuration,
    long AllocatedBytes,
    int CanonicalInstructionCount,
    int DirectCanonicalInstructionCount,
    int SlowPathCanonicalInstructionCount,
    int PlanInstructionCount,
    long EstimatedCodeBytes);

public readonly record struct LuaJitLoopOsrCompilationMetrics(
    TimeSpan CanonicalVerificationDuration,
    TimeSpan LoopAnalysisDuration,
    bool LivenessCacheHit,
    TimeSpan SpecializationPlanningDuration,
    TimeSpan CilEmissionDuration,
    TimeSpan DelegateCreationDuration,
    long AllocatedBytes,
    LuaJitLoopOsrCodeKind CodeKind,
    int LoopInstructionCount,
    int SpecializedInstructionCount,
    int GuardCount,
    long EstimatedCodeBytes);

public readonly record struct LuaJitTier2CompilationMetrics(
    TimeSpan CanonicalVerificationDuration,
    TimeSpan LivenessAnalysisDuration,
    bool LivenessCacheHit,
    TimeSpan OptimizationPlanningDuration,
    TimeSpan CilEmissionDuration,
    TimeSpan DelegateCreationDuration,
    long AllocatedBytes,
    LuaJitTier2CodeKind CodeKind,
    int OptimizationCount,
    int SpecializedOptimizationCount,
    int DeoptSiteCount,
    long EstimatedCodeBytes);

public sealed record LuaJitEvent(
    LuaJitEventKind Kind,
    string ModuleContentId,
    int FunctionId,
    LuaJitFunctionState State,
    long EstimatedCodeBytes = 0,
    TimeSpan Duration = default,
    string? DiagnosticCode = null,
    LuaJitCompilationTier Tier = LuaJitCompilationTier.Tier1,
    LuaJitCompilationMetrics? CompilationMetrics = null,
    LuaJitFunctionEligibility? Eligibility = null,
    LuaJitTier2CompilationMetrics? Tier2CompilationMetrics = null,
    LuaJitTier2Eligibility? Tier2Eligibility = null,
    LuaJitLoopOsrCompilationMetrics? LoopOsrCompilationMetrics = null,
    LuaJitLoopOsrEligibility? LoopOsrEligibility = null);

public sealed class LuaJitException : Exception
{
    public LuaJitException(string diagnosticCode, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrEmpty(diagnosticCode);
        DiagnosticCode = diagnosticCode;
    }

    public string DiagnosticCode { get; }
}
