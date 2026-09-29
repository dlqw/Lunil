namespace Lunil.Runtime.Memory;

/// <summary>
/// Allocates the process-unique identities that cross-state safety checks compare when a
/// value moves between Lua states. The process default is the single process-wide
/// composition scope; isolated hosts and tests can inject a dedicated source instead of
/// sharing hidden global state.
/// </summary>
internal sealed class LuaHeapIdentitySource
{
    internal static readonly LuaHeapIdentitySource Process = new();

    private long _nextIdentity;

    internal long Allocate() => Interlocked.Increment(ref _nextIdentity);
}
