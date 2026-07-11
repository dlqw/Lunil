namespace Luac.IR.Lua54;

public sealed record Lua54Chunk(
    Lua54ChunkTarget Target,
    byte MainUpvalueCount,
    Lua54Prototype MainPrototype);
