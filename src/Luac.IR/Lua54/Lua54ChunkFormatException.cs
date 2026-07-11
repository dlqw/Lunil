namespace Luac.IR.Lua54;

public sealed class Lua54ChunkFormatException : FormatException
{
    public Lua54ChunkFormatException(string reason, int byteOffset)
        : base($"Bad Lua 5.4 binary chunk at byte {byteOffset}: {reason}")
    {
        Reason = reason;
        ByteOffset = byteOffset;
    }

    public string Reason { get; }

    public int ByteOffset { get; }
}
