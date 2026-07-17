using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

/// <summary>Backend entry consumed by the shared scheduler.</summary>
internal interface ILuaInstructionExecutor
{
    LuaCompiledExit Execute(
        LuaExecutionEngine engine,
        LuaExecutionContext context,
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction);

    LuaFrameInstructionRoute GetInitialFrameInstructionRoute(LuaFrame frame) =>
        LuaFrameInstructionRoute.Backend;

    void CommitPendingBackedges(LuaFrame frame)
    {
    }

    void ObserveInstruction(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int programCounter,
        LuaIrInstruction instruction)
    {
    }

    void ObserveLoopOsrBackedges(
        LuaFrame frame,
        int programCounter,
        int backedgeCount)
    {
    }
}

internal enum LuaFrameInstructionRoute : byte
{
    Backend,
    Interpreter,
    InterpreterWithBackedgeProbes,
}

internal static class LuaInstructionRouting
{
    public static bool IsBackedge(int programCounter, LuaIrInstruction instruction) =>
        instruction.B <= programCounter && instruction.Opcode is
            LuaIrOpcode.Jump or LuaIrOpcode.JumpIfFalse or LuaIrOpcode.JumpIfTrue or
            LuaIrOpcode.NumericForPrepare or LuaIrOpcode.NumericForLoop;
}
