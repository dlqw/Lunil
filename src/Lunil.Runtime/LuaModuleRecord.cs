using Lunil.IR.Canonical;
using Lunil.Runtime.Values;

namespace Lunil.Runtime;

/// <summary>Identifies how <c>require</c> obtained a loaded module's loader.</summary>
public enum LuaModuleLoaderKind : byte
{
    /// <summary>The loader came from <c>package.preload</c>.</summary>
    Preload,

    /// <summary>The built-in Lua file searcher compiled the loader from a source path.</summary>
    LuaFile,

    /// <summary>The loader came from a host-defined or replaced package searcher.</summary>
    CustomSearcher,
}

/// <summary>
/// Immutable snapshot of one successful <c>require</c> operation tracked by a
/// <see cref="LuaState"/>.
/// </summary>
/// <remarks>
/// <see cref="Loader"/>, <see cref="LoaderData"/>, and <see cref="CachedValue"/> are borrowed
/// Lua values. The state keeps them rooted while this record remains current; callers that retain
/// them after cache eviction or replacement should create a <see cref="Memory.LuaHandle"/>.
/// </remarks>
public sealed record LuaModuleRecord(
    string Name,
    LuaModuleLoaderKind LoaderKind,
    LuaValue Loader,
    LuaValue LoaderData,
    LuaValue CachedValue,
    LuaIrModule? Module,
    long Revision);
