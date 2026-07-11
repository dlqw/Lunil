using System.Collections.Immutable;
using Luac.IR.Canonical;

namespace Luac.IR.Tests.Canonical;

public sealed class LuaIrVerifierTests
{
    [Fact]
    public void AcceptsAWellFormedFunctionAndReconstructsItsBlocks()
    {
        var code = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, 0, 0),
            new LuaIrInstruction(LuaIrOpcode.JumpIfFalse, 0, 3),
            new LuaIrInstruction(LuaIrOpcode.Jump, b: 0, c: -1),
            new LuaIrInstruction(LuaIrOpcode.Return, 0, 1));
        var module = Module(code, registerCount: 1, [LuaIrConstant.FromBoolean(true)]);

        Assert.Empty(LuaIrVerifier.Verify(module));
        Assert.Equal([0, 2, 3], module.Functions[0].BasicBlocks.Select(static block => block.Start));
    }

    [Fact]
    public void RejectsOutOfRangeRegistersTargetsAndCallWindows()
    {
        var code = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.Call, 0, 2, 2),
            new LuaIrInstruction(LuaIrOpcode.Jump, b: 99, c: -1),
            new LuaIrInstruction(LuaIrOpcode.Return, 0, 0));
        var module = Module(code, registerCount: 2, []);

        var errors = LuaIrVerifier.Verify(module);

        Assert.Contains(errors, error => error.ProgramCounter == 0);
        Assert.Contains(errors, error => error.ProgramCounter == 1);
    }

    [Fact]
    public void RejectsForgedBasicBlocksAndInvalidCaptureSources()
    {
        var parentCode = ImmutableArray.Create(new LuaIrInstruction(LuaIrOpcode.Return, 0, 0));
        var childCode = ImmutableArray.Create(new LuaIrInstruction(LuaIrOpcode.Return, 0, 0));
        var parent = new LuaIrFunction
        {
            Id = 0,
            Span = default,
            RegisterCount = 1,
            Instructions = parentCode,
            BasicBlocks = LuaIrControlFlow.Build(parentCode),
        };
        var child = new LuaIrFunction
        {
            Id = 1,
            Span = default,
            ParentFunctionId = 0,
            RegisterCount = 1,
            Upvalues = [new LuaIrUpvalue("x", 1, LuaIrUpvalueSourceKind.Register, 9)],
            Instructions = childCode,
            BasicBlocks = [new LuaIrBasicBlock(0, 1, [0])],
        };
        var module = new LuaIrModule { Functions = [parent, child] };

        var errors = LuaIrVerifier.Verify(module);

        Assert.Contains(errors, error => error.Message.Contains("invalid source", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Message.Contains("Basic blocks", StringComparison.Ordinal));
    }

    private static LuaIrModule Module(
        ImmutableArray<LuaIrInstruction> code,
        int registerCount,
        ImmutableArray<LuaIrConstant> constants)
    {
        var function = new LuaIrFunction
        {
            Id = 0,
            Span = default,
            RegisterCount = registerCount,
            Constants = constants,
            Upvalues = [new LuaIrUpvalue("_ENV", 0, LuaIrUpvalueSourceKind.Environment, 0)],
            Instructions = code,
            BasicBlocks = LuaIrControlFlow.Build(code),
        };
        return new LuaIrModule { Functions = [function] };
    }
}
