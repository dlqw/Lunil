using Lunil.Runtime.Values;
using Lunil.Runtime.Operations;
using Lunil.IR.Canonical;

namespace Lunil.Runtime.Execution;

/// <summary>Borrowed live view of one activation owned by a <see cref="LuaThread"/>.</summary>
/// <remarks>
/// Frames are pooled. A frame reference is valid only while that frame remains in its owning
/// thread's <see cref="LuaThread.Frames"/> collection; after removal, its properties and value
/// windows can be reset and reused for a different activation.
/// </remarks>
public sealed class LuaFrame
{
    private const int MaximumRetainedVarArguments = 64;
    private readonly LuaValueWindow _varArgs = new();

    internal LuaFrame()
    {
    }

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
        Initialize(
            closure,
            @base,
            top,
            returnBase,
            expectedResults,
            varArgs,
            protectionKind,
            errorHandler,
            isCloseHandler,
            isDebugHook,
            isHidden);
    }

    public LuaClosure Closure { get; private set; } = null!;

    /// <summary>
    /// Gets the immutable function version captured when this activation entered. A suspended
    /// frame retains this version even if its closure publishes a successor.
    /// </summary>
    public LuaFunctionVersion FunctionVersion { get; private set; } = null!;

    internal LuaIrModule Module => FunctionVersion.Module;

    internal LuaIrFunction Function => FunctionVersion.Function;

    public int Base { get; private set; }

    public int Top { get; internal set; }

    public int ProgramCounter { get; internal set; }

    public int ReturnBase { get; private set; }

    public int ExpectedResults { get; private set; }

    /// <summary>
    /// Gets the borrowed live vararg window for this activation. This is not a snapshot and can be
    /// reset or reused after the frame leaves its owning thread.
    /// </summary>
    public IReadOnlyList<LuaValue> VarArgs => _varArgs;

    public bool IsDebugHook { get; internal set; }

    public bool IsHidden { get; internal set; }

    internal string? PendingDebugHookEvent { get; set; }

    public bool IsTailCall { get; internal set; }

    public string? DebugFunctionName { get; internal set; }

    public string? DebugFunctionNameWhat { get; internal set; }

    internal LuaValue DebugFunctionOverride { get; set; }

    internal int LastDebugHookLine { get; set; } = -1;

    internal int LastDebugHookProgramCounter { get; set; } = -1;

    internal int LastLineHookProgramCounter { get; set; } = -1;

    internal bool HasSourceLineInformation { get; private set; }

    internal int DebugHookCheckedProgramCounter { get; set; } = -1;

    internal string? DispatchedDebugHookEvent { get; set; }

    internal int ReturnHookProgramCounter { get; set; } = -1;

    internal int NativeCallHookProgramCounter { get; set; } = -1;

    internal int NativeCallSourceLine { get; set; } = -1;

    internal LuaValueWindow VarArgStorage => _varArgs;

    internal int RetainedVarArgCapacity => _varArgs.Capacity;

    internal List<int> ToBeClosedSlots { get; } = [];

    internal LuaContinuation Continuation { get; } = new();

    internal LuaFrameInstructionRoute InstructionRoute { get; set; }

    internal bool BackendEntryObserved { get; set; }

    internal void Initialize(
        LuaClosure closure,
        int @base,
        int top,
        int returnBase,
        int expectedResults,
        ReadOnlySpan<LuaValue> varArgs,
        LuaProtectedCallKind protectionKind = LuaProtectedCallKind.None,
        LuaValue errorHandler = default,
        bool isCloseHandler = false,
        bool isDebugHook = false,
        bool isHidden = false,
        LuaFunctionVersion? functionVersion = null)
    {
        ArgumentNullException.ThrowIfNull(closure);
        Closure = closure;
        FunctionVersion = functionVersion ?? closure.FunctionVersion;
        Base = @base;
        Top = top;
        ProgramCounter = 0;
        ReturnBase = returnBase;
        ExpectedResults = expectedResults;
        _varArgs.Set(varArgs);
        Continuation.ResetForFrameReuse();
        Continuation.ProtectionKind = protectionKind;
        Continuation.ErrorHandler = errorHandler;
        Continuation.IsCloseHandler = isCloseHandler;
        IsDebugHook = isDebugHook;
        IsHidden = isHidden;
        InstructionRoute = LuaFrameInstructionRoute.Backend;
        BackendEntryObserved = false;
        BackendBackedgeProbeCountdown = 1;
        UnreportedBackendBackedgeCount = 0;
        HasSourceLineInformation = FunctionVersion.HasSourceLineInformation;
    }

    internal void ResetForPool()
    {
        Closure = null!;
        FunctionVersion = null!;
        Base = 0;
        Top = 0;
        ProgramCounter = 0;
        ReturnBase = 0;
        ExpectedResults = 0;
        _varArgs.Clear(MaximumRetainedVarArguments);
        IsDebugHook = false;
        IsHidden = false;
        PendingDebugHookEvent = null;
        IsTailCall = false;
        DebugFunctionName = null;
        DebugFunctionNameWhat = null;
        DebugFunctionOverride = LuaValue.Nil;
        LastDebugHookLine = -1;
        LastDebugHookProgramCounter = -1;
        LastLineHookProgramCounter = -1;
        HasSourceLineInformation = false;
        DebugHookCheckedProgramCounter = -1;
        DispatchedDebugHookEvent = null;
        ReturnHookProgramCounter = -1;
        NativeCallHookProgramCounter = -1;
        NativeCallSourceLine = -1;
        InstructionRoute = LuaFrameInstructionRoute.Backend;
        BackendEntryObserved = false;
        BackendBackedgeProbeCountdown = 1;
        UnreportedBackendBackedgeCount = 0;
        ToBeClosedSlots.Clear();
        Continuation.ResetForFrameReuse();
    }

    internal int BackendBackedgeProbeCountdown { get; set; } = 1;

    internal int UnreportedBackendBackedgeCount { get; set; }

    internal LuaString GetOrCreateStringConstant(
        LuaState state,
        LuaThread thread,
        int constantIndex)
    {
        if (!ReferenceEquals(state.Heap, Closure.Owner) || !ReferenceEquals(thread.Owner, state.Heap))
        {
            throw new LuaRuntimeException("cannot materialize a constant in another Lua state");
        }

        var value = FunctionVersion.GetOrCreateStringConstant(state, constantIndex);
        Closure.Owner.WriteBarrier(thread, value);
        return value;
    }

    internal LuaTableAllocationHint GetOrCreateTableAllocationHint(int programCounter) =>
        FunctionVersion.GetOrCreateTableAllocationHint(programCounter);

}

internal enum LuaProtectedCallKind : byte
{
    None,
    ProtectedCall,
    ProtectedCallWithHandler,
    ErrorHandler,
    Finalizer,
}
