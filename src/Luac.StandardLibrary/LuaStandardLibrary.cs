using Luac.Runtime;
using Luac.Runtime.Values;

namespace Luac.StandardLibrary;

/// <summary>Registration entry points for standard-library modules implemented by Luac.</summary>
public static class LuaStandardLibrary
{
    /// <summary>Installs the complete Lua 5.4 coroutine module into the global environment.</summary>
    public static LuaTable InstallCoroutine(LuaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.InstallCoroutineModule();
    }
}
