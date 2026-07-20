using Lunil.Core;
using Lunil.Core.Text;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Syntax.Tests;

public sealed class LanguageVersionTests
{
    [Fact]
    public void LexerAndParserPreserveAnExplicitVersionIdentity()
    {
        var lexing = LuaLexer.Lex(
            SourceText.FromUtf8("return 1"),
            LuaLexerOptions.Default with { LanguageVersion = LuaLanguageVersion.Lua53 });
        var parsing = LuaParser.Parse(lexing);

        Assert.Equal(LuaLanguageVersion.Lua53, lexing.LanguageVersion);
        Assert.Equal(LuaLanguageVersion.Lua53, parsing.LanguageVersion);
    }

    [Fact]
    public void Lua53RejectsLua54LocalAttributes()
    {
        var result = LuaParser.Parse(
            SourceText.FromUtf8("local value <const> = 1"),
            LuaLexerOptions.Default with { LanguageVersion = LuaLanguageVersion.Lua53 },
            LuaParserOptions.Default with { LanguageVersion = LuaLanguageVersion.Lua53 });

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "LUA2010" &&
            diagnostic.Message.Contains("Lua 5.4", StringComparison.Ordinal));
    }

    [Fact]
    public void ParserRejectsMismatchedLexerAndParserVersions()
    {
        var lexing = LuaLexer.Lex(
            SourceText.FromUtf8("return 1"),
            LuaLexerOptions.Default with { LanguageVersion = LuaLanguageVersion.Lua53 });

        Assert.Throws<ArgumentException>(() => LuaParser.Parse(
            lexing,
            LuaParserOptions.Default with { LanguageVersion = LuaLanguageVersion.Lua54 }));
    }

    [Fact]
    public void Lua55AcceptsGlobalDeclarationsWhileEarlierVersionsRejectThem()
    {
        var lua55 = LuaParser.Parse(
            SourceText.FromUtf8("global<const> print\nreturn print"),
            LuaLexerOptions.Default with { LanguageVersion = LuaLanguageVersion.Lua55 },
            LuaParserOptions.Default with { LanguageVersion = LuaLanguageVersion.Lua55 });
        Assert.Empty(lua55.Diagnostics);
        Assert.Contains(lua55.Root.DescendantNodes(),
            node => node.Kind == LuaSyntaxKind.GlobalDeclarationStatement);

        var lua54 = LuaParser.Parse(
            SourceText.FromUtf8("global print"),
            LuaLexerOptions.Default with { LanguageVersion = LuaLanguageVersion.Lua54 },
            LuaParserOptions.Default with { LanguageVersion = LuaLanguageVersion.Lua54 });
        Assert.NotEmpty(lua54.Diagnostics);
        Assert.DoesNotContain(lua54.Root.DescendantNodes(),
            node => node.Kind == LuaSyntaxKind.GlobalDeclarationStatement);
    }

    [Theory]
    [InlineData(LuaLanguageVersion.Lua51)]
    [InlineData(LuaLanguageVersion.Lua52)]
    [InlineData(LuaLanguageVersion.Lua53)]
    [InlineData(LuaLanguageVersion.Lua54)]
    public void EarlierVersionsTreatGlobalAsAnIdentifier(LuaLanguageVersion version)
    {
        var result = LuaParser.Parse(
            SourceText.FromUtf8("local global = 1\nreturn global"),
            LuaLexerOptions.Default with { LanguageVersion = version },
            LuaParserOptions.Default with { LanguageVersion = version });

        Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain(result.Root.DescendantNodes(),
            node => node.Kind == LuaSyntaxKind.GlobalDeclarationStatement);
    }
}
