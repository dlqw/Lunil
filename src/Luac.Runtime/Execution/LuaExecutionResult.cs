using System.Collections.Immutable;
using Luac.Runtime.Values;

namespace Luac.Runtime.Execution;

public enum LuaVmSignal : byte
{
    Completed,
    Yielded,
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
