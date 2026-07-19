using Lunil.Core;
using Lunil.Hosting;
using Lunil.Runtime;

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
    public void HostDoesNotInstallLua54LibraryIntoAnotherLanguageState()
    {
        Assert.Throws<NotSupportedException>(() => new LuaHost(new LuaHostOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
        }));
    }
}
