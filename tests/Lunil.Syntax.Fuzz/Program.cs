using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Lunil.Core;
using Lunil.Core.Text;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

var incrementalEditText = GetOption(args, "--incremental-edits=");
if (incrementalEditText is not null)
{
    RunIncrementalOracle(ParsePositive(incrementalEditText, "incremental-edits"));
    return;
}

var corpusRoot = GetOption(args, "--corpus-root=");
if (corpusRoot is not null)
{
    RunCorpus(corpusRoot);
    return;
}

var iterations = ParsePositive(GetOption(args, "--iterations=") ?? "1000000", "iterations");
var maximumLength = ParsePositive(GetOption(args, "--maximum-length=") ?? "64", "maximum-length");
var seed = uint.Parse(
    GetOption(args, "--seed=") ?? "3237998146",
    NumberStyles.None,
    CultureInfo.InvariantCulture);
var state = seed;
var versions = Enum.GetValues<LuaLanguageVersion>();
var alphabet = "abcdefghijklmnopqrstuvwxyz0123456789(){}[]<>=~+-*/%^#&|:;,. '\"\\\r\n"u8;
var parserOptions = LuaParserOptions.Default with
{
    MaximumRecursionDepth = 64,
    MaximumNodeCount = 2_048,
    MaximumDiagnosticCount = 64,
};
long totalBytes = 0;
long totalDiagnostics = 0;
var stopwatch = Stopwatch.StartNew();
for (var iteration = 0; iteration < iterations; iteration++)
{
    var length = (int)(Next(ref state) % (uint)(maximumLength + 1));
    var bytes = new byte[length];
    for (var index = 0; index < bytes.Length; index++)
    {
        bytes[index] = alphabet[(int)(Next(ref state) % (uint)alphabet.Length)];
    }

    var version = versions[iteration % versions.Length];
    var lexerOptions = LuaLexerOptions.Default with { LanguageVersion = version };
    var lexing = LuaLexer.Lex(new SourceText(bytes), lexerOptions);
    var parsing = LuaParser.Parse(
        lexing,
        parserOptions with { LanguageVersion = version });
    var parsedRealTokens = parsing.Root.DescendantTokens()
        .Where(static token => !token.IsMissing)
        .ToArray();
    if (!lexing.Tokens.SequenceEqual(parsedRealTokens))
    {
        throw new InvalidOperationException(
            $"Lossless token projection failed at iteration {iteration}, seed {seed}.");
    }

    if (parsing.Diagnostics.Length > parserOptions.MaximumDiagnosticCount)
    {
        throw new InvalidOperationException(
            $"Diagnostic budget failed at iteration {iteration}, seed {seed}.");
    }

    totalBytes += length;
    totalDiagnostics += parsing.Diagnostics.Length;
}

stopwatch.Stop();
Console.WriteLine(
    $"syntax_fuzz iterations={iterations},seed={seed},maximum_length={maximumLength}," +
    $"bytes={totalBytes},diagnostics={totalDiagnostics},elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:R}");

static uint Next(ref uint state)
{
    state ^= state << 13;
    state ^= state >> 17;
    state ^= state << 5;
    return state;
}

static void RunCorpus(string corpusRoot)
{
    var root = Path.GetFullPath(corpusRoot);
    var parsedFiles = 0;
    long parsedBytes = 0;
    foreach (var manifestPath in Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories)
                 .OrderBy(static path => path, StringComparer.Ordinal))
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var manifestRoot = manifest.RootElement;
        var versionText = manifestRoot.TryGetProperty("languageVersion", out var languageVersion)
            ? languageVersion.GetString()
            : "Lua" + new DirectoryInfo(Path.GetDirectoryName(manifestPath)!).Name.Replace(".", string.Empty);
        if (!Enum.TryParse<LuaLanguageVersion>(versionText, out var version))
        {
            throw new InvalidDataException($"Unknown language version in {manifestPath}.");
        }

        var sourceRoot = manifestRoot.TryGetProperty("suiteDirectory", out var suiteDirectory)
            ? Path.Combine(Path.GetDirectoryName(manifestPath)!, suiteDirectory.GetString()!)
            : Path.GetDirectoryName(manifestPath)!;

        var paths = new List<string>();
        if (manifestRoot.TryGetProperty("cases", out var cases))
        {
            paths.AddRange(cases.EnumerateArray().Select(item => item.GetProperty("path").GetString()!));
        }

        if (manifestRoot.TryGetProperty("files", out var files))
        {
            paths.AddRange(files.EnumerateArray()
                .Where(item => item.GetProperty("classification").GetString() == "executed-user-mode")
                .Select(item => item.GetProperty("path").GetString()!));
        }

        foreach (var relativePath in paths.OrderBy(static path => path, StringComparer.Ordinal))
        {
            var path = Path.GetFullPath(Path.Combine(sourceRoot, relativePath));
            var bytes = File.ReadAllBytes(path);
            var lexing = LuaLexer.Lex(
                new SourceText(bytes),
                LuaLexerOptions.Default with { LanguageVersion = version });
            var parsing = LuaParser.Parse(
                lexing,
                LuaParserOptions.Default with
                {
                    LanguageVersion = version,
                    MaximumNodeCount = 10_000_000,
                    MaximumDiagnosticCount = 10_000,
                });
            if (!parsing.Diagnostics.IsEmpty)
            {
                var first = parsing.Diagnostics[0];
                throw new InvalidDataException(
                    $"{version} corpus parse failed for {path}: {first.Code} {first.Message} at {first.Span}.");
            }

            parsedFiles++;
            parsedBytes += bytes.Length;
        }
    }

    Console.WriteLine($"syntax_corpus files={parsedFiles},bytes={parsedBytes},versions=5");
}

