using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;

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
