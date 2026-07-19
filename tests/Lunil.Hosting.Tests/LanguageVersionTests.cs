using Lunil.Core;
using Lunil.Hosting;
using Lunil.Runtime;
using Lunil.Runtime.Execution;

namespace Lunil.Hosting.Tests;

public sealed class LanguageVersionTests
{
    [Fact]
    public void HostAlignsNestedCompilerWorkspaceAndStateOptions()
    {
        using var host = new LuaHost(new LuaHostOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
            InstallStandardLibrary = false,
        });

        Assert.Equal(LuaLanguageVersion.Lua53, host.Options.LanguageVersion);
        Assert.Equal(LuaLanguageVersion.Lua53, host.Compiler.Options.LanguageVersion);
        Assert.Equal(LuaLanguageVersion.Lua53, host.Workspace.Options.LanguageVersion);
        Assert.Equal(LuaLanguageVersion.Lua53, host.State.LanguageVersion);
    }

    [Fact]
    public void Lua53RunsIntegerBitwiseGotoAndClosureSemantics()
    {
        using var host = new LuaHost(new LuaHostOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
        });

        var result = host.RunUtf8(
            "local function add(value) return value + 1 end\n" +
            "local total = 0\n" +
            "for i = 1, 4 do total = total + (add(i) << 1) end\n" +
            "goto done\n" +
            "total = 0\n" +
            "::done::\n" +
            "return total, 5 & 3, 8 >> 1");

        Assert.True(result.Succeeded);
        Assert.Equal(28, result.Execution!.Values[0].AsInteger());
        Assert.Equal(1, result.Execution.Values[1].AsInteger());
        Assert.Equal(4, result.Execution.Values[2].AsInteger());
    }

    [Fact]
    public void HostInstallsLua53LibraryWithLua53OnlySurface()
    {
        using var host = new LuaHost(new LuaHostOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
        });

        var result = host.RunUtf8(
            "return _VERSION, type(warn), type(coroutine.close), math.type(1), 5 // 2");

        Assert.Equal(LuaVmSignal.Completed, result.Execution?.Signal);
        Assert.True(result.Compilation.Succeeded);
        Assert.Equal("Lua 5.3", result.Execution!.Values[0].AsString().ToString());
        Assert.Equal("nil", result.Execution.Values[1].AsString().ToString());
        Assert.Equal("nil", result.Execution.Values[2].AsString().ToString());
        Assert.Equal("integer", result.Execution.Values[3].AsString().ToString());
        Assert.Equal(2, result.Execution.Values[4].AsInteger());
    }
}
