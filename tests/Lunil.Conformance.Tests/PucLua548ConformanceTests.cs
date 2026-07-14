using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lunil.Hosting;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.StandardLibrary;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Lunil.Conformance.Tests;

public sealed class PucLua548ConformanceTests
{
    private static readonly string[] AllowedClassifications =
        ["driver-or-helper", "executed-user-mode", "excluded-user-mode"];
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string ExpectedArchiveSha256 =
        "9581D5A7C39FFBF29B8CCDE2709083C380F7BBDDBD968DCB15712D2F2E33F4E5";
    private const string ArchiveFileName = "lua-5.4.8-tests.tar.gz";
    private const string SuiteDirectoryName = "lua-5.4.8-tests";

    [Fact]
    public void OfficialArchiveFixturesAndClassificationsArePinned()
    {
        var fixtures = FixtureRoot();
        var archivePath = Path.Combine(fixtures, ArchiveFileName);
        var suitePath = Path.Combine(fixtures, SuiteDirectoryName);
        var manifest = ReadManifest(fixtures);

        Assert.Equal("lunil.lua-conformance-manifest.v1", manifest.Schema);
        Assert.Equal("Lua 5.4.8", manifest.UpstreamVersion);
        Assert.Equal(ExpectedArchiveSha256, manifest.ArchiveSha256);
        Assert.Equal(ExpectedArchiveSha256, HashFile(archivePath));
        Assert.Equal(41, manifest.Files.Length);
        Assert.Equal(28, manifest.Files.Count(static entry =>
            entry.Classification == "executed-user-mode"));
        Assert.All(manifest.Files, static entry =>
        {
            Assert.NotEmpty(entry.Reason);
            Assert.Contains(
                entry.Classification,
                AllowedClassifications);
        });

        var byPath = manifest.Files.ToDictionary(
            static entry => entry.Path,
            StringComparer.Ordinal);
        var committedPaths = Directory.EnumerateFiles(suitePath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(suitePath, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(byPath.Keys.Order(StringComparer.Ordinal), committedPaths);

        foreach (var entry in manifest.Files)
        {
            Assert.Equal(
                entry.Sha256,
                HashFile(Path.Combine(suitePath, entry.Path.Replace('/', Path.DirectorySeparatorChar))));
        }

        AssertArchiveMatchesCommittedFixtures(archivePath, suitePath, byPath);
    }

    [Fact]
    public void UnmodifiedOfficialUserModeSuiteReachesFinalOk()
    {
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "lunil-lua-5.4.8-conformance-" + Guid.NewGuid().ToString("N"));
        var temporarySuite = Path.Combine(temporaryRoot, SuiteDirectoryName);
        var previousDirectory = Environment.CurrentDirectory;
        Directory.CreateDirectory(temporarySuite);
        CopyDirectory(Path.Combine(FixtureRoot(), SuiteDirectoryName), temporarySuite);

        try
        {
            Environment.CurrentDirectory = temporarySuite;
            var console = new LuaBufferedConsole();
            using var host = new LuaHost(LuaHostOptions.Trusted with
            {
                StandardLibrary = LuaStandardLibraryOptions.Default with
                {
                    Console = console,
                },
            });
            host.State.SetGlobal("_U", LuaValue.FromBoolean(true));

            var result = host.RunUtf8(
                "return dofile('all.lua')",
                "@lunil-conformance-driver.lua");

            Assert.True(
                result.CompilationSucceeded,
                string.Join(Environment.NewLine, result.Compilation.Diagnostics));
            Assert.NotNull(result.Execution);
            Assert.Equal(LuaVmSignal.Completed, result.Execution!.Signal);
            Assert.Contains(
                "final OK !!!",
                Encoding.UTF8.GetString(console.GetStandardOutput()),
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    private static void AssertArchiveMatchesCommittedFixtures(
        string archivePath,
        string suitePath,
        IReadOnlyDictionary<string, ManifestFile> manifest)
    {
        var archivedPaths = new List<string>();
        using var archive = File.OpenRead(archivePath);
        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue;
            }

            const string prefix = SuiteDirectoryName + "/";
            Assert.StartsWith(prefix, entry.Name, StringComparison.Ordinal);
            var relativePath = entry.Name[prefix.Length..];
            archivedPaths.Add(relativePath);
            Assert.True(manifest.TryGetValue(relativePath, out var expected));
            Assert.NotNull(entry.DataStream);
            var archiveHash = Convert.ToHexString(SHA256.HashData(entry.DataStream!));
            Assert.Equal(expected!.Sha256, archiveHash);
            Assert.Equal(
                archiveHash,
                HashFile(Path.Combine(
                    suitePath,
                    relativePath.Replace('/', Path.DirectorySeparatorChar))));
        }

        Assert.Equal(
            manifest.Keys.Order(StringComparer.Ordinal),
            archivedPaths.Order(StringComparer.Ordinal));
    }

    private static ConformanceManifest ReadManifest(string fixtures)
    {
        var json = File.ReadAllText(
            Path.Combine(fixtures, "lua-5.4.8-manifest.json"),
            Encoding.UTF8);
        return JsonSerializer.Deserialize<ConformanceManifest>(json, ManifestJsonOptions) ??
            throw new InvalidDataException("The Lua 5.4.8 manifest is empty.");
    }

    private static string FixtureRoot() =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destination,
                Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destinationPath = Path.Combine(
                destination,
                Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: false);
        }
    }

    private sealed record ConformanceManifest(
        string Schema,
        string UpstreamVersion,
        string UpstreamUrl,
        string ArchiveSha256,
        string Mode,
        ManifestFile[] Files);

    private sealed record ManifestFile(
        string Path,
        string Sha256,
        string Classification,
        string Reason);
}
