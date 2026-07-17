using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;

namespace Lunil.Runtime.Tests.Execution;

public sealed class LuaCodegenAbiV4Tests
{
    [Fact]
    public void RuntimeAbiV4KeepsEarlierContractsVersioned()
    {
        Assert.Equal(1, LuaCodegenAbiV1.RuntimeAbiVersion);
        Assert.Equal(2, LuaCodegenAbiV2.RuntimeAbiVersion);
        Assert.Equal(3, LuaCodegenAbiV3.RuntimeAbiVersion);
        Assert.Equal(4, LuaCodegenAbiV4.RuntimeAbiVersion);
    }

    [Theory]
    [InlineData(1L, 63L, true, long.MinValue)]
    [InlineData(-1L, 1L, false, long.MaxValue)]
    [InlineData(1L, -1L, true, 0L)]
    [InlineData(8L, -2L, false, 32L)]
    [InlineData(1L, 64L, true, 0L)]
    [InlineData(1L, long.MinValue, true, 0L)]
    public void ShiftImplementsLuaLogicalAndReverseCounts(
        long value,
        long count,
        bool left,
        long expected)
    {
        Assert.Equal(expected, LuaCodegenAbiV4.Shift(value, count, left));
    }

    [Theory]
    [InlineData(5d, 3d, 2d)]
    [InlineData(-5d, 3d, 1d)]
    [InlineData(5d, -3d, -1d)]
    [InlineData(-5d, -3d, -2d)]
    public void FloatingModuloUsesFloorDivisionSign(double dividend, double divisor, double expected)
    {
        Assert.Equal(expected, LuaCodegenAbiV4.FloatingModulo(dividend, divisor));
    }

    [Fact]
    public void MixedComparisonPreservesIntegerPrecisionAndNanRules()
    {
        var equal = (int)LuaIrBinaryOperator.Equal;
        var lessThan = (int)LuaIrBinaryOperator.LessThan;
        var notEqual = (int)LuaIrBinaryOperator.NotEqual;

        Assert.False(LuaCodegenAbiV4.CompareMixed(9_007_199_254_740_993L,
            9_007_199_254_740_992d, integerOnLeft: true, lessThan));
        Assert.True(LuaCodegenAbiV4.CompareMixed(long.MaxValue,
            9_223_372_036_854_775_808d, integerOnLeft: true, lessThan));
        Assert.False(LuaCodegenAbiV4.CompareMixed(0, double.NaN, true, equal));
        Assert.True(LuaCodegenAbiV4.CompareMixed(0, double.NaN, true, notEqual));
    }

    [Fact]
    public void GeneratedCodeAccessorsExposeOnlyRequiredControlState()
    {
        var function = new LuaIrFunction
        {
            Id = 0,
            Span = new TextSpan(0, 0),
            RegisterCount = 1,
            Constants = [],
            Instructions = [new LuaIrInstruction(LuaIrOpcode.Return, 0, 0)],
            BasicBlocks = [new LuaIrBasicBlock(0, 1, [])],
        };
        var module = new LuaIrModule
        {
            MainFunctionId = 0,
            Functions = [function],
        };
        var state = new LuaState();
        var thread = state.MainThread;
        var frame = new LuaFrame(
            state.CreateMainClosure(module),
            @base: 0,
            top: 0,
            returnBase: 0,
            expectedResults: 0,
            varArgs: []);
        var context = new LuaExecutionContext(state, thread, remainingInstructionCount: 3);

        LuaCodegenAbiV4.SetProgramCounter(frame, 0);
        Assert.True(context.TryReserveInstructions(2));

        Assert.Equal(0, frame.ProgramCounter);
        Assert.Equal(2, LuaCodegenAbiV4.GetInstructionsConsumed(context));
    }
}
