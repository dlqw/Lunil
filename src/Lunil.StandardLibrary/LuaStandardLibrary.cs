using Lunil.Core;
using Lunil.Runtime;
using Lunil.Runtime.Values;

namespace Lunil.StandardLibrary;

/// <summary>Registration entry points for versioned standard-library modules implemented by Lunil.</summary>
public static class LuaStandardLibrary
{
    /// <summary>Installs every standard-library module supported by the state's Lua version.</summary>
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
        var stringModule = InstallString(state);
        var features = LuaVersionFeatureTable.Get(state.LanguageVersion);
        if (!features.HasStringPack)
        {
            Remove(state, stringModule, "pack");
            Remove(state, stringModule, "packsize");
            Remove(state, stringModule, "unpack");
        }
        if (features.HasStringGFind)
        {
            LuaLibraryHelpers.SetFunction(state, stringModule, "gfind", LuaStringLibrary.GMatch);
        }

        var table = InstallTable(state);
        if (!features.HasTableMove)
        {
            Remove(state, table, "move");
        }
        if (!features.HasTablePack)
        {
            Remove(state, table, "pack");
        }
        if (features.HasLegacyTable)
        {
            LuaLibraryHelpers.SetFunction(state, table, "maxn", LuaTableLibrary.MaxN);
            LuaLibraryHelpers.SetFunction(state, table, "getn", LuaTableLibrary.MaxN);
            if (state.LanguageVersion == LuaLanguageVersion.Lua51)
            {
                LuaLibraryHelpers.SetFunction(state, table, "foreach", LuaTableLibrary.Foreach);
                LuaLibraryHelpers.SetFunction(state, table, "foreachi", LuaTableLibrary.ForeachI);
                LuaLibraryHelpers.SetFunction(state, table, "setn", LuaTableLibrary.SetN);
            }
        }
        else
        {
            Remove(state, table, "maxn");
            Remove(state, table, "getn");
        }
        if (features.HasTableCreate)
        {
            LuaLibraryHelpers.SetFunction(state, table, "create", LuaTableLibrary.Create);
        }

        var math = InstallMath(state);
        if (!features.HasLegacyMath)
        {
            Remove(state, math, "log10");
            Remove(state, math, "atan2");
            Remove(state, math, "pow");
            Remove(state, math, "sinh");
            Remove(state, math, "cosh");
            Remove(state, math, "tanh");
        }
        if (state.LanguageVersion is LuaLanguageVersion.Lua51 or LuaLanguageVersion.Lua52)
        {
            Remove(state, math, "tointeger");
            Remove(state, math, "type");
            Remove(state, math, "ult");
            Remove(state, math, "maxinteger");
            Remove(state, math, "mininteger");
        }

        RegisterLoaded(state, loaded, "string", stringModule);
        if (LuaVersionFeatureTable.Get(state.LanguageVersion).HasUtf8Library)
        {
            RegisterLoaded(state, loaded, "utf8", InstallUtf8(state));
        }
        RegisterLoaded(state, loaded, "table", table);
        RegisterLoaded(state, loaded, "math", math);
        RegisterLoaded(state, loaded, "io", InstallIo(state));
        RegisterLoaded(state, loaded, "os", InstallOs(state));
        RegisterLoaded(state, loaded, "debug", InstallDebug(state));
        if (LuaVersionFeatureTable.Get(state.LanguageVersion).HasBit32Library)
        {
            RegisterLoaded(state, loaded, "bit32", LuaBit32Library.Install(state));
        }
        return globals;
    }

    private static void RegisterLoaded(
        LuaState state,
        LuaTable loaded,
        string name,
        LuaTable module) =>
        loaded.Set(LuaLibraryHelpers.String(state, name), LuaValue.FromTable(module));

    /// <summary>Installs the version-selected basic library into the global environment.</summary>
    public static LuaTable InstallBasic(
        LuaState state,
        LuaStandardLibraryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return LuaBasicLibrary.Install(state, options);
    }

    /// <summary>Installs the version-selected math module into the global environment.</summary>
    public static LuaTable InstallMath(LuaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return LuaMathLibrary.Install(state);
    }

    /// <summary>Installs the version-selected UTF-8 module into the global environment.</summary>
    public static LuaTable InstallUtf8(LuaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return LuaUtf8Library.Install(state);
    }

    /// <summary>Installs the version-selected table module into the global environment.</summary>
    public static LuaTable InstallTable(LuaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return LuaTableLibrary.Install(state);
    }

    /// <summary>Installs the version-selected string module and string metatable.</summary>
    public static LuaTable InstallString(LuaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return LuaStringLibrary.Install(state);
    }

    /// <summary>Installs the version-selected package module and global require function.</summary>
    public static LuaTable InstallPackage(LuaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return LuaPackageLibrary.Install(state);
    }

    /// <summary>Installs the version-selected I/O module and FILE* userdata metatable.</summary>
    public static LuaTable InstallIo(
        LuaState state,
        LuaStandardLibraryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return LuaIoLibrary.Install(state, options);
    }

    /// <summary>Installs the version-selected operating-system module.</summary>
    public static LuaTable InstallOs(
        LuaState state,
        LuaStandardLibraryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return LuaOsLibrary.Install(state, options);
    }

    /// <summary>Installs the version-selected debug module and runtime hook bridge.</summary>
    public static LuaTable InstallDebug(LuaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return LuaDebugLibrary.Install(state);
    }

    /// <summary>Installs the version-selected coroutine module into the global environment.</summary>
    public static LuaTable InstallCoroutine(LuaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureImplemented(state);
        return state.InstallCoroutineModule();
    }

    private static void EnsureImplemented(LuaState state)
    {
        if (!LuaVersionFeatureTable.Get(state.LanguageVersion).IsImplemented)
        {
            throw new NotSupportedException(
                $"The {LuaLanguageVersions.GetDisplayName(state.LanguageVersion)} standard library " +
                "is not implemented yet.");
        }
    }

    private static void Remove(LuaState state, LuaTable table, string name) =>
        table.Set(LuaLibraryHelpers.String(state, name), LuaValue.Nil);
}
