namespace Lunil.IR.Lua52;

public sealed record Lua52ChunkReaderOptions
{
    public static Lua52ChunkReaderOptions Default { get; } = new();

    public int MaximumChunkBytes { get; init; } = 64 * 1024 * 1024;
    public int MaximumPrototypeDepth { get; init; } = 128;
    public int MaximumPrototypeCount { get; init; } = 100_000;
    public int MaximumInstructionCount { get; init; } = 4_000_000;
    public int MaximumConstantCount { get; init; } = 1_000_000;
    public int MaximumUpvalueCount { get; init; } = 100_000;
    public int MaximumStringBytes { get; init; } = 16 * 1024 * 1024;
    public int MaximumDebugEntryCount { get; init; } = 2_000_000;
    public bool AllowTrailingData { get; init; }
}
