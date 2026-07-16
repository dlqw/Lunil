using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lunil.CodeGen.Cil.Artifacts;
using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;

namespace Lunil.CodeGen.Cil.Caching;

public enum LuaBackendCacheArtifactKind : byte
{
    CanonicalIr,
    PersistedCil,
    Profile,
}

public enum LuaBackendOptimizationMode : byte
{
    Debug,
    Release,
}

public enum LuaBackendHookMode : byte
{
    Disabled,
    Exact,
}

public enum LuaBackendSandboxMode : byte
{
    Default,
    Trusted,
    Restricted,
}

public enum LuaBackendDeploymentMode : byte
{
    Portable,
    CoreClr,
    ReadyToRun,
    NativeAot,
}

public enum LuaBackendTrimmingMode : byte
{
    Disabled,
    Enabled,
}

[Flags]
public enum LuaBackendCacheMismatch : uint
{
    None = 0,
    CacheKeySchema = 1U << 0,
    ArtifactKind = 1U << 1,
    SourceContent = 1U << 2,
    CanonicalModule = 1U << 3,
    Dependencies = 1U << 4,
    SourceBinding = 1U << 5,
    IrFormat = 1U << 6,
    RuntimeAbi = 1U << 7,
    Codegen = 1U << 8,
    ProfileSchema = 1U << 9,
    ArtifactSchema = 1U << 10,
    CompilerVersion = 1U << 11,
    Optimization = 1U << 12,
    DebugSymbols = 1U << 13,
    HookMode = 1U << 14,
    SandboxMode = 1U << 15,
    TargetFramework = 1U << 16,
    RuntimeIdentifier = 1U << 17,
    DeploymentMode = 1U << 18,
    TrimmingMode = 1U << 19,
    FeatureSet = 1U << 20,
}

public readonly record struct LuaBackendCacheCompatibility(
    LuaBackendCacheMismatch Mismatches)
{
    public bool IsCompatible => Mismatches == LuaBackendCacheMismatch.None;
}

public sealed record LuaBackendCacheKeyParameters
{
    public required LuaBackendCacheArtifactKind ArtifactKind { get; init; }

    public required string SourceContentHash { get; init; }

    public required string CanonicalModuleHash { get; init; }

    public string DependencyHash { get; init; } = LuaBackendCacheKey.EmptyContentSha256;

    public required string SourceBindingId { get; init; }

    public int CacheKeySchemaVersion { get; init; } =
        LuaBackendCacheKey.CurrentCacheKeySchemaVersion;

    public int IrFormatVersion { get; init; } = LuaIrModule.CurrentFormatVersion;

    public int RuntimeAbiVersion { get; init; } = LuaCodegenAbiV4.RuntimeAbiVersion;

    public int CodegenVersion { get; init; } = LuaAotArtifactManifest.CurrentCodegenVersion;

    public int ProfileSchemaVersion { get; init; } =
        LuaBackendCacheKey.CurrentProfileSchemaVersion;

    public int ArtifactSchemaVersion { get; init; } =
        LuaAotArtifactManifest.CurrentArtifactSchemaVersion;

    public required string CompilerVersion { get; init; }

    public required LuaBackendOptimizationMode Optimization { get; init; }

    public required bool DebugSymbols { get; init; }

    public required LuaBackendHookMode HookMode { get; init; }

    public required LuaBackendSandboxMode SandboxMode { get; init; }

    public required string TargetFramework { get; init; }

    public required string RuntimeIdentifier { get; init; }

    public required LuaBackendDeploymentMode DeploymentMode { get; init; }

    public required LuaBackendTrimmingMode TrimmingMode { get; init; }

    public IReadOnlyCollection<string> FeatureSet { get; init; } = [];
}

/// <summary>
/// Complete, normalized identity for persistent backend data. Deliberately excludes JIT machine
/// code: only canonical IR, persisted CIL, and owner-free profiles are valid persistent kinds.
/// </summary>
public sealed class LuaBackendCacheKey
{
    public const int CurrentCacheKeySchemaVersion = 1;
    public const int CurrentProfileSchemaVersion = 1;
    public const string EmptyContentSha256 =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private const string Magic = "LUNIL-BACKEND-CACHE-KEY";
    private const int MaximumDescriptorBytes = 64 * 1024;

