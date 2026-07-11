namespace Luac.Syntax.Parsing;

public sealed record LuaParserOptions
{
    public static LuaParserOptions Default { get; } = new();

    public int MaximumRecursionDepth { get; init; } = 200;

    public int MaximumNodeCount { get; init; } = 2_000_000;

    public int MaximumDiagnosticCount { get; init; } = 1_000;
}
