using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Lunil.Hosting;

public interface ILuaPatchSigner
{
    string Algorithm { get; }

    string KeyId { get; }

    byte[] SignDigest(ReadOnlySpan<byte> digest);
}

public interface ILuaPatchSignatureVerifier
{
    bool IsTrusted(string algorithm, string keyId);

    bool VerifyDigest(
        string algorithm,
        string keyId,
        ReadOnlySpan<byte> digest,
        ReadOnlySpan<byte> signature);
}

public enum LuaPatchSignatureTrustStatus : byte
{
    Trusted,
    UnsupportedAlgorithm,
    UnknownKey,
    NotYetValid,
    Expired,
    Revoked,
}

public sealed record LuaPatchSignatureTrustResult(
    LuaPatchSignatureTrustStatus Status,
    string? Message)
{
    public bool Trusted => Status == LuaPatchSignatureTrustStatus.Trusted;
}

/// <summary>
/// Extends signature verification with a deterministic, time-aware key lifecycle decision.
/// Implementations must apply the same decision to the matching verification overload.
/// </summary>
public interface ILuaPatchSignatureTrustPolicy : ILuaPatchSignatureVerifier
{
    LuaPatchSignatureTrustResult EvaluateTrust(
        string algorithm,
        string keyId,
        DateTimeOffset verificationTime);

    bool VerifyDigest(
        string algorithm,
        string keyId,
        ReadOnlySpan<byte> digest,
        ReadOnlySpan<byte> signature,
        DateTimeOffset verificationTime);
}

public sealed record LuaPatchTrustedEcdsaKey(
    string KeyId,
    ReadOnlyMemory<byte> SubjectPublicKeyInfo)
{
    /// <summary>The first instant at which this key may authorize a patch.</summary>
    public DateTimeOffset? ValidFrom { get; init; }

    /// <summary>The exclusive instant after which this key no longer authorizes patches.</summary>
    public DateTimeOffset? ValidUntil { get; init; }

    /// <summary>The instant at which this key is revoked, independent of its validity window.</summary>
    public DateTimeOffset? RevokedAt { get; init; }
}

public sealed class LuaPatchEcdsaSigner : ILuaPatchSigner
{
    public const string AlgorithmName = "ECDSA-P256-SHA256";

    private readonly ECDsa _key;

    public LuaPatchEcdsaSigner(string keyId, ECDsa key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentNullException.ThrowIfNull(key);
        if (key.KeySize != 256)
        {
            throw new ArgumentException("The patch signing key must use the P-256 curve.", nameof(key));
        }

        KeyId = keyId;
        _key = key;
    }

    public string Algorithm => AlgorithmName;

    public string KeyId { get; }

    public byte[] SignDigest(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException("The patch digest must be SHA-256.", nameof(digest));
        }

        return _key.SignHash(digest, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}

public sealed class LuaPatchEcdsaTrustStore : ILuaPatchSignatureTrustPolicy
{
    private readonly ImmutableDictionary<string, TrustedKey> _keys;

    public LuaPatchEcdsaTrustStore(IEnumerable<LuaPatchTrustedEcdsaKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var builder = ImmutableDictionary.CreateBuilder<string, TrustedKey>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentException.ThrowIfNullOrWhiteSpace(key.KeyId);
            if (key.ValidFrom is { } validFrom && key.ValidUntil is { } validUntil &&
                validUntil <= validFrom)
            {
                throw new ArgumentException(
                    $"Trusted patch key '{key.KeyId}' has an empty validity window.",
                    nameof(keys));
            }

            var subjectPublicKeyInfo = key.SubjectPublicKeyInfo.ToArray();
            ValidateKey(key.KeyId, subjectPublicKeyInfo, nameof(keys));
            if (!builder.TryAdd(
                    key.KeyId,
                    new TrustedKey(
                        subjectPublicKeyInfo,
                        key.ValidFrom,
                        key.ValidUntil,
                        key.RevokedAt)))
            {
                throw new ArgumentException($"Duplicate trusted patch key '{key.KeyId}'.", nameof(keys));
            }
        }

