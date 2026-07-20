using System.Collections.Immutable;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua55;

namespace Lunil.IR.Tests.Lua55;

public sealed class Lua55AdapterTests
{
    [Fact]
    public void VersionedAdapterKeepsLua55IdentityAcrossItsBoundary()
    {
        var instructions = ImmutableArray.Create(new LuaIrInstruction(LuaIrOpcode.Return, 0, 0));
        var module = new LuaIrModule
        {
            LanguageVersion = LuaLanguageVersion.Lua55,
            MainFunctionId = 0,
            Functions = [new LuaIrFunction
            {
                Id = 0,
                ParentFunctionId = -1,
                Span = default,
                RegisterCount = 1,
                Instructions = instructions,
                BasicBlocks = LuaIrControlFlow.Build(instructions),
            }],
        };

        var bytes = Lua55CanonicalPrototypeWriter.Write(module, 0);
        var converted = Lua55PrototypeConverter.Convert(bytes);

        Assert.Equal(0x55, bytes[4]);
        Assert.Equal(LuaLanguageVersion.Lua55, converted.LanguageVersion);
    }

    [Fact]
    public void AdapterRejectsLua54VersionMarker()
    {
        var bytes = new byte[] { 0x1b, (byte)'L', (byte)'u', (byte)'a', 0x54 };
        Assert.Throws<Lua55ChunkFormatException>(() => Lua55PrototypeConverter.Convert(bytes));
    }
}