    private LuaBackendCacheKey(
        LuaBackendCacheKeyParameters parameters,
        ImmutableArray<string> featureSet)
    {
        ArtifactKind = parameters.ArtifactKind;
        SourceContentHash = NormalizeHash(
            parameters.SourceContentHash,
            nameof(parameters.SourceContentHash));
        CanonicalModuleHash = NormalizeHash(
            parameters.CanonicalModuleHash,
            nameof(parameters.CanonicalModuleHash));
        DependencyHash = NormalizeHash(
            parameters.DependencyHash,
            nameof(parameters.DependencyHash));
        SourceBindingId = NormalizeRequired(
            parameters.SourceBindingId,
            nameof(parameters.SourceBindingId));
        CacheKeySchemaVersion = Positive(
            parameters.CacheKeySchemaVersion,
            nameof(parameters.CacheKeySchemaVersion));
        IrFormatVersion = Positive(
            parameters.IrFormatVersion,
            nameof(parameters.IrFormatVersion));
        RuntimeAbiVersion = Positive(
            parameters.RuntimeAbiVersion,
            nameof(parameters.RuntimeAbiVersion));
        CodegenVersion = Positive(parameters.CodegenVersion, nameof(parameters.CodegenVersion));
        ProfileSchemaVersion = Positive(
            parameters.ProfileSchemaVersion,
            nameof(parameters.ProfileSchemaVersion));
        ArtifactSchemaVersion = Positive(
            parameters.ArtifactSchemaVersion,
            nameof(parameters.ArtifactSchemaVersion));
        CompilerVersion = NormalizeRequired(
            parameters.CompilerVersion,
            nameof(parameters.CompilerVersion));
        Optimization = Defined(parameters.Optimization, nameof(parameters.Optimization));
        DebugSymbols = parameters.DebugSymbols;
        HookMode = Defined(parameters.HookMode, nameof(parameters.HookMode));
        SandboxMode = Defined(parameters.SandboxMode, nameof(parameters.SandboxMode));
        TargetFramework = NormalizeIdentifier(
            parameters.TargetFramework,
            nameof(parameters.TargetFramework));
        RuntimeIdentifier = NormalizeIdentifier(
            parameters.RuntimeIdentifier,
            nameof(parameters.RuntimeIdentifier));
        DeploymentMode = Defined(parameters.DeploymentMode, nameof(parameters.DeploymentMode));
        TrimmingMode = Defined(parameters.TrimmingMode, nameof(parameters.TrimmingMode));
        FeatureSet = featureSet;
        _ = Defined(parameters.ArtifactKind, nameof(parameters.ArtifactKind));

        var descriptor = SerializeCanonicalDescriptor();
        CacheId = Convert.ToHexStringLower(SHA256.HashData(descriptor));
    }

    public string CacheId { get; }

    public LuaBackendCacheArtifactKind ArtifactKind { get; }

    public string SourceContentHash { get; }

    public string CanonicalModuleHash { get; }

    public string DependencyHash { get; }

    public string SourceBindingId { get; }

    public int CacheKeySchemaVersion { get; }

    public int IrFormatVersion { get; }

    public int RuntimeAbiVersion { get; }

    public int CodegenVersion { get; }

    public int ProfileSchemaVersion { get; }

    public int ArtifactSchemaVersion { get; }

    public string CompilerVersion { get; }

    public LuaBackendOptimizationMode Optimization { get; }

    public bool DebugSymbols { get; }

    public LuaBackendHookMode HookMode { get; }

    public LuaBackendSandboxMode SandboxMode { get; }

    public string TargetFramework { get; }

    public string RuntimeIdentifier { get; }

    public LuaBackendDeploymentMode DeploymentMode { get; }

    public LuaBackendTrimmingMode TrimmingMode { get; }

    public ImmutableArray<string> FeatureSet { get; }

