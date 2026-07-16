using Lunil.Runtime;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.CodeGen.Cil;

public sealed class LuaStaticAotExecutor
{
    private readonly LuaExecutionEngine _engine;

    public LuaStaticAotExecutor(LuaInterpreterOptions? interpreterOptions = null)
    {
        InterpreterOptions = interpreterOptions ?? LuaInterpreterOptions.Default;
        _engine = new LuaExecutionEngine(InterpreterOptions, StaticInstructionExecutor.Instance);
    }

    public LuaInterpreterOptions InterpreterOptions { get; }

    public LuaExecutionResult Execute(
        LuaState state,
        LuaClosure closure,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _engine.Execute(state, closure, arguments);

    public LuaExecutionResult Start(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _engine.Start(state, thread, arguments);

    public LuaExecutionResult Resume(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _engine.Resume(state, thread, arguments);

    public LuaExecutionResult Close(LuaState state, LuaThread thread) =>
        _engine.Close(state, thread);

    private sealed class StaticInstructionExecutor : ILuaInstructionExecutor
    {
        public static StaticInstructionExecutor Instance { get; } = new();

        public LuaCompiledExit Execute(
            LuaExecutionEngine engine,
            LuaExecutionContext context,
            LuaState state,
            LuaThread thread,
            LuaFrame frame,
            Lunil.IR.Canonical.LuaIrInstruction instruction)
        {
            if (LuaStaticAotRegistry.TryGetFunction(
                frame.Module,
                frame.Function.Id,
                out var function) && function is not null)
            {
                return function(context, thread, frame);
            }

            return LuaCompiledExit.Deopt(
                frame.ProgramCounter,
                0,
                LuaCompiledExitReason.UnsupportedInstruction);
        }
    }
}
