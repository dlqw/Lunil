namespace Lunil.IR;

/// <summary>
/// Single source of the default resource limits shared by every version-specific binary
/// chunk reader options record, so the resource contract is identical across Lua 5.1–5.5
/// and cannot silently drift between the per-version copies.
/// </summary>
internal static class LuaChunkReaderLimits
{
    public const int MaximumChunkBytes = 64 * 1024 * 1024;

    public const int MaximumPrototypeDepth = 200;

    public const int MaximumPrototypeCount = 100_000;

    public const int MaximumInstructionCount = 10_000_000;

    public const int MaximumConstantCount = 1_000_000;

    public const int MaximumUpvalueCount = 1_000_000;

    public const int MaximumStringBytes = 64 * 1024 * 1024;

    public const int MaximumDebugEntryCount = 10_000_000;
}
