using System.Collections.Immutable;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

public enum LuaVmSignal : byte
{
    Completed,
    Yielded,
    Error,
}

public sealed record LuaExecutionResult(
    LuaVmSignal Signal,
    ImmutableArray<LuaValue> Values);

public sealed record LuaInterpreterOptions
{
    public static LuaInterpreterOptions Default { get; } = new();

    public long MaximumInstructionCount { get; init; } = 100_000_000;

    public int MaximumStackSlots { get; init; } = 1_000_000;

    public int MaximumCallDepth { get; init; } = 20_000;
}
