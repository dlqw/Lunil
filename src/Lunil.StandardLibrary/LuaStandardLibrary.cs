using Lunil.Core;
using Lunil.Runtime;
using Lunil.Runtime.Values;

namespace Lunil.StandardLibrary;

/// <summary>Registration entry points for standard-library modules implemented by Lunil.</summary>
public static class LuaStandardLibrary
{
    /// <summary>Installs every PUC Lua 5.4 standard library into the state.</summary>
    public static LuaTable InstallAll(
        LuaState state,
        LuaStandardLibraryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        var globals = InstallBasic(state, options);
        var coroutine = InstallCoroutine(state);
        var package = InstallPackage(state);
        var loaded = package.Get(LuaLibraryHelpers.String(state, "loaded")).AsTable();
        RegisterLoaded(state, loaded, "_G", globals);
        RegisterLoaded(state, loaded, "coroutine", coroutine);
        RegisterLoaded(state, loaded, "string", InstallString(state));
        RegisterLoaded(state, loaded, "utf8", InstallUtf8(state));
        RegisterLoaded(state, loaded, "table", InstallTable(state));
        RegisterLoaded(state, loaded, "math", InstallMath(state));
        RegisterLoaded(state, loaded, "io", InstallIo(state));
        RegisterLoaded(state, loaded, "os", InstallOs(state));
        RegisterLoaded(state, loaded, "debug", InstallDebug(state));
        return globals;
    }

    private static void RegisterLoaded(
        LuaState state,
        LuaTable loaded,
        string name,
        LuaTable module) =>
        loaded.Set(LuaLibraryHelpers.String(state, name), LuaValue.FromTable(module));

    /// <summary>Installs the Lua 5.4 basic library into the global environment.</summary>
    public static LuaTable InstallBasic(
        LuaState state,
        LuaStandardLibraryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return LuaBasicLibrary.Install(state, options);
    }

    /// <summary>Installs the complete Lua 5.4 math module into the global environment.</summary>
    public static LuaTable InstallMath(LuaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return LuaMathLibrary.Install(state);
    }

    /// <summary>Installs the complete Lua 5.4 UTF-8 module into the global environment.</summary>
    public static LuaTable InstallUtf8(LuaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return LuaUtf8Library.Install(state);
    }

    /// <summary>Installs the complete Lua 5.4 table module into the global environment.</summary>
    public static LuaTable InstallTable(LuaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return LuaTableLibrary.Install(state);
    }

    /// <summary>Installs the Lua 5.4 string module and string metatable.</summary>
    public static LuaTable InstallString(LuaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return LuaStringLibrary.Install(state);
    }

    /// <summary>Installs the Lua 5.4 package module and global require function.</summary>
    public static LuaTable InstallPackage(LuaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return LuaPackageLibrary.Install(state);
    }

    /// <summary>Installs the Lua 5.4 I/O module and FILE* userdata metatable.</summary>
    public static LuaTable InstallIo(
        LuaState state,
        LuaStandardLibraryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return LuaIoLibrary.Install(state, options);
    }

    /// <summary>Installs the Lua 5.4 operating-system module.</summary>
    public static LuaTable InstallOs(
        LuaState state,
        LuaStandardLibraryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return LuaOsLibrary.Install(state, options);
    }

    /// <summary>Installs the Lua 5.4 debug module and runtime hook bridge.</summary>
    public static LuaTable InstallDebug(LuaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return LuaDebugLibrary.Install(state);
    }

    /// <summary>Installs the complete Lua 5.4 coroutine module into the global environment.</summary>
    public static LuaTable InstallCoroutine(LuaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return state.InstallCoroutineModule();
    }

    private static void EnsureImplemented(LuaState state)
    {
        if (state.LanguageVersion != LuaLanguageVersion.Lua54)
        {
            throw new NotSupportedException(
                $"The {LuaLanguageVersions.GetDisplayName(state.LanguageVersion)} standard library " +
                "is not implemented yet.");
        }
    }
}
