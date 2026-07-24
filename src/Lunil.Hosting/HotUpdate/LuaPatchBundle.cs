using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lunil.Core;

namespace Lunil.Hosting;

public sealed class LuaPatchBundle
{
    private static ReadOnlySpan<byte> Magic => "LUNILPCH"u8;

    private readonly byte[] _manifestBytes;

    private LuaPatchBundle(
        LuaPatchManifest manifest,
        ImmutableArray<LuaPatchEntry> entries,
        LuaPatchSignature signature,
        byte[] manifestBytes)
    {
        Manifest = manifest;
        Entries = entries;
        Signature = signature;
        _manifestBytes = manifestBytes;
    }

    public LuaPatchManifest Manifest { get; }

    public ImmutableArray<LuaPatchEntry> Entries { get; }

    public LuaPatchSignature Signature { get; }

    public static LuaPatchBundle Create(
        LuaPatchManifest manifest,
        IEnumerable<LuaPatchEntry> entries,
        ILuaPatchSigner signer)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(signer);
        ValidateManifestHeader(manifest, LuaPatchBundleReadOptions.Default, false);

        var normalizedEntries = NormalizeEntries(entries);
        var descriptors = normalizedEntries.Select(static entry => new LuaPatchEntryManifest
        {
            Name = entry.Name,
            ModuleName = entry.ModuleName,
            Kind = entry.Kind,
            ContentHash = Convert.ToHexString(SHA256.HashData(entry.Content.Span)),
            Length = entry.Content.Length,
            Dependencies = NormalizeDependencies(entry.Dependencies),
        }).ToImmutableArray();
        var completeManifest = manifest with
        {
            Entries = descriptors,
            RequiredCapabilities = NormalizeCapabilities(manifest.RequiredCapabilities),
            RequiredTargetLabels = NormalizeTargetLabels(manifest.RequiredTargetLabels),
        };
        _ = LuaPatchDependencyPlan.Create(normalizedEntries);
        var manifestBytes = SerializeManifest(completeManifest);
        var digest = SHA256.HashData(manifestBytes);
        var signatureBytes = signer.SignDigest(digest);
        if (signatureBytes.Length == 0)
        {
            throw new ArgumentException("The patch signer returned an empty signature.", nameof(signer));
        }

