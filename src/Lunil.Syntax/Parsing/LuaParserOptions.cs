using Lunil.Core;

namespace Lunil.Syntax.Parsing;

public sealed record LuaParserOptions
{
    public const int MaximumSupportedRecursionDepth = 512;

    public static LuaParserOptions Default { get; } = new();

    public LuaLanguageVersion LanguageVersion { get; init; } = LuaLanguageVersions.Default;

    public int MaximumRecursionDepth { get; init; } = 200;

    public int MaximumNodeCount { get; init; } = 2_000_000;

    public int MaximumDiagnosticCount { get; init; } = 1_000;

    /// <summary>
    /// Stores the completed tree in a compact arena. Disable this when the tree will be bound
    /// immediately so the parser can hand the already materialized nodes directly to the binder.
    /// </summary>
    public bool UseCompactSyntaxArena { get; init; } = true;
}
