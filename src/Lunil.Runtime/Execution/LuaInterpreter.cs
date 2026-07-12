using Lunil.IR.Lua54;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

/// <summary>
/// Executes canonical IR with the reference interpreter backend and the shared resumable kernel.
/// </summary>
public sealed class LuaInterpreter
{
    private readonly LuaExecutionEngine _engine;

    public LuaInterpreter(LuaInterpreterOptions? options = null)
    {
        _engine = new LuaExecutionEngine(options);
    }

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

    public LuaExecutionResult Resume(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _engine.Resume(state, thread, arguments);

    public LuaExecutionResult Close(LuaState state, LuaThread thread) =>
        _engine.Close(state, thread);
}