static void RunIncrementalOracle(int editCount)
{
    var builder = new System.Text.StringBuilder();
    var digitOffsets = new int[64];
    for (var index = 0; index < digitOffsets.Length; index++)
    {
        builder.Append("local value").Append(index.ToString("D3", CultureInfo.InvariantCulture))
            .Append(" = ");
        digitOffsets[index] = builder.Length;
        builder.Append('0').Append('\n');
    }

    builder.Append("return value000\n");
    var current = LuaParser.Parse(SourceText.FromUtf8(builder.ToString()));
    uint random = 0x14a3_2026;
    long reusedNodes = 0;
    var fullReparseCount = 0;
    var stopwatch = Stopwatch.StartNew();
    for (var edit = 0; edit < editCount; edit++)
    {
        var statement = (int)(Next(ref random) % (uint)digitOffsets.Length);
        var digit = (byte)('0' + Next(ref random) % 10);
        var change = LuaTextChange.FromBytes(new TextSpan(digitOffsets[statement], 1), [digit]);
        var incremental = LuaParser.ParseIncremental(current, change);
        var full = LuaParser.Parse(change.Apply(current.Source));
        var incrementalHash = HashSyntax(incremental);
        var fullHash = HashSyntax(full);
        if (incrementalHash != fullHash)
        {
            throw new InvalidOperationException(
                $"Incremental/full mismatch at edit {edit}: {incrementalHash:x16} != {fullHash:x16}.");
        }

        if (incremental.IncrementalMetrics!.WasFullReparse)
        {
            fullReparseCount++;
        }

        reusedNodes += incremental.IncrementalMetrics.ReusedNodeCount;
        current = incremental;
    }

    stopwatch.Stop();
    Console.WriteLine(
        $"syntax_incremental edits={editCount},full_reparse={fullReparseCount}," +
        $"reused_nodes={reusedNodes},elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:R}");
}

static ulong HashSyntax(LuaParseResult result)
{
    const ulong Offset = 14695981039346656037;
    const ulong Prime = 1099511628211;
    var hash = Offset;
    foreach (var diagnostic in result.Diagnostics)
    {
        Mix(ref hash, (ulong)diagnostic.Span.Start, Prime);
        Mix(ref hash, (ulong)diagnostic.Span.Length, Prime);
        foreach (var character in diagnostic.Code)
        {
            Mix(ref hash, character, Prime);
        }
    }

    foreach (var node in result.Root.DescendantNodes())
    {
        Mix(ref hash, (ulong)node.Kind, Prime);
        Mix(ref hash, (ulong)node.Span.Start, Prime);
        Mix(ref hash, (ulong)node.Span.Length, Prime);
        Mix(ref hash, (ulong)node.FullSpan.Start, Prime);
        Mix(ref hash, (ulong)node.FullSpan.Length, Prime);
    }

    foreach (var token in result.Root.DescendantTokens())
    {
        Mix(ref hash, (ulong)token.Kind, Prime);
        Mix(ref hash, (ulong)token.Span.Start, Prime);
        Mix(ref hash, (ulong)token.Span.Length, Prime);
        Mix(ref hash, token.IsMissing ? 1UL : 0UL, Prime);
        if (!token.IsMissing)
        {
            foreach (var value in result.Source.GetSpan(token.Span))
            {
                Mix(ref hash, value, Prime);
            }
        }
    }

    return hash;
}

static void Mix(ref ulong hash, ulong value, ulong prime)
{
    hash ^= value;
    hash *= prime;
}

static int ParsePositive(string text, string name)
{
    if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
    {
        throw new ArgumentOutOfRangeException(name, text, "Expected a positive integer.");
    }

    return value;
}

static string? GetOption(IEnumerable<string> arguments, string prefix) =>
    arguments.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.Ordinal))?
        [prefix.Length..];
