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
}
