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
}

public enum LuaJitOsrState : byte
{
    Disabled,
    Profiling,
    Queued,
    Compiling,
    Ready,
    Failed,
    Invalidated,
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
    /// Enables experimental profile-guided Tier 2 promotion. Tier 2 remains opt-in until its
    /// cross-platform throughput and allocation gates pass independently from Tier 1.
    /// </summary>
    public bool EnableTier2 { get; init; }

    public int Tier2InvocationThreshold { get; init; } = 128;

    public int Tier2BackedgeThreshold { get; init; } = 1_024;

    public int MaximumTier2GuardFailures { get; init; } = 16;

    /// <summary>
    /// Enables experimental loop on-stack replacement. It remains disabled by default until
    /// representative steady-state benchmarks show at least a ten percent improvement.
    /// </summary>
    public bool EnableLoopOsr { get; init; }

    public int LoopOsrBackedgeThreshold { get; init; } = 1_024;

    public int MaximumLoopOsrGuardFailures { get; init; } = 16;

    public TimeSpan CompilationRetryBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public long MaximumCodeCacheBytes { get; init; } = 64 * 1024 * 1024;

    public LuaInterpreterOptions Interpreter { get; init; } = LuaInterpreterOptions.Default;
}

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
    long EligibilityRejected);

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
    LuaJitFunctionEligibility? Eligibility = null);

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
