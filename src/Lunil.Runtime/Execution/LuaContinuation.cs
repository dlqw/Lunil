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

    /// <summary>
    /// Absolute exclusive caller top preserved by a resumable native runtime operation, or -1
    /// for an ordinary native call.
    /// </summary>
    public int NativeOperationTop { get; set; } = -1;

    public LuaValue Value { get; set; }

    public LuaValue[] Values { get; set; } = [];

    /// <summary>Byte-only native state; it never contains Lua values or CLR continuations.</summary>
    public LuaNativeByteBuffer? NativeByteBuffer { get; set; }

    public LuaProtectedCallKind ProtectionKind { get; set; }

    public LuaValue ProtectionFunction { get; set; }

    public LuaValue ErrorHandler { get; set; }

    public bool IsCloseHandler { get; set; }

    public bool IsYieldBarrier { get; set; }

    public bool NativeCallbackIsProtected { get; set; }

    public bool IsNativeProtectedBoundary { get; set; }

    public int NativeProtectedReturnBase { get; set; }

    public int NativeProtectedExpectedResults { get; set; }

    public bool NativeProtectedTailCall { get; set; }

    public bool IsEmpty => Kind == LuaContinuationKind.None;

    public void Reset()
    {
        Kind = LuaContinuationKind.None;
        State = 0;
        Base = 0;
        Count = 0;
        ExpectedResults = 0;
        Transform = LuaResultTransform.None;
        NativeOperationTop = -1;
        Value = LuaValue.Nil;
        Values = [];
        NativeByteBuffer = null;
        IsYieldBarrier = false;
        NativeCallbackIsProtected = false;
    }

    /// <summary>Clears both transient continuation state and frame-lifetime protection state.</summary>
    internal void ResetForFrameReuse()
    {
        Reset();
        ProtectionKind = LuaProtectedCallKind.None;
        ProtectionFunction = LuaValue.Nil;
        ErrorHandler = LuaValue.Nil;
        IsCloseHandler = false;
        IsNativeProtectedBoundary = false;
        NativeProtectedReturnBase = 0;
        NativeProtectedExpectedResults = 0;
        NativeProtectedTailCall = false;
    }
}
