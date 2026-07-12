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
    long TotalCompilationTicks);

public sealed record LuaJitEvent(
    LuaJitEventKind Kind,
    string ModuleContentId,
    int FunctionId,
    LuaJitFunctionState State,
    long EstimatedCodeBytes = 0,
    TimeSpan Duration = default,
    string? DiagnosticCode = null);

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
