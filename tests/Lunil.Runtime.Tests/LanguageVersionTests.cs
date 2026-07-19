using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.Runtime;

namespace Lunil.Runtime.Tests;

public sealed class LanguageVersionTests
{
    [Fact]
    public void StatePublishesConfiguredLanguageVersion()
    {
        var state = new LuaState(new LuaStateOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
        });

        Assert.Equal(LuaLanguageVersion.Lua53, state.LanguageVersion);
    }

    [Fact]
    public void StateRejectsAClosureFromAnotherLanguageContract()
    {
        var state = new LuaState(new LuaStateOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
        });
        var module = new LuaIrModule
        {
            LanguageVersion = LuaLanguageVersion.Lua54,
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

        var error = Assert.Throws<LuaRuntimeException>(() => state.CreateMainClosure(module));

        Assert.Contains("Lua 5.3", error.Message, StringComparison.Ordinal);
        Assert.Contains("Lua 5.4", error.Message, StringComparison.Ordinal);
    }
}
