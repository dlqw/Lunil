using Lunil.Runtime.Operations;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

/// <summary>Identifies the VM action that must continue after a resumable boundary.</summary>
internal enum LuaContinuationKind : byte
{
    None,
    LuaCall,
    TailCall,
    ProtectedCall,
    ErrorHandler,
    ReturnAndClose,
    CoroutineYield,
    CoroutineResume,
    NativeCallLua,
    NativeYield,
}

/// <summary>
/// Tagged, GC-visible continuation state. It intentionally contains only scalar state, stack
/// windows, and owner-validated Lua values; CLR closures are never used as VM continuations.
/// </summary>
internal sealed class LuaContinuation
{
    public LuaContinuationKind Kind { get; set; }

    public int State { get; set; }

    public int Base { get; set; }

    public int Count { get; set; }

    public int ExpectedResults { get; set; }

    public LuaResultTransform Transform { get; set; }

    public LuaValue Value { get; set; }

    public LuaValue[] Values { get; set; } = [];

    public LuaProtectedCallKind ProtectionKind { get; set; }

    public LuaValue ErrorHandler { get; set; }

    public bool IsCloseHandler { get; set; }

    public bool IsEmpty => Kind == LuaContinuationKind.None;

    public void Reset()
    {
        Kind = LuaContinuationKind.None;
        State = 0;
        Base = 0;
        Count = 0;
        ExpectedResults = 0;
        Transform = LuaResultTransform.None;
        Value = LuaValue.Nil;
        Values = [];
    }
}
