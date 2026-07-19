namespace Lunil.Core;

/// <summary>Identifies the Lua language and runtime contract selected for a compilation or state.</summary>
public enum LuaLanguageVersion : byte
{
    Lua51 = 0x51,
    Lua52 = 0x52,
    Lua53 = 0x53,
    Lua54 = 0x54,
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
