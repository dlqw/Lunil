using System.Security.Cryptography;
using System.Text.Json;
using Lunil.BackendDifferential.Tests.Infrastructure;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.BackendDifferential.Tests;

public sealed class PucLua548GoldenTests
{
    private const string SourceArchiveSha256 =
        "9581D5A7C39FFBF29B8CCDE2709083C380F7BBDDBD968DCB15712D2F2E33F4E5";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void AllSixBackendsMatchCommittedPucLua548Observations()
    {
        var corpusDirectory = Path.Combine(AppContext.BaseDirectory, "PucLua54");
        var document = ReadDocument(Path.Combine(corpusDirectory, "goldens.json"));
        Assert.Equal(1, document.SchemaVersion);
        Assert.Equal("PUC-Lua", document.Oracle.Implementation);
        Assert.Equal("Lua 5.4.8", document.Oracle.Version);
        Assert.Equal("Lua 5.4", document.Oracle.LuaVersionGlobal);
        Assert.Equal(SourceArchiveSha256, document.Oracle.SourceArchiveSha256);
        Assert.Matches("^[0-9A-F]{64}$", document.Oracle.ExecutableSha256);
        Assert.Equal(8, document.Cases.Count);

        var committedSources = Directory.GetFiles(corpusDirectory, "*.lua")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var declaredSources = document.Cases
            .Select(static item => item.File)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(committedSources, declaredSources);

        foreach (var testCase in document.Cases)
        {
            var sourcePath = Path.Combine(corpusDirectory, testCase.File);
            Assert.Equal(
                testCase.SourceSha256,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourcePath))));

            var source = File.ReadAllText(sourcePath);
            var expected = testCase.Values
                .Select(static value => new LuaObservedValue(
                    Enum.Parse<LuaValueKind>(value.Kind, ignoreCase: false),
                    value.Representation))
                .ToArray();

            foreach (var backend in LuaBackendCatalog.All)
            {
                var observation = LuaBackendSession.Create(
                    backend,
                    source,
                    installStandardLibrary: testCase.InstallStandardLibrary).Execute();
                Assert.True(
                    observation.Signal == LuaVmSignal.Completed &&
                    observation.Values.SequenceEqual(expected),
                    $"PUC-Lua 5.4.8 case '{testCase.Name}' on backend " +
                    $"'{backend.Name}' returned {observation}; expected Completed: " +
                    $"[{string.Join(", ", expected)}].");
            }
        }
    }

    private static GoldenDocument ReadDocument(string path)
    {
        var document = JsonSerializer.Deserialize<GoldenDocument>(
            File.ReadAllText(path),
            SerializerOptions);
        return document ?? throw new InvalidDataException(
            "The PUC-Lua 5.4.8 golden document was empty.");
    }

    private sealed record GoldenDocument(
        int SchemaVersion,
        GoldenOracle Oracle,
        IReadOnlyList<GoldenCase> Cases);

    private sealed record GoldenOracle(
        string Implementation,
        string Version,
        string LuaVersionGlobal,
        string ExecutableSha256,
        string SourceArchiveSha256);

    private sealed record GoldenCase(
        string Name,
        string File,
        string SourceSha256,
        bool InstallStandardLibrary,
        IReadOnlyList<GoldenValue> Values);

    private sealed record GoldenValue(string Kind, string Representation);
}
