using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Tests.Execution;

public sealed class LuaCodegenAbiV1Tests
{
    [Fact]
    public void CreatesTaggedCompiledExitsWithCanonicalControlState()
    {
        var continued = LuaCompiledExit.Continue(4, 3);
        var call = LuaCompiledExit.Call(7, 1);
        var deopt = LuaCompiledExit.Deopt(9, 2, LuaCompiledExitReason.GuardFailure);

        Assert.Equal(LuaCompiledExitKind.Continue, continued.Kind);
        Assert.Equal(4, continued.ProgramCounter);
        Assert.Equal(3, continued.InstructionsConsumed);
        Assert.Equal(LuaCompiledExitKind.Call, call.Kind);
        Assert.Equal(LuaCompiledExitReason.None, call.Reason);
        Assert.Equal(LuaCompiledExitKind.Deopt, deopt.Kind);
        Assert.Equal(LuaCompiledExitReason.GuardFailure, deopt.Reason);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LuaCompiledExit.Poll(0, 0, LuaCompiledExitReason.None));
    }

    [Fact]
    public void CompiledExitKeepsARegisterSizedControlPayload()
    {
        Assert.Equal(16, Unsafe.SizeOf<LuaCompiledExit>());
    }

    [Fact]
    public void ExecutionContextReservesOnlyWholeCanonicalInstructionRanges()
    {
        var (state, thread, _) = CreateFrame();
        var context = new LuaExecutionContext(state, thread, remainingInstructionCount: 3);

        Assert.True(context.TryReserveInstructions(2));
        Assert.Equal(1, context.RemainingInstructionCount);
        Assert.False(context.TryReserveInstructions(2));
        Assert.Equal(1, context.RemainingInstructionCount);
        Assert.Equal(2, context.InstructionsConsumed);
    }

    [Fact]
    public void ExecutionContextAccountsBeyondTheInt32Range()
    {
        var (state, thread, _) = CreateFrame();
        var expected = (long)int.MaxValue + 10;
        var context = new LuaExecutionContext(state, thread, expected);

        Assert.True(context.TryReserveInstructions(int.MaxValue));
        Assert.True(context.TryReserveInstructions(10));

        Assert.Equal(0, context.RemainingInstructionCount);
        Assert.Equal(expected, context.InstructionsConsumed);
        Assert.Equal(expected, LuaCompiledExit.Continue(0, expected).InstructionsConsumed);
    }

    [Fact]
    public void ExecutionContextDetectsDebugModeChanges()
    {
        var (state, thread, _) = CreateFrame();
        var context = new LuaExecutionContext(state, thread, remainingInstructionCount: 3);
        var hook = LuaValue.FromFunction(new LuaNativeFunction(
            "hook",
            static (_, _) => []));

        LuaDebugApi.SetHook(state, thread, hook, LuaDebugHookMask.Line, count: 0);

        Assert.False(context.HasExactDebugHooks);
        Assert.False(context.IsDebugModeCurrent());
        var updated = new LuaExecutionContext(state, thread, remainingInstructionCount: 3);
        Assert.True(updated.HasExactDebugHooks);
        Assert.True(updated.IsDebugModeCurrent());
    }

    [Fact]
    public void CodegenAbiReadsWritesClearsAndBoundsCanonicalRegisters()
    {
        var (_, thread, frame) = CreateFrame();

        LuaCodegenAbiV1.WriteRegister(thread, frame, 1, LuaValue.FromInteger(42));

        Assert.Equal(LuaValue.FromInteger(42), LuaCodegenAbiV1.ReadRegister(thread, frame, 1));
        Assert.Equal(frame.Base + 2, frame.Top);
        LuaCodegenAbiV1.ClearRegisters(thread, frame, 1, 1);
        Assert.Equal(LuaValue.Nil, LuaCodegenAbiV1.ReadRegister(thread, frame, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LuaCodegenAbiV1.ReadRegister(thread, frame, 4));
    }

    [Fact]
    public void CodegenAbiMaterializesConstantsInTheOwningState()
    {
        var (state, thread, frame) = CreateFrame(
            [LuaIrConstant.FromString("value"u8)]);
        var context = new LuaExecutionContext(state, thread, remainingInstructionCount: 1);

        var value = LuaCodegenAbiV1.MaterializeConstant(context, frame, 0);

        Assert.Equal(LuaValueKind.String, value.Kind);
        Assert.Equal("value"u8.ToArray(), value.AsString().ToArray());
        Assert.Same(state.Heap, value.AsString().Owner);
    }

    private static (LuaState State, LuaThread Thread, LuaFrame Frame) CreateFrame(
        ImmutableArray<LuaIrConstant> constants = default)
    {
        var function = new LuaIrFunction
        {
            Id = 0,
            Span = new TextSpan(0, 0),
            RegisterCount = 4,
            Constants = constants.IsDefault ? [] : constants,
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
