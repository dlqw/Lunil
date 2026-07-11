namespace Luac.Runtime.Memory;

public sealed record LuaHeapOptions
{
    public static LuaHeapOptions Default { get; } = new();

    public long MaximumLogicalBytes { get; init; } = 256L * 1024 * 1024;

    public long StepSizeBytes { get; init; } = 4 * 1024;

    public int StepObjectBudget { get; init; } = 32;

    public int MinorCyclesBeforeMajor { get; init; } = 8;

    public LuaGcMode InitialMode { get; init; } = LuaGcMode.Incremental;

    public bool StressEveryAllocation { get; init; }

    public int? HashSeed { get; init; }
}
