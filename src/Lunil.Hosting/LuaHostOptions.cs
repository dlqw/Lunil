using Lunil.CodeGen.Cil.Jit;
using Lunil.Compiler;
using Lunil.Core;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.StandardLibrary;
using Lunil.Workspace;

namespace Lunil.Hosting;

public enum LuaHostProfile : byte
{
    Trusted,
    Restricted,
    Deterministic,
}

/// <summary>Selects the execution engine owned by a <see cref="LuaHost"/>.</summary>
public enum LuaHostExecutionBackend : byte
{
    /// <summary>
    /// Uses the qualified tiered JIT when dynamic code is available and otherwise uses the
    /// interpreter.
    /// </summary>
    Auto,

    /// <summary>Always uses the reference interpreter.</summary>
    Interpreter,

    /// <summary>
    /// Requires a runtime that supports dynamic code and uses the qualified tiered JIT. Functions
    /// that are not eligible for compilation retain the JIT executor's interpreter fallback.
    /// </summary>
    Jit,
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

    /// <summary>
    /// Gets the authoritative language contract for source compilation, workspace analysis,
    /// runtime execution, binary chunks, and standard-library installation owned by this host.
    /// </summary>
    public LuaLanguageVersion LanguageVersion { get; init; } = LuaLanguageVersions.Default;

    public LuaCompilerOptions Compiler { get; init; } = LuaCompilerOptions.Default;

    public LuaStateOptions State { get; init; } = LuaStateOptions.Default;

    public LuaInterpreterOptions Execution { get; init; } = LuaInterpreterOptions.Default;

    /// <summary>Gets the requested execution backend. The default selects a qualified backend.</summary>
    public LuaHostExecutionBackend ExecutionBackend { get; init; } = LuaHostExecutionBackend.Auto;

    /// <summary>
    /// Gets tiered-JIT configuration. <see cref="Execution"/> always supplies the interpreter
    /// fallback budgets so both backends enforce the same execution limits.
    /// </summary>
    public LuaJitExecutorOptions Jit { get; init; } = LuaJitExecutorOptions.Default;

    public LuaWorkspaceOptions Workspace { get; init; } = LuaWorkspaceOptions.Default;

    public ILuaModuleResolver? ModuleResolver { get; init; }

    public bool InstallStandardLibrary { get; init; } = true;

    /// <summary>
    /// Gets explicit standard-library capabilities. When null, the selected profile creates
    /// its own capability set. Explicit capabilities take precedence over profile defaults.
    /// </summary>
    public LuaStandardLibraryOptions? StandardLibrary { get; init; }
}
