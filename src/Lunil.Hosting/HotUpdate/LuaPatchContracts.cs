using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Lunil.Core;

namespace Lunil.Hosting;

public static class LuaPatchFormat
{
    public const int CurrentVersion = 1;
}

[JsonConverter(typeof(JsonStringEnumConverter<LuaPatchEntryKind>))]
public enum LuaPatchEntryKind : byte
{
    Source,
    BinaryChunk,
    CanonicalIr,
    CompanionData,
}

[JsonConverter(typeof(JsonStringEnumConverter<LuaPatchDependencyKind>))]
public enum LuaPatchDependencyKind : byte
{
    Required,
    Optional,
}

public enum LuaPatchErrorCode : byte
{
    InvalidHeader,
    UnsupportedFormatVersion,
    InvalidManifest,
    NonCanonicalManifest,
    InvalidSignature,
    SignatureRequired,
    UntrustedSigningKey,
    Expired,
    ResourceLimitExceeded,
    UnsafeEntryName,
    DuplicateEntry,
    DuplicateModule,
    MissingEntry,
    EntryMetadataMismatch,
    ContentHashMismatch,
    MissingDependency,
    TrailingData,
    SigningKeyNotYetValid,
    SigningKeyExpired,
    SigningKeyRevoked,
}

public sealed class LuaPatchFormatException : Exception
{
    public LuaPatchFormatException(LuaPatchErrorCode code, string message) : base(message)
    {
        Code = code;
    }

    public LuaPatchFormatException(
        LuaPatchErrorCode code,
        string message,
        Exception innerException) : base(message, innerException)
    {
        Code = code;
    }

    public LuaPatchErrorCode Code { get; }
}

public sealed record LuaPatchDependency(
    string ModuleName,
    LuaPatchDependencyKind Kind);

public sealed record LuaPatchEntry(
    string Name,
    string? ModuleName,
    LuaPatchEntryKind Kind,
    ReadOnlyMemory<byte> Content,
    ImmutableArray<LuaPatchDependency> Dependencies = default);

public sealed record LuaPatchEntryManifest
{
    public required string Name { get; init; }

    public string? ModuleName { get; init; }

    public required LuaPatchEntryKind Kind { get; init; }

    public required string ContentHash { get; init; }

    public required long Length { get; init; }

    public ImmutableArray<LuaPatchDependency> Dependencies { get; init; } = [];
}

public sealed record LuaPatchManifest
{
    public int FormatVersion { get; init; } = LuaPatchFormat.CurrentVersion;

    public required string PatchId { get; init; }

    public required string Channel { get; init; }

    public required string TargetBuild { get; init; }

    public required string BaseRevision { get; init; }

    public required string TargetRevision { get; init; }

    public required LuaLanguageVersion LanguageVersion { get; init; }

    public required string RuntimeAbi { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public required string Nonce { get; init; }

    public ImmutableArray<LuaPatchEntryManifest> Entries { get; init; } = [];
}

public sealed record LuaPatchSignature(
    string Algorithm,
    string KeyId,
    ImmutableArray<byte> Value);

public sealed record LuaPatchBundleReadOptions
{
    public static LuaPatchBundleReadOptions Default { get; } = new();

    public long MaximumBundleBytes { get; init; } = 256L * 1024 * 1024;

    public int MaximumManifestBytes { get; init; } = 4 * 1024 * 1024;

    public int MaximumEntryCount { get; init; } = 4096;

    public long MaximumEntryBytes { get; init; } = 64L * 1024 * 1024;

    public long MaximumTotalEntryBytes { get; init; } = 192L * 1024 * 1024;

    public int MaximumNameBytes { get; init; } = 4096;

    public int MaximumSignatureBytes { get; init; } = 16 * 1024;

    public bool RequireSignature { get; init; } = true;

    public bool AllowExpired { get; init; }

    public DateTimeOffset? UtcNow { get; init; }
}
