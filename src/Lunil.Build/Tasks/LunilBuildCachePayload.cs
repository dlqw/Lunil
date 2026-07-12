using System.Collections.Immutable;
using System.Text;
using Lunil.CodeGen.Cil;
using Lunil.CodeGen.Cil.Artifacts;
using Lunil.CodeGen.Cil.Loading;

namespace Lunil.Build.Tasks;

internal sealed record LunilBuildCachedArtifact(
    ImmutableArray<byte> CanonicalModule,
    LuaAotArtifactManifest Manifest,
    ImmutableArray<byte> PeImage,
    ImmutableArray<byte> PortablePdbImage);

internal static class LunilBuildCachePayload
{
    private const string Magic = "LUNIL-BUILD-AOT-CACHE";
    private const int SchemaVersion = 1;
    private const int MaximumCanonicalModuleBytes = 64 * 1024 * 1024;
    private const int MaximumPeBytes = 256 * 1024 * 1024;
    private const int MaximumPdbBytes = 256 * 1024 * 1024;

    public static byte[] Serialize(
        ReadOnlySpan<byte> canonicalModule,
        LuaAotArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(SchemaVersion);
            WriteBytes(writer, canonicalModule);
            WriteBytes(writer, artifact.PeImage.AsSpan());
            WriteBytes(writer, artifact.PortablePdbImage.AsSpan());
        }

        return stream.ToArray();
    }

    public static LunilBuildCachedArtifact Deserialize(
        ReadOnlySpan<byte> payload,
        string expectedModuleContentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedModuleContentId);
        try
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (!string.Equals(reader.ReadString(), Magic, StringComparison.Ordinal) ||
                reader.ReadInt32() != SchemaVersion)
            {
                throw new InvalidDataException("Lunil build cache payload version is invalid.");
            }

            var canonicalBytes = ReadBytes(reader, MaximumCanonicalModuleBytes);
            var peBytes = ReadBytes(reader, MaximumPeBytes);
            var pdbBytes = ReadBytes(reader, MaximumPdbBytes);
            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("Lunil build cache payload has trailing data.");
            }

            var canonicalModule = LuaAotModuleIdentity.DeserializeCanonicalModule(canonicalBytes);
            var canonicalContentId = LuaAotModuleIdentity.ComputeContentId(canonicalModule);
            if (!string.Equals(
                canonicalContentId,
                expectedModuleContentId,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Cached canonical module identity does not match the requested module.");
            }

            var peImage = peBytes.ToImmutableArray();
            var portablePdbImage = pdbBytes.ToImmutableArray();
            var validation = LuaAotArtifactLoader.Validate(
                peImage,
                portablePdbImage,
                new LuaAotLoadOptions
                {
                    ExpectedModuleContentId = expectedModuleContentId,
                });
            if (!validation.Succeeded || validation.Manifest is null)
            {
                var diagnostic = validation.Diagnostics.IsDefaultOrEmpty
                    ? "unknown validation failure"
                    : string.Join(
                        "; ",
                        validation.Diagnostics.Select(static item =>
                            $"[{item.Code}] {item.Message}"));
                throw new InvalidDataException(
                    $"Cached persisted CIL artifact is invalid: {diagnostic}");
            }

            return new LunilBuildCachedArtifact(
                canonicalBytes.ToImmutableArray(),
                validation.Manifest,
                peImage,
                portablePdbImage);
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException("Lunil build cache payload is truncated.", error);
        }
    }

    private static void WriteBytes(BinaryWriter writer, ReadOnlySpan<byte> content)
    {
        writer.Write(content.Length);
        writer.Write(content);
    }

    private static byte[] ReadBytes(BinaryReader reader, int maximumLength)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > maximumLength ||
            length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("Lunil build cache payload length is invalid.");
        }

        return reader.ReadBytes(length);
    }
}
