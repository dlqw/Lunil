using Lunil.Runtime.Values;
using Lunil.Runtime.Operations;

namespace Lunil.Runtime.Execution;

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
        bool isCloseHandler = false,
        bool isDebugHook = false,
        bool isHidden = false)
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
        IsDebugHook = isDebugHook;
        IsHidden = isHidden;
    }

    public LuaClosure Closure { get; }

    public int Base { get; }

    public int Top { get; internal set; }

    public int ProgramCounter { get; internal set; }

    public int ReturnBase { get; }

    public int ExpectedResults { get; }

    public IReadOnlyList<LuaValue> VarArgs { get; }

    public bool IsDebugHook { get; internal set; }

    public bool IsHidden { get; internal set; }

    internal string? PendingDebugHookEvent { get; set; }

    public bool IsTailCall { get; internal set; }

    internal int LastDebugHookLine { get; set; } = -1;

    internal int DebugHookCheckedProgramCounter { get; set; } = -1;

    internal int ReturnHookProgramCounter { get; set; } = -1;

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
