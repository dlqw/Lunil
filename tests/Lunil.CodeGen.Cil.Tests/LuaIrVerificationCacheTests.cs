using Lunil.CodeGen.Cil.Analysis;
using Lunil.IR.Canonical;

namespace Lunil.CodeGen.Cil.Tests;

public sealed class LuaIrVerificationCacheTests
{
    [Fact]
    public void VerificationResultsAreReusedWithoutHidingInvalidModules()
    {
        var module = new LuaIrModule();

        var first = LuaIrVerificationCache.Verify(module);
        var second = LuaIrVerificationCache.Verify(module);

        Assert.NotEmpty(first);
        Assert.True(first == second);
    }
}
