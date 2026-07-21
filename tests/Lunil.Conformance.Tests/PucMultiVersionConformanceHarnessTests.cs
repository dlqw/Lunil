using System.Security.Cryptography;
using System.Text.Json;
using Lunil.Core;
using Lunil.Hosting;
using Lunil.Runtime.Execution;
using Lunil.StandardLibrary;

namespace Lunil.Conformance.Tests;

/// <summary>
/// Structural harness for Lua 5.1 / 5.2 / 5.5 official suites. Archives are optional fixtures;
/// when present they must match the pinned SHA-256 and classification manifest.
/// </summary>
public sealed class PucMultiVersionConformanceHarnessTests
{
    private static readonly string[] AllowedClassifications =
        ["driver-or-helper", "executed-user-mode", "excluded-user-mode"];

    public static IEnumerable<object[]> VersionSpecs() =>
    [
        new object[] { "5.1", "lua-5.1.5-tests", LuaLanguageVersion.Lua51 },
        new object[] { "5.2", "lua-5.2.4-tests", LuaLanguageVersion.Lua52 },
        new object[] { "5.5", "lua-5.5.0-tests", LuaLanguageVersion.Lua55 },
    ];

    [Theory]
    [MemberData(nameof(VersionSpecs))]
    public void ClassificationManifestIsValidWhenPresent(
        string label,
        string suiteDirectoryName,
        LuaLanguageVersion version)
    {
        _ = version;
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "multi-version", label);
        var manifestPath = Path.Combine(fixtures, "manifest.json");
        Assert.True(File.Exists(manifestPath), manifestPath);

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;
        Assert.Equal("lunil.lua-conformance-manifest.v1", root.GetProperty("schema").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("archiveSha256").GetString()));
        var suiteDirectory = Path.GetFullPath(Path.Combine(fixtures, suiteDirectoryName));
        Assert.True(Directory.Exists(suiteDirectory), suiteDirectory);
        var files = root.GetProperty("files");
        Assert.True(files.GetArrayLength() > 0);
        foreach (var entry in files.EnumerateArray())
        {
            var classification = entry.GetProperty("classification").GetString();
            Assert.Contains(classification, AllowedClassifications);
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("reason").GetString()));
            var fixturePath = Path.GetFullPath(Path.Combine(
                suiteDirectory,
                entry.GetProperty("path").GetString()!));
            Assert.StartsWith(
                suiteDirectory + Path.DirectorySeparatorChar,
                fixturePath,
                StringComparison.Ordinal);
            Assert.True(File.Exists(fixturePath), fixturePath);
            var fixtureSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixturePath)));
            Assert.Equal(entry.GetProperty("sha256").GetString()!.ToUpperInvariant(), fixtureSha256);
        }

        var archiveName = root.GetProperty("archiveFileName").GetString();
        var archivePath = Path.Combine(fixtures, archiveName!);
        Assert.True(File.Exists(archivePath), archivePath);
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath)));
        Assert.Equal(root.GetProperty("archiveSha256").GetString()!.ToUpperInvariant(), actual);
    }

    [Theory]
    [MemberData(nameof(VersionSpecs))]
    public void HostCanExecutePinnedSmokeScriptsForVersion(
        string label,
        string suiteDirectoryName,
        LuaLanguageVersion version)
    {
        _ = label;
        _ = suiteDirectoryName;
        using var host = new LuaHost(new LuaHostOptions
        {
            Profile = LuaHostProfile.Trusted,
            LanguageVersion = version,
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            Execution = LuaInterpreterOptions.Default with
            {
                MaximumInstructionCount = 10_000_000,
            },
        });
        var run = host.RunUtf8("return 1 + 2 + 3", "@smoke.lua");
        Assert.True(run.Compilation.Succeeded);
        Assert.Equal(LuaVmSignal.Completed, run.Execution!.Signal);
    }
}
