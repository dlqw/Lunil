using Lunil.Core;

namespace Lunil.Semantics.Binding;

public sealed record LuaBinderOptions
{
    public static LuaBinderOptions Default { get; } = new();

    public LuaLanguageVersion LanguageVersion { get; init; } = LuaLanguageVersions.Default;

    public int MaximumActiveLocalsPerFunction { get; init; } = 200;

    public int MaximumUpvaluesPerFunction { get; init; } = 255;

    public int MaximumDiagnosticCount { get; init; } = 1_000;
}