        return new LuaPatchBundle(
            completeManifest,
            normalizedEntries,
            new LuaPatchSignature(
                signer.Algorithm,
                signer.KeyId,
                ImmutableArray.Create(signatureBytes)),
            manifestBytes);
    }

    public static LuaPatchBundle Read(
        Stream stream,
        ILuaPatchSignatureVerifier signatureVerifier,
        LuaPatchBundleReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(signatureVerifier);
        options ??= LuaPatchBundleReadOptions.Default;
        ValidateOptions(options);
        var verificationTime = options.UtcNow ?? DateTimeOffset.UtcNow;

        var reader = new BoundedReader(stream, options.MaximumBundleBytes);
        Span<byte> magic = stackalloc byte[8];
        reader.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic))
        {
            throw Format(LuaPatchErrorCode.InvalidHeader, "The input is not a Lunil patch bundle.");
        }

        var formatVersion = reader.ReadInt32();
        if (formatVersion != LuaPatchFormat.CurrentVersion)
        {
            throw Format(
                LuaPatchErrorCode.UnsupportedFormatVersion,
                $"Patch format version {formatVersion} is unsupported.");
        }

        var manifestLength = reader.ReadInt32();
        if (manifestLength <= 0 || manifestLength > options.MaximumManifestBytes)
        {
            throw Format(LuaPatchErrorCode.ResourceLimitExceeded, "The patch manifest exceeds its configured limit.");
        }

        var manifestBytes = reader.ReadBytes(manifestLength);
        LuaPatchManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(manifestBytes, LuaPatchJsonContext.Default.LuaPatchManifest) ??
                throw Format(LuaPatchErrorCode.InvalidManifest, "The patch manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new LuaPatchFormatException(
                LuaPatchErrorCode.InvalidManifest,
                "The patch manifest is invalid JSON.",
                exception);
        }

        ValidateManifestHeader(manifest, options, true);
        var canonicalManifest = SerializeManifest(manifest);
        if (!manifestBytes.AsSpan().SequenceEqual(canonicalManifest))
        {
            throw Format(LuaPatchErrorCode.NonCanonicalManifest, "The patch manifest is not canonically encoded.");
        }

        if (manifest.Entries.Length > options.MaximumEntryCount)
        {
            throw Format(LuaPatchErrorCode.ResourceLimitExceeded, "The patch entry count exceeds its configured limit.");
        }

        var algorithm = reader.ReadString(options.MaximumNameBytes);
        var keyId = reader.ReadString(options.MaximumNameBytes);
        var signatureLength = reader.ReadInt32();
        if (signatureLength <= 0 || signatureLength > options.MaximumSignatureBytes)
        {
            throw Format(LuaPatchErrorCode.ResourceLimitExceeded, "The patch signature exceeds its configured limit.");
        }

        var signatureBytes = reader.ReadBytes(signatureLength);
        if (options.RequireSignature &&
            (string.IsNullOrWhiteSpace(algorithm) || string.IsNullOrWhiteSpace(keyId)))
        {
            throw Format(LuaPatchErrorCode.SignatureRequired, "A trusted patch signature is required.");
        }

        if (signatureVerifier is ILuaPatchSignatureTrustPolicy trustPolicy)
        {
            var trust = trustPolicy.EvaluateTrust(algorithm, keyId, verificationTime);
            if (!trust.Trusted)
            {
                throw Format(MapTrustError(trust.Status), trust.Message ?? "The patch signing key is not trusted.");
            }
        }
        else if (!signatureVerifier.IsTrusted(algorithm, keyId))
        {
            throw Format(LuaPatchErrorCode.UntrustedSigningKey, "The patch signing key is not trusted.");
        }

        var entryCount = reader.ReadInt32();
        if (entryCount != manifest.Entries.Length || entryCount < 0)
        {
            throw Format(LuaPatchErrorCode.EntryMetadataMismatch, "The payload entry count does not match the manifest.");
        }

        var entries = ImmutableArray.CreateBuilder<LuaPatchEntry>(entryCount);
        long totalEntryBytes = 0;
        foreach (var descriptor in manifest.Entries)
        {
            var name = reader.ReadString(options.MaximumNameBytes);
            var length = reader.ReadInt64();
            if (!string.Equals(name, descriptor.Name, StringComparison.Ordinal) || length != descriptor.Length)
            {
                throw Format(LuaPatchErrorCode.EntryMetadataMismatch, "A payload entry does not match the manifest.");
            }

            if (length < 0 || length > options.MaximumEntryBytes ||
                totalEntryBytes > options.MaximumTotalEntryBytes - length || length > int.MaxValue)
            {
                throw Format(LuaPatchErrorCode.ResourceLimitExceeded, "Patch payload data exceeds its configured limit.");
            }

            totalEntryBytes += length;
            var content = reader.ReadBytes((int)length);
            var actualHash = Convert.ToHexString(SHA256.HashData(content));
            if (!string.Equals(actualHash, descriptor.ContentHash, StringComparison.Ordinal))
            {
                throw Format(
                    LuaPatchErrorCode.ContentHashMismatch,
                    $"Patch entry '{descriptor.Name}' failed content verification.");
            }

            entries.Add(new LuaPatchEntry(
                descriptor.Name,
                descriptor.ModuleName,
                descriptor.Kind,
                content,
                descriptor.Dependencies));
        }

        if (reader.TryReadByte(out _))
        {
            throw Format(LuaPatchErrorCode.TrailingData, "Trailing data follows the patch payload.");
        }

        if (!options.AllowExpired && manifest.ExpiresAt is { } expiresAt &&
            expiresAt <= verificationTime)
        {
            throw Format(LuaPatchErrorCode.Expired, "The patch bundle has expired.");
        }

        var digest = SHA256.HashData(manifestBytes);
        var signatureValid = signatureVerifier is ILuaPatchSignatureTrustPolicy timedTrustPolicy
            ? timedTrustPolicy.VerifyDigest(
                algorithm,
                keyId,
                digest,
                signatureBytes,
                verificationTime)
            : signatureVerifier.VerifyDigest(algorithm, keyId, digest, signatureBytes);
        if (!signatureValid)
        {
            throw Format(LuaPatchErrorCode.InvalidSignature, "The patch signature is invalid.");
        }

        var immutableEntries = entries.ToImmutable();
        ValidateEntriesAgainstManifest(manifest, immutableEntries);
        _ = LuaPatchDependencyPlan.Create(immutableEntries);
        return new LuaPatchBundle(
            manifest,
            immutableEntries,
            new LuaPatchSignature(algorithm, keyId, ImmutableArray.Create(signatureBytes)),
            manifestBytes);
    }

    public void Write(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The patch output stream is not writable.", nameof(stream));
        }

        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(LuaPatchFormat.CurrentVersion);
        writer.Write(_manifestBytes.Length);
        writer.Write(_manifestBytes);
        WriteString(writer, Signature.Algorithm);
        WriteString(writer, Signature.KeyId);
        writer.Write(Signature.Value.Length);
        writer.Write(Signature.Value.AsSpan());
        writer.Write(Entries.Length);
        foreach (var entry in Entries)
        {
            WriteString(writer, entry.Name);
            writer.Write((long)entry.Content.Length);
            writer.Write(entry.Content.Span);
        }
    }

    private static ImmutableArray<LuaPatchEntry> NormalizeEntries(IEnumerable<LuaPatchEntry> entries)
    {
        var result = entries.OrderBy(static entry => entry.Name, StringComparer.Ordinal).ToImmutableArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var modules = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in result)
        {
            ValidateEntryName(entry.Name);
            if (!names.Add(entry.Name))
            {
                throw Format(LuaPatchErrorCode.DuplicateEntry, $"Duplicate patch entry '{entry.Name}'.");
            }

            if (entry.Kind == LuaPatchEntryKind.CompanionData)
            {
                if (entry.ModuleName is not null)
                {
                    throw Format(LuaPatchErrorCode.InvalidManifest, "Companion data cannot declare a Lua module name.");
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(entry.ModuleName))
                {
                    throw Format(LuaPatchErrorCode.InvalidManifest, "A Lua module entry requires a module name.");
                }

                if (!modules.Add(entry.ModuleName))
                {
                    throw Format(LuaPatchErrorCode.DuplicateModule, $"Duplicate patch module '{entry.ModuleName}'.");
                }
            }
        }

        if (result.IsEmpty)
        {
            throw Format(LuaPatchErrorCode.InvalidManifest, "A patch bundle must contain at least one entry.");
        }

        return result;
    }

    private static ImmutableArray<LuaPatchDependency> NormalizeDependencies(
        ImmutableArray<LuaPatchDependency> dependencies)
    {
        if (dependencies.IsDefaultOrEmpty)
        {
            return [];
        }

        var result = dependencies
            .OrderBy(static dependency => dependency.ModuleName, StringComparer.Ordinal)
            .ThenBy(static dependency => dependency.Kind)
            .ToImmutableArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in result)
        {
            if (string.IsNullOrWhiteSpace(dependency.ModuleName) || !Enum.IsDefined(dependency.Kind))
            {
                throw Format(LuaPatchErrorCode.InvalidManifest, "A patch dependency is invalid.");
            }

            if (!names.Add(dependency.ModuleName))
            {
                throw Format(
                    LuaPatchErrorCode.InvalidManifest,
                    $"Duplicate dependency '{dependency.ModuleName}'.");
            }
        }

        return result;
    }

    private static void ValidateEntriesAgainstManifest(
        LuaPatchManifest manifest,
        ImmutableArray<LuaPatchEntry> entries)
    {
        var normalized = NormalizeEntries(entries);
        if (normalized.Length != manifest.Entries.Length)
        {
            throw Format(LuaPatchErrorCode.MissingEntry, "Patch payload entries are incomplete.");
        }

        for (var index = 0; index < normalized.Length; index++)
        {
            var entry = normalized[index];
            var descriptor = manifest.Entries[index];
            if (!string.Equals(entry.Name, descriptor.Name, StringComparison.Ordinal) ||
                !string.Equals(entry.ModuleName, descriptor.ModuleName, StringComparison.Ordinal) ||
                entry.Kind != descriptor.Kind)
            {
                throw Format(LuaPatchErrorCode.EntryMetadataMismatch, "Patch entry metadata is inconsistent.");
            }
        }
    }

    private static void ValidateManifestHeader(
        LuaPatchManifest manifest,
        LuaPatchBundleReadOptions options,
        bool requireCanonicalClaims)
    {
        if (manifest.FormatVersion != LuaPatchFormat.CurrentVersion)
        {
            throw Format(LuaPatchErrorCode.UnsupportedFormatVersion, "The manifest format version is unsupported.");
        }

        if (string.IsNullOrWhiteSpace(manifest.PatchId) ||
            string.IsNullOrWhiteSpace(manifest.Channel) ||
            string.IsNullOrWhiteSpace(manifest.TargetBuild) ||
            string.IsNullOrWhiteSpace(manifest.BaseRevision) ||
            string.IsNullOrWhiteSpace(manifest.TargetRevision) ||
            string.IsNullOrWhiteSpace(manifest.RuntimeAbi) ||
            string.IsNullOrWhiteSpace(manifest.Nonce) ||
            !LuaLanguageVersions.IsKnown(manifest.LanguageVersion) ||
            !Enum.IsDefined(manifest.UpdateIntent) ||
            manifest.CreatedAt == default ||
            manifest.ExpiresAt is { } expiresAt && expiresAt <= manifest.CreatedAt)
        {
            throw Format(LuaPatchErrorCode.InvalidManifest, "The patch manifest header is invalid.");
        }

        ValidateCapabilities(
            manifest.RequiredCapabilities,
            options.MaximumCapabilityCount,
            options.MaximumCapabilityNameBytes,
            requireCanonicalClaims);
        ValidateTargetLabels(
            manifest.RequiredTargetLabels,
            options.MaximumTargetLabelCount,
            options.MaximumTargetLabelNameBytes,
            options.MaximumTargetLabelValueBytes,
            requireCanonicalClaims);

        if (!manifest.Entries.IsDefault && !manifest.Entries.IsEmpty)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            string? previous = null;
            foreach (var entry in manifest.Entries)
            {
                ValidateEntryName(entry.Name);
                if (!names.Add(entry.Name) ||
                    previous is not null && string.CompareOrdinal(previous, entry.Name) >= 0 ||
                    entry.Length < 0 || entry.ContentHash.Length != SHA256.HashSizeInBytes * 2 ||
                    !entry.ContentHash.All(static character =>
                        character is >= '0' and <= '9' or >= 'A' and <= 'F'))
                {
                    throw Format(LuaPatchErrorCode.InvalidManifest, "The patch entry manifest is invalid.");
                }

                previous = entry.Name;
            }
        }
    }

    private static void ValidateEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Contains('\\') ||
            name.Contains('\0') || name.Split('/').Any(static segment =>
                segment.Length == 0 || segment is "." or ".."))
        {
            throw Format(LuaPatchErrorCode.UnsafeEntryName, $"Patch entry name '{name}' is unsafe.");
        }
    }

    private static byte[] SerializeManifest(LuaPatchManifest manifest) =>
        LuaPatchManifestSerializer.Serialize(manifest);

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void ValidateOptions(LuaPatchBundleReadOptions options)
    {
        if (options.MaximumBundleBytes <= 0 || options.MaximumManifestBytes <= 0 ||
            options.MaximumEntryCount <= 0 || options.MaximumEntryBytes <= 0 ||
            options.MaximumTotalEntryBytes <= 0 || options.MaximumNameBytes <= 0 ||
            options.MaximumSignatureBytes <= 0 || options.MaximumCapabilityCount <= 0 ||
            options.MaximumCapabilityNameBytes <= 0 || options.MaximumTargetLabelCount <= 0 ||
            options.MaximumTargetLabelNameBytes <= 0 || options.MaximumTargetLabelValueBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Patch read limits must be positive.");
        }
    }

    private static LuaPatchFormatException Format(LuaPatchErrorCode code, string message) =>
        new(code, message);

    private static ImmutableArray<string> NormalizeCapabilities(
        ImmutableArray<string> capabilities)
    {
        if (capabilities.IsDefaultOrEmpty)
        {
            return [];
        }

        var normalized = capabilities.Order(StringComparer.Ordinal).ToImmutableArray();
        ValidateCapabilities(
            normalized,
            LuaPatchBundleReadOptions.Default.MaximumCapabilityCount,
            LuaPatchBundleReadOptions.Default.MaximumCapabilityNameBytes,
            true);
        return normalized;
    }

    private static void ValidateCapabilities(
        ImmutableArray<string> capabilities,
        int maximumCount,
        int maximumNameBytes,
        bool requireCanonicalOrder)
    {
        if (capabilities.IsDefaultOrEmpty)
        {
            return;
        }

        if (capabilities.Length > maximumCount)
        {
            throw Format(
                LuaPatchErrorCode.ResourceLimitExceeded,
                "The patch capability count exceeds its configured limit.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        string? previous = null;
        foreach (var capability in capabilities)
        {
            if (string.IsNullOrWhiteSpace(capability) ||
                !string.Equals(capability, capability.Trim(), StringComparison.Ordinal))
            {
                throw Format(
                    LuaPatchErrorCode.InvalidManifest,
                    "Patch capabilities must be unique, canonical, non-blank names.");
            }

            if (Encoding.UTF8.GetByteCount(capability) > maximumNameBytes)
            {
                throw Format(
                    LuaPatchErrorCode.ResourceLimitExceeded,
                    "A patch capability name exceeds its configured limit.");
            }

            if (!names.Add(capability) ||
                requireCanonicalOrder && previous is not null &&
                    string.CompareOrdinal(previous, capability) >= 0)
            {
                throw Format(
                    LuaPatchErrorCode.InvalidManifest,
                    "Patch capabilities must be unique, canonical, non-blank names.");
            }

            previous = capability;
        }
    }

    private static ImmutableArray<LuaPatchTargetLabel> NormalizeTargetLabels(
        ImmutableArray<LuaPatchTargetLabel> labels)
    {
        if (labels.IsDefaultOrEmpty)
        {
            return [];
        }

        var defaults = LuaPatchBundleReadOptions.Default;
        ValidateTargetLabels(
            labels,
            defaults.MaximumTargetLabelCount,
            defaults.MaximumTargetLabelNameBytes,
            defaults.MaximumTargetLabelValueBytes,
            false);
        var normalized = labels.OrderBy(static label => label.Name, StringComparer.Ordinal)
            .ToImmutableArray();
        ValidateTargetLabels(
            normalized,
            defaults.MaximumTargetLabelCount,
            defaults.MaximumTargetLabelNameBytes,
            defaults.MaximumTargetLabelValueBytes,
            true);
        return normalized;
    }

    private static void ValidateTargetLabels(
        ImmutableArray<LuaPatchTargetLabel> labels,
        int maximumCount,
        int maximumNameBytes,
        int maximumValueBytes,
        bool requireCanonicalOrder)
    {
        if (labels.IsDefaultOrEmpty)
        {
            return;
        }

        if (labels.Length > maximumCount)
        {
            throw Format(
                LuaPatchErrorCode.ResourceLimitExceeded,
                "The patch target-label count exceeds its configured limit.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        string? previous = null;
        foreach (var label in labels)
        {
            if (label is null || string.IsNullOrWhiteSpace(label.Name) ||
                !string.Equals(label.Name, label.Name.Trim(), StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(label.Value) ||
                !string.Equals(label.Value, label.Value.Trim(), StringComparison.Ordinal))
            {
                throw Format(
                    LuaPatchErrorCode.InvalidManifest,
                    "Patch target labels must have unique, canonical, non-blank names and values.");
            }

            if (Encoding.UTF8.GetByteCount(label.Name) > maximumNameBytes ||
                Encoding.UTF8.GetByteCount(label.Value) > maximumValueBytes)
            {
                throw Format(
                    LuaPatchErrorCode.ResourceLimitExceeded,
                    "A patch target-label name or value exceeds its configured limit.");
            }

            if (!names.Add(label.Name) || requireCanonicalOrder && previous is not null &&
                string.CompareOrdinal(previous, label.Name) >= 0)
            {
                throw Format(
                    LuaPatchErrorCode.InvalidManifest,
                    "Patch target labels must have unique, canonical, non-blank names and values.");
            }

            previous = label.Name;
        }
    }

    private static LuaPatchErrorCode MapTrustError(LuaPatchSignatureTrustStatus status) => status switch
    {
        LuaPatchSignatureTrustStatus.NotYetValid => LuaPatchErrorCode.SigningKeyNotYetValid,
        LuaPatchSignatureTrustStatus.Expired => LuaPatchErrorCode.SigningKeyExpired,
        LuaPatchSignatureTrustStatus.Revoked => LuaPatchErrorCode.SigningKeyRevoked,
        _ => LuaPatchErrorCode.UntrustedSigningKey,
    };

    private sealed class BoundedReader(Stream stream, long maximumBytes)
    {
        private long _read;

        public void ReadExactly(Span<byte> destination)
        {
            if (destination.Length > maximumBytes - _read)
            {
                throw Format(LuaPatchErrorCode.ResourceLimitExceeded, "The patch bundle exceeds its configured limit.");
            }

            try
            {
                stream.ReadExactly(destination);
                _read += destination.Length;
            }
            catch (EndOfStreamException exception)
            {
                throw new LuaPatchFormatException(
                    LuaPatchErrorCode.InvalidHeader,
                    "The patch bundle ended unexpectedly.",
                    exception);
            }
        }

        public byte[] ReadBytes(int count)
        {
            if (count < 0)
            {
                throw Format(LuaPatchErrorCode.InvalidHeader, "The patch contains a negative length.");
            }

            var bytes = GC.AllocateUninitializedArray<byte>(count);
            ReadExactly(bytes);
            return bytes;
        }

        public int ReadInt32()
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            ReadExactly(bytes);
            return BinaryPrimitives.ReadInt32LittleEndian(bytes);
        }

        public long ReadInt64()
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            ReadExactly(bytes);
            return BinaryPrimitives.ReadInt64LittleEndian(bytes);
        }

        public string ReadString(int maximumBytes)
        {
            var count = ReadInt32();
            if (count < 0 || count > maximumBytes)
            {
                throw Format(LuaPatchErrorCode.ResourceLimitExceeded, "A patch string exceeds its configured limit.");
            }

            try
            {
                return new UTF8Encoding(false, true).GetString(ReadBytes(count));
            }
            catch (DecoderFallbackException exception)
            {
                throw new LuaPatchFormatException(
                    LuaPatchErrorCode.InvalidHeader,
                    "A patch string is not valid UTF-8.",
                    exception);
            }
        }

        public bool TryReadByte(out byte value)
        {
            if (_read >= maximumBytes)
            {
                value = 0;
                return stream.ReadByte() >= 0
                    ? throw Format(LuaPatchErrorCode.ResourceLimitExceeded, "The patch bundle exceeds its configured limit.")
                    : false;
            }

            var next = stream.ReadByte();
            if (next < 0)
            {
                value = 0;
                return false;
            }

            _read++;
            value = (byte)next;
            return true;
        }
    }
}
