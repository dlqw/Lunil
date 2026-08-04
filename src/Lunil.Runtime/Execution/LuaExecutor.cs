using Lunil.IR.Lua54;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

/// <summary>Selects an execution backend while preserving the shared Lua execution semantics.</summary>
public sealed class LuaExecutor
{
    private readonly LuaExecutionEngine _engine;

    public LuaExecutor(LuaExecutorOptions? options = null)
    {
        Options = options ?? LuaExecutorOptions.Default;
        _engine = new LuaExecutionEngine(Options.Interpreter);
    }

    public LuaExecutorOptions Options { get; }

    public LuaExecutionResult Execute(
        LuaState state,
        LuaClosure closure,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _engine.Execute(state, closure, arguments);

    public LuaExecutionResult ExecuteBinaryChunk(
        LuaState state,
        ReadOnlySpan<byte> binaryChunk,
        ReadOnlySpan<LuaValue> arguments = default,
        Lua54ChunkReaderOptions? readerOptions = null) =>
        _engine.ExecuteBinaryChunk(state, binaryChunk, arguments, readerOptions);

    public LuaExecutionResult Start(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _engine.Start(state, thread, arguments);

    /// <summary>Starts a coroutine with an invocation-specific instruction limit.</summary>
    public LuaExecutionResult Start(
        LuaState state,
        LuaThread thread,
        long maximumInstructionCount,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _engine.Start(state, thread, maximumInstructionCount, arguments);

    public LuaExecutionResult Resume(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _engine.Resume(state, thread, arguments);

    /// <summary>Resumes a coroutine with an invocation-specific instruction limit.</summary>
    public LuaExecutionResult Resume(
        LuaState state,
        LuaThread thread,
        long maximumInstructionCount,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _engine.Resume(state, thread, maximumInstructionCount, arguments);

    /// <summary>
    /// Resumes a debugger-paused turn: reactivates the root thread together with the suspended
    /// coroutine chain so pending <c>coroutine.resume</c> calls complete with their results.
    /// </summary>
    public LuaExecutionResult ResumeDebugged(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _engine.ResumeDebuggedTurn(state, thread, arguments);

    public LuaExecutionResult Close(LuaState state, LuaThread thread) =>
        _engine.Close(state, thread);
}

public sealed record LuaExecutorOptions
{
    public static LuaExecutorOptions Default { get; } = new();

    /// <summary>
    /// Reference-interpreter limits shared by fallback execution and exact debug/hook mode.
    /// </summary>
    public LuaInterpreterOptions Interpreter { get; init; } = LuaInterpreterOptions.Default;
}
