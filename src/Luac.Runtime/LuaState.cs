using System.Text;
using Luac.IR.Canonical;
using Luac.Runtime.Execution;
using Luac.Runtime.Memory;
using Luac.Runtime.Values;

namespace Luac.Runtime;

public sealed class LuaState
{
    private readonly Dictionary<LuaValueKind, LuaTable> _typeMetatables = [];

    public LuaState(LuaStateOptions? options = null)
    {
        options ??= LuaStateOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MainThreadInitialStackCapacity);
        Heap = new LuaHeap(options.Heap);
        Strings = new LuaStringPool(Heap);
        Globals = new LuaTable(Heap);
        MainThread = new LuaThread(Heap, options.MainThreadInitialStackCapacity);
        Heap.AddPermanentRoot(Globals);
        Heap.AddPermanentRoot(MainThread);
    }

    public LuaHeap Heap { get; }

    public LuaStringPool Strings { get; }

    public LuaTable Globals { get; }

    public LuaThread MainThread { get; }

    public LuaThread? RunningThread { get; internal set; }

    internal bool RunningThreadIsYieldable { get; set; }

    public event Action<LuaValue>? WarningRaised;

    public LuaTable CreateTable() => new(Heap);

    public LuaThread CreateThread(int initialStackCapacity = 128) =>
        new(Heap, initialStackCapacity);

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
        _typeMetatables.GetValueOrDefault(kind);

    public void SetTypeMetatable(LuaValueKind kind, LuaTable? metatable)
    {
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

    internal void ReportWarning(LuaValue warning) => WarningRaised?.Invoke(warning);

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
        return new LuaClosure(
            Heap,
            module,
            function,
            [new LuaUpvalue(Heap, LuaValue.FromTable(Globals))]);
    }
}
