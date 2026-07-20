using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Lunil.Core;
using Lunil.Hosting;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.StandardLibrary;

namespace Lunil.Conformance.Tests;

public sealed class PucLua53ConformanceTests
{
    private static readonly string[] OfficialUserModeFixtures =
    [
        "attrib.lua",
        "big.lua",
        "bitwise.lua",
        "calls.lua",
        "closure.lua",
        "code.lua",
        "constructs.lua",
        "coroutine.lua",
        "gc.lua",
        "events.lua",
        "errors.lua",
        "files.lua",
        "goto.lua",
        "literals.lua",
        "locals.lua",
        "math.lua",
        "nextvar.lua",
        "pm.lua",
        "sort.lua",
        "strings.lua",
        "tpack.lua",
        "utf8.lua",
        "vararg.lua",
        "verybig.lua",
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
            Assert.NotNull(ReadOfficialFixture(fixtures, fixture));
        }
    }

    [Theory]
    [MemberData(nameof(OfficialUserModeFixtureData))]
    public void ExecutesSelectedOfficialLua53UserModeFixtures(string fixture, bool softMode)
    {
        var console = new CaptureConsole();
        var fixtureRoot = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "lua-5.3.4-selected");
        using var host = new LuaHost(new LuaHostOptions
        {
            Profile = LuaHostProfile.Trusted,
            LanguageVersion = LuaLanguageVersion.Lua53,
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            Execution = LuaInterpreterOptions.Default with
            {
                MaximumInstructionCount = 300_000_000,
            },
            StandardLibrary = LuaStandardLibraryOptions.Default with
            {
                Console = console,
                FileSystem = new RootedFileSystem(fixtureRoot),
            },
        });
        var source = ReadOfficialFixture(
            Path.Combine(AppContext.BaseDirectory, "Fixtures"),
            fixture)!;
        if (softMode)
        {
            host.State.SetGlobal("_soft", LuaValue.FromBoolean(true));
            host.State.SetGlobal("_port", LuaValue.FromBoolean(true));
        }

        var compilation = host.Compiler.CompileBytes(source, $"@{fixture}");

        Assert.True(
            compilation.Succeeded,
            string.Join(Environment.NewLine, compilation.Diagnostics));
        var execution = host.Execute(compilation);
        Assert.Equal(LuaVmSignal.Completed, execution.Signal);
    }

    public static TheoryData<string, bool> OfficialUserModeFixtureData => new()
    {
        { "attrib.lua", true },
        { "big.lua", true },
        { "bitwise.lua", false },
        { "calls.lua", true },
        { "closure.lua", false },
        { "code.lua", true },
        { "constructs.lua", true },
        { "coroutine.lua", true },
        { "events.lua", true },
        { "gc.lua", true },
        { "errors.lua", true },
        { "files.lua", true },
        { "goto.lua", false },
        { "literals.lua", false },
        { "locals.lua", true },
        { "math.lua", true },
        { "nextvar.lua", true },
        { "pm.lua", true },
        { "sort.lua", true },
        { "strings.lua", true },
        { "tpack.lua", true },
        { "utf8.lua", true },
        { "vararg.lua", false },
        { "verybig.lua", true },
    };

    [Fact]
    public void SelectedOfficialLua53FixturesRetainUpstreamIdentity()
    {
        foreach (var fixture in OfficialUserModeFixtures)
        {
            var source = Encoding.UTF8.GetString(
                ReadOfficialFixture(Path.Combine(AppContext.BaseDirectory, "Fixtures"), fixture)!);
            Assert.Contains("See Copyright Notice", source, StringComparison.Ordinal);
            Assert.Contains("$Id:", source, StringComparison.Ordinal);
        }
    }

    private static byte[]? ReadOfficialFixture(string fixtures, string fixture)
    {
        var selected = Path.Combine(fixtures, "lua-5.3.4-selected", fixture);
        if (File.Exists(selected))
        {
            return File.ReadAllBytes(selected);
        }

        if (!string.Equals(fixture, "gc.lua", StringComparison.Ordinal))
        {
            return null;
        }

        using var archive = File.OpenRead(Path.Combine(fixtures, "lua-5.3.4-tests.tar.gz"));
        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            if (!entry.Name.EndsWith("/gc.lua", StringComparison.Ordinal))
            {
                continue;
            }

            using var content = new MemoryStream();
            entry.DataStream?.CopyTo(content);
            return content.ToArray();
        }

        return null;
    }

    private sealed class RootedFileSystem(string root) : ILuaFileSystem
    {
        public byte[] ReadAllBytes(string path) =>
            SystemLuaFileSystem.Instance.ReadAllBytes(Resolve(path));

        public bool FileExists(string path) =>
            SystemLuaFileSystem.Instance.FileExists(Resolve(path));

        public Stream Open(string path, LuaFileMode mode) =>
            SystemLuaFileSystem.Instance.Open(Resolve(path), mode);

        public Stream OpenTemporary(out string? path) =>
            SystemLuaFileSystem.Instance.OpenTemporary(out path);

        public string CreateTemporaryName() =>
            SystemLuaFileSystem.Instance.CreateTemporaryName();

        public void Delete(string path) =>
            SystemLuaFileSystem.Instance.Delete(Resolve(path));

        public void Move(string source, string destination) =>
            SystemLuaFileSystem.Instance.Move(Resolve(source), Resolve(destination));

        private string Resolve(string path) => Path.IsPathRooted(path)
            ? path
            : Path.Combine(root, path);
    }

    private sealed class CaptureConsole : ILuaConsole
    {
        private readonly StringBuilder _output = new();

        public string Output => _output.ToString();

        public byte[] ReadStandardInput() => [];

        public void Write(ReadOnlyMemory<byte> bytes) =>
            _output.Append(Encoding.UTF8.GetString(bytes.Span));

        public void WriteLine() => _output.AppendLine();
    }
}
