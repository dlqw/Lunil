using Lunil.Compiler;
using Lunil.Runtime.Execution;

namespace Lunil.Hosting;

/// <summary>Structured compile-and-execute result; execution is absent after compile failure.</summary>
public sealed record LuaHostRunResult(
    LuaCompilationResult Compilation,
    LuaExecutionResult? Execution)
{
    public bool CompilationSucceeded => Compilation.Succeeded;

    public bool ExecutionStarted => Execution is not null;

    public bool Succeeded => CompilationSucceeded &&
        Execution?.Signal == LuaVmSignal.Completed;
}
