using Lunil.CodeGen.Cil.Analysis;
using Lunil.IR.Canonical;

namespace Lunil.CodeGen.Cil.Tests;

public sealed class LuaIrVerificationCacheTests
{
    [Fact]
    public void VerificationResultsAreReusedWithoutHidingInvalidModules()
    {
        var cache = new LuaIrVerificationCache();
        var module = new LuaIrModule();

        var first = cache.Verify(module);
        var second = cache.Verify(module);

        Assert.NotEmpty(first);
        Assert.True(first == second);
    }
}
