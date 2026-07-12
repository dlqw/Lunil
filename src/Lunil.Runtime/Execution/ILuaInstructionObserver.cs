using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;

namespace Lunil.Runtime.Execution;

/// <summary>Optional instrumentation boundary used by tiered execution backends.</summary>
internal interface ILuaInstructionObserver
{
    void ObserveInstruction(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int programCounter,
        LuaIrInstruction instruction);
}

/// <summary>Low-overhead accounting boundary for loop OSR backedges.</summary>
internal interface ILuaLoopOsrObserver
{
    void ObserveLoopOsrBackedge(LuaFrame frame, int programCounter);
}
