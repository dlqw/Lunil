using System.Collections.Immutable;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;

namespace Lunil.IR.Tests;

public sealed class LuaChunkCodecTests
{
    [Theory]
    [InlineData(LuaChunkFormat.Lua51)]
    [InlineData(LuaChunkFormat.Lua52)]
    [InlineData(LuaChunkFormat.Lua53)]
    [InlineData(LuaChunkFormat.Lua54)]
    [InlineData(LuaChunkFormat.Lua55)]
    public void WriteAndReadRoundTripsThroughTheSingleCodec(LuaChunkFormat format)
    {
        var module = Compile(format);
        var bytes = LuaChunkCodec.WriteCanonicalModule(
            format,
            module,
            functionId: 0,
            stripDebug: false);

        var roundTripped = LuaChunkCodec.ReadPrototypeModule(format, bytes);

        Assert.Equal(
            module.MainFunctionId,
            roundTripped.MainFunctionId);
        Assert.Equal(
            module.Functions[0].Instructions.Length,
            roundTripped.Functions[0].Instructions.Length);
    }

    [Fact]
    public void FormatFailuresAreCatchableThroughTheCommonBase()
    {
        var exception = Assert.ThrowsAny<LuaChunkFormatException>(() =>
            LuaChunkCodec.ReadPrototypeModule(LuaChunkFormat.Lua54, [0x1b, 0x4c, 0x75]));

        var versionException = Assert.IsType<Lua54ChunkFormatException>(exception);
        Assert.Equal(exception.Offset, versionException.ByteOffset);
    }

    [Fact]
    public void UnsupportedFormatsAreRejected()
    {
        Assert.Throws<NotSupportedException>(() =>
            LuaChunkCodec.ReadPrototypeModule((LuaChunkFormat)0xFF, []));
        Assert.Throws<NotSupportedException>(() =>
            LuaChunkCodec.WriteCanonicalModule((LuaChunkFormat)0xFF, Compile(LuaChunkFormat.Lua54), 0, false));
    }

    private static LuaIrModule Compile(LuaChunkFormat format)
    {
        var languageVersion = format switch
        {
            LuaChunkFormat.Lua51 => LuaLanguageVersion.Lua51,
            LuaChunkFormat.Lua52 => LuaLanguageVersion.Lua52,
            LuaChunkFormat.Lua53 => LuaLanguageVersion.Lua53,
            LuaChunkFormat.Lua54 => LuaLanguageVersion.Lua54,
            LuaChunkFormat.Lua55 => LuaLanguageVersion.Lua55,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.VarArg, 0, 1),
            new LuaIrInstruction(LuaIrOpcode.Return, 0, 1));
        var function = new LuaIrFunction
        {
            Id = 0,
            Span = default,
            ParameterCount = 0,
            IsVarArg = true,
            RegisterCount = 1,
            Instructions = instructions,
            BasicBlocks = LuaIrControlFlow.Build(instructions),
        };
        return new LuaIrModule
        {
            LanguageVersion = languageVersion,
            MainFunctionId = 0,
            Functions = [function],
        };
    }
}
