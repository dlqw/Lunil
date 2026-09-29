using Lunil.Runtime;

namespace Lunil.StandardLibrary;

internal sealed class LuaStandardLibraryContext
{
    private LuaStandardLibraryContext(LuaStandardLibraryOptions options)
    {
        Options = options;
    }

    public LuaStandardLibraryOptions Options { get; }

    public bool WarningsEnabled { get; set; }

    public static LuaStandardLibraryContext Configure(
        LuaState state,
        LuaStandardLibraryOptions? options)
    {
        LunilGuard.NotNull(state);
        var context = new LuaStandardLibraryContext(options ?? LuaStandardLibraryOptions.Default);
        state.SetService(context);
        return context;
    }

    public static LuaStandardLibraryContext Get(LuaState state) =>
        state.GetOrCreateService(
            static () => new LuaStandardLibraryContext(LuaStandardLibraryOptions.Default));
}
