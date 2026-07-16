using Lunil.CodeGen.Cil.Caching;

namespace Lunil.CodeGen.Cil.Tests;

public sealed class LuaBackendCacheKeyTests
{
    [Fact]
    public void CanonicalDescriptorNormalizesHashesTargetsAndFeatureOrder()
    {
        var first = LuaBackendCacheKey.Create(CreateParameters() with
        {
            SourceContentHash = Hash('A'),
            TargetFramework = " NET10.0 ",
            RuntimeIdentifier = " WIN-X64 ",
            FeatureSet = ["Tracing", "nativeaot", "tracing"],
        });
        var second = LuaBackendCacheKey.Create(CreateParameters() with
        {
            SourceContentHash = Hash('a'),
            FeatureSet = ["nativeaot", "tracing"],
        });

        Assert.Equal(second.CacheId, first.CacheId);
        Assert.Equal(second.SerializeCanonicalDescriptor(), first.SerializeCanonicalDescriptor());
        Assert.Equal(["nativeaot", "tracing"], first.FeatureSet.ToArray());
    }

    [Fact]
    public void CanonicalDescriptorRoundTripsWithoutChangingIdentity()
    {
        var key = LuaBackendCacheKey.Create(CreateParameters());

        var roundTripped = LuaBackendCacheKey.ParseCanonicalDescriptor(
            key.SerializeCanonicalDescriptor());

        Assert.Equal(key.CacheId, roundTripped.CacheId);
        Assert.True(key.GetCompatibility(roundTripped).IsCompatible);
    }

    [Fact]
    public void DependencyHashIsOrderIndependentAndDeduplicated()
    {
        var first = LuaBackendCacheKey.ComputeDependencyHash(
            [Hash('b'), Hash('A'), Hash('a')]);
        var second = LuaBackendCacheKey.ComputeDependencyHash([Hash('a'), Hash('b')]);

        Assert.Equal(second, first);
        Assert.Equal(
            LuaBackendCacheKey.EmptyContentSha256,
            LuaBackendCacheKey.ComputeDependencyHash([]));
    }

