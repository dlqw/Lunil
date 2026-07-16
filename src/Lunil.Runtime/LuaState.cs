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
    private readonly Dictionary<string, LoadedModuleRegistration> _loadedModules =
        new(StringComparer.Ordinal);
    private LuaTable? _loadedModuleCache;
    private long _nextModuleRevision;

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

    /// <summary>
    /// Gets whether no Lua thread, native callback, or finalizer is currently executing in this
    /// state. This is an observation boundary, not a cross-thread execution barrier.
    /// </summary>
    public bool IsIdle => RunningThread is null && RunningNativeFunction.IsNil &&
        !IsRunningFinalizer;

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

    /// <summary>
    /// Gets the current successful <c>require</c> record for <paramref name="name"/>. A record is
    /// discarded when its cached value no longer matches the corresponding
    /// <c>package.loaded</c> entry.
    /// </summary>
    public bool TryGetModule(string name, out LuaModuleRecord? module)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!_loadedModules.TryGetValue(name, out var registration))
        {
            module = null;
            return false;
        }

        if (_loadedModuleCache is null ||
            !ModuleCacheValuesMatch(
                _loadedModuleCache.Get(GetModuleCacheKey(name)),
                registration.CachedValue))
        {
            RemoveLoadedModule(name, registration);
            module = null;
            return false;
        }

        module = registration.Snapshot();
        return true;
    }

    /// <summary>Gets a stable, ordinally sorted snapshot of current required module names.</summary>
    public IReadOnlyList<string> GetLoadedModuleNames()
    {
        foreach (var name in _loadedModules.Keys.ToArray())
        {
            _ = TryGetModule(name, out _);
        }

        return _loadedModules.Keys.Order(StringComparer.Ordinal).ToArray();
    }

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
            new LuaModuleRuntimeData(module),
            function,
            upvalues);
    }

    public LuaClosure LoadBinaryChunk(
        ReadOnlySpan<byte> binaryChunk,
        Lua54ChunkReaderOptions? options = null) =>
        CreateMainClosure(Lua54PrototypeConverter.Convert(binaryChunk, options));

    public LuaClosure LoadBinaryChunk(Lua54Chunk chunk) =>
        CreateMainClosure(Lua54PrototypeConverter.Convert(chunk));

    internal void AttachLoadedModuleCache(LuaTable loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        if (!ReferenceEquals(loaded.Owner, Heap))
        {
            throw new LuaRuntimeException("package.loaded must belong to its Lua state");
        }

        if (_loadedModuleCache is not null && !ReferenceEquals(_loadedModuleCache, loaded))
        {
            foreach (var registration in _loadedModules.Values)
            {
                registration.Dispose();
            }

            _loadedModules.Clear();
        }

        _loadedModuleCache = loaded;
    }

    internal LuaModuleRecord RegisterLoadedModule(
        string name,
        LuaModuleLoaderKind loaderKind,
        LuaValue loader,
        LuaValue loaderData,
        LuaValue cachedValue,
        LuaIrModule? module)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!Enum.IsDefined(loaderKind))
        {
            throw new ArgumentOutOfRangeException(nameof(loaderKind));
        }

        if (_loadedModuleCache is null)
        {
            throw new InvalidOperationException("The package.loaded table is not attached.");
        }

        if (!ModuleCacheValuesMatch(
            _loadedModuleCache.Get(GetModuleCacheKey(name)),
            cachedValue))
        {
            throw new InvalidOperationException(
                "The module record does not match the current package.loaded value.");
        }

        var revision = checked(_nextModuleRevision + 1);
        var replacement = new LoadedModuleRegistration(
            this,
            name,
            loaderKind,
            loader,
            loaderData,
            cachedValue,
            module,
            revision);
        _loadedModules.TryGetValue(name, out var previous);
        try
        {
            _loadedModules[name] = replacement;
        }
        catch
        {
            replacement.Dispose();
            throw;
        }

        previous?.Dispose();
        _nextModuleRevision = revision;
        return replacement.Snapshot();
    }

    internal LuaValue GetLoadedModuleCacheValue(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _loadedModuleCache?.Get(GetModuleCacheKey(name)) ?? LuaValue.Nil;
    }

    internal void SetLoadedModuleCacheValue(string name, LuaValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_loadedModuleCache is null)
        {
            throw new InvalidOperationException("The package.loaded table is not attached.");
        }

        _loadedModuleCache.Set(GetModuleCacheKey(name), value);
    }

    internal void RestoreLoadedModule(LuaModuleRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_loadedModuleCache is null || !ModuleCacheValuesMatch(
            _loadedModuleCache.Get(GetModuleCacheKey(record.Name)),
            record.CachedValue))
        {
            throw new InvalidOperationException(
                "The restored module record does not match package.loaded.");
        }

        var replacement = new LoadedModuleRegistration(
            this,
            record.Name,
            record.LoaderKind,
            record.Loader,
            record.LoaderData,
            record.CachedValue,
            record.Module,
            record.Revision);
        _loadedModules.TryGetValue(record.Name, out var current);
        try
        {
            _loadedModules[record.Name] = replacement;
        }
        catch
        {
            replacement.Dispose();
            throw;
        }

        current?.Dispose();
        _nextModuleRevision = Math.Max(_nextModuleRevision, record.Revision);
    }

    private LuaValue GetModuleCacheKey(string name) =>
        LuaValue.FromString(Strings.GetOrCreate(Encoding.UTF8.GetBytes(name)));

    private static bool ModuleCacheValuesMatch(LuaValue current, LuaValue recorded)
    {
        if (current.Kind != recorded.Kind)
        {
            return false;
        }

        return current.Kind == LuaValueKind.Float
            ? BitConverter.DoubleToInt64Bits(current.AsFloat()) ==
                BitConverter.DoubleToInt64Bits(recorded.AsFloat())
            : current == recorded;
    }

    private void RemoveLoadedModule(string name, LoadedModuleRegistration registration)
    {
        if (_loadedModules.Remove(name, out var removed))
        {
            removed.Dispose();
        }
        else
        {
            registration.Dispose();
        }
    }

    private sealed class LoadedModuleRegistration : IDisposable
    {
        private readonly LuaHandle _loader;
        private readonly LuaHandle _loaderData;
        private readonly LuaHandle _cachedValue;

        public LoadedModuleRegistration(
            LuaState state,
            string name,
            LuaModuleLoaderKind loaderKind,
            LuaValue loader,
            LuaValue loaderData,
            LuaValue cachedValue,
            LuaIrModule? module,
            long revision)
        {
            Name = name;
            LoaderKind = loaderKind;
            Module = module;
            Revision = revision;
            _loader = state.CreateHandle(loader);
            try
            {
                _loaderData = state.CreateHandle(loaderData);
                try
                {
                    _cachedValue = state.CreateHandle(cachedValue);
                }
                catch
                {
                    _loaderData.Dispose();
                    throw;
                }
            }
            catch
            {
                _loader.Dispose();
                throw;
            }
        }

        public string Name { get; }

        public LuaModuleLoaderKind LoaderKind { get; }

        public LuaIrModule? Module { get; }

        public long Revision { get; }

        public LuaValue CachedValue => _cachedValue.Value;

        public LuaModuleRecord Snapshot() => new(
            Name,
            LoaderKind,
            _loader.Value,
            _loaderData.Value,
            CachedValue,
            Module,
            Revision);

        public void Dispose()
        {
            _loader.Dispose();
            _loaderData.Dispose();
            _cachedValue.Dispose();
        }
    }
}
