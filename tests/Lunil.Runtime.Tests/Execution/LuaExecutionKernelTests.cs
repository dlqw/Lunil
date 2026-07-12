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

    [Fact]
    public void RejectsBackendInstructionAccountingThatBypassesTheContext()
    {
        var state = new LuaState();
        var engine = new LuaExecutionEngine(
            instructionExecutor: new InvalidAccountingInstructionExecutor());

        Assert.Throws<InvalidOperationException>(() =>
            engine.Execute(state, state.CreateMainClosure(Compile("return 1"))));
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
            LuaIrInstruction instruction) =>
            LuaCompiledExit.Deopt(
                frame.ProgramCounter,
                instructionsConsumed: 0,
                LuaCompiledExitReason.UnsupportedInstruction);
    }

    private sealed class InvalidAccountingInstructionExecutor : ILuaInstructionExecutor
    {
        public LuaCompiledExit Execute(
            LuaExecutionEngine engine,
            LuaExecutionContext context,
            LuaState state,
            LuaThread thread,
            LuaFrame frame,
            LuaIrInstruction instruction)
        {
            _ = context.TryReserveInstructions(1);
            return LuaCompiledExit.Continue(frame.ProgramCounter, instructionsConsumed: 0);
        }
    }
}
