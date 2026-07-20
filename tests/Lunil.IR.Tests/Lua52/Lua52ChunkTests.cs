using System.Collections.Immutable;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua52;

namespace Lunil.IR.Tests.Lua52;

public sealed class Lua52ChunkTests
{
    [Fact]
    public void WriterRoundTripsALua52ModuleThroughTheIndependentReader()
    {
        var bytes = Lua52CanonicalPrototypeWriter.Write(
            CreateModule(),
            0);
        var chunk = Lua52ChunkReader.Read(bytes);
        var converted = Lua52PrototypeConverter.Convert(chunk);

        Assert.Equal(LuaLanguageVersion.Lua52, converted.LanguageVersion);
        Assert.NotEmpty(chunk.MainPrototype.Code);
        Assert.NotEmpty(chunk.MainPrototype.Constants);
    }

    [Fact]
    public void ReaderRejectsLua53HeaderWithoutApplyingTheWrongAdapter()
    {
        var lua53 = Lunil.IR.Lua53.Lua53CanonicalPrototypeWriter.Write(
            CreateModule() with { LanguageVersion = LuaLanguageVersion.Lua53 },
            0);

        Assert.Throws<Lua52ChunkFormatException>(() => Lua52ChunkReader.Read(lua53));
    }

    private static LuaIrModule CreateModule()
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, 0, 0),
            new LuaIrInstruction(LuaIrOpcode.Return, 0, 1));
        return new LuaIrModule
        {
            LanguageVersion = LuaLanguageVersion.Lua52,
            MainFunctionId = 0,
            Functions = ImmutableArray.Create(new LuaIrFunction
            {
                Id = 0,
                ParentFunctionId = -1,
                Span = default,
                ParameterCount = 0,
                RegisterCount = 1,
                Constants = ImmutableArray.Create(LuaIrConstant.FromFloat(2.5)),
                Instructions = instructions,
                BasicBlocks = LuaIrControlFlow.Build(instructions),
            }),
        };
    }
}
