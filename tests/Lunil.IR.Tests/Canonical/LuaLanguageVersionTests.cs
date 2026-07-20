using Lunil.Core;
using Lunil.IR.Canonical;

namespace Lunil.IR.Tests.Canonical;

public sealed class LuaLanguageVersionTests
{
    [Fact]
    public void VerifierAcceptsKnownLanguageIdentity()
    {
        var module = CreateModule(LuaLanguageVersion.Lua53);

        Assert.Empty(LuaIrVerifier.Verify(module));
    }

    [Fact]
    public void VerifierRejectsUnknownLanguageIdentity()
    {
        var module = CreateModule((LuaLanguageVersion)0x56);

        Assert.Contains(
            LuaIrVerifier.Verify(module),
            error => error.Message.Contains("language version", StringComparison.Ordinal));
    }

    private static LuaIrModule CreateModule(LuaLanguageVersion version) => new()
    {
        LanguageVersion = version,
        Functions =
        [
            new LuaIrFunction
            {
                Id = 0,
                Span = default,
                RegisterCount = 1,
                Instructions = [new LuaIrInstruction(LuaIrOpcode.Return, 0, 0)],
                BasicBlocks = LuaIrControlFlow.Build(
                    [new LuaIrInstruction(LuaIrOpcode.Return, 0, 0)]),
            },
        ],
    };
}
