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
    public void WriterRestoresLua51GlobalOpcodesAndRemovesTheEnvironmentUpvalue()
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.GetUpvalue, 0, 0),
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, 1, 0),
            new LuaIrInstruction(LuaIrOpcode.GetTable, 2, 0, 1),
            new LuaIrInstruction(LuaIrOpcode.GetUpvalue, 0, 0),
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, 1, 1),
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, 2, 2),
            new LuaIrInstruction(LuaIrOpcode.SetTable, 0, 1, 2),
            new LuaIrInstruction(LuaIrOpcode.Return, 2, 1));
        var module = new LuaIrModule
        {
            LanguageVersion = LuaLanguageVersion.Lua51,
            MainFunctionId = 0,
            Functions = [new LuaIrFunction
            {
                Id = 0,
                ParentFunctionId = -1,
                Span = default,
                RegisterCount = 3,
                Constants =
                [
                    LuaIrConstant.FromString("read"u8),
                    LuaIrConstant.FromString("write"u8),
                    LuaIrConstant.FromFloat(7),
                ],
                Upvalues =
                [
                    new LuaIrUpvalue(
                        "_ENV",
                        0,
                        LuaIrUpvalueSourceKind.Environment,
                        0),
                ],
                Instructions = instructions,
                BasicBlocks = LuaIrControlFlow.Build(instructions),
            }],
        };

        var prototype = Lua51CanonicalPrototypeWriter.CreateChunk(module, 0).MainPrototype;

        Assert.Equal(0, prototype.UpvalueCount);
        Assert.Empty(prototype.UpvalueNames);
        Assert.DoesNotContain(prototype.Code, static instruction =>
            instruction.Opcode == Lua51Opcode.GetUpvalue);
        Assert.Contains(prototype.Code, static instruction =>
            instruction.Opcode == Lua51Opcode.GetGlobal && instruction.Bx == 0);
        Assert.Contains(prototype.Code, static instruction =>
            instruction.Opcode == Lua51Opcode.SetGlobal && instruction.Bx == 1);
    }

    [Fact]
    public void WriterRemovesNestedEnvironmentBindingsAndRemapsOrdinaryUpvalues()
    {
        var rootInstructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.Closure, 0, 1),
            new LuaIrInstruction(LuaIrOpcode.Return, 0, 1));
        var childInstructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.GetUpvalue, 0, 0),
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, 1, 0),
            new LuaIrInstruction(LuaIrOpcode.GetTable, 0, 0, 1),
            new LuaIrInstruction(LuaIrOpcode.GetUpvalue, 1, 1),
            new LuaIrInstruction(LuaIrOpcode.Return, 0, 2));
        var module = new LuaIrModule
        {
            LanguageVersion = LuaLanguageVersion.Lua51,
            MainFunctionId = 0,
            Functions =
            [
                new LuaIrFunction
                {
                    Id = 0,
                    ParentFunctionId = -1,
                    Span = default,
                    RegisterCount = 1,
                    Upvalues =
                    [
                        new LuaIrUpvalue(
                            "_ENV",
                            0,
                            LuaIrUpvalueSourceKind.Environment,
                            0),
                        new LuaIrUpvalue(
                            "ordinary",
                            1,
                            LuaIrUpvalueSourceKind.Upvalue,
                            0),
                    ],
                    Instructions = rootInstructions,
                    BasicBlocks = LuaIrControlFlow.Build(rootInstructions),
                },
                new LuaIrFunction
                {
                    Id = 1,
                    ParentFunctionId = 0,
                    Span = default,
                    RegisterCount = 2,
                    Constants = [LuaIrConstant.FromString("value"u8)],
                    Upvalues =
                    [
                        new LuaIrUpvalue(
                            "_ENV",
                            0,
                            LuaIrUpvalueSourceKind.Upvalue,
                            0),
                        new LuaIrUpvalue(
                            "ordinary",
                            1,
                            LuaIrUpvalueSourceKind.Upvalue,
                            1),
                    ],
                    Instructions = childInstructions,
                    BasicBlocks = LuaIrControlFlow.Build(childInstructions),
                },
            ],
        };

        var root = Lua51CanonicalPrototypeWriter.CreateChunk(module, 0).MainPrototype;
        var child = Assert.Single(root.NestedPrototypes);

        Assert.Equal(1, root.UpvalueCount);
        Assert.Equal(1, child.UpvalueCount);
        var closureIndex = Array.FindIndex(root.Code.ToArray(), static instruction =>
            instruction.Opcode == Lua51Opcode.Closure);
        Assert.True(closureIndex >= 0);
        Assert.Equal(Lua51Opcode.GetUpvalue, root.Code[closureIndex + 1].Opcode);
        Assert.Equal(0, root.Code[closureIndex + 1].B);
        Assert.Equal(1, child.Code.Count(static instruction =>
            instruction.Opcode == Lua51Opcode.GetUpvalue));
        Assert.Contains(child.Code, static instruction =>
            instruction.Opcode == Lua51Opcode.GetUpvalue && instruction.B == 0);
        Assert.Contains(child.Code, static instruction =>
            instruction.Opcode == Lua51Opcode.GetGlobal);
    }

    [Fact]
    public void WriterUsesLua51AbsoluteEndRegisterForLoadNil()
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.LoadNil, 2, 2),
            new LuaIrInstruction(LuaIrOpcode.Return, 0, 0));
        var module = new LuaIrModule
        {
            LanguageVersion = LuaLanguageVersion.Lua51,
            MainFunctionId = 0,
            Functions = [new LuaIrFunction
            {
                Id = 0,
                ParentFunctionId = -1,
                Span = default,
                RegisterCount = 4,
                Instructions = instructions,
                BasicBlocks = LuaIrControlFlow.Build(instructions),
            }],
        };

        var loadNil = Lua51CanonicalPrototypeWriter.CreateChunk(module, 0)
            .MainPrototype.Code[0];

        Assert.Equal(Lua51Opcode.LoadNil, loadNil.Opcode);
        Assert.Equal(2, loadNil.A);
        Assert.Equal(3, loadNil.B);
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
