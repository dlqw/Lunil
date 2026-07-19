using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.Runtime.Tests.Execution;

public sealed class LuaExecutionKernelTests
{
    [Fact]
    public void DeoptimizesToTheReferenceExecutorAtTheCanonicalProgramCounter()
    {
        var state = new LuaState();
        var engine = new LuaExecutionEngine(
            instructionExecutor: new AlwaysDeoptInstructionExecutor());

        var result = engine.Execute(
            state,
            state.CreateMainClosure(Compile("return 20 + 22")));

        Assert.True(result.Values.SequenceEqual([LuaValue.FromInteger(42)]));
    }

#if DEBUG
    [Fact]
    public void RejectsBackendInstructionAccountingThatBypassesTheContext()
    {
        var state = new LuaState();
        var engine = new LuaExecutionEngine(
            instructionExecutor: new InvalidAccountingInstructionExecutor());

        Assert.Throws<InvalidOperationException>(() =>
            engine.Execute(state, state.CreateMainClosure(Compile("return 1"))));
    }
#endif

    [Fact]
    public void ChargesReservedInstructionsWhenExecutionThrowsARecoverableLuaError()
    {
        var state = new LuaState();
        state.InstallProtectedCallFunctions();
        var executor = new ThrowOnceInsideProtectedCallExecutor();
        var engine = new LuaExecutionEngine(instructionExecutor: executor);

        var result = engine.Execute(
            state,
            state.CreateMainClosure(Compile("local ok = pcall(function() return 1 end); return ok")));

        Assert.True(result.Values.SequenceEqual([LuaValue.FromBoolean(false)]));
        Assert.NotNull(executor.RemainingBeforeThrow);
        Assert.Equal(executor.RemainingBeforeThrow - 1, executor.RemainingAfterThrow);
    }

    private static LuaIrModule Compile(string source)
    {
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        return lowering.Succeeded && lowering.Module is not null
            ? lowering.Module
            : throw new InvalidOperationException("Kernel test source did not compile.");
    }

    private sealed class AlwaysDeoptInstructionExecutor : ILuaInstructionExecutor
    {
        public LuaCompiledExit Execute(
            LuaExecutionEngine engine,
            LuaExecutionContext context,
            LuaState state,
            LuaThread thread,
            LuaFrame frame,
            in LuaIrInstruction instruction) =>
            LuaCompiledExit.Deopt(
                frame.ProgramCounter,
                instructionsConsumed: 0,
                LuaCompiledExitReason.UnsupportedInstruction);
    }

#if DEBUG
    private sealed class InvalidAccountingInstructionExecutor : ILuaInstructionExecutor
    {
        public LuaCompiledExit Execute(
            LuaExecutionEngine engine,
            LuaExecutionContext context,
            LuaState state,
            LuaThread thread,
            LuaFrame frame,
            in LuaIrInstruction instruction)
        {
            _ = context.TryReserveInstructions(1);
            return LuaCompiledExit.Continue(frame.ProgramCounter, instructionsConsumed: 0);
        }
    }
#endif

    private sealed class ThrowOnceInsideProtectedCallExecutor : ILuaInstructionExecutor
    {
        private readonly LuaInterpreterInstructionExecutor _reference = new();

        public long? RemainingBeforeThrow { get; private set; }

        public long? RemainingAfterThrow { get; private set; }

        public LuaCompiledExit Execute(
            LuaExecutionEngine engine,
            LuaExecutionContext context,
            LuaState state,
            LuaThread thread,
            LuaFrame frame,
            in LuaIrInstruction instruction)
        {
            if (RemainingBeforeThrow is null && thread.Frames.Count > 1)
            {
                RemainingBeforeThrow = context.RemainingInstructionCount;
                Assert.True(context.TryReserveInstructions(1));
                throw new LuaRuntimeException("injected protected failure");
            }

            if (RemainingBeforeThrow is not null && RemainingAfterThrow is null)
            {
                RemainingAfterThrow = context.RemainingInstructionCount;
            }

            return _reference.Execute(engine, context, state, thread, frame, instruction);
        }
    }
}
