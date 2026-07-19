using Lunil.Core;

namespace Lunil.Core.Tests;

public sealed class LuaLanguageVersionTests
{
    [Fact]
    public void KnowsExactlyTheFivePlannedLanguageContracts()
    {
        Assert.Equal(LuaLanguageVersion.Lua54, LuaLanguageVersions.Default);
        Assert.All(
            new[]
            {
                LuaLanguageVersion.Lua51,
                LuaLanguageVersion.Lua52,
                LuaLanguageVersion.Lua53,
                LuaLanguageVersion.Lua54,
                LuaLanguageVersion.Lua55,
            },
            version => Assert.True(LuaLanguageVersions.IsKnown(version)));
        Assert.False(LuaLanguageVersions.IsKnown((LuaLanguageVersion)0x56));
    }

    [Theory]
    [InlineData("5.1", LuaLanguageVersion.Lua51, "Lua 5.1")]
    [InlineData("Lua 5.4", LuaLanguageVersion.Lua54, "Lua 5.4")]
    [InlineData(" lua 5.5 ", LuaLanguageVersion.Lua55, "Lua 5.5")]
    public void ParsesAndDisplaysConfiguredVersions(
        string text,
        LuaLanguageVersion expected,
        string displayName)
    {
        Assert.True(LuaLanguageVersions.TryParse(text, out var actual));
        Assert.Equal(expected, actual);
        Assert.Equal(displayName, LuaLanguageVersions.GetDisplayName(actual));
    }

    [Fact]
    public void RejectsUnknownConfigurationText()
    {
        Assert.False(LuaLanguageVersions.TryParse("5.6", out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LuaLanguageVersions.GetDisplayName((LuaLanguageVersion)0x56));
    }
}
