using Lunil.Compiler;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.StandardLibrary;

namespace Lunil.Hosting;

public enum LuaHostProfile : byte
{
    Trusted,
    Restricted,
    Deterministic,
}

/// <summary>Compiler, runtime-budget, and capability configuration for a reusable Lua host.</summary>
public sealed record LuaHostOptions
{
    public static LuaHostOptions Default { get; } = new();

    public static LuaHostOptions Trusted { get; } = new() { Profile = LuaHostProfile.Trusted };

    public static LuaHostOptions Restricted { get; } = new();

    public static LuaHostOptions Deterministic { get; } = new()
    {
        Profile = LuaHostProfile.Deterministic,
    };

    public LuaHostProfile Profile { get; init; } = LuaHostProfile.Restricted;

    public LuaCompilerOptions Compiler { get; init; } = LuaCompilerOptions.Default;

    public LuaStateOptions State { get; init; } = LuaStateOptions.Default;

    public LuaInterpreterOptions Execution { get; init; } = LuaInterpreterOptions.Default;

    public bool InstallStandardLibrary { get; init; } = true;

    /// <summary>
    /// Gets explicit standard-library capabilities. When null, the selected profile creates
    /// its own capability set. Explicit capabilities take precedence over profile defaults.
    /// </summary>
    public LuaStandardLibraryOptions? StandardLibrary { get; init; }
}
