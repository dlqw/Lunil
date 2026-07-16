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
}

/// <summary>
/// Supplies the initial execution route for a newly-created Lua frame. Implementations must not
/// retain the closure, its owner state, or the module through this call.
/// </summary>
internal interface ILuaFrameInstructionRouter
{
    LuaFrameInstructionRoute GetInitialFrameInstructionRoute(LuaFrame frame);

    void CommitPendingBackedges(LuaFrame frame);
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
