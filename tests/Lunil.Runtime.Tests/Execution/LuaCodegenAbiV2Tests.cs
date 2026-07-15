using System.Collections.Immutable;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Tests.Execution;

public sealed class LuaCodegenAbiV2Tests
{
    [Fact]
    public void RuntimeAbiV2RemainsDistinctFromV1()
    {
        Assert.Equal(1, LuaCodegenAbiV1.RuntimeAbiVersion);
        Assert.Equal(2, LuaCodegenAbiV2.RuntimeAbiVersion);
    }

    [Fact]
    public void PrimitiveBinaryGuardAndExecutionShareNumericSemantics()
    {
        var (state, thread, frame) = CreateFrame();
        var context = new LuaExecutionContext(state, thread, remainingInstructionCount: 1);
        LuaCodegenAbiV2.WriteRegisterUnchecked(thread, frame, 0, LuaValue.FromInteger(40));
        LuaCodegenAbiV2.WriteRegisterUnchecked(thread, frame, 1, LuaValue.FromFloat(2));

        Assert.True(LuaCodegenAbiV2.CanExecuteBinaryPrimitive(
            thread,
            frame,
            (int)LuaIrBinaryOperator.Add,
            0,
            1));
        LuaCodegenAbiV2.ExecuteBinaryPrimitive(
            context,
            thread,
            frame,
            destinationRegister: 2,
            operation: (int)LuaIrBinaryOperator.Add,
            leftRegister: 0,
            rightRegister: 1);

        Assert.Equal(42d, LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, 2).AsFloat());
    }

    [Fact]
    public void PrimitiveBinaryExecutionUsesExactIntegerAndNumericStringSemantics()
    {
        var (state, thread, frame) = CreateFrame();
        var context = new LuaExecutionContext(state, thread, remainingInstructionCount: 2);
        LuaCodegenAbiV2.WriteRegisterUnchecked(thread, frame, 0, LuaValue.FromInteger(-5));
        LuaCodegenAbiV2.WriteRegisterUnchecked(thread, frame, 1, LuaValue.FromInteger(3));

        Assert.True(LuaCodegenAbiV2.CanExecuteBinaryPrimitive(
            thread,
            frame,
            (int)LuaIrBinaryOperator.Modulo,
            0,
            1));
        LuaCodegenAbiV2.ExecuteBinaryPrimitive(
            context,
            thread,
            frame,
            destinationRegister: 2,
            operation: (int)LuaIrBinaryOperator.Modulo,
            leftRegister: 0,
            rightRegister: 1);
        Assert.Equal(
            LuaValue.FromInteger(1),
            LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, 2));

        LuaCodegenAbiV2.WriteRegisterUnchecked(
            thread,
            frame,
            0,
            LuaValue.FromString(state.Strings.GetOrCreate("40"u8)));
        LuaCodegenAbiV2.WriteRegisterUnchecked(thread, frame, 1, LuaValue.FromInteger(2));
        Assert.True(LuaCodegenAbiV2.CanExecuteBinaryPrimitive(
            thread,
            frame,
            (int)LuaIrBinaryOperator.Add,
            0,
            1));
        LuaCodegenAbiV2.ExecuteBinaryPrimitive(
            context,
            thread,
            frame,
            destinationRegister: 2,
            operation: (int)LuaIrBinaryOperator.Add,
            leftRegister: 0,
            rightRegister: 1);
        Assert.Equal(
            LuaValue.FromInteger(42),
            LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, 2));
    }

    [Fact]
    public void PrimitiveGuardRejectsPotentialMetamethodPaths()
    {
        var (state, thread, frame) = CreateFrame();
        LuaCodegenAbiV2.WriteRegisterUnchecked(
            thread,
            frame,
            0,
            LuaValue.FromTable(state.CreateTable()));
        LuaCodegenAbiV2.WriteRegisterUnchecked(
            thread,
            frame,
            1,
            LuaValue.FromTable(state.CreateTable()));

        Assert.False(LuaCodegenAbiV2.CanExecuteBinaryPrimitive(
            thread,
            frame,
            (int)LuaIrBinaryOperator.Add,
            0,
            1));
    }

    [Fact]
    public void CloseCanOnlyBeSkippedWhenNoEligibleSlotExists()
    {
        var (_, thread, frame) = CreateFrame();

        Assert.True(LuaCodegenAbiV2.CanSkipClose(thread, frame, 1));
        frame.ToBeClosedSlots.Add(frame.Base + 2);
        Assert.False(LuaCodegenAbiV2.CanSkipClose(thread, frame, 1));
        Assert.True(LuaCodegenAbiV2.CanSkipClose(thread, frame, 3));
        thread.GetOrCreateOpenUpvalue(frame.Base + 3);
        Assert.False(LuaCodegenAbiV2.CanSkipClose(thread, frame, 3));
    }

    private static (LuaState State, LuaThread Thread, LuaFrame Frame) CreateFrame()
    {
        var function = new LuaIrFunction
        {
            Id = 0,
            Span = new TextSpan(0, 0),
            RegisterCount = 4,
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
        return (state, thread, frame);
    }
}
