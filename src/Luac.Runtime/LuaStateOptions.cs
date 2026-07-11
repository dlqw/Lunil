using Luac.Runtime.Memory;

namespace Luac.Runtime;

public sealed record LuaStateOptions
{
    public static LuaStateOptions Default { get; } = new();

    public LuaHeapOptions Heap { get; init; } = LuaHeapOptions.Default;

    public int MainThreadInitialStackCapacity { get; init; } = 128;
}
