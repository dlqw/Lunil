namespace Lunil.IR.Lua54;

public sealed class Lua54ChunkFormatException : LuaChunkFormatException
{
    public Lua54ChunkFormatException(string reason, int byteOffset)
        : base("Lua 5.4", reason, byteOffset)
    {
    }

    public int ByteOffset => Offset;
}
