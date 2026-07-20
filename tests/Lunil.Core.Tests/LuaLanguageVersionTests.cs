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

    [Fact]
    public void ReportsOnlyCompileTimeEnabledAdaptersAsImplemented()
    {
        Assert.True(LuaLanguageVersions.IsImplemented(LuaLanguageVersion.Lua51));
        Assert.True(LuaLanguageVersions.IsImplemented(LuaLanguageVersion.Lua52));
        Assert.True(LuaLanguageVersions.IsImplemented(LuaLanguageVersion.Lua53));
        Assert.True(LuaLanguageVersions.IsImplemented(LuaLanguageVersion.Lua54));
        Assert.True(LuaLanguageVersions.IsImplemented(LuaLanguageVersion.Lua55));
    }

    [Fact]
    public void GeneratedProfilesKeepChunkAndSurfaceCapabilitiesVersioned()
    {
        var lua53 = LuaVersionFeatureTable.Get(LuaLanguageVersion.Lua53);
        Assert.Equal(LuaChunkFormat.Lua53, lua53.ChunkFormat);
        Assert.True(lua53.SynchronousFinalizerErrors);
        Assert.False(lua53.HasWarnLibrary);
        Assert.False(lua53.HasCoroutineClose);
        Assert.True(lua53.HasUtf8Library);

        var lua52 = LuaVersionFeatureTable.Get(LuaLanguageVersion.Lua52);
        Assert.Equal(LuaChunkFormat.Lua52, lua52.ChunkFormat);
        Assert.True(lua52.HasBit32Library);
        Assert.False(lua52.HasUtf8Library);

        var lua54 = LuaVersionFeatureTable.Get(LuaLanguageVersion.Lua54);
        Assert.Equal(LuaChunkFormat.Lua54, lua54.ChunkFormat);
        Assert.False(lua54.SynchronousFinalizerErrors);
        Assert.True(lua54.HasWarnLibrary);
        Assert.True(lua54.HasCoroutineClose);

        var lua55 = LuaVersionFeatureTable.Get(LuaLanguageVersion.Lua55);
        Assert.Equal(LuaChunkFormat.Lua55, lua55.ChunkFormat);
        Assert.True(lua55.HasWarnLibrary);
        Assert.True(lua55.HasCoroutineClose);
        Assert.True(lua55.HasUtf8Library);
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
