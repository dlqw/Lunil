using System.Collections.Immutable;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.Hosting;

public enum LuaHostJitPolicy : byte
{
    InterpreterOnly,
    Auto,
    PreferJit,
    RequireJit,
}

/// <summary>Portable configuration contract for the optional dynamic-code backend.</summary>
public sealed record LuaHostJitOptions
{
    public static LuaHostJitOptions Default { get; } = new();

    public LuaHostJitPolicy Policy { get; init; } = LuaHostJitPolicy.Auto;

    public int FunctionEntryThreshold { get; init; } = 32;

    public int BackedgeThreshold { get; init; } = 64;

    public bool SynchronousCompilation { get; init; }

    public int CompilationQueueCapacity { get; init; } = 1_024;

    public int MaximumConcurrentCompilations { get; init; } = 1;

    public int MaximumCompilationAttempts { get; init; } = 1;

    public int MaximumPolymorphicShapes { get; init; } = 4;

    public bool EnableTier2 { get; init; } = true;

    public bool EnableTier2ManagedFallback { get; init; }

    public int Tier2InvocationThreshold { get; init; } = 128;

    public int Tier2BackedgeThreshold { get; init; } = 1_024;

    public int MaximumTier2GuardFailures { get; init; } = 16;

    public bool EnableLoopOsr { get; init; } = true;

    public bool EnableLoopOsrManagedFallback { get; init; }

    public int LoopOsrBackedgeThreshold { get; init; } = 1_024;

    public int MaximumLoopOsrGuardFailures { get; init; } = 16;

    public TimeSpan CompilationRetryBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public long MaximumCodeCacheBytes { get; init; } = 64 * 1024 * 1024;
}

/// <summary>Process-local counters reported by the optional dynamic-code backend.</summary>
public sealed record LuaHostJitStatistics(
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
    public long Tier2MethodEntries { get; init; }

    public long Tier2CompletedInvocations { get; init; }

    public long Tier2UnsupportedExits { get; init; }

    public long DirectCallEntries { get; init; }

    public long DirectCallCompletions { get; init; }

    public long DirectCallFallbacks { get; init; }

    public long DirectCallInvalidations { get; init; }

    public long SchedulerExitsAvoided { get; init; }

    public long TablePicHits { get; init; }

    public long TablePicMisses { get; init; }

    public long TablePicInvalidations { get; init; }
}

public enum LuaHostJitWarmupStatus : byte
{
    Completed,
    CompletedWithFailures,
    TimedOut,
    Disabled,
}

public enum LuaHostJitWarmupFunctionStatus : byte
{
    ReadyTier1,
    ReadyTier2,
    Ineligible,
    Tier1Failed,
    Tier2Failed,
}

public enum LuaHostJitCompilationTier : byte
{
    Interpreter,
    Tier1,
    Tier2,
    LoopOsr,
}

public sealed record LuaHostJitWarmupOptions
{
    public static LuaHostJitWarmupOptions Default { get; } = new();

    public int MaximumFunctions { get; init; } = 256;

    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromSeconds(5);

    public bool IncludeTier2 { get; init; } = true;

    public bool ProfiledFunctionsOnly { get; init; }
}

public sealed record LuaHostJitWarmupFunctionResult(
    int FunctionId,
    long ProfileSamples,
    LuaHostJitWarmupFunctionStatus Status,
    LuaHostJitCompilationTier Tier,
    string? DiagnosticCode)
{
    public bool Succeeded => Status is LuaHostJitWarmupFunctionStatus.ReadyTier1 or
        LuaHostJitWarmupFunctionStatus.ReadyTier2;
}

public sealed record LuaHostJitWarmupResult(
    LuaHostJitWarmupStatus Status,
    int CandidateFunctionCount,
    int SelectedFunctionCount,
    int ReadyFunctionCount,
    int IneligibleFunctionCount,
    int FailedFunctionCount,
    int SkippedFunctionCount,
    TimeSpan Duration,
    ImmutableArray<LuaHostJitWarmupFunctionResult> Functions)
{
    public bool Succeeded => Status == LuaHostJitWarmupStatus.Completed;
}

internal enum LuaHostJitProfileImportStatus : byte
{
    Imported,
    Rejected,
    Incompatible,
    Disabled,
}

internal sealed record LuaHostJitProfileImportResult(
    LuaHostJitProfileImportStatus Status,
    string? DiagnosticCode,
    string? Message);

internal sealed record LuaHostJitProfileRemapResult(
    bool Succeeded,
    byte[]? Payload,
    int RemappedFunctionCount,
    int IncompatibleFunctionCount,
    int AddedFunctionCount,
    int RemovedFunctionCount,
    string? DiagnosticCode,
    string? Message);

internal interface ILuaHostJitBackend : IDisposable
{
    LuaHostJitStatistics Statistics { get; }

    LuaExecutionResult Execute(
        LuaState state,
        LuaClosure closure,
        ReadOnlySpan<LuaValue> arguments);

    LuaExecutionResult ExecuteBinaryChunk(
        LuaState state,
        ReadOnlySpan<byte> binaryChunk,
        ReadOnlySpan<LuaValue> arguments,
        Lua54ChunkReaderOptions? readerOptions);

    LuaExecutionResult Start(
        LuaState state,
        LuaThread thread,
        long maximumInstructionCount,
        ReadOnlySpan<LuaValue> arguments);

    LuaExecutionResult Resume(
        LuaState state,
        LuaThread thread,
        long maximumInstructionCount,
        ReadOnlySpan<LuaValue> arguments);

    LuaExecutionResult Close(LuaState state, LuaThread thread);

    void Invalidate(LuaIrModule module);

    byte[] ExportProfile(LuaIrModule module);

    LuaHostJitProfileRemapResult RemapProfile(
        LuaIrModule sourceModule,
        LuaIrModule targetModule,
        ReadOnlySpan<byte> payload);

    LuaHostJitProfileImportResult ImportProfile(
        LuaIrModule module,
        ReadOnlySpan<byte> payload);

    LuaHostJitWarmupResult Warmup(
        LuaIrModule module,
        LuaHostJitWarmupOptions options,
        CancellationToken cancellationToken);
}

internal static class LuaHostJitBackendFactory
{
    public static ILuaHostJitBackend Create(
        LuaHostJitOptions options,
        LuaInterpreterOptions interpreter)
    {
#if NET10_0_OR_GREATER
        return new LuaHostCilJitBackend(options, interpreter);
#else
        throw new PlatformNotSupportedException(
            "The dynamic-code backend is not included in the portable Lunil host asset.");
#endif
    }
}
