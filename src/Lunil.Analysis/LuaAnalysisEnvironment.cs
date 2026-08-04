using System.Collections.Immutable;

namespace Lunil.Analysis;

/// <summary>Immutable external type inputs supplied by a workspace or embedding host.</summary>
public sealed record LuaAnalysisEnvironment
{
    public static LuaAnalysisEnvironment Empty { get; } = new();

    /// <summary>
    /// Gets the type returned by a direct global <c>require("name")</c> call for each resolved
    /// module name. Calls through a shadowed local or with a dynamic name remain conservative.
    /// </summary>
    public ImmutableDictionary<string, LuaType> ModuleTypes { get; init; } =
        ImmutableDictionary<string, LuaType>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>
    /// Gets cross-file type declarations (<c>---@class</c>, <c>---@alias</c>, <c>---@enum</c>)
    /// collected from other documents in the workspace. Local declarations of the analyzed
    /// document take precedence over these.
    /// </summary>
    public ImmutableDictionary<string, LuaExternalTypeDeclaration> ExternalTypeDeclarations { get; init; } =
        ImmutableDictionary<string, LuaExternalTypeDeclaration>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>Gets the optional versioned contract for globals and modules injected by a host.</summary>
    public LuaHostAnalysisContract? HostContract { get; init; }
}
