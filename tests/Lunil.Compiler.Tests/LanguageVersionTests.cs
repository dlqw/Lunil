using Lunil.Core;
using Lunil.Compiler;

namespace Lunil.Compiler.Tests;

public sealed class LanguageVersionTests
{
    [Fact]
    public void DefaultCompilationPublishesLua54Identity()
    {
        var result = new LuaCompiler().CompileUtf8("return 1");

        Assert.True(result.Succeeded);
        Assert.Equal(LuaLanguageVersion.Lua54, result.LanguageVersion);
        Assert.Equal(LuaLanguageVersion.Lua54, result.Module!.LanguageVersion);
    }

    [Theory]
    [InlineData(LuaLanguageVersion.Lua51)]
    [InlineData(LuaLanguageVersion.Lua55)]
    public void NewlyImplementedVersionsPublishTheirOwnIdentity(
        LuaLanguageVersion version)
    {
        var result = new LuaCompiler(new LuaCompilerOptions
        {
            LanguageVersion = version,
        }).CompileUtf8("return 1");

        Assert.True(result.Succeeded);
        Assert.Equal(version, result.LanguageVersion);
        Assert.Equal(version, result.Module!.LanguageVersion);
    }

    [Fact]
    public void Lua53CompilationPublishesLua53CanonicalIdentity()
    {
        var result = new LuaCompiler(new LuaCompilerOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
        }).CompileUtf8("return 1 // 0x2");

        Assert.True(result.Succeeded);
        Assert.Equal(LuaLanguageVersion.Lua53, result.LanguageVersion);
        Assert.Equal(LuaLanguageVersion.Lua53, result.Module!.LanguageVersion);
    }

    [Fact]
    public void Lua52CompilationPublishesLua52CanonicalIdentityAndNumberSemantics()
    {
        var result = new LuaCompiler(new LuaCompilerOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua52,
        }).CompileUtf8("return 1 / 2");

        Assert.True(result.Succeeded);
        Assert.Equal(LuaLanguageVersion.Lua52, result.LanguageVersion);
        Assert.Equal(LuaLanguageVersion.Lua52, result.Module!.LanguageVersion);
    }

    [Theory]
    [InlineData("return 1 // 2")]
    [InlineData("return 1 & 2")]
    [InlineData("return ~1")]
    public void Lua52RejectsLua53OnlyOperators(string source)
    {
        var result = new LuaCompiler(new LuaCompilerOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua52,
        }).CompileUtf8(source);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "LUA2012");
    }
}
