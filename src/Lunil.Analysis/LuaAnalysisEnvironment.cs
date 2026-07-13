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
}
