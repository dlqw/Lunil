using System.Collections.Immutable;

namespace Lunil.CodeGen.Cil.Artifacts;

public readonly record struct LuaAotProgramCounterMapEntry(
    int IlOffset,
    int CanonicalProgramCounter,
    int LogicalProgramCounter);

public static class LuaAotPortablePdbMetadata
{
    private static ReadOnlySpan<byte> Magic => "LPCM1"u8;

    public static Guid ProgramCounterMapKind { get; } =
        new("36f31184-a25a-4a05-a5b7-6e5034922f3d");

    public static ImmutableArray<LuaAotProgramCounterMapEntry> DecodeProgramCounterMap(
        ReadOnlySpan<byte> content)
    {
        if (content.Length < Magic.Length + sizeof(int) ||
            !content[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("The Lunil PDB program-counter map is malformed.");
        }

        var offset = Magic.Length;
        var count = BitConverter.ToInt32(content[offset..]);
        offset += sizeof(int);
        if (count < 0 || content.Length - offset != checked(count * sizeof(int) * 3))
        {
            throw new InvalidDataException("The Lunil PDB program-counter map has an invalid length.");
        }

        var result = ImmutableArray.CreateBuilder<LuaAotProgramCounterMapEntry>(count);
        for (var index = 0; index < count; index++)
        {
            var ilOffset = BitConverter.ToInt32(content[offset..]);
            offset += sizeof(int);
            var canonicalProgramCounter = BitConverter.ToInt32(content[offset..]);
            offset += sizeof(int);
            var logicalProgramCounter = BitConverter.ToInt32(content[offset..]);
            offset += sizeof(int);
            result.Add(new LuaAotProgramCounterMapEntry(
                ilOffset,
                canonicalProgramCounter,
                logicalProgramCounter));
        }

        return result.ToImmutable();
    }

    internal static byte[] EncodeProgramCounterMap(
        IEnumerable<LuaAotProgramCounterMapEntry> entries)
    {
        var materialized = entries.ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Magic);
        writer.Write(materialized.Length);
        foreach (var entry in materialized)
        {
            writer.Write(entry.IlOffset);
            writer.Write(entry.CanonicalProgramCounter);
            writer.Write(entry.LogicalProgramCounter);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
