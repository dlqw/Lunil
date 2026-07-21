using System.Collections.Immutable;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua51;

namespace Lunil.IR.Tests.Lua51;

public sealed class Lua51ChunkTests
{
    [Fact]
    public void WriterRoundTripsLua51NumberOnlyConstantsAndOpcodeIdentity()
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, 0, 0),
            new LuaIrInstruction(LuaIrOpcode.Return, 0, 1));
        var module = new LuaIrModule
        {
            LanguageVersion = LuaLanguageVersion.Lua51,
            MainFunctionId = 0,
            Functions = [new LuaIrFunction
            {
                Id = 0,
                ParentFunctionId = -1,
                Span = default,
                RegisterCount = 1,
                Constants = [LuaIrConstant.FromFloat(2.5)],
                Instructions = instructions,
                BasicBlocks = LuaIrControlFlow.Build(instructions),
            }],
        };

        var bytes = Lua51CanonicalPrototypeWriter.Write(module, 0);
        var chunk = Lua51ChunkReader.Read(bytes);
        var converted = Lua51PrototypeConverter.Convert(chunk);

        Assert.Equal(0x51, bytes[4]);
        Assert.Equal(LuaLanguageVersion.Lua51, converted.LanguageVersion);
        Assert.Equal(Lua51Opcode.LoadConstant, chunk.MainPrototype.Code[0].Opcode);
        Assert.Equal(Lua51ConstantKind.Number, chunk.MainPrototype.Constants[0].Kind);
    }

    [Fact]
    public void ReaderRejectsANewerChunkWithoutFallback()
    {
        var bytes = Lua51CanonicalPrototypeWriter.Write(CreateEmptyModule(), 0);
        bytes[4] = 0x52;
        Assert.Throws<Lua51ChunkFormatException>(() => Lua51ChunkReader.Read(bytes));
    }

    [Fact]
    public void WriterUsesLua51VarArgFlagsInsteadOfLua53BitIdentity()
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.VarArg, 0, 1),
            new LuaIrInstruction(LuaIrOpcode.Return, 0, 1));
        var module = new LuaIrModule
        {
            LanguageVersion = LuaLanguageVersion.Lua51,
            MainFunctionId = 0,
            Functions = [new LuaIrFunction
            {
                Id = 0,
                ParentFunctionId = -1,
                Span = default,
                IsVarArg = true,
                RegisterCount = 1,
                Instructions = instructions,
                BasicBlocks = LuaIrControlFlow.Build(instructions),
            }],
        };

        var chunk = Lua51ChunkReader.Read(Lua51CanonicalPrototypeWriter.Write(module, 0));

        Assert.Equal(2, chunk.MainPrototype.VarArgFlags);
        Assert.Equal(Lua51Opcode.VarArg, chunk.MainPrototype.Code[0].Opcode);
    }

    [Fact]
    public void ConverterAddsAnEnvironmentUpvalueForLua51GlobalInstructions()
    {
        var prototype = new Lua51Prototype
        {
            UpvalueCount = 0,
            MaximumStackSize = 2,
            Code =
            [
                Lua51Instruction.CreateABx(Lua51Opcode.GetGlobal, 0, 0),
                Lua51Instruction.CreateAbc(Lua51Opcode.Return, 0, 2, 0),
            ],
            Constants = [Lua51Constant.FromString(new Lua51String("value"u8.ToArray()))],
            NestedPrototypes = [],
            LineInfo = [],
            LocalVariables = [],
            UpvalueNames = [],
        };

        var module = Lua51PrototypeConverter.Convert(
            new Lua51Chunk(Lua51ChunkTarget.Host, prototype));

        var function = Assert.Single(module.Functions);
        Assert.Equal(LuaIrUpvalueSourceKind.Environment, Assert.Single(function.Upvalues).SourceKind);
        Assert.Contains(function.Instructions, instruction =>
            instruction.Opcode == LuaIrOpcode.GetUpvalue && instruction.B == 0);
    }

    private static LuaIrModule CreateEmptyModule()
    {
        var instructions = ImmutableArray.Create(new LuaIrInstruction(LuaIrOpcode.Return, 0, 0));
        return new LuaIrModule
        {
            LanguageVersion = LuaLanguageVersion.Lua51,
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
    }
}
