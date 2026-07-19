using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;

namespace Lunil.IR.Tests.Lua54;

public sealed class Lua54LanguageVersionTests
{
    [Fact]
    public void Lua54WriterRejectsADifferentLanguageContract()
    {
        var instruction = new LuaIrInstruction(LuaIrOpcode.Return, 0, 0);
        var module = new LuaIrModule
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
            Functions =
            [
                new LuaIrFunction
                {
                    Id = 0,
                    Span = default,
                    RegisterCount = 1,
                    Instructions = [instruction],
                    BasicBlocks = LuaIrControlFlow.Build([instruction]),
                },
            ],
        };

        Assert.Throws<InvalidDataException>(() =>
            Lua54CanonicalPrototypeWriter.Write(module, 0));
    }
}
