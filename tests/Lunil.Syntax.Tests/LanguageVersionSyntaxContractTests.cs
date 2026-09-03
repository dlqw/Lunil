using Lunil.Core;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Syntax.Tests;

/// <summary>
/// Systematic syntax/lexical boundary matrix for Lua 5.1–5.5 (ADR 0028).
/// </summary>
public sealed class LanguageVersionSyntaxContractTests
{
    public static IEnumerable<object[]> AllVersions() =>
    [
        new object[] { LuaLanguageVersion.Lua51 },
        new object[] { LuaLanguageVersion.Lua52 },
        new object[] { LuaLanguageVersion.Lua53 },
        new object[] { LuaLanguageVersion.Lua54 },
        new object[] { LuaLanguageVersion.Lua55 },
    ];

    [Theory]
    [InlineData(LuaLanguageVersion.Lua51, true)]
    [InlineData(LuaLanguageVersion.Lua52, false)]
    [InlineData(LuaLanguageVersion.Lua53, false)]
    [InlineData(LuaLanguageVersion.Lua54, false)]
    [InlineData(LuaLanguageVersion.Lua55, false)]
    public void GotoIsRejectedOnlyOnLua51(LuaLanguageVersion version, bool rejected)
    {
        var result = Parse("::label::\ngoto label\nreturn 0", version);
        if (rejected)
        {
            // Lua 5.1 has neither labels nor goto; 'goto' lexes as an ordinary
            // identifier there, so the sequence is rejected as a syntax error.
            Assert.NotEmpty(result.Diagnostics);
        }
        else
        {
            Assert.Empty(result.Diagnostics);
        }
    }

    [Theory]
    [InlineData(LuaLanguageVersion.Lua51, true)]
    [InlineData(LuaLanguageVersion.Lua52, true)]
    [InlineData(LuaLanguageVersion.Lua53, false)]
    [InlineData(LuaLanguageVersion.Lua54, false)]
    [InlineData(LuaLanguageVersion.Lua55, false)]
    public void BitwiseAndFloorDivisionRejectedBeforeLua53(LuaLanguageVersion version, bool rejected)
    {
        var bitwise = Parse("return 1 & 2 | 3 ~ 4", version);
        var floor = Parse("return 7 // 2", version);
        if (rejected)
        {
            Assert.NotEmpty(bitwise.Diagnostics);
            Assert.NotEmpty(floor.Diagnostics);
        }
        else
        {
            Assert.Empty(bitwise.Diagnostics);
            Assert.Empty(floor.Diagnostics);
        }
    }

    [Theory]
    [InlineData(LuaLanguageVersion.Lua51, true)]
    [InlineData(LuaLanguageVersion.Lua52, true)]
    [InlineData(LuaLanguageVersion.Lua53, true)]
    [InlineData(LuaLanguageVersion.Lua54, false)]
    [InlineData(LuaLanguageVersion.Lua55, false)]
    public void LocalAttributesRejectedBeforeLua54(LuaLanguageVersion version, bool rejected)
    {
        var result = Parse("local value <const> = 1\nreturn value", version);
        if (rejected)
        {
            Assert.Contains(result.Diagnostics, d =>
                d.Code == "LUA2010" ||
                d.Message.Contains("attribute", StringComparison.OrdinalIgnoreCase) ||
                d.Message.Contains("Lua 5.4", StringComparison.Ordinal));
        }
        else
        {
            Assert.Empty(result.Diagnostics);
        }
    }

