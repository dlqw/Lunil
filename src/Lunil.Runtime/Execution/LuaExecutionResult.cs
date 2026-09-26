using System.Collections.Immutable;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

public enum LuaVmSignal : byte
{
    Completed,
    Yielded,
    Error,
    Paused,
}

public sealed record LuaExecutionResult(
    LuaVmSignal Signal,
    ImmutableArray<LuaValue> Values)
{
    /// <summary>Gets the canonical instructions charged by this execution turn.</summary>
    public long ExecutedInstructionCount { get; init; }
}

public enum LuaPerformanceProfile : byte
{
    Balanced = 0,
    Unchecked = 1,
}

public sealed record LuaInterpreterOptions
{
    public static LuaInterpreterOptions Default { get; } = new();

    public LuaPerformanceProfile PerformanceProfile { get; init; } = LuaPerformanceProfile.Balanced;

    public long MaximumInstructionCount { get; init; } = 100_000_000;

    public int MaximumStackSlots { get; init; } = 1_000_000;

    public int MaximumCallDepth { get; init; } = 20_000;
}