    public static LuaBackendCacheKey Create(LuaBackendCacheKeyParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(parameters.FeatureSet);
        var featureSet = parameters.FeatureSet
            .Select(static feature => NormalizeIdentifier(feature, "featureSet"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        return new LuaBackendCacheKey(parameters, featureSet);
    }

    public static string ComputeContentHash(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    public static string ComputeDependencyHash(IEnumerable<string> contentHashes)
    {
        ArgumentNullException.ThrowIfNull(contentHashes);
        var normalized = contentHashes
            .Select(static hash => NormalizeHash(hash, "contentHashes"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return ComputeContentHash(Encoding.ASCII.GetBytes(string.Join('\n', normalized)));
    }

    public static LuaBackendCacheKey ParseCanonicalDescriptor(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.IsEmpty || descriptor.Length > MaximumDescriptorBytes)
        {
            throw new InvalidDataException("Backend cache key descriptor size is invalid.");
        }

        using var document = JsonDocument.Parse(descriptor.ToArray());
        var root = document.RootElement;
        if (!string.Equals(RequiredString(root, "magic"), Magic, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Backend cache key magic is invalid.");
        }

        var features = Required(root, "featureSet")
            .EnumerateArray()
            .Select(static value => value.GetString() ??
                throw new InvalidDataException("Backend cache feature is null."))
            .ToArray();
        return Create(new LuaBackendCacheKeyParameters
        {
            ArtifactKind = RequiredEnum<LuaBackendCacheArtifactKind>(root, "artifactKind"),
            SourceContentHash = RequiredString(root, "sourceContentHash"),
            CanonicalModuleHash = RequiredString(root, "canonicalModuleHash"),
            DependencyHash = RequiredString(root, "dependencyHash"),
            SourceBindingId = RequiredString(root, "sourceBindingId"),
            CacheKeySchemaVersion = RequiredInt32(root, "cacheKeySchemaVersion"),
            IrFormatVersion = RequiredInt32(root, "irFormatVersion"),
            RuntimeAbiVersion = RequiredInt32(root, "runtimeAbiVersion"),
            CodegenVersion = RequiredInt32(root, "codegenVersion"),
            ProfileSchemaVersion = RequiredInt32(root, "profileSchemaVersion"),
            ArtifactSchemaVersion = RequiredInt32(root, "artifactSchemaVersion"),
            CompilerVersion = RequiredString(root, "compilerVersion"),
            Optimization = RequiredEnum<LuaBackendOptimizationMode>(root, "optimization"),
            DebugSymbols = Required(root, "debugSymbols").GetBoolean(),
            HookMode = RequiredEnum<LuaBackendHookMode>(root, "hookMode"),
            SandboxMode = RequiredEnum<LuaBackendSandboxMode>(root, "sandboxMode"),
            TargetFramework = RequiredString(root, "targetFramework"),
            RuntimeIdentifier = RequiredString(root, "runtimeIdentifier"),
            DeploymentMode = RequiredEnum<LuaBackendDeploymentMode>(root, "deploymentMode"),
            TrimmingMode = RequiredEnum<LuaBackendTrimmingMode>(root, "trimmingMode"),
            FeatureSet = features,
        });
    }

    public byte[] SerializeCanonicalDescriptor()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("magic", Magic);
            writer.WriteNumber("cacheKeySchemaVersion", CacheKeySchemaVersion);
            writer.WriteNumber("artifactKind", (int)ArtifactKind);
            writer.WriteString("sourceContentHash", SourceContentHash);
            writer.WriteString("canonicalModuleHash", CanonicalModuleHash);
            writer.WriteString("dependencyHash", DependencyHash);
            writer.WriteString("sourceBindingId", SourceBindingId);
            writer.WriteNumber("irFormatVersion", IrFormatVersion);
            writer.WriteNumber("runtimeAbiVersion", RuntimeAbiVersion);
            writer.WriteNumber("codegenVersion", CodegenVersion);
            writer.WriteNumber("profileSchemaVersion", ProfileSchemaVersion);
            writer.WriteNumber("artifactSchemaVersion", ArtifactSchemaVersion);
            writer.WriteString("compilerVersion", CompilerVersion);
            writer.WriteNumber("optimization", (int)Optimization);
            writer.WriteBoolean("debugSymbols", DebugSymbols);
            writer.WriteNumber("hookMode", (int)HookMode);
            writer.WriteNumber("sandboxMode", (int)SandboxMode);
            writer.WriteString("targetFramework", TargetFramework);
            writer.WriteString("runtimeIdentifier", RuntimeIdentifier);
            writer.WriteNumber("deploymentMode", (int)DeploymentMode);
            writer.WriteNumber("trimmingMode", (int)TrimmingMode);
            writer.WriteStartArray("featureSet");
            foreach (var feature in FeatureSet)
            {
                writer.WriteStringValue(feature);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public LuaBackendCacheCompatibility GetCompatibility(LuaBackendCacheKey candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var mismatches = LuaBackendCacheMismatch.None;
        AddMismatch(CacheKeySchemaVersion != candidate.CacheKeySchemaVersion,
            LuaBackendCacheMismatch.CacheKeySchema, ref mismatches);
        AddMismatch(ArtifactKind != candidate.ArtifactKind,
            LuaBackendCacheMismatch.ArtifactKind, ref mismatches);
        AddMismatch(!StringEquals(SourceContentHash, candidate.SourceContentHash),
            LuaBackendCacheMismatch.SourceContent, ref mismatches);
        AddMismatch(!StringEquals(CanonicalModuleHash, candidate.CanonicalModuleHash),
            LuaBackendCacheMismatch.CanonicalModule, ref mismatches);
        AddMismatch(!StringEquals(DependencyHash, candidate.DependencyHash),
            LuaBackendCacheMismatch.Dependencies, ref mismatches);
        AddMismatch(!StringEquals(SourceBindingId, candidate.SourceBindingId),
            LuaBackendCacheMismatch.SourceBinding, ref mismatches);
        AddMismatch(IrFormatVersion != candidate.IrFormatVersion,
            LuaBackendCacheMismatch.IrFormat, ref mismatches);
        AddMismatch(RuntimeAbiVersion != candidate.RuntimeAbiVersion,
            LuaBackendCacheMismatch.RuntimeAbi, ref mismatches);
        AddMismatch(CodegenVersion != candidate.CodegenVersion,
            LuaBackendCacheMismatch.Codegen, ref mismatches);
        AddMismatch(ProfileSchemaVersion != candidate.ProfileSchemaVersion,
            LuaBackendCacheMismatch.ProfileSchema, ref mismatches);
        AddMismatch(ArtifactSchemaVersion != candidate.ArtifactSchemaVersion,
            LuaBackendCacheMismatch.ArtifactSchema, ref mismatches);
        AddMismatch(!StringEquals(CompilerVersion, candidate.CompilerVersion),
            LuaBackendCacheMismatch.CompilerVersion, ref mismatches);
        AddMismatch(Optimization != candidate.Optimization,
            LuaBackendCacheMismatch.Optimization, ref mismatches);
        AddMismatch(DebugSymbols != candidate.DebugSymbols,
            LuaBackendCacheMismatch.DebugSymbols, ref mismatches);
        AddMismatch(HookMode != candidate.HookMode,
            LuaBackendCacheMismatch.HookMode, ref mismatches);
        AddMismatch(SandboxMode != candidate.SandboxMode,
            LuaBackendCacheMismatch.SandboxMode, ref mismatches);
        AddMismatch(!StringEquals(TargetFramework, candidate.TargetFramework),
            LuaBackendCacheMismatch.TargetFramework, ref mismatches);
        AddMismatch(!StringEquals(RuntimeIdentifier, candidate.RuntimeIdentifier),
            LuaBackendCacheMismatch.RuntimeIdentifier, ref mismatches);
        AddMismatch(DeploymentMode != candidate.DeploymentMode,
            LuaBackendCacheMismatch.DeploymentMode, ref mismatches);
        AddMismatch(TrimmingMode != candidate.TrimmingMode,
            LuaBackendCacheMismatch.TrimmingMode, ref mismatches);
        AddMismatch(!FeatureSet.SequenceEqual(candidate.FeatureSet, StringComparer.Ordinal),
            LuaBackendCacheMismatch.FeatureSet, ref mismatches);
        return new LuaBackendCacheCompatibility(mismatches);
    }

    private static void AddMismatch(
        bool condition,
        LuaBackendCacheMismatch mismatch,
        ref LuaBackendCacheMismatch mismatches)
    {
        if (condition)
        {
            mismatches |= mismatch;
        }
    }

    private static T Defined<T>(T value, string parameterName)
        where T : struct, Enum => Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, value, "Value is not defined.");

    private static int Positive(int value, string parameterName) => value > 0
        ? value
        : throw new ArgumentOutOfRangeException(parameterName, value, "Value must be positive.");

    private static string NormalizeHash(string value, string parameterName)
    {
        var normalized = NormalizeRequired(value, parameterName).ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(static character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Value must be a SHA-256 hexadecimal digest.", parameterName);
        }

        return normalized;
    }

    private static string NormalizeIdentifier(string value, string parameterName) =>
        NormalizeRequired(value, parameterName).ToLowerInvariant();

    private static string NormalizeRequired(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static JsonElement Required(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value
            : throw new InvalidDataException(
                $"Backend cache key property '{property}' is missing.");

    private static string RequiredString(JsonElement element, string property) =>
        Required(element, property).GetString() ??
        throw new InvalidDataException($"Backend cache key property '{property}' is null.");

    private static int RequiredInt32(JsonElement element, string property) =>
        Required(element, property).GetInt32();

    private static T RequiredEnum<T>(JsonElement element, string property)
        where T : struct, Enum
    {
        var value = RequiredInt32(element, property);
        var candidate = (T)Enum.ToObject(typeof(T), value);
        return Enum.IsDefined(candidate)
            ? candidate
            : throw new InvalidDataException(
                $"Backend cache key property '{property}' has an invalid value.");
    }

    private static bool StringEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);
}