        _keys = builder.ToImmutable();
    }

    public bool IsTrusted(string algorithm, string keyId) =>
        EvaluateTrust(algorithm, keyId, DateTimeOffset.UtcNow).Trusted;

    public LuaPatchSignatureTrustResult EvaluateTrust(
        string algorithm,
        string keyId,
        DateTimeOffset verificationTime)
    {
        if (!string.Equals(algorithm, LuaPatchEcdsaSigner.AlgorithmName, StringComparison.Ordinal))
        {
            return Rejected(
                LuaPatchSignatureTrustStatus.UnsupportedAlgorithm,
                "The patch signature algorithm is not trusted.");
        }

        if (string.IsNullOrWhiteSpace(keyId) || !_keys.TryGetValue(keyId, out var key))
        {
            return Rejected(
                LuaPatchSignatureTrustStatus.UnknownKey,
                "The patch signing key is not in the trust store.");
        }

        if (key.RevokedAt is { } revokedAt && verificationTime >= revokedAt)
        {
            return Rejected(
                LuaPatchSignatureTrustStatus.Revoked,
                $"Patch signing key '{keyId}' is revoked.");
        }

        if (key.ValidFrom is { } validFrom && verificationTime < validFrom)
        {
            return Rejected(
                LuaPatchSignatureTrustStatus.NotYetValid,
                $"Patch signing key '{keyId}' is not yet valid.");
        }

        if (key.ValidUntil is { } validUntil && verificationTime >= validUntil)
        {
            return Rejected(
                LuaPatchSignatureTrustStatus.Expired,
                $"Patch signing key '{keyId}' has expired.");
        }

        return new LuaPatchSignatureTrustResult(LuaPatchSignatureTrustStatus.Trusted, null);
    }

    public bool VerifyDigest(
        string algorithm,
        string keyId,
        ReadOnlySpan<byte> digest,
        ReadOnlySpan<byte> signature,
        DateTimeOffset verificationTime) => VerifyDigestCore(
            algorithm,
            keyId,
            digest,
            signature,
            verificationTime);

    public bool VerifyDigest(
        string algorithm,
        string keyId,
        ReadOnlySpan<byte> digest,
        ReadOnlySpan<byte> signature) => VerifyDigestCore(
            algorithm,
            keyId,
            digest,
            signature,
            DateTimeOffset.UtcNow);

    private bool VerifyDigestCore(
        string algorithm,
        string keyId,
        ReadOnlySpan<byte> digest,
        ReadOnlySpan<byte> signature,
        DateTimeOffset verificationTime)
    {
        if (!EvaluateTrust(algorithm, keyId, verificationTime).Trusted ||
            digest.Length != SHA256.HashSizeInBytes ||
            !_keys.TryGetValue(keyId, out var trustedKey))
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(trustedKey.SubjectPublicKeyInfo, out var bytesRead);
            return bytesRead == trustedKey.SubjectPublicKeyInfo.Length && key.KeySize == 256 &&
                key.VerifyHash(
                    digest,
                    signature,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static void ValidateKey(string keyId, byte[] subjectPublicKeyInfo, string parameterName)
    {
        if (subjectPublicKeyInfo.Length == 0)
        {
            throw new ArgumentException(
                $"Trusted patch key '{keyId}' is empty.",
                parameterName);
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
            if (bytesRead != subjectPublicKeyInfo.Length || key.KeySize != 256)
            {
                throw new ArgumentException(
                    $"Trusted patch key '{keyId}' must be a complete P-256 public key.",
                    parameterName);
            }
        }
        catch (CryptographicException exception)
        {
            throw new ArgumentException(
                $"Trusted patch key '{keyId}' is not a valid P-256 public key.",
                parameterName,
                exception);
        }
    }

    private static LuaPatchSignatureTrustResult Rejected(
        LuaPatchSignatureTrustStatus status,
        string message) => new(status, message);

    private sealed record TrustedKey(
        byte[] SubjectPublicKeyInfo,
        DateTimeOffset? ValidFrom,
        DateTimeOffset? ValidUntil,
        DateTimeOffset? RevokedAt);
}
