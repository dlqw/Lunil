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
        ProtectionKind = protectionKind;
        ErrorHandler = errorHandler;
        IsCloseHandler = isCloseHandler;
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

    internal LuaResultTransform PendingResultTransform { get; set; }

    internal LuaProtectedCallKind ProtectionKind { get; }

    internal LuaValue ErrorHandler { get; }

    internal LuaValue[]? PendingReturnValues { get; set; }

    internal LuaPendingTailCall? PendingTailCall { get; set; }

    internal int PendingTailProtectedReturnRegister { get; set; } = -1;

    internal bool IsCloseHandler { get; }
}

internal sealed class LuaPendingTailCall
{
    public LuaPendingTailCall(LuaValue callable, LuaValue[] arguments)
    {
        Callable = callable;
        Arguments = arguments;
    }

    public LuaValue Callable { get; }

    public LuaValue[] Arguments { get; }
}

internal enum LuaProtectedCallKind : byte
{
    None,
    ProtectedCall,
    ProtectedCallWithHandler,
    ErrorHandler,
    Finalizer,
}
