using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;

namespace Lunil.IR.Lua55;

/// <summary>
/// Lua 5.5 versioned adapter.  Lua 5.5 keeps the 5.4 canonical VM contract for source execution;
/// this boundary owns the version marker and prevents accidental loading as an unversioned 5.4
/// module.  The dedicated type is intentionally kept separate so the generated profile can grow
/// to the final 5.5 varint instruction codec without changing public dispatch code.
/// </summary>
public sealed record Lua55Chunk(byte[] Bytes)
{
    public byte[] ToArray() => [.. Bytes];
}

public sealed class Lua55ChunkFormatException(string reason, int offset = 0)
    : FormatException($"Bad Lua 5.5 binary chunk at byte {offset}: {reason}")
{
    public string Reason { get; } = reason;
    public int Offset { get; } = offset;
}

public static class Lua55PrototypeConverter
{
    public static LuaIrModule Convert(ReadOnlySpan<byte> bytes, Lua54ChunkReaderOptions? options = null)
    {
        Validate(bytes);
        var translated = bytes.ToArray();
        translated[4] = 0x54;
        try
        {
            return Lua54PrototypeConverter.Convert(translated, options) with
            {
                LanguageVersion = LuaLanguageVersion.Lua55,
            };
        }
        catch (Exception exception) when (exception is Lua54ChunkFormatException or InvalidDataException)
        {
            throw new Lua55ChunkFormatException(exception.Message);
        }
    }

    private static void Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 5 || bytes[0] != 0x1b || bytes[1] != (byte)'L' || bytes[2] != (byte)'u' ||
            bytes[3] != (byte)'a')
            throw new Lua55ChunkFormatException("not a binary chunk");
        if (bytes[4] != 0x55)
            throw new Lua55ChunkFormatException("version mismatch; expected Lua 5.5", 4);
    }
}

public static class Lua55CanonicalPrototypeWriter
{
    public static byte[] Write(LuaIrModule module, int functionId, bool stripDebug = false)
    {
        if (module.LanguageVersion != LuaLanguageVersion.Lua55)
            throw new InvalidDataException("Lua 5.5 writer requires a Lua 5.5 canonical module.");
        var bytes = Lua54CanonicalPrototypeWriter.Write(
            module with { LanguageVersion = LuaLanguageVersion.Lua54 }, functionId, stripDebug);
        bytes[4] = 0x55;
        return bytes;
    }
}
