using Lunil.Runtime.CodeGen;

namespace Lunil.CodeGen.Cil.Jit;

/// <summary>
/// Per-compiled-method mutable inline-cache state. It is held by the generated delegate and
/// uses weak Runtime cache entries, so compiled code does not keep Lua objects alive.
/// </summary>
internal sealed class LuaTier2RuntimeSites
{
    private readonly LuaCodegenTableSiteCache?[] _tableSites;
    private readonly LuaCodegenCallSiteCache?[] _callSites;

    public LuaTier2RuntimeSites(int instructionCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(instructionCount);
        _tableSites = new LuaCodegenTableSiteCache?[instructionCount];
        _callSites = new LuaCodegenCallSiteCache?[instructionCount];
    }

    internal LuaCodegenTableSiteCache GetTableSite(int programCounter)
    {
        var site = Volatile.Read(ref _tableSites[programCounter]);
        if (site is not null)
        {
            return site;
        }

        var created = new LuaCodegenTableSiteCache();
        return Interlocked.CompareExchange(
            ref _tableSites[programCounter],
            created,
            null) ?? created;
    }

    internal LuaCodegenCallSiteCache GetCallSite(int programCounter)
    {
        var site = Volatile.Read(ref _callSites[programCounter]);
        if (site is not null)
        {
            return site;
        }

        var created = new LuaCodegenCallSiteCache();
        return Interlocked.CompareExchange(
            ref _callSites[programCounter],
            created,
            null) ?? created;
    }
}
