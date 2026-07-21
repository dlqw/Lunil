using System.Security.Cryptography;
using System.Text.Json;
using Lunil.Core;
using Lunil.Hosting;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.StandardLibrary;

namespace Lunil.Conformance.Tests;

/// <summary>
/// Structural harness for Lua 5.1-5.5. Official archives must match their pinned SHA-256 and
/// classification manifest; local semantic fixtures execute with typed result assertions.
/// </summary>
public sealed class PucMultiVersionConformanceHarnessTests
{
    private static readonly string[] AllowedClassifications =
        ["driver-or-helper", "executed-user-mode", "excluded-user-mode"];

    public static IEnumerable<object[]> VersionSpecs() =>
    [
        new object[] { "5.1", "lua-5.1.5-tests", LuaLanguageVersion.Lua51 },
        new object[] { "5.2", "lua-5.2.4-tests", LuaLanguageVersion.Lua52 },
        new object[] { "5.3", "lua-5.3.6-semantic-fixture", LuaLanguageVersion.Lua53 },
        new object[] { "5.4", "lua-5.4.8-semantic-fixture", LuaLanguageVersion.Lua54 },
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
        if (root.GetProperty("schema").GetString() == "lunil.lua-semantic-fixture.v1")
        {
            ValidateSemanticFixtureManifest(root, fixtures, version);
            return;
        }

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
    public void HostExecutesVersionSpecificSemanticCases(
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

        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "multi-version", label);
        var manifestPath = Path.Combine(fixtures, "manifest.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (manifest.RootElement.GetProperty("schema").GetString() ==
            "lunil.lua-semantic-fixture.v1")
        {
            foreach (var testCase in manifest.RootElement.GetProperty("cases").EnumerateArray())
            {
                var path = Path.Combine(fixtures, testCase.GetProperty("path").GetString()!);
                var semanticRun = host.RunUtf8(File.ReadAllText(path), "@" + path);
                Assert.True(semanticRun.Compilation.Succeeded, string.Join(
                    Environment.NewLine,
                    semanticRun.Compilation.Diagnostics));
                Assert.NotNull(semanticRun.Execution);
                var values = semanticRun.Execution!.Values;
                var expected = testCase.GetProperty("expected");
                Assert.Equal(expected.GetArrayLength(), values.Length);
                for (var index = 0; index < values.Length; index++)
                {
                    AssertExpectedValue(expected[index], values[index]);
                }
            }

            return;
        }

        var source = version switch
        {
            LuaLanguageVersion.Lua51 =>
                "local env = { answer = 42 }; local function read() return answer end; " +
                "setfenv(read, env); return read()",
            LuaLanguageVersion.Lua52 =>
                "local _ENV = { value = 41 }; goto done; value = 0; ::done:: return value + 1",
            LuaLanguageVersion.Lua55 =>
                "global value = 40; local function add(... values) return values[1] + 2 end; " +
                "return add(value)",
            _ => throw new InvalidOperationException(
                $"Version {version} should use its checked-in semantic manifest."),
        };
        var run = host.RunUtf8(source, "@semantic-smoke.lua");
        Assert.True(run.Compilation.Succeeded);
        Assert.Equal(LuaVmSignal.Completed, run.Execution!.Signal);
        var value = Assert.Single(run.Execution.Values);
        if (version is LuaLanguageVersion.Lua51 or LuaLanguageVersion.Lua52)
        {
            Assert.Equal(LuaValueKind.Float, value.Kind);
            Assert.Equal(42.0, value.AsFloat(), 1e-9);
        }
        else
        {
            Assert.Equal(LuaValueKind.Integer, value.Kind);
            Assert.Equal(42, value.AsInteger());
        }
    }

    private static void ValidateSemanticFixtureManifest(
        JsonElement root,
        string fixtures,
        LuaLanguageVersion version)
    {
        Assert.Equal(version.ToString(), root.GetProperty("languageVersion").GetString());
        Assert.Equal("semantic-version-fixture", root.GetProperty("mode").GetString());
        var cases = root.GetProperty("cases");
        Assert.True(cases.GetArrayLength() >= 3);
        foreach (var testCase in cases.EnumerateArray())
        {
            var path = Path.GetFullPath(Path.Combine(
                fixtures,
                testCase.GetProperty("path").GetString()!));
            Assert.StartsWith(
                fixtures + Path.DirectorySeparatorChar,
                path,
                StringComparison.Ordinal);
            Assert.True(File.Exists(path), path);
            Assert.NotEmpty(File.ReadAllText(path));
            Assert.True(testCase.GetProperty("expected").GetArrayLength() > 0);
        }
    }

    private static void AssertExpectedValue(JsonElement expected, LuaValue actual)
    {
        var kind = expected.GetProperty("kind").GetString();
        switch (kind)
        {
            case "Integer":
                Assert.Equal(LuaValueKind.Integer, actual.Kind);
                Assert.Equal(expected.GetProperty("value").GetInt64(), actual.AsInteger());
                break;
            case "Float":
                Assert.Equal(LuaValueKind.Float, actual.Kind);
                Assert.Equal(expected.GetProperty("value").GetDouble(), actual.AsFloat(), 1e-9);
                break;
            case "Boolean":
                Assert.Equal(LuaValueKind.Boolean, actual.Kind);
                Assert.Equal(expected.GetProperty("value").GetBoolean(), actual.AsBoolean());
                break;
            case "String":
                Assert.Equal(LuaValueKind.String, actual.Kind);
                Assert.Equal(expected.GetProperty("value").GetString(), actual.AsString().ToString());
                break;
            default:
                throw new Xunit.Sdk.XunitException($"Unknown fixture value kind '{kind}'.");
        }
    }
}
