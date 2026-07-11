namespace Lunil.IR.Lua54;

public sealed record Lua54ChunkReaderOptions
{
    public static Lua54ChunkReaderOptions Default { get; } = new();

    public int MaximumChunkBytes { get; init; } = 64 * 1024 * 1024;

    public int MaximumPrototypeDepth { get; init; } = 200;

    public int MaximumPrototypeCount { get; init; } = 100_000;

    public int MaximumInstructionCount { get; init; } = 10_000_000;

    public int MaximumConstantCount { get; init; } = 1_000_000;

    public int MaximumUpvalueCount { get; init; } = 1_000_000;

    public int MaximumStringBytes { get; init; } = 64 * 1024 * 1024;

    public int MaximumDebugEntryCount { get; init; } = 10_000_000;

    public bool AllowTrailingData { get; init; }
}
