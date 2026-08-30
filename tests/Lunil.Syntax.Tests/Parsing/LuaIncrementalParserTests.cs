using Lunil.Core;
using Lunil.Core.Text;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Syntax.Tests.Parsing;

public sealed class LuaIncrementalParserTests
{
    [Fact]
    public void LocalEditReusesTopLevelPrefixAndMatchesFullParse()
    {
        const string Source = "local first = 1\nlocal second = 2\nreturn first + second\n";
        var previous = LuaParser.Parse(SourceText.FromUtf8(Source));
        var offset = Source.IndexOf('2');
        var change = LuaTextChange.FromUtf8(new TextSpan(offset, 1), "42");

        var incremental = LuaParser.ParseIncremental(previous, change);
        var full = LuaParser.Parse(change.Apply(previous.Source));

        AssertEquivalent(full, incremental);
        Assert.False(incremental.IncrementalMetrics!.WasFullReparse);
        Assert.True(incremental.IncrementalMetrics.ReusedNodeCount > 0);
        Assert.True(incremental.IncrementalMetrics.ReusedTokenCount > 0);
        Assert.NotNull(previous.CompactMetrics);
    }

    [Fact]
    public void MaterializedTreeModeMatchesCompactArenaWithoutCopyingTheTree()
    {
        const string Source = "local value = { answer = 42 }\nreturn value.answer\n";
        var compact = LuaParser.Parse(SourceText.FromUtf8(Source));
        var materialized = LuaParser.Parse(
            SourceText.FromUtf8(Source),
            parserOptions: LuaParserOptions.Default with { UseCompactSyntaxArena = false });

        AssertEquivalent(compact, materialized);
        Assert.NotNull(compact.CompactMetrics);
        Assert.Null(materialized.CompactMetrics);
        Assert.False(materialized.Configuration.Parser.UseCompactSyntaxArena);
    }

    [Fact]
    public void LongDelimiterEditReparsesFromTheOwningStatement()
    {
        const string Source = "local first = 1\nlocal text = [==[payload]==]\nreturn text\n";
        var previous = LuaParser.Parse(SourceText.FromUtf8(Source));
        var offset = Source.IndexOf("payload", StringComparison.Ordinal);
        var change = LuaTextChange.FromUtf8(new TextSpan(offset, "payload".Length), "updated");

        var incremental = LuaParser.ParseIncremental(previous, change);
        var full = LuaParser.Parse(change.Apply(previous.Source));

        AssertEquivalent(full, incremental);
        Assert.False(incremental.IncrementalMetrics!.WasFullReparse);
        Assert.True(incremental.IncrementalMetrics.ReparsedNewSpan.Start > 0);
    }

    [Fact]
    public void BomAndShebangConfigurationSurvivesIncrementalParse()
    {
        byte[] source = [.. "\uFEFF#!/usr/bin/env lua\nlocal value = 1\nreturn value\n"u8];
        var lexerOptions = LuaLexerOptions.File;
        var parserOptions = LuaParserOptions.Default;
        var previous = LuaParser.Parse(new SourceText(source), lexerOptions, parserOptions);
        var offset = Array.IndexOf(source, (byte)'1');
        var change = LuaTextChange.FromUtf8(new TextSpan(offset, 1), "2");

        var incremental = LuaParser.ParseIncremental(previous, change);
        var full = LuaParser.Parse(change.Apply(previous.Source), lexerOptions, parserOptions);

        AssertEquivalent(full, incremental);
        Assert.Equal(lexerOptions, incremental.Configuration.Lexer);
    }

    [Fact]
    public void UnsafeUtf8BoundaryFallsBackToFullParseWithoutLosingBytes()
    {
        const string Source = "local value = '😀'\nreturn value";
        var previous = LuaParser.Parse(SourceText.FromUtf8(Source));
        var emojiStart = SourceText.FromUtf8("local value = '").Length;
        var change = LuaTextChange.FromBytes(new TextSpan(emojiStart + 1, 1), [(byte)'x']);

        var incremental = LuaParser.ParseIncremental(previous, change);
        var full = LuaParser.Parse(change.Apply(previous.Source));

        AssertEquivalent(full, incremental);
        Assert.True(incremental.IncrementalMetrics!.WasFullReparse);
        Assert.Equal("unsafe-utf8-boundary", incremental.IncrementalMetrics.Reason);
    }

    [Fact]
    public void OptionOrVersionChangeInvalidatesTheWholeTree()
    {
        var previous = LuaParser.Parse(SourceText.FromUtf8("return 1"));
        var change = LuaTextChange.FromUtf8(new TextSpan(7, 1), "2");
        var lexerOptions = LuaLexerOptions.Default with
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
        };
        var parserOptions = LuaParserOptions.Default with
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
        };

        var result = LuaParser.ParseIncremental(
            previous,
            change,
            lexerOptions,
            parserOptions);

        Assert.True(result.IncrementalMetrics!.WasFullReparse);
        Assert.Equal("configuration-changed", result.IncrementalMetrics.Reason);
        Assert.Equal(LuaLanguageVersion.Lua53, result.LanguageVersion);
    }

    [Fact]
    public void StatementAfterTopLevelReturnMatchesFullParseDiagnostics()
    {
        const string Source = "local first = 1\nreturn first\n";
        var previous = LuaParser.Parse(SourceText.FromUtf8(Source));
        var change = LuaTextChange.FromUtf8(new TextSpan(Source.Length, 0), "local after = 2\n");

        var incremental = LuaParser.ParseIncremental(previous, change);
        var full = LuaParser.Parse(change.Apply(previous.Source));

        Assert.False(incremental.IncrementalMetrics!.WasFullReparse);
        Assert.Contains(full.Diagnostics, static diagnostic => diagnostic.Code == "LUA2008");
        Assert.Contains(incremental.Diagnostics, static diagnostic => diagnostic.Code == "LUA2008");
    }

    private static void AssertEquivalent(LuaParseResult expected, LuaParseResult actual)
    {
        Assert.Equal(expected.Source.ToArray(), actual.Source.ToArray());
        Assert.Equal(
            expected.Diagnostics.Select(static item =>
                (item.Code, item.Severity, item.Span, item.Message)),
            actual.Diagnostics.Select(static item =>
                (item.Code, item.Severity, item.Span, item.Message)));
        Assert.Equal(
            expected.Root.DescendantNodes().Select(static node => (node.Kind, node.Span, node.FullSpan)),
            actual.Root.DescendantNodes().Select(static node => (node.Kind, node.Span, node.FullSpan)));
        Assert.Equal(
            TokenFacts(expected),
            TokenFacts(actual));
    }

    private static IEnumerable<(LuaTokenKind Kind, TextSpan Span, TextSpan FullSpan, bool Missing, string Text)>
        TokenFacts(LuaParseResult result) =>
        result.Root.DescendantTokens().Select(token =>
            (token.Kind,
                token.Span,
                token.FullSpan,
                token.IsMissing,
                token.IsMissing ? string.Empty : Convert.ToHexString(result.Source.GetSpan(token.Span))));
}
