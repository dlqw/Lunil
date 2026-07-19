namespace Lunil.Conformance.Tests;

public sealed class PucLua53ConformanceTests
{
    private static readonly string[] OfficialUserModeFixtures =
    [
        "literals.lua",
        "bitwise.lua",
        "goto.lua",
        "vararg.lua",
        "closure.lua",
        "constructs.lua",
    ];

    [Fact]
    public void OfficialLua53ArchiveAndSelectedFixturesArePinned()
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var archive = Path.Combine(fixtures, "lua-5.3.4-tests.tar.gz");
        Assert.True(File.Exists(archive));
        Assert.Equal(
            "B80771238271C72565E5A1183292EF31BD7166414CD0D43A8EB79845FA7F599F",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archive))));

        foreach (var fixture in OfficialUserModeFixtures)
        {
            Assert.True(File.Exists(Path.Combine(fixtures, "lua-5.3.4-selected", fixture)), fixture);
        }
    }

    [Fact]
    public void SelectedOfficialLua53FixturesRetainUpstreamIdentity()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "lua-5.3.4-selected");
        foreach (var fixture in OfficialUserModeFixtures)
        {
            var source = File.ReadAllText(Path.Combine(root, fixture));
            Assert.Contains("See Copyright Notice", source, StringComparison.Ordinal);
            Assert.Contains("$Id:", source, StringComparison.Ordinal);
        }
    }

}
