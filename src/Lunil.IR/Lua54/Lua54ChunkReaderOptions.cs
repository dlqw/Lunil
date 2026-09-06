namespace Lunil.IR.Lua54;

public sealed record Lua54ChunkReaderOptions
{
    public static Lua54ChunkReaderOptions Default { get; } = new();

    public int MaximumChunkBytes { get; init; } = LuaChunkReaderLimits.MaximumChunkBytes;

    public int MaximumPrototypeDepth { get; init; } = LuaChunkReaderLimits.MaximumPrototypeDepth;

    public int MaximumPrototypeCount { get; init; } = LuaChunkReaderLimits.MaximumPrototypeCount;

    public int MaximumInstructionCount { get; init; } = LuaChunkReaderLimits.MaximumInstructionCount;

    public int MaximumConstantCount { get; init; } = LuaChunkReaderLimits.MaximumConstantCount;

    public int MaximumUpvalueCount { get; init; } = LuaChunkReaderLimits.MaximumUpvalueCount;

    public int MaximumStringBytes { get; init; } = LuaChunkReaderLimits.MaximumStringBytes;

    public int MaximumDebugEntryCount { get; init; } = LuaChunkReaderLimits.MaximumDebugEntryCount;

    public bool AllowTrailingData { get; init; }
}
