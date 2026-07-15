using System.Text;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Lunil.CodeGen.Cil.Loading;

namespace Lunil.Cli.Tests;

public sealed class LunilCliTests
{
    [Fact]
    public async Task HelpAndVersionAreAvailableWithoutACommand()
    {
        using var fixture = new CliFixture();

        var help = await fixture.RunAsync("--help");
        var version = await fixture.RunAsync("--version");

        Assert.Equal(0, help.ExitCode);
        Assert.Contains("lunil run", help.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("--execution <backend>", help.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, version.ExitCode);
        var informationalVersion = typeof(LunilCli).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;
        var expectedVersion = informationalVersion.Split('+', 2)[0];
        Assert.Equal(expectedVersion + "\n", version.StandardOutput);
    }

    [Fact]
    public async Task UsageErrorsCanBeMachineReadable()
    {
        using var fixture = new CliFixture();

        var result = await fixture.RunAsync("--diagnostic-format", "json", "unknown");

        Assert.Equal(2, result.ExitCode);
        using var json = JsonDocument.Parse(result.StandardError);
        Assert.Equal("lunil.diagnostics.v1", json.RootElement.GetProperty("schema").GetString());
        Assert.Equal(
            "LUNIL0001",
            json.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("run", "--output", "out.txt")]
    [InlineData("check", "--target", "chunk")]
    [InlineData("run", "--strip-debug", null)]
    [InlineData("build", "--kind", "syntax")]
    [InlineData("check", "--format", "json")]
    public async Task CommandSpecificOptionsAreRejected(
        string command,
        string option,
        string? value = null)
    {
        using var fixture = new CliFixture();
        var script = fixture.Write("input.lua", "return 1");
        var arguments = new List<string> { command, script, option };
        if (value is not null)
        {
            arguments.Add(value);
        }

        if (command == "build")
        {
            arguments.AddRange(["--output", Path.Combine(fixture.Root, "out.luac")]);
        }

        var result = await fixture.RunAsync([.. arguments]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("valid only", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunExecutesSourceAndPublishesScriptArguments()
    {
        using var fixture = new CliFixture();
        var script = fixture.Write("args.lua", "print(...); print(arg[0], arg[1], arg[2])");

        var result = await fixture.RunAsync("run", script, "--", "one", "two");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("one\ttwo", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("one\ttwo", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(result.StandardError);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("interpreter")]
    [InlineData("jit")]
    public async Task RunSupportsQualifiedExecutionBackends(string backend)
    {
        using var fixture = new CliFixture();
        var script = fixture.Write(
            "backend.lua",
            "local n=0; for i=1,1000 do n=n+i end; print(n)");

        var result = await fixture.RunAsync("run", script, "--execution", backend);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("500500\n", result.StandardOutput);
    }

    [Fact]
    public async Task InvalidExecutionBackendIsAUsageError()
    {
        using var fixture = new CliFixture();
        var script = fixture.Write("backend.lua", "return 1");

        var result = await fixture.RunAsync("run", script, "--execution", "fastest");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("auto", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("interpreter", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("jit", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunPreflightsAndExecutesRequiredModules()
    {
        using var fixture = new CliFixture();
        fixture.Write("dep.lua", "return { value = 41 }");
        var app = fixture.Write(
            "app.lua",
            "local dep = require('dep')\nprint(dep.value + 1)\nreturn dep.value + 1");

        var result = await fixture.RunAsync("run", app, "--module-root", fixture.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("42\n", result.StandardOutput);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task SandboxAllowsRootedReadsAndDeniesWrites()
    {
        using var fixture = new CliFixture();
        fixture.Write("dep.lua", "return 42");
        var readable = fixture.Write("read.lua", "print(require('dep'))");
        var writer = fixture.Write(
            "write.lua",
            "local f, err = io.open('created.txt', 'w')\nassert(f, err)");

        var read = await fixture.RunAsync(
            "run",
            readable,
            "--sandbox",
            "--module-root",
            fixture.Root);
        var write = await fixture.RunAsync(
            "run",
            writer,
            "--sandbox",
            "--module-root",
            fixture.Root);

        Assert.Equal(0, read.ExitCode);
        Assert.StartsWith("42\t", read.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(4, write.ExitCode);
        Assert.Contains("Sandbox denies", write.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "created.txt")));
    }

    [Fact]
    public async Task SandboxDeniesReadsOutsideConfiguredRoots()
    {
        using var fixture = new CliFixture();
        var outside = Path.Combine(Path.GetTempPath(), "lunil-outside-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(outside, "secret", new UTF8Encoding(false));
        try
        {
            var script = fixture.Write(
                "outside.lua",
                $"local f, err = io.open([=[{outside}]=], 'r')\nassert(f, err)");

            var result = await fixture.RunAsync("run", script, "--sandbox");

            Assert.Equal(4, result.ExitCode);
            Assert.Contains("Sandbox denies", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task DeterministicProfileFixesTimeAndHashSeed()
    {
        using var fixture = new CliFixture();
        var script = fixture.Write("time.lua", "print(os.time()); print(os.clock())");

        var first = await fixture.RunAsync("run", script, "--deterministic");
        var second = await fixture.RunAsync("run", script, "--deterministic");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(first.StandardOutput, second.StandardOutput);
        Assert.Equal("0\n0.0\n", first.StandardOutput);
    }

    [Fact]
    public async Task RuntimeErrorsAndInstructionBudgetsUseExecutionExitCode()
    {
        using var fixture = new CliFixture();
        var error = fixture.Write("error.lua", "error('boom')");
        var loop = fixture.Write("loop.lua", "while true do end");

        var failed = await fixture.RunAsync("run", error);
        var budgeted = await fixture.RunAsync(
            "run",
            loop,
            "--maximum-instructions",
            "100");

        Assert.Equal(4, failed.ExitCode);
        Assert.Contains("boom", failed.StandardError, StringComparison.Ordinal);
        Assert.Equal(4, budgeted.ExitCode);
        Assert.Contains("instruction", budgeted.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("local function f() local value = f(); return value end; return f()", "--maximum-call-depth", "8", "stack")]
    [InlineData("local a, b, c, d = 1, 2, 3, 4; return a + b + c + d", "--maximum-stack-slots", "1", "stack")]
    [InlineData("return 1", "--maximum-heap-bytes", "1", "quota")]
    public async Task RuntimeResourceBudgetsUseExecutionExitCode(
        string source,
        string option,
        string value,
        string expectedMessage)
    {
        using var fixture = new CliFixture();
        var script = fixture.Write("budget.lua", source);

        var result = await fixture.RunAsync("run", script, option, value);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains(expectedMessage, result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckPromotesWarningsWhenRequested()
    {
        using var fixture = new CliFixture();
        var script = fixture.Write("warning.lua", "return 'text' + 1");

        var normal = await fixture.RunAsync("check", script);
        var strict = await fixture.RunAsync(
            "check",
            script,
            "--warnings-as-errors",
            "--diagnostic-format",
            "json");

        Assert.Equal(0, normal.ExitCode);
        Assert.Contains("warning LUA6003", normal.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, strict.ExitCode);
        using var json = JsonDocument.Parse(strict.StandardError);
        Assert.Equal(
            "error",
            json.RootElement.GetProperty("diagnostics")[0].GetProperty("severity").GetString());
    }

    [Fact]
    public async Task CheckReportsSyntaxErrorsAndCrossModuleDiagnostics()
    {
        using var fixture = new CliFixture();
        var syntax = fixture.Write("syntax.lua", "local =");
        fixture.Write("dep.lua", "return { value = 'text' }");
        var app = fixture.Write("app.lua", "local dep = require('dep')\nreturn dep.value + 1");

        var syntaxResult = await fixture.RunAsync("check", syntax);
        var workspaceResult = await fixture.RunAsync(
            "check",
            app,
            "--module-root",
            fixture.Root,
            "--warnings-as-errors");

        Assert.Equal(1, syntaxResult.ExitCode);
        Assert.Contains("LUA", syntaxResult.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, workspaceResult.ExitCode);
        Assert.Contains("LUA6003", workspaceResult.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StandardInputCanBeACompleteSourceDocument()
    {
        using var fixture = new CliFixture();

        var result = await fixture.RunWithInputAsync(
            "print('stdin-ok')",
            "run",
            "-");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("stdin-ok\n", result.StandardOutput);
    }

    [Fact]
    public async Task ResponseFilesSupportQuotesCommentsAndNestedPaths()
    {
        using var fixture = new CliFixture();
        var script = fixture.Write("space name.lua", "print('response-ok')");
        var nestedDirectory = Directory.CreateDirectory(Path.Combine(fixture.Root, "rsp"));
        File.WriteAllText(
            Path.Combine(nestedDirectory.FullName, "nested.rsp"),
            $"run \"{script}\" # comment\n--deterministic",
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(fixture.Root, "main.rsp"),
            "@rsp/nested.rsp",
            new UTF8Encoding(false));

        var result = await fixture.RunAsync("@main.rsp");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("response-ok\n", result.StandardOutput);
    }

    [Fact]
    public async Task ResponseFilesSupportLiteralAtArgumentsAndEnforceArgumentBudget()
    {
        using var fixture = new CliFixture();
        var script = fixture.Write("at.lua", "print(...)");
        File.WriteAllText(
            Path.Combine(fixture.Root, "literal.rsp"),
            $"run \"{script}\" -- @@literal",
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(fixture.Root, "oversized.rsp"),
            string.Join(' ', Enumerable.Repeat("--help", 4_097)),
            new UTF8Encoding(false));

        var literal = await fixture.RunAsync("@literal.rsp");
        var oversized = await fixture.RunAsync("@oversized.rsp");

        Assert.Equal(0, literal.ExitCode);
        Assert.Equal("@literal\n", literal.StandardOutput);
        Assert.Equal(2, oversized.ExitCode);
        Assert.Contains("argument count", oversized.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResponseFilesRejectOversizedAndInvalidUtf8Files()
    {
        using var fixture = new CliFixture();
        File.WriteAllBytes(
            Path.Combine(fixture.Root, "large.rsp"),
            new byte[1024 * 1024 + 1]);
        File.WriteAllBytes(Path.Combine(fixture.Root, "invalid.rsp"), [0xff]);

        var large = await fixture.RunAsync("@large.rsp");
        var invalid = await fixture.RunAsync("@invalid.rsp");

        Assert.Equal(2, large.ExitCode);
        Assert.Contains("exceeds", large.StandardError, StringComparison.Ordinal);
        Assert.Equal(2, invalid.ExitCode);
        Assert.Contains("valid UTF-8", invalid.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponseFileCyclesAreRejected()
    {
        using var fixture = new CliFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "a.rsp"), "@b.rsp");
        File.WriteAllText(Path.Combine(fixture.Root, "b.rsp"), "@a.rsp");

        var result = await fixture.RunAsync("@a.rsp");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("cycle", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfigurationEnvironmentAndCliUseDefinedPrecedence()
    {
        using var fixture = new CliFixture();
        var script = fixture.Write("config.lua", "print(os.time())");
        File.WriteAllText(
            Path.Combine(fixture.Root, "lunil.json"),
            """
            {
              "profile": "sandbox",
              "execution": "interpreter",
              "diagnosticFormat": "json",
              "maximumInstructions": 1000
            }
            """);
        fixture.Environment["LUNIL_PROFILE"] = "deterministic";
        fixture.Environment["LUNIL_EXECUTION"] = "auto";

        var fromEnvironment = await fixture.RunAsync("run", script);
        var fromCli = await fixture.RunAsync(
            "run",
            script,
            "--trusted",
            "--execution",
            "interpreter");

        Assert.Equal(0, fromEnvironment.ExitCode);
        Assert.Equal("0\n", fromEnvironment.StandardOutput);
        Assert.Equal(0, fromCli.ExitCode);
        Assert.NotEqual("0\n", fromCli.StandardOutput);
    }

    [Fact]
    public async Task InvalidConfigurationIsAUsageError()
    {
        using var fixture = new CliFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "lunil.json"), "{ \"typo\": true }");

        var result = await fixture.RunAsync("--version");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Unknown Lunil configuration property", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildChunkProducesRunnablePortableBytecode()
    {
        using var fixture = new CliFixture();
        var script = fixture.Write("app.lua", "print('chunk-ok')");
        var output = Path.Combine(fixture.Root, "app.luac");

        var build = await fixture.RunAsync("build", script, "-o", output);
        var run = await fixture.RunAsync("run", output);

        Assert.Equal(0, build.ExitCode);
        Assert.True(File.Exists(output));
        Assert.Equal(0, run.ExitCode);
        Assert.Equal("chunk-ok\n", run.StandardOutput);
    }

    [Fact]
    public async Task BinaryChunkRuntimeErrorsUseExecutionExitCode()
    {
        using var fixture = new CliFixture();
        var script = fixture.Write("error.lua", "error('chunk-boom')");
        var output = Path.Combine(fixture.Root, "error.luac");
        Assert.Equal(0, (await fixture.RunAsync("build", script, "-o", output)).ExitCode);

        var run = await fixture.RunAsync("run", output);

        Assert.Equal(4, run.ExitCode);
        Assert.Contains("chunk-boom", run.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildWorkspaceEmitsEveryResolvedModule()
    {
        using var fixture = new CliFixture();
        fixture.Write("dep.lua", "return 42");
        var app = fixture.Write("app.lua", "return require('dep')");
        var output = Path.Combine(fixture.Root, "chunks") + Path.DirectorySeparatorChar;

        var result = await fixture.RunAsync(
            "build",
            app,
            "-o",
            output,
            "--module-root",
            fixture.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(output, "app.luac")));
        Assert.True(File.Exists(Path.Combine(output, "dep.luac")));
    }

    [Fact]
    public async Task BuildAotEmitsAssemblyCanonicalModuleAndManifest()
    {
        using var fixture = new CliFixture();
        var script = fixture.Write("aot.lua", "return 42");
        var output = Path.Combine(fixture.Root, "aot");

        var result = await fixture.RunAsync(
            "build",
            script,
            "-o",
            output,
            "--target",
            "aot");

        Assert.Equal(0, result.ExitCode);
        var assembly = Path.Combine(output, "aot.dll");
        Assert.True(File.Exists(assembly));
        Assert.True(File.Exists(Path.Combine(output, "aot.pdb")));
        Assert.True(File.Exists(Path.Combine(output, "aot.canonical.bin")));
        Assert.True(File.Exists(Path.Combine(output, "aot.manifest.json")));
        var validation = LuaAotArtifactLoader.Validate(
            File.ReadAllBytes(assembly).ToImmutableArray(),
            File.ReadAllBytes(Path.Combine(output, "aot.pdb")).ToImmutableArray());
        Assert.True(validation.Succeeded);
    }

    [Fact]
    public async Task StrippedAotBuildRemovesAStalePortablePdb()
    {
        using var fixture = new CliFixture();
        var script = fixture.Write("aot.lua", "return 42");
        var output = Path.Combine(fixture.Root, "aot");

        Assert.Equal(0, (await fixture.RunAsync(
            "build", script, "-o", output, "--target", "aot")).ExitCode);
        Assert.True(File.Exists(Path.Combine(output, "aot.pdb")));

        var stripped = await fixture.RunAsync(
            "build", script, "-o", output, "--target", "aot", "--strip-debug");

        Assert.Equal(0, stripped.ExitCode);
        Assert.False(File.Exists(Path.Combine(output, "aot.pdb")));
    }

    [Fact]
    public async Task BuildRejectsCollidingSanitizedModuleArtifactPaths()
    {
        using var fixture = new CliFixture();
        fixture.Write(Path.Combine("a", "b.lua"), "return 1");
        var script = fixture.Write("app.lua", "return require('a.b')");
        var output = Path.Combine(fixture.Root, "chunks") + Path.DirectorySeparatorChar;

        var result = await fixture.RunAsync(
            "build",
            script,
            "-o",
            output,
            "--module-root",
            fixture.Root,
            "--module-name",
            "a/b");

        Assert.Equal(5, result.ExitCode);
        Assert.Contains("same artifact path", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("syntax")]
    [InlineData("annotations")]
    [InlineData("analysis")]
    [InlineData("ir")]
    [InlineData("chunk")]
    public async Task DumpSupportsEverySourceView(string kind)
    {
        using var fixture = new CliFixture();
        var script = fixture.Write("dump.lua", "---@type integer\nlocal value = 42\nreturn value");

        var result = await fixture.RunAsync(
            "dump",
            script,
            "--kind",
            kind,
            "--format",
            "json");

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal(kind, json.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task BinaryChunksRejectSourceOnlyDumpKinds()
    {
        using var fixture = new CliFixture();
        var source = fixture.Write("source.lua", "return 1");
        var chunk = Path.Combine(fixture.Root, "source.luac");
        Assert.Equal(0, (await fixture.RunAsync("build", source, "-o", chunk)).ExitCode);

        var result = await fixture.RunAsync("dump", chunk, "--kind", "syntax");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("LUA8003", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoverableSourceDumpStillReturnsDiagnosticExitCode()
    {
        using var fixture = new CliFixture();
        var source = fixture.Write("broken.lua", "local =");

        var result = await fixture.RunAsync(
            "dump", source, "--kind", "syntax", "--format", "json");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("LUA", result.StandardError, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("syntax", json.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task MissingAndOversizedInputsUseInputOutputExitCode()
    {
        using var fixture = new CliFixture();
        var large = fixture.Write("large.lua", new string(' ', 128));

        var missing = await fixture.RunAsync("check", "missing.lua");
        var oversized = await fixture.RunAsync(
            "check",
            large,
            "--maximum-input-bytes",
            "64");

        Assert.Equal(3, missing.ExitCode);
        Assert.Equal(3, oversized.ExitCode);
        Assert.Contains("input limit", oversized.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationUsesStableExitCode()
    {
        using var fixture = new CliFixture();
        var script = fixture.Write("cancel.lua", "return 1");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await fixture.RunAsync(cancellation.Token, "check", script);

        Assert.Equal(130, result.ExitCode);
        Assert.Contains("cancelled", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnexpectedHostFailuresUseInternalExitCode()
    {
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();
        await using var error = new MemoryStream();

        var exitCode = await LunilCli.RunAsync(
            ["--version"],
            input,
            output,
            error,
            Environment.CurrentDirectory,
            _ => throw new KeyNotFoundException("host failure"));

        Assert.Equal(5, exitCode);
        var diagnostic = Encoding.UTF8.GetString(error.ToArray());
        Assert.Contains("KeyNotFoundException", diagnostic, StringComparison.Ordinal);
        Assert.Contains("host failure", diagnostic, StringComparison.Ordinal);
    }

    private sealed class CliFixture : IDisposable
    {
        public CliFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "lunil-cli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public Dictionary<string, string?> Environment { get; } = new(StringComparer.Ordinal);

        public string Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        public Task<CliResult> RunAsync(params string[] arguments) =>
            RunWithInputAsync(string.Empty, CancellationToken.None, arguments);

        public Task<CliResult> RunAsync(CancellationToken cancellationToken, params string[] arguments) =>
            RunWithInputAsync(string.Empty, cancellationToken, arguments);

        public Task<CliResult> RunWithInputAsync(string input, params string[] arguments) =>
            RunWithInputAsync(input, CancellationToken.None, arguments);

        private async Task<CliResult> RunWithInputAsync(
            string input,
            CancellationToken cancellationToken,
            params string[] arguments)
        {
            await using var standardInput = new MemoryStream(Encoding.UTF8.GetBytes(input));
            await using var standardOutput = new MemoryStream();
            await using var standardError = new MemoryStream();
            var exitCode = await LunilCli.RunAsync(
                arguments,
                standardInput,
                standardOutput,
                standardError,
                Root,
                name => Environment.GetValueOrDefault(name),
                cancellationToken);
            return new CliResult(
                exitCode,
                Encoding.UTF8.GetString(standardOutput.ToArray()),
                Encoding.UTF8.GetString(standardError.ToArray()));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
}
