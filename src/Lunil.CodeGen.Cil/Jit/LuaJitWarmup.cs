using System.Collections.Immutable;

namespace Lunil.CodeGen.Cil.Jit;

public enum LuaJitWarmupStatus : byte
{
    Completed,
    CompletedWithFailures,
    TimedOut,
    Disabled,
}

public enum LuaJitWarmupFunctionStatus : byte
{
    ReadyTier1,
    ReadyTier2,
    Ineligible,
    Tier1Failed,
    Tier2Failed,
}

/// <summary>Bounds an explicit, non-executing JIT warmup pass.</summary>
public sealed record LuaJitWarmupOptions
{
    public static LuaJitWarmupOptions Default { get; } = new();

    public int MaximumFunctions { get; init; } = 256;

    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Attempts profile-qualified Tier 2 compilation after Tier 1 succeeds.</summary>
    public bool IncludeTier2 { get; init; } = true;

    /// <summary>Limits warmup to functions with imported or locally observed profile samples.</summary>
    public bool ProfiledFunctionsOnly { get; init; }
}

public sealed record LuaJitWarmupFunctionResult(
    int FunctionId,
    long ProfileSamples,
    LuaJitWarmupFunctionStatus Status,
    LuaJitCompilationTier Tier,
    string? DiagnosticCode)
{
    public bool Succeeded => Status is LuaJitWarmupFunctionStatus.ReadyTier1 or
        LuaJitWarmupFunctionStatus.ReadyTier2;
}

public sealed record LuaJitWarmupResult(
    LuaJitWarmupStatus Status,
    int CandidateFunctionCount,
    int SelectedFunctionCount,
    int ReadyFunctionCount,
    int IneligibleFunctionCount,
    int FailedFunctionCount,
    int SkippedFunctionCount,
    TimeSpan Duration,
    ImmutableArray<LuaJitWarmupFunctionResult> Functions)
{
    public bool Succeeded => Status == LuaJitWarmupStatus.Completed;
}
