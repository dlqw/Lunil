using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;

namespace Lunil.IR.Lua55;

/// <summary>Lua 5.5 versioned adapter over the canonical execution IR.</summary>
public sealed record Lua55Chunk(byte[] Bytes)
{
    public byte[] ToArray() => [.. Bytes];
}

public sealed class Lua55ChunkFormatException(string reason, int offset = 0)
    : LuaChunkFormatException("Lua 5.5", reason, offset);

public static class Lua55PrototypeConverter
{
    public static LuaIrModule Convert(ReadOnlySpan<byte> bytes, Lua54ChunkReaderOptions? options = null)
    {
        try
        {
            var chunk = Lua55ChunkReader.Read(bytes, options);
            return Lua54PrototypeConverter.Convert(chunk) with
            {
                LanguageVersion = LuaLanguageVersion.Lua55,
            };
        }
        catch (Exception exception) when (exception is Lua55ChunkFormatException or Lua54ChunkFormatException or InvalidDataException)
        {
            throw new Lua55ChunkFormatException(exception.Message);
        }
    }
}

public static class Lua55CanonicalPrototypeWriter
{
    public static byte[] Write(LuaIrModule module, int functionId, bool stripDebug = false)
    {
        if (module.LanguageVersion != LuaLanguageVersion.Lua55)
            throw new InvalidDataException("Lua 5.5 writer requires a Lua 5.5 canonical module.");
        var carrier = Lua54CanonicalPrototypeWriter.CreateLua55AdapterChunk(
            module,
            functionId);
        return Lua55ChunkWriter.Write(carrier, stripDebug);
    }
}
