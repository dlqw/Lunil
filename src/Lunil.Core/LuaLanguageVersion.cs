namespace Lunil.Core;

/// <summary>Binary chunk family used by a compiled language-version adapter.</summary>
public enum LuaChunkFormat : byte
{
    None = 0,
    Lua53 = 1,
    Lua54 = 2,
    Lua52 = 3,
    Lua51 = 4,
    Lua55 = 5,
}

/// <summary>Identifies the Lua language and runtime contract selected for a compilation or state.</summary>
public enum LuaLanguageVersion : byte
{
    [LuaVersionProfile(
        ChunkFormat = LuaChunkFormat.Lua51,
        HasGlobalUnpack = true,
        HasLoadString = true,
        HasModuleLibrary = true,
        HasLegacyTable = true,
        HasStringGFind = true,
        HasLegacyMath = true,
        HasPackageLoaders = true,
        HasPackageSeeAll = true)]
    Lua51 = 0x51,
    [LuaVersionProfile(
        ChunkFormat = LuaChunkFormat.Lua52,
        CachesClosuresByUpvalues = true,
        HasBit32Library = true,
        HasRawLength = true,
        HasGlobalUnpack = true,
        HasLoadString = true,
        HasModuleLibrary = true,
        HasLegacyTable = true,
        HasTableMove = false,
        HasTablePack = true,
        HasStringPack = false,
        HasLegacyMath = true,
        HasPackageSearchers = true,
        HasPackageLoaders = true,
        HasPackageSeeAll = true)]
    Lua52 = 0x52,
    [LuaVersionProfile(
        ChunkFormat = LuaChunkFormat.Lua53,
        SynchronousFinalizerErrors = true,
        PreservesDeadThreadOpenUpvalues = true,
        CachesClosuresByUpvalues = true,
        HasUtf8Library = true,
        HasRawLength = true,
        HasTableMove = true,
        HasTablePack = true,
        HasStringPack = true,
        HasLegacyMath = true,
        HasPackageSearchers = true)]
    Lua53 = 0x53,
    [LuaVersionProfile(
        ChunkFormat = LuaChunkFormat.Lua54,
        SupportsGenerationalCollection = true,
        HasToBeClosedProtocol = true,
        HasWarnLibrary = true,
        HasCoroutineClose = true,
        HasUtf8Library = true,
        HasRawLength = true,
        HasTableMove = true,
        HasTablePack = true,
        HasStringPack = true,
        HasLegacyMath = true,
        HasDebugSetCStackLimit = true,
        HasPackageSearchers = true)]
    Lua54 = 0x54,
    [LuaVersionProfile(
        ChunkFormat = LuaChunkFormat.Lua55,
        SupportsGenerationalCollection = true,
        HasToBeClosedProtocol = true,
        HasWarnLibrary = true,
        HasCoroutineClose = true,
        HasUtf8Library = true,
        HasRawLength = true,
        HasTableMove = true,
        HasTablePack = true,
        HasTableCreate = true,
        HasStringPack = true,
        HasPackageSearchers = true)]
    Lua55 = 0x55,
}

/// <summary>Validation, display, and configuration helpers for supported Lua version identities.</summary>
public static class LuaLanguageVersions
{
    /// <summary>The compatibility-preserving default used when no version is selected explicitly.</summary>
    public const LuaLanguageVersion Default = LuaLanguageVersion.Lua54;

    public static bool IsKnown(LuaLanguageVersion version) => version is
        LuaLanguageVersion.Lua51 or
        LuaLanguageVersion.Lua52 or
        LuaLanguageVersion.Lua53 or
        LuaLanguageVersion.Lua54 or
        LuaLanguageVersion.Lua55;

    /// <summary>
    /// Returns whether the current build contains an adapter for <paramref name="version"/>.
    /// This is generated from compile-time adapter symbols and version metadata; callers must
    /// not infer support from enum membership alone.
    /// </summary>
    public static bool IsImplemented(LuaLanguageVersion version) =>
        IsKnown(version) && LuaVersionFeatureTable.Get(version).IsImplemented;

    public static string GetDisplayName(LuaLanguageVersion version) => version switch
    {
        LuaLanguageVersion.Lua51 => "Lua 5.1",
        LuaLanguageVersion.Lua52 => "Lua 5.2",
        LuaLanguageVersion.Lua53 => "Lua 5.3",
        LuaLanguageVersion.Lua54 => "Lua 5.4",
        LuaLanguageVersion.Lua55 => "Lua 5.5",
        _ => throw new ArgumentOutOfRangeException(nameof(version), version, "Unknown Lua language version."),
    };

    public static bool TryParse(string? value, out LuaLanguageVersion version)
    {
        version = value?.Trim() switch
        {
            "5.1" or "Lua 5.1" or "lua 5.1" => LuaLanguageVersion.Lua51,
            "5.2" or "Lua 5.2" or "lua 5.2" => LuaLanguageVersion.Lua52,
            "5.3" or "Lua 5.3" or "lua 5.3" => LuaLanguageVersion.Lua53,
            "5.4" or "Lua 5.4" or "lua 5.4" => LuaLanguageVersion.Lua54,
            "5.5" or "Lua 5.5" or "lua 5.5" => LuaLanguageVersion.Lua55,
            _ => default,
        };
        return IsKnown(version);
    }
}
