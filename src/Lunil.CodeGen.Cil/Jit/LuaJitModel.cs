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
}

public sealed record LuaJitExecutorOptions
{
    public static LuaJitExecutorOptions Default { get; } = new();

    public LuaJitPolicy Policy { get; init; } = LuaJitPolicy.Auto;

    public int FunctionEntryThreshold { get; init; } = 32;

    public int BackedgeThreshold { get; init; } = 64;

    public bool SynchronousCompilation { get; init; }

    public int CompilationQueueCapacity { get; init; } = 1_024;

    public int MaximumConcurrentCompilations { get; init; } = 1;

    public int MaximumCompilationAttempts { get; init; } = 1;

    public int MaximumPolymorphicShapes { get; init; } = 4;

    public bool EnableTier2 { get; init; } = true;

    public int Tier2InvocationThreshold { get; init; } = 128;

    public int Tier2BackedgeThreshold { get; init; } = 1_024;

    public int MaximumTier2GuardFailures { get; init; } = 16;

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
    long Tier2Invalidations);

public sealed record LuaJitEvent(
    LuaJitEventKind Kind,
    string ModuleContentId,
    int FunctionId,
    LuaJitFunctionState State,
    long EstimatedCodeBytes = 0,
    TimeSpan Duration = default,
    string? DiagnosticCode = null,
    LuaJitCompilationTier Tier = LuaJitCompilationTier.Tier1);

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
