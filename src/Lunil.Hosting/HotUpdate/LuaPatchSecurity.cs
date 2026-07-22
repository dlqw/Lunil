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

public sealed record LuaPatchTrustedEcdsaKey(string KeyId, ReadOnlyMemory<byte> SubjectPublicKeyInfo);

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

public sealed class LuaPatchEcdsaTrustStore : ILuaPatchSignatureVerifier
{
    private readonly ImmutableDictionary<string, byte[]> _keys;

    public LuaPatchEcdsaTrustStore(IEnumerable<LuaPatchTrustedEcdsaKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var builder = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key.KeyId);
            if (!builder.TryAdd(key.KeyId, key.SubjectPublicKeyInfo.ToArray()))
            {
                throw new ArgumentException($"Duplicate trusted patch key '{key.KeyId}'.", nameof(keys));
            }
        }

        _keys = builder.ToImmutable();
    }

    public bool IsTrusted(string algorithm, string keyId) =>
        string.Equals(algorithm, LuaPatchEcdsaSigner.AlgorithmName, StringComparison.Ordinal) &&
        _keys.ContainsKey(keyId);

    public bool VerifyDigest(
        string algorithm,
        string keyId,
        ReadOnlySpan<byte> digest,
        ReadOnlySpan<byte> signature)
    {
        if (!IsTrusted(algorithm, keyId) || digest.Length != SHA256.HashSizeInBytes)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(_keys[keyId], out var bytesRead);
            return bytesRead == _keys[keyId].Length && key.KeySize == 256 &&
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
}
