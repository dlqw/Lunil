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
    [InlineData(LuaLanguageVersion.Lua52)]
    [InlineData(LuaLanguageVersion.Lua55)]
    public void UnimplementedVersionsFailExplicitlyInsteadOfUsingLua54Semantics(
        LuaLanguageVersion version)
    {
        var result = new LuaCompiler(new LuaCompilerOptions
        {
            LanguageVersion = version,
        }).CompileUtf8("return 1");

        Assert.False(result.Succeeded);
        Assert.Null(result.Module);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Phase == LuaCompilationPhase.Configuration &&
            diagnostic.Code == "LUA0001" &&
            diagnostic.Message.Contains("not implemented yet", StringComparison.Ordinal));
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
}
