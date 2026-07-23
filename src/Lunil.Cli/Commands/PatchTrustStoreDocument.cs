using System.Text.Json.Serialization;

namespace Lunil.Cli.Commands;

internal sealed record PatchTrustStoreDocument
{
    public const string SchemaName = "lunil.patch-trust.v1";
    public const int MaximumKeyCount = 1024;

    public required string Schema { get; init; }

    public required PatchTrustStoreKey[] Keys { get; init; }
}

internal sealed record PatchTrustStoreKey
{
    public required string KeyId { get; init; }

    public required string PublicKey { get; init; }

    public DateTimeOffset? ValidFrom { get; init; }

    public DateTimeOffset? ValidUntil { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PatchTrustStoreDocument))]
internal sealed partial class PatchTrustStoreJsonContext : JsonSerializerContext;
