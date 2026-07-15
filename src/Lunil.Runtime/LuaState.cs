using System.Text;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;

namespace Lunil.Runtime;

public sealed class LuaState
{
    private readonly Dictionary<LuaValueKind, LuaTable> _typeMetatables = [];

    public LuaState(LuaStateOptions? options = null)
    {
        options ??= LuaStateOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MainThreadInitialStackCapacity);
        Heap = new LuaHeap(options.Heap);
        Strings = new LuaStringPool(Heap);
        MemoryErrorString = Strings.GetOrCreate("not enough memory"u8);
        Globals = new LuaTable(Heap);
        Registry = new LuaTable(Heap);
        MainThread = new LuaThread(Heap, options.MainThreadInitialStackCapacity);
        Heap.AddPermanentRoot(MemoryErrorString);
        Heap.AddPermanentRoot(Globals);
        Heap.AddPermanentRoot(Registry);
        Heap.AddPermanentRoot(MainThread);
    }

    public LuaHeap Heap { get; }

    public LuaStringPool Strings { get; }

    internal LuaString MemoryErrorString { get; }

    public LuaTable Globals { get; }

    /// <summary>Per-state registry used by the standard library and embedding hosts.</summary>
    public LuaTable Registry { get; }

    public LuaThread MainThread { get; }

    public LuaThread? RunningThread { get; internal set; }

    public LuaValue RunningNativeFunction { get; internal set; }

    public bool IsRunningFinalizer { get; internal set; }

    internal bool RunningThreadIsYieldable { get; set; }


    public event Action<LuaValue>? WarningRaised;

    public LuaTable CreateTable(int arrayCapacity = 0, int hashCapacity = 0) =>
        new(Heap, arrayCapacity, hashCapacity);

    internal LuaTable CreateTableForAllocationSite(
        int arrayCapacity,
        int hashCapacity,
        LuaTableAllocationHint allocationHint) =>
        new(
            Heap,
            arrayCapacity,
            hashCapacity,
            Math.Max(arrayCapacity, allocationHint.ArrayCapacity),
            allocationHint);

    public LuaThread CreateThread(int initialStackCapacity = 128) =>
        new(Heap, initialStackCapacity);

    public LuaUserdata CreateUserdata(
        object? payload = null,
        int userValueCount = 1,
        long payloadLogicalSize = 0) =>
        new(Heap, payload, userValueCount, payloadLogicalSize);

    public LuaThread CreateThread(LuaValue entry, int initialStackCapacity = 128)
    {
        var thread = new LuaThread(Heap, initialStackCapacity);
        thread.Initialize(entry);
        return thread;
    }

    public LuaThread CreateThread(LuaClosure entry, int initialStackCapacity = 128) =>
        CreateThread(LuaValue.FromFunction(entry), initialStackCapacity);

    public LuaNativeClosure CreateNativeClosure(
        LuaNativeFunction descriptor,
        ReadOnlySpan<LuaValue> captures = default) =>
        new(Heap, descriptor, captures);

    public LuaHandle CreateHandle(LuaValue value) => Heap.CreateHandle(value);

    public LuaTable? GetTypeMetatable(LuaValueKind kind) =>
        _typeMetatables.GetValueOrDefault(NormalizeTypeMetatableKind(kind));

    public void SetTypeMetatable(LuaValueKind kind, LuaTable? metatable)
    {
        kind = NormalizeTypeMetatableKind(kind);
        if (_typeMetatables.Remove(kind, out var previous))
        {
            Heap.RemovePermanentRoot(previous);
        }

        if (metatable is null)
        {
            return;
        }

        Heap.ValidateValue(LuaValue.FromTable(metatable));
        _typeMetatables[kind] = metatable;
        Heap.AddPermanentRoot(metatable);
    }

    private static LuaValueKind NormalizeTypeMetatableKind(LuaValueKind kind) =>
        kind == LuaValueKind.Float ? LuaValueKind.Integer : kind;

    internal void ReportWarning(LuaValue warning) => WarningRaised?.Invoke(warning);

    public void RaiseWarning(LuaValue warning)
    {
        Heap.ValidateValue(warning);
        ReportWarning(warning);
    }

    public void SetGlobal(string name, LuaValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        Globals.Set(
            LuaValue.FromString(Strings.GetOrCreate(Encoding.UTF8.GetBytes(name))),
            value);
    }

    public void InstallProtectedCallFunctions()
    {
        SetGlobal(
            "pcall",
            LuaValue.FromFunction(new LuaNativeFunction(
                "pcall",
                static (_, _) => throw new InvalidOperationException("pcall is a VM intrinsic."),
                LuaNativeFunctionKind.ProtectedCall)));
        SetGlobal(
            "xpcall",
            LuaValue.FromFunction(new LuaNativeFunction(
                "xpcall",
                static (_, _) => throw new InvalidOperationException("xpcall is a VM intrinsic."),
                LuaNativeFunctionKind.ProtectedCallWithHandler)));
    }

    public LuaTable InstallCoroutineModule()
    {
        var module = LuaCoroutineModule.CreateModule(this);
        SetGlobal("coroutine", LuaValue.FromTable(module));
        return module;
    }

    public LuaValue GetGlobal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Globals.Get(LuaValue.FromString(
            Strings.GetOrCreate(Encoding.UTF8.GetBytes(name))));
    }

    public LuaClosure CreateMainClosure(LuaIrModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var function = module.Functions[module.MainFunctionId];
        var upvalues = new LuaUpvalue[function.Upvalues.Length];
        for (var index = 0; index < upvalues.Length; index++)
        {
            upvalues[index] = new LuaUpvalue(
                Heap,
                index == 0 ? LuaValue.FromTable(Globals) : LuaValue.Nil);
        }

        return new LuaClosure(
            Heap,
            module,
            function,
            upvalues,
            new LuaModuleStringConstants());
    }

    public LuaClosure LoadBinaryChunk(
        ReadOnlySpan<byte> binaryChunk,
        Lua54ChunkReaderOptions? options = null) =>
        CreateMainClosure(Lua54PrototypeConverter.Convert(binaryChunk, options));

    public LuaClosure LoadBinaryChunk(Lua54Chunk chunk) =>
        CreateMainClosure(Lua54PrototypeConverter.Convert(chunk));
}
