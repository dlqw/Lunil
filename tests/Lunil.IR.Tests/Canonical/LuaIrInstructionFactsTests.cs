using Lunil.IR.Canonical;

namespace Lunil.IR.Tests.Canonical;

public sealed class LuaIrInstructionFactsTests
{
    private const LuaIrInstructionEffects AllocationSafePoint =
        LuaIrInstructionEffects.MayAllocate |
        LuaIrInstructionEffects.MayThrow |
        LuaIrInstructionEffects.IsGcSafePoint;

    private const LuaIrInstructionEffects ResumableCallSafePoint =
        LuaIrInstructionEffects.MayAllocate |
        LuaIrInstructionEffects.MayCall |
        LuaIrInstructionEffects.MayYield |
        LuaIrInstructionEffects.MayThrow |
        LuaIrInstructionEffects.IsGcSafePoint;

    public static TheoryData<LuaIrOpcode, LuaIrInstructionEffects> OpcodeEffects => new()
    {
        { LuaIrOpcode.LoadConstant, AllocationSafePoint },
        { LuaIrOpcode.LoadNil, LuaIrInstructionEffects.None },
        { LuaIrOpcode.Move, LuaIrInstructionEffects.None },
        { LuaIrOpcode.SetTop, LuaIrInstructionEffects.None },
        { LuaIrOpcode.GetUpvalue, LuaIrInstructionEffects.None },
        { LuaIrOpcode.SetUpvalue, LuaIrInstructionEffects.None },
        { LuaIrOpcode.NewTable, AllocationSafePoint },
        { LuaIrOpcode.GetTable, ResumableCallSafePoint },
        { LuaIrOpcode.SetTable, ResumableCallSafePoint },
        { LuaIrOpcode.SetList, AllocationSafePoint },
        { LuaIrOpcode.Closure, AllocationSafePoint },
        { LuaIrOpcode.VarArg, LuaIrInstructionEffects.None },
        { LuaIrOpcode.Unary, ResumableCallSafePoint },
        { LuaIrOpcode.Binary, ResumableCallSafePoint },
        { LuaIrOpcode.Jump, ResumableCallSafePoint },
        { LuaIrOpcode.JumpIfFalse, LuaIrInstructionEffects.None },
        { LuaIrOpcode.JumpIfTrue, LuaIrInstructionEffects.None },
        { LuaIrOpcode.Call, ResumableCallSafePoint },
        { LuaIrOpcode.TailCall, ResumableCallSafePoint },
        { LuaIrOpcode.Return, ResumableCallSafePoint },
        { LuaIrOpcode.Close, ResumableCallSafePoint },
        { LuaIrOpcode.MarkToBeClosed, LuaIrInstructionEffects.MayThrow },
        { LuaIrOpcode.NumericForPrepare, LuaIrInstructionEffects.MayThrow },
        { LuaIrOpcode.NumericForLoop, LuaIrInstructionEffects.MayThrow },
    };

    [Theory]
    [MemberData(nameof(OpcodeEffects))]
    public void ReportsConservativeOpcodeEffects(
        LuaIrOpcode opcode,
        LuaIrInstructionEffects expected)
    {
        Assert.Equal(expected, LuaIrInstructionFacts.GetEffects(opcode));
    }

    [Fact]
    public void TreatsOnlyJumpsWithCloseOperandsAsResumableSafePoints()
    {
        var plainJump = new LuaIrInstruction(LuaIrOpcode.Jump, b: 1, c: -1);
        var closingJump = new LuaIrInstruction(LuaIrOpcode.Jump, b: 1, c: 0);

        Assert.Equal(LuaIrInstructionEffects.None, plainJump.Effects);
        Assert.Equal(ResumableCallSafePoint, closingJump.Effects);
    }

    [Fact]
    public void DefinesAnEffectContractForEveryCanonicalOpcode()
    {
        Assert.Equal(Enum.GetValues<LuaIrOpcode>().Length, OpcodeEffects.Count);
    }
}
