using System.Runtime.CompilerServices;
using Lunil.Runtime;

namespace Lunil.StandardLibrary;

internal sealed class LuaStandardLibraryContext
{
    private static readonly ConditionalWeakTable<LuaState, LuaStandardLibraryContext> Contexts = new();

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
        ArgumentNullException.ThrowIfNull(state);
        var context = new LuaStandardLibraryContext(options ?? LuaStandardLibraryOptions.Default);
        Contexts.Remove(state);
        Contexts.Add(state, context);
        return context;
    }

    public static LuaStandardLibraryContext Get(LuaState state) =>
        Contexts.GetValue(
            state,
            static _ => new LuaStandardLibraryContext(LuaStandardLibraryOptions.Default));
}
