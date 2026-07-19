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
