using System.Text.Json;

namespace Lunil.Hosting;

public static class LuaPatchManifestSerializer
{
    public static byte[] Serialize(LuaPatchManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            LuaPatchJsonContext.Default.LuaPatchManifest);
    }

    public static LuaPatchManifest Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return JsonSerializer.Deserialize(
                utf8Json,
                LuaPatchJsonContext.Default.LuaPatchManifest) ??
                throw new LuaPatchFormatException(
                    LuaPatchErrorCode.InvalidManifest,
                    "The patch manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new LuaPatchFormatException(
                LuaPatchErrorCode.InvalidManifest,
                "The patch manifest is invalid JSON.",
                exception);
        }
    }
}