    [Theory]
    [MemberData(nameof(AllVersions))]
    public void IntegerAndFloatLiteralsLexOnEveryVersion(LuaLanguageVersion version)
    {
        var lexing = LuaLexer.Lex(
            SourceText.FromUtf8("return 42 3.14 1e-3 0xFF"),
            LuaLexerOptions.Default with { LanguageVersion = version });
        Assert.Equal(version, lexing.LanguageVersion);
        Assert.DoesNotContain(lexing.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(LuaLanguageVersion.Lua51)]
    [InlineData(LuaLanguageVersion.Lua52)]
    [InlineData(LuaLanguageVersion.Lua53)]
    [InlineData(LuaLanguageVersion.Lua54)]
    public void GlobalKeywordIsIdentifierBeforeLua55(LuaLanguageVersion version)
    {
        var result = Parse("local global = 1\nreturn global", version);
        Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain(
            result.Root.DescendantNodes(),
            node => node.Kind == LuaSyntaxKind.GlobalDeclarationStatement);
    }

    [Fact]
    public void Lua55AcceptsGlobalAndNamedVararg()
    {
        var global = Parse("global print\nreturn print", LuaLanguageVersion.Lua55);
        Assert.Empty(global.Diagnostics);
        Assert.Contains(
            global.Root.DescendantNodes(),
            node => node.Kind == LuaSyntaxKind.GlobalDeclarationStatement);

        var vararg = Parse(
            "return function(a, ... values) return values end",
            LuaLanguageVersion.Lua55);
        Assert.Empty(vararg.Diagnostics);
    }

    [Theory]
    [InlineData(LuaLanguageVersion.Lua51)]
    [InlineData(LuaLanguageVersion.Lua52)]
    public void UnaryBitwiseNotRejectedOnLua51And52(LuaLanguageVersion version)
    {
        var result = Parse("return ~1", version);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Theory]
    [InlineData(LuaLanguageVersion.Lua51, false, false, false, false)]
    [InlineData(LuaLanguageVersion.Lua52, true, true, false, false)]
    [InlineData(LuaLanguageVersion.Lua53, true, true, true, false)]
    [InlineData(LuaLanguageVersion.Lua54, true, true, true, true)]
    [InlineData(LuaLanguageVersion.Lua55, true, true, true, true)]
    public void GrammarFeatureTableFreezesLiteralAndAttributeBoundaries(
        LuaLanguageVersion version,
        bool hexadecimalFloat,
        bool extendedStringEscapes,
        bool unicodeEscape,
        bool localAttributes)
    {
        var features = LuaGrammarFeatureTable.Get(version);

        Assert.Equal(hexadecimalFloat, features.SupportsHexadecimalFloats);
        Assert.Equal(extendedStringEscapes, features.SupportsHexadecimalStringEscapes);
        Assert.Equal(extendedStringEscapes, features.SupportsWhitespaceEatingStringEscape);
        Assert.Equal(unicodeEscape, features.SupportsUnicodeStringEscapes);
        Assert.Equal(localAttributes, features.SupportsLocalAttributes);
    }

    [Theory]
    [InlineData(LuaLanguageVersion.Lua51, true)]
    [InlineData(LuaLanguageVersion.Lua52, false)]
    [InlineData(LuaLanguageVersion.Lua53, false)]
    [InlineData(LuaLanguageVersion.Lua54, false)]
    [InlineData(LuaLanguageVersion.Lua55, false)]
    public void HexadecimalFloatBoundaryMatchesVersion(LuaLanguageVersion version, bool rejected)
    {
        var result = Parse("return 0x1.8p1", version);

        Assert.Equal(rejected, result.Diagnostics.Any(static item => item.Code == "LUA2015"));
    }

    [Theory]
    [InlineData(LuaLanguageVersion.Lua51, 2)]
    [InlineData(LuaLanguageVersion.Lua52, 0)]
    [InlineData(LuaLanguageVersion.Lua53, 0)]
    [InlineData(LuaLanguageVersion.Lua54, 0)]
    [InlineData(LuaLanguageVersion.Lua55, 0)]
    public void ExtendedStringEscapeBoundaryMatchesVersion(
        LuaLanguageVersion version,
        int expectedParserDiagnostics)
    {
        var result = Parse("return '\\x41', 'a\\z  b'", version);

        Assert.Equal(
            expectedParserDiagnostics,
            result.Diagnostics.Count(static item => item.Code == "LUA2016"));
    }

    [Theory]
    [InlineData(LuaLanguageVersion.Lua51, 1)]
    [InlineData(LuaLanguageVersion.Lua52, 0)]
    [InlineData(LuaLanguageVersion.Lua53, 0)]
    [InlineData(LuaLanguageVersion.Lua54, 0)]
    [InlineData(LuaLanguageVersion.Lua55, 0)]
    public void SugarCallStringArgumentEscapesMatchVersion(
        LuaLanguageVersion version,
        int expectedParserDiagnostics)
    {
        var result = Parse("return f'\\x41'", version);

        Assert.Equal(
            expectedParserDiagnostics,
            result.Diagnostics.Count(static item => item.Code == "LUA2016"));
    }

    [Fact]
    public void LongStringArgumentNeverReportsEscapeDiagnostics()
    {
        var result = Parse("return f[[a\\z b]]", LuaLanguageVersion.Lua51);

        Assert.DoesNotContain(result.Diagnostics, static item => item.Code == "LUA2016");
    }

    [Fact]
    public void Lua55AcceptsPrefixAttributesFromTheOfficialGrammar()
    {
        var result = Parse(
            "local <close> resource = acquire()\nlocal <const> value = 1",
            LuaLanguageVersion.Lua55);

        Assert.Empty(result.Diagnostics);
        Assert.True(LuaGrammarFeatureTable.Get(LuaLanguageVersion.Lua55)
            .SupportsPrefixAttributes);
        Assert.False(LuaGrammarFeatureTable.Get(LuaLanguageVersion.Lua54)
            .SupportsPrefixAttributes);
    }

    private static LuaParseResult Parse(string source, LuaLanguageVersion version) =>
        LuaParser.Parse(
            SourceText.FromUtf8(source),
            LuaLexerOptions.Default with { LanguageVersion = version },
            LuaParserOptions.Default with { LanguageVersion = version });
}
