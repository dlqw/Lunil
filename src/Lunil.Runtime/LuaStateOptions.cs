using Lunil.Core;
using Lunil.Runtime.Memory;

namespace Lunil.Runtime;

public sealed record LuaStateOptions
{
    public static LuaStateOptions Default { get; } = new();

    public LuaLanguageVersion LanguageVersion { get; init; } = LuaLanguageVersions.Default;

    public LuaHeapOptions Heap { get; init; } = LuaHeapOptions.Default;

    public int MainThreadInitialStackCapacity { get; init; } = 128;
}
