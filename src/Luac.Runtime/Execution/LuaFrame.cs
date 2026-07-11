using Luac.Runtime.Values;
using Luac.Runtime.Operations;

namespace Luac.Runtime.Execution;

public sealed class LuaFrame
{
    internal LuaFrame(
        LuaClosure closure,
        int @base,
        int top,
        int returnBase,
        int expectedResults,
        LuaValue[] varArgs,
        LuaProtectedCallKind protectionKind = LuaProtectedCallKind.None,
        LuaValue errorHandler = default,
        bool isCloseHandler = false)
    {
        Closure = closure;
        Base = @base;
        Top = top;
        ReturnBase = returnBase;
        ExpectedResults = expectedResults;
        VarArgs = varArgs;
        Continuation.ProtectionKind = protectionKind;
        Continuation.ErrorHandler = errorHandler;
        Continuation.IsCloseHandler = isCloseHandler;
    }

    public LuaClosure Closure { get; }

    public int Base { get; }

    public int Top { get; internal set; }

    public int ProgramCounter { get; internal set; }

    public int ReturnBase { get; }

    public int ExpectedResults { get; }

    public IReadOnlyList<LuaValue> VarArgs { get; }

    internal LuaValue[] VarArgStorage => (LuaValue[])VarArgs;

    internal List<int> ToBeClosedSlots { get; } = [];

    internal LuaContinuation Continuation { get; } = new();

}

internal enum LuaProtectedCallKind : byte
{
    None,
    ProtectedCall,
    ProtectedCallWithHandler,
    ErrorHandler,
    Finalizer,
}
