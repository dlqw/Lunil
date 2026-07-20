using Lunil.Core;

namespace Lunil.IR.Lua54;

public sealed record Lua54Chunk(
    Lua54ChunkTarget Target,
    byte MainUpvalueCount,
    Lua54Prototype MainPrototype)
{
    /// <summary>
    /// Identifies the source chunk family when this logical model carries version-specific
    /// extension opcodes through the shared Lua 5.4-shaped prototype representation.
    /// </summary>
    public LuaChunkFormat SourceFormat { get; init; } = LuaChunkFormat.Lua54;
}