    [Fact]
    public void EveryCompatibilityDimensionChangesContentAddress()
    {
        var baseline = CreateParameters();
        var variants = new LuaBackendCacheKeyParameters[]
        {
            baseline,
            baseline with { ArtifactKind = LuaBackendCacheArtifactKind.Profile },
            baseline with { SourceContentHash = Hash('d') },
            baseline with { CanonicalModuleHash = Hash('e') },
            baseline with { DependencyHash = Hash('f') },
            baseline with { SourceBindingId = "module:other" },
            baseline with { CacheKeySchemaVersion = 2 },
            baseline with { IrFormatVersion = 4 },
            baseline with { RuntimeAbiVersion = baseline.RuntimeAbiVersion + 1 },
            baseline with { CodegenVersion = baseline.CodegenVersion + 1 },
            baseline with { ProfileSchemaVersion = 2 },
            baseline with { ArtifactSchemaVersion = baseline.ArtifactSchemaVersion + 1 },
            baseline with { CompilerVersion = "0.6.0-alpha.6" },
            baseline with { Optimization = LuaBackendOptimizationMode.Debug },
            baseline with { DebugSymbols = true },
            baseline with { HookMode = LuaBackendHookMode.Exact },
            baseline with { SandboxMode = LuaBackendSandboxMode.Trusted },
            baseline with { TargetFramework = "net11.0" },
            baseline with { RuntimeIdentifier = "linux-x64" },
            baseline with { DeploymentMode = LuaBackendDeploymentMode.NativeAot },
            baseline with { TrimmingMode = LuaBackendTrimmingMode.Enabled },
            baseline with { FeatureSet = ["baseline", "extra"] },
        };

        var cacheIds = variants
            .Select(LuaBackendCacheKey.Create)
            .Select(static key => key.CacheId)
            .ToArray();

        Assert.Equal(cacheIds.Length, cacheIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CompatibilityReportsAllMismatchedDimensions()
    {
        var baseline = LuaBackendCacheKey.Create(CreateParameters());
        var candidate = LuaBackendCacheKey.Create(CreateParameters() with
        {
            ArtifactKind = LuaBackendCacheArtifactKind.Profile,
            SourceContentHash = Hash('d'),
            CanonicalModuleHash = Hash('e'),
            DependencyHash = Hash('f'),
            SourceBindingId = "module:other",
            CacheKeySchemaVersion = 2,
            IrFormatVersion = 4,
            RuntimeAbiVersion = baseline.RuntimeAbiVersion + 1,
            CodegenVersion = baseline.CodegenVersion + 1,
            ProfileSchemaVersion = 2,
            ArtifactSchemaVersion = baseline.ArtifactSchemaVersion + 1,
            CompilerVersion = "0.6.0-alpha.6",
            Optimization = LuaBackendOptimizationMode.Debug,
            DebugSymbols = true,
            HookMode = LuaBackendHookMode.Exact,
            SandboxMode = LuaBackendSandboxMode.Trusted,
            TargetFramework = "net11.0",
            RuntimeIdentifier = "linux-x64",
            DeploymentMode = LuaBackendDeploymentMode.NativeAot,
            TrimmingMode = LuaBackendTrimmingMode.Enabled,
            FeatureSet = ["different"],
        });

        var compatibility = baseline.GetCompatibility(candidate);

        Assert.False(compatibility.IsCompatible);
        Assert.Equal(
            LuaBackendCacheMismatch.CacheKeySchema |
            LuaBackendCacheMismatch.ArtifactKind |
            LuaBackendCacheMismatch.SourceContent |
            LuaBackendCacheMismatch.CanonicalModule |
            LuaBackendCacheMismatch.Dependencies |
            LuaBackendCacheMismatch.SourceBinding |
            LuaBackendCacheMismatch.IrFormat |
            LuaBackendCacheMismatch.RuntimeAbi |
            LuaBackendCacheMismatch.Codegen |
            LuaBackendCacheMismatch.ProfileSchema |
            LuaBackendCacheMismatch.ArtifactSchema |
            LuaBackendCacheMismatch.CompilerVersion |
            LuaBackendCacheMismatch.Optimization |
            LuaBackendCacheMismatch.DebugSymbols |
            LuaBackendCacheMismatch.HookMode |
            LuaBackendCacheMismatch.SandboxMode |
            LuaBackendCacheMismatch.TargetFramework |
            LuaBackendCacheMismatch.RuntimeIdentifier |
            LuaBackendCacheMismatch.DeploymentMode |
            LuaBackendCacheMismatch.TrimmingMode |
            LuaBackendCacheMismatch.FeatureSet,
            compatibility.Mismatches);
    }

    [Fact]
    public void RejectsMalformedOrUnsupportedDescriptors()
    {
        Assert.Throws<ArgumentException>(() => LuaBackendCacheKey.Create(
            CreateParameters() with { SourceContentHash = "not-a-hash" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => LuaBackendCacheKey.Create(
            CreateParameters() with { RuntimeAbiVersion = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => LuaBackendCacheKey.Create(
            CreateParameters() with
            {
                DeploymentMode = (LuaBackendDeploymentMode)byte.MaxValue,
            }));
        Assert.Throws<ArgumentException>(() => LuaBackendCacheKey.Create(
            CreateParameters() with { FeatureSet = [" "] }));
        Assert.Throws<InvalidDataException>(() =>
            LuaBackendCacheKey.ParseCanonicalDescriptor("{}"u8));
    }

    private static LuaBackendCacheKeyParameters CreateParameters() => new()
    {
        ArtifactKind = LuaBackendCacheArtifactKind.PersistedCil,
        SourceContentHash = Hash('a'),
        CanonicalModuleHash = Hash('b'),
        DependencyHash = Hash('c'),
        SourceBindingId = "module:sample",
        CompilerVersion = "0.6.0-alpha.5",
        Optimization = LuaBackendOptimizationMode.Release,
        DebugSymbols = false,
        HookMode = LuaBackendHookMode.Disabled,
        SandboxMode = LuaBackendSandboxMode.Restricted,
        TargetFramework = "net10.0",
        RuntimeIdentifier = "win-x64",
        DeploymentMode = LuaBackendDeploymentMode.CoreClr,
        TrimmingMode = LuaBackendTrimmingMode.Disabled,
        FeatureSet = ["baseline"],
    };

    private static string Hash(char character) => new(character, 64);
}
