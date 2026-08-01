using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Lunil.Core;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.StandardLibrary;

internal static class LuaFfiLibrary
{
    /// <summary>Version of the installed FFI module surface, kept in step with the 0.15 line.</summary>
    private const string ModuleVersion = "0.15";

    public static LuaTable Install(LuaState state, LuaStandardLibraryOptions options)
    {
        LunilGuard.NotNull(state);
        LunilGuard.NotNull(options);
        var context = new LuaFfiContext(state, options.Ffi);
        var module = state.CreateTable(hashCapacity: 8);
        LuaLibraryHelpers.SetFunction(state, module, "load",
            (owner, arguments) => LuaFfiContext.Execute(owner, arguments, context.Load));
        LuaLibraryHelpers.SetFunction(state, module, "bind",
            (owner, arguments) => LuaFfiContext.Execute(owner, arguments, context.Bind));
        LuaLibraryHelpers.SetFunction(state, module, "close",
            (owner, arguments) => LuaFfiContext.Execute(owner, arguments, context.Close));
        LuaLibraryHelpers.SetFunction(state, module, "alloc",
            (owner, arguments) => LuaFfiContext.Execute(owner, arguments, context.Alloc));
        LuaLibraryHelpers.SetFunction(state, module, "free",
            (owner, arguments) => LuaFfiContext.Execute(owner, arguments, context.Free));
        LuaLibraryHelpers.SetFunction(state, module, "read",
            (owner, arguments) => LuaFfiContext.Execute(owner, arguments, context.Read));
        LuaLibraryHelpers.SetFunction(state, module, "write",
            (owner, arguments) => LuaFfiContext.Execute(owner, arguments, context.Write));
        LuaLibraryHelpers.Set(state, module, "enabled", LuaValue.FromBoolean(true));
        LuaLibraryHelpers.Set(state, module, "version", LuaLibraryHelpers.String(state, ModuleVersion));
        state.SetGlobal("ffi", LuaValue.FromTable(module));
        return module;
    }
}

internal sealed class LuaFfiContext : IDisposable
{
    private static long _nextMetatableId;
    private readonly LuaState _state;
    private readonly LuaFfiOptions _options;
    private readonly ImmutableHashSet<string> _allowedLibraries;
    private readonly ImmutableHashSet<string> _allowedSymbols;
    private readonly LuaTable _libraryMetatable;
    private readonly LuaTable _bufferMetatable;
    private readonly Dictionary<string, LuaFfiLibraryHandle> _libraries =
        new(StringComparer.Ordinal);
    private readonly object _delegateTypeGate = new();
    private readonly Dictionary<LuaFfiSignature, Type> _delegateTypes = [];
    private long _allocatedBytes;
    private int _disposed;

    public LuaFfiContext(LuaState state, LuaFfiOptions? options)
    {
        LunilGuard.NotNull(state);
        LunilGuard.NotNull(options);
        _state = state;
        _options = options;
        _allowedLibraries = Normalize(options.AllowedLibraryNames, nameof(options.AllowedLibraryNames));
        _allowedSymbols = Normalize(options.AllowedSymbolNames, nameof(options.AllowedSymbolNames));
        LunilGuard.NotNull(options.LibraryLoader);
        if (!options.Enabled)
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.Disabled,
                "Native FFI is disabled for this Lua state.");
        }

        if (options.MaximumOpenLibraries is < 1 or > 4096 ||
            options.MaximumSignatureLength is < 8 or > 65_536 ||
            options.MaximumArgumentCount is < 0 or > 32 ||
            options.MaximumStringBytes is < 1 or > 1_073_741_824 ||
            options.MaximumBufferBytes is < 1 or > 1_073_741_824 ||
            options.MaximumAllocationBytes is < 1 or > long.MaxValue / 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Native FFI limits are outside their supported ranges.");
        }

        if (!LunilEnum.IsDefined(options.DefaultCallingConvention))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.DefaultCallingConvention,
                "The native FFI calling convention is invalid.");
        }

        if (_allowedLibraries.Count == 0)
        {
            throw new ArgumentException(
                "Native FFI requires at least one exact allowed library name.",
                nameof(options));
        }

        if (_allowedSymbols.Count == 0 &&
            (options.BindingRegistry is null || options.BindingRegistry.GetBindings().IsEmpty))
        {
            throw new ArgumentException(
                "Native FFI requires exact allowed symbols or registered bindings.",
                nameof(options));
        }

        _libraryMetatable = state.CreateTable(hashCapacity: 3);
        LuaLibraryHelpers.Set(
            state,
            _libraryMetatable,
            "__name",
            LuaLibraryHelpers.String(state, "ffi.library"));
        LuaLibraryHelpers.SetFunction(
            state,
            _libraryMetatable,
            "__gc",
            CollectLibrary,
            "ffi.library.__gc");
        LuaLibraryHelpers.SetFunction(
            state,
            _libraryMetatable,
            "__close",
            CloseLibraryMetamethod,
            "ffi.library.__close");

        _bufferMetatable = state.CreateTable(hashCapacity: 3);
        LuaLibraryHelpers.Set(
            state,
            _bufferMetatable,
            "__name",
            LuaLibraryHelpers.String(state, "ffi.buffer"));
        LuaLibraryHelpers.SetFunction(
            state,
            _bufferMetatable,
            "__gc",
            CollectBuffer,
            "ffi.buffer.__gc");
        LuaLibraryHelpers.SetFunction(
            state,
            _bufferMetatable,
            "__close",
            CloseBufferMetamethod,
            "ffi.buffer.__close");

        var metatableRoot = $"_LUNIL_FFI_METATABLE_{Interlocked.Increment(ref _nextMetatableId)}";
        LuaLibraryHelpers.Set(
            state,
            state.Registry,
            metatableRoot + ".library",
            LuaValue.FromTable(_libraryMetatable));
        LuaLibraryHelpers.Set(
            state,
            state.Registry,
            metatableRoot + ".buffer",
            LuaValue.FromTable(_bufferMetatable));
    }

    internal static LuaValue[] Execute(
        LuaState state,
        ReadOnlySpan<LuaValue> arguments,
        LuaNativeFunctionBody operation)
    {
        try
        {
            return operation(state, arguments);
        }
        catch (LuaRuntimeException)
        {
            throw;
        }
        catch (LuaFfiException exception)
        {
            throw ToLuaException(exception);
        }
        catch (OverflowException exception)
        {
            throw new LuaRuntimeException(
                $"ffi {LuaFfiErrorCode.RangeExceeded}: a native FFI value is outside its supported range.",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new LuaRuntimeException(
                $"ffi {LuaFfiErrorCode.InvalidArgument}: a native FFI value has an invalid representation.",
                exception);
        }
        catch (InvalidCastException exception)
        {
            throw new LuaRuntimeException(
                $"ffi {LuaFfiErrorCode.InvalidArgument}: a native FFI value has an invalid representation.",
                exception);
        }
    }

    internal static LuaRuntimeException ToLuaException(LuaFfiException exception) =>
        new($"ffi {exception.Code}: {exception.Message}", exception);

    public LuaValue[] Load(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        EnsureState(state);
        var libraryName = CheckText(arguments, 0, "ffi.load");
        EnsureLibraryAllowed(libraryName);
        lock (_libraries)
        {
            if (_libraries.TryGetValue(libraryName, out var cached) && !cached.IsClosed)
            {
                var lease = cached.AcquireLease();
                try
                {
                    return [CreateLibraryValue(state, lease)];
                }
                catch
                {
                    lease.Dispose();
                    throw;
                }
            }

            if (_libraries.Count >= _options.MaximumOpenLibraries)
            {
                throw new LuaFfiException(
                    LuaFfiErrorCode.ResourceLimitExceeded,
                    "The native FFI library limit has been reached.");
            }

            var hasRegisteredBinding = _options.BindingRegistry?.GetBindings()
                .Any(binding => string.Equals(binding.LibraryName, libraryName, StringComparison.Ordinal))
                == true;
            var nativeHandle = IntPtr.Zero;
            if (!hasRegisteredBinding || RuntimeFeature.IsDynamicCodeSupported)
            {
                try
                {
                    nativeHandle = _options.LibraryLoader.Load(libraryName);
                    if (nativeHandle == IntPtr.Zero)
                    {
                        throw new DllNotFoundException(
                            $"Native library '{libraryName}' returned a null handle.");
                    }
                }
                catch (Exception exception)
                {
                    throw new LuaFfiException(
                        LuaFfiErrorCode.LibraryLoadFailed,
                        $"Native library '{libraryName}' could not be loaded.",
                        exception);
                }
            }

            var handle = new LuaFfiLibraryHandle(this, libraryName, nativeHandle);
            _libraries.Add(libraryName, handle);
            var newLease = handle.AcquireLease();
            try
            {
                return [CreateLibraryValue(state, newLease)];
            }
            catch
            {
                newLease.Dispose();
                throw;
            }
        }
    }

    public LuaValue[] Bind(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        EnsureState(state);
        var libraryLease = RequireLibrary(arguments, 0, "ffi.bind");
        var library = libraryLease.Handle;
        var symbolName = CheckText(arguments, 1, "ffi.bind");
        EnsureSymbolAllowed(library.Name, symbolName);
        var declaration = CheckText(arguments, 2, "ffi.bind");
        if (declaration.Length > _options.MaximumSignatureLength)
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.InvalidSignature,
                "The native signature exceeds the configured length limit.");
        }

        var convention = arguments.Length > 3 && !arguments[3].IsNil
            ? ParseCallingConvention(CheckText(arguments, 3, "ffi.bind"))
            : _options.DefaultCallingConvention;
        var signature = LuaFfiSignature.Parse(declaration, convention);
        if (signature.ParameterTypes.Length > _options.MaximumArgumentCount)
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.InvalidSignature,
                "The native signature has too many parameters.");
        }

        try
        {
            LuaFfiNativeInvoker invoker;
            var registered = _options.BindingRegistry;
            if (registered is not null && registered.TryGet(library.Name, symbolName, out var binding))
            {
                if (!signature.Equals(binding!.Signature))
                {
                    throw new LuaFfiException(
                        LuaFfiErrorCode.InvalidSignature,
                        $"The requested signature does not match the registered binding for '{library.Name}!{symbolName}'.");
                }

                invoker = binding.Invoker;
            }
            else
            {
                if (!RuntimeFeature.IsDynamicCodeSupported)
                {
                    throw new LuaFfiException(
                        LuaFfiErrorCode.DynamicCodeUnavailable,
                        "Dynamic native signature adaptation is unavailable; register an AOT binding.");
                }

                var address = library.GetExport(symbolName);
                try
                {
                    invoker = CreateDynamicInvoker(address, signature);
                }
                catch (LuaFfiException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new LuaFfiException(
                        LuaFfiErrorCode.UnsupportedSignature,
                        $"Native signature '{signature}' cannot be adapted for dynamic invocation.",
                        exception);
                }
            }

            var callLease = library.AcquireLease();
            try
            {
                var call = new LuaFfiNativeCall(this, library, symbolName, signature, invoker);
                var descriptor = new LuaNativeFunction(
                    $"ffi.{library.Name}!{symbolName}",
                    (owner, values) => call.Invoke(owner, values));
                var leaseUserdata = CreateLibraryUserdata(state, callLease);
                try
                {
                    return [LuaValue.FromFunction(state.CreateNativeClosure(
                        descriptor,
                        [LuaValue.FromUserdata(leaseUserdata)]))];
                }
                catch
                {
                    leaseUserdata.DisposePayload();
                    throw;
                }
            }
            catch
            {
                callLease.Dispose();
                throw;
            }
        }
        catch (LuaFfiException)
        {
            throw;
        }
    }

    public LuaValue[] Close(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        EnsureState(state);
        var value = LuaLibraryHelpers.Required(arguments, 0, "ffi.close");
        if (value.Kind != LuaValueKind.Userdata ||
            value.AsUserdata().Payload is not LuaFfiLibraryLease library)
        {
            throw LuaLibraryHelpers.BadArgument("ffi.close", 0, "native library expected");
        }

        library.CloseExplicit();
        return [];
    }

    public LuaValue[] Alloc(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        EnsureState(state);
        var sizeValue = LuaLibraryHelpers.Required(arguments, 0, "ffi.alloc");
        if (!sizeValue.TryGetInteger(out var size) || size < 1 || size > int.MaxValue)
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.RangeExceeded,
                "ffi.alloc requires a positive integer size.");
        }

        if (size > _options.MaximumBufferBytes)
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.RangeExceeded,
                "The native buffer exceeds the configured buffer limit.");
        }

        var buffer = new LuaFfiBuffer(this, checked((int)size));
        try
        {
            var userdata = state.CreateUserdata(buffer, 1, 64 + buffer.Length);
            userdata.SetMetatable(_bufferMetatable);
            return [LuaValue.FromUserdata(userdata)];
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    public LuaValue[] Free(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        EnsureState(state);
        var value = LuaLibraryHelpers.Required(arguments, 0, "ffi.free");
        if (value.Kind != LuaValueKind.Userdata)
        {
            throw LuaLibraryHelpers.BadArgument("ffi.free", 0, "native buffer expected");
        }

        if (value.AsUserdata().Payload is LuaFfiBuffer buffer)
        {
            buffer.Dispose();
        }
        else if (value.AsUserdata().Payload is not null)
        {
            throw LuaLibraryHelpers.BadArgument("ffi.free", 0, "native buffer expected");
        }

        return [];
    }

    public LuaValue[] Read(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        EnsureState(state);
        var buffer = RequireBuffer(arguments, 0, "ffi.read");
        var offset = CheckOffset(arguments, 1, "ffi.read");
        var type = ParseStorageType(CheckText(arguments, 2, "ffi.read"));
        return [buffer.Read(state, offset, type)];
    }

    public LuaValue[] Write(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        EnsureState(state);
        var buffer = RequireBuffer(arguments, 0, "ffi.write");
        var offset = CheckOffset(arguments, 1, "ffi.write");
        var type = ParseStorageType(CheckText(arguments, 2, "ffi.write"));
        buffer.Write(state, offset, type, LuaLibraryHelpers.Required(arguments, 3, "ffi.write"));
        return [];
    }

    internal IntPtr GetExport(IntPtr handle, string symbolName) =>
        _options.LibraryLoader.GetExport(handle, symbolName);

    internal void Free(IntPtr handle) => _options.LibraryLoader.Free(handle);

    internal LuaValue CreateLibraryValue(LuaState state, LuaFfiLibraryLease lease) =>
        LuaValue.FromUserdata(CreateLibraryUserdata(state, lease));

    private LuaUserdata CreateLibraryUserdata(LuaState state, LuaFfiLibraryLease lease)
    {
        var userdata = state.CreateUserdata(lease, 1, 64);
        userdata.SetMetatable(_libraryMetatable);
        return userdata;
    }

    internal LuaFfiLibraryLease AcquireLease(LuaFfiLibraryHandle handle)
    {
        lock (_libraries)
        {
            if (_disposed != 0 ||
                !_libraries.TryGetValue(handle.Name, out var current) ||
                !ReferenceEquals(current, handle) ||
                handle.IsClosed)
            {
                throw new LuaFfiException(
                    LuaFfiErrorCode.LibraryClosed,
                    $"Native library '{handle.Name}' is closed.");
            }

            handle.AddLease();
            return new LuaFfiLibraryLease(handle);
        }
    }

    internal void ReleaseLease(LuaFfiLibraryHandle handle)
    {
        lock (_libraries)
        {
            if (!handle.RemoveLease())
            {
                return;
            }

            if (_libraries.TryGetValue(handle.Name, out var current) &&
                ReferenceEquals(current, handle))
            {
                _libraries.Remove(handle.Name);
            }

            handle.CloseNative();
        }
    }

    internal void CloseLibrary(LuaFfiLibraryHandle handle)
    {
        lock (_libraries)
        {
            if (_libraries.TryGetValue(handle.Name, out var current) &&
                ReferenceEquals(current, handle))
            {
                _libraries.Remove(handle.Name);
            }

            handle.ForceClose();
        }
    }

    internal void Reserve(long bytes)
    {
        long current;
        do
        {
            current = Volatile.Read(ref _allocatedBytes);
            if (bytes > _options.MaximumAllocationBytes - current)
            {
                throw new LuaFfiException(
                    LuaFfiErrorCode.AllocationLimitExceeded,
                    "The native FFI allocation budget has been exceeded.");
            }
        }
        while (Interlocked.CompareExchange(ref _allocatedBytes, current + bytes, current) != current);
    }

    internal void Release(long bytes) => Interlocked.Add(ref _allocatedBytes, -bytes);

    internal int MaximumStringBytes => _options.MaximumStringBytes;

    internal static LuaValue PointerValue(
        LuaState state,
        IntPtr address,
        LuaFfiLibraryHandle? library = null)
    {
        if (address == IntPtr.Zero)
        {
            return LuaValue.Nil;
        }

        LuaFfiLibraryLease? lease = null;
        try
        {
            lease = library?.AcquireLease();
            return LuaValue.FromUserdata(state.CreateUserdata(
                new LuaFfiPointer(address, lease),
                1,
                32));
        }
        catch
        {
            lease?.Dispose();
            throw;
        }
    }

    internal void EnsureSymbolAllowed(string libraryName, string symbolName)
    {
        var qualifiedName = libraryName + "!" + symbolName;
        var registered = _options.BindingRegistry?.TryGet(libraryName, symbolName, out _) == true;
        if (!_allowedSymbols.Contains(qualifiedName) && !registered)
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.SymbolNotAllowed,
                $"Native symbol '{qualifiedName}' is not allowlisted.");
        }
    }

    private static LuaValue[] CollectLibrary(
        LuaState _,
        ReadOnlySpan<LuaValue> arguments)
    {
        if (arguments.Length > 0 &&
            arguments[0].Kind == LuaValueKind.Userdata &&
            arguments[0].AsUserdata().Payload is LuaFfiLibraryLease lease)
        {
            lease.Dispose();
        }

        return [];
    }

    private static LuaValue[] CloseLibraryMetamethod(
        LuaState _,
        ReadOnlySpan<LuaValue> arguments)
    {
        if (arguments.Length > 0 &&
            arguments[0].Kind == LuaValueKind.Userdata &&
            arguments[0].AsUserdata().Payload is LuaFfiLibraryLease lease)
        {
            lease.CloseExplicit();
        }

        return [];
    }

    private static LuaValue[] CollectBuffer(
        LuaState _,
        ReadOnlySpan<LuaValue> arguments)
    {
        if (arguments.Length > 0 &&
            arguments[0].Kind == LuaValueKind.Userdata &&
            arguments[0].AsUserdata().Payload is LuaFfiBuffer buffer)
        {
            buffer.Dispose();
        }

        return [];
    }

    private static LuaValue[] CloseBufferMetamethod(
        LuaState _,
        ReadOnlySpan<LuaValue> arguments)
    {
        if (arguments.Length > 0 &&
            arguments[0].Kind == LuaValueKind.Userdata &&
            arguments[0].AsUserdata().Payload is LuaFfiBuffer buffer)
        {
            buffer.Dispose();
        }

        return [];
    }

    internal LuaFfiNativeType ParseStorageType(string text)
    {
        if (text.Length > _options.MaximumSignatureLength)
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.InvalidSignature,
                "The native storage type exceeds the configured length limit.");
        }

        var signature = LuaFfiSignature.Parse(text + "()", _options.DefaultCallingConvention);
        if (signature.ReturnType == LuaFfiNativeType.Void)
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.UnsupportedSignature,
                "This type cannot be used for buffer storage.");
        }

        return signature.ReturnType;
    }

    internal static int SizeOf(LuaFfiNativeType type) => type switch
    {
        LuaFfiNativeType.Boolean or LuaFfiNativeType.Int8 or LuaFfiNativeType.UInt8 => 1,
        LuaFfiNativeType.Int16 or LuaFfiNativeType.UInt16 => 2,
        LuaFfiNativeType.Int32 or LuaFfiNativeType.UInt32 or LuaFfiNativeType.Float => 4,
        LuaFfiNativeType.Int64 or LuaFfiNativeType.UInt64 or LuaFfiNativeType.Double => 8,
        LuaFfiNativeType.IntPtr or LuaFfiNativeType.UIntPtr or LuaFfiNativeType.Pointer =>
            IntPtr.Size,
        _ => throw new LuaFfiException(
            LuaFfiErrorCode.UnsupportedSignature,
            $"Native storage type '{type}' is not supported."),
    };

    internal object? ConvertArgument(
        LuaState state,
        LuaValue value,
        LuaFfiNativeType type,
        List<(IntPtr Address, int Size)> temporary)
    {
        switch (type)
        {
            case LuaFfiNativeType.Boolean:
                return ConvertBoolean(value, type);
            case LuaFfiNativeType.Int8:
                return checked((sbyte)ConvertInteger(value, type));
            case LuaFfiNativeType.UInt8:
                return checked((byte)ConvertInteger(value, type));
            case LuaFfiNativeType.Int16:
                return checked((short)ConvertInteger(value, type));
            case LuaFfiNativeType.UInt16:
                return checked((ushort)ConvertInteger(value, type));
            case LuaFfiNativeType.Int32:
                return checked((int)ConvertInteger(value, type));
            case LuaFfiNativeType.UInt32:
                return checked((uint)ConvertInteger(value, type));
            case LuaFfiNativeType.Int64:
                return ConvertInteger(value, type);
            case LuaFfiNativeType.UInt64:
                return checked((ulong)ConvertInteger(value, type));
            case LuaFfiNativeType.IntPtr:
                return new IntPtr(ConvertInteger(value, type));
            case LuaFfiNativeType.UIntPtr:
                return new UIntPtr(checked((ulong)ConvertInteger(value, type)));
            case LuaFfiNativeType.Float:
                return checked((float)ConvertNumber(value, type));
            case LuaFfiNativeType.Double:
                return ConvertNumber(value, type);
            case LuaFfiNativeType.Pointer:
                return ConvertPointer(value, type);
            case LuaFfiNativeType.Utf8String:
                {
                    if (value.Kind != LuaValueKind.String)
                    {
                        throw new LuaFfiException(
                            LuaFfiErrorCode.InvalidArgument,
                            "A UTF-8 string argument is required.");
                    }

                    var bytes = value.AsString().ToArray();
                    if (bytes.Length > _options.MaximumStringBytes)
                    {
                        throw new LuaFfiException(
                            LuaFfiErrorCode.RangeExceeded,
                            "The UTF-8 string exceeds the configured length limit.");
                    }

                    var size = checked(bytes.Length + 1);
                    Reserve(size);
                    var address = IntPtr.Zero;
                    try
                    {
                        address = Marshal.AllocHGlobal(size);
                        Marshal.Copy(bytes, 0, address, bytes.Length);
                        Marshal.WriteByte(address, bytes.Length, 0);
                        temporary.Add((address, size));
                        return address;
                    }
                    catch
                    {
                        if (address != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(address);
                        }

                        Release(size);
                        throw;
                    }
                }
            default:
                throw new LuaFfiException(
                    LuaFfiErrorCode.UnsupportedSignature,
                    $"Native argument type '{type}' is not supported.");
        }
    }

    internal LuaValue ConvertReturn(
        LuaState state,
        LuaFfiNativeType type,
        object? value,
        LuaFfiLibraryHandle? library = null)
    {
        switch (type)
        {
            case LuaFfiNativeType.Void:
                return LuaValue.Nil;
            case LuaFfiNativeType.Boolean:
                return LuaValue.FromBoolean(Convert.ToByte(value ?? 0, CultureInfo.InvariantCulture) != 0);
            case LuaFfiNativeType.Int8:
            case LuaFfiNativeType.Int16:
            case LuaFfiNativeType.Int32:
            case LuaFfiNativeType.Int64:
                return LuaValue.FromInteger(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case LuaFfiNativeType.IntPtr:
                return LuaValue.FromInteger(ToInt64(value));
            case LuaFfiNativeType.UInt8:
            case LuaFfiNativeType.UInt16:
            case LuaFfiNativeType.UInt32:
            case LuaFfiNativeType.UInt64:
                {
                    var unsigned = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
                    if (unsigned > long.MaxValue)
                    {
                        throw new LuaFfiException(
                            LuaFfiErrorCode.RangeExceeded,
                            "The native unsigned result cannot be represented by a Lua integer.");
                    }

                    return LuaValue.FromInteger((long)unsigned);
                }
            case LuaFfiNativeType.UIntPtr:
                {
                    var unsigned = ToUInt64(value);
                    if (unsigned > long.MaxValue)
                    {
                        throw new LuaFfiException(
                            LuaFfiErrorCode.RangeExceeded,
                            "The native unsigned result cannot be represented by a Lua integer.");
                    }

                    return LuaValue.FromInteger((long)unsigned);
                }
            case LuaFfiNativeType.Float:
            case LuaFfiNativeType.Double:
                return LuaValue.FromFloat(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            case LuaFfiNativeType.Pointer:
            case LuaFfiNativeType.Utf8String:
                {
                    if (type == LuaFfiNativeType.Utf8String && value is string text)
                    {
                        var bytes = Encoding.UTF8.GetBytes(text);
                        if (bytes.Length > _options.MaximumStringBytes)
                        {
                            throw new LuaFfiException(
                                LuaFfiErrorCode.RangeExceeded,
                                "The native string exceeds the configured length limit.");
                        }

                        return LuaValue.FromString(state.Strings.GetOrCreate(bytes));
                    }

                    if (type == LuaFfiNativeType.Utf8String && value is byte[] bytesValue)
                    {
                        if (bytesValue.Length > _options.MaximumStringBytes)
                        {
                            throw new LuaFfiException(
                                LuaFfiErrorCode.RangeExceeded,
                                "The native string exceeds the configured length limit.");
                        }

                        return LuaValue.FromString(state.Strings.GetOrCreate(bytesValue));
                    }

                    var address = value switch
                    {
                        IntPtr pointer => pointer,
                        UIntPtr pointer => new IntPtr(unchecked((long)pointer.ToUInt64())),
                        _ => IntPtr.Zero,
                    };
                    return type == LuaFfiNativeType.Pointer
                        ? PointerValue(state, address, library)
                        : address == IntPtr.Zero
                            ? LuaValue.Nil
                            : LuaValue.FromString(
                                state.Strings.GetOrCreate(ReadNullTerminated(
                                    address,
                                    _options.MaximumStringBytes)));
                }
            default:
                throw new LuaFfiException(
                    LuaFfiErrorCode.UnsupportedSignature,
                    $"Native return type '{type}' is not supported.");
        }
    }

    private static long ToInt64(object? value) => value switch
    {
        IntPtr pointer => pointer.ToInt64(),
        UIntPtr pointer => checked((long)pointer.ToUInt64()),
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture),
    };

    private static ulong ToUInt64(object? value) => value switch
    {
        UIntPtr pointer => pointer.ToUInt64(),
        IntPtr pointer => checked((ulong)pointer.ToInt64()),
        _ => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
    };

    internal static byte[] ReadNullTerminated(IntPtr address, int maximumBytes)
    {
        var bytes = new List<byte>(Math.Min(maximumBytes, 256));
        for (var index = 0; index < maximumBytes; index++)
        {
            var value = Marshal.ReadByte(IntPtr.Add(address, index));
            if (value == 0)
            {
                return [.. bytes];
            }

            bytes.Add(value);
        }

        throw new LuaFfiException(
            LuaFfiErrorCode.RangeExceeded,
            "The native string has no terminator within the configured limit.");
    }

    internal static LuaFfiCallingConvention ParseCallingConvention(string text) =>
        text.Trim().ToLowerInvariant() switch
        {
            "default" or "platform" or "platformdefault" => LuaFfiCallingConvention.PlatformDefault,
            "cdecl" => LuaFfiCallingConvention.Cdecl,
            "stdcall" => LuaFfiCallingConvention.Stdcall,
            _ => throw new LuaFfiException(
                LuaFfiErrorCode.InvalidSignature,
                $"Calling convention '{text}' is not supported."),
        };

    private static LuaFfiLibraryLease RequireLibrary(
        ReadOnlySpan<LuaValue> arguments,
        int index,
        string function)
    {
        var value = LuaLibraryHelpers.Required(arguments, index, function);
        if (value.Kind != LuaValueKind.Userdata ||
            value.AsUserdata().Payload is not LuaFfiLibraryLease library)
        {
            throw LuaLibraryHelpers.BadArgument(function, index, "native library expected");
        }

        if (library.IsClosed)
        {
            throw new LuaFfiException(LuaFfiErrorCode.LibraryClosed, "The native library is closed.");
        }

        return library;
    }

    private static LuaFfiBuffer RequireBuffer(
        ReadOnlySpan<LuaValue> arguments,
        int index,
        string function)
    {
        var value = LuaLibraryHelpers.Required(arguments, index, function);
        if (value.Kind != LuaValueKind.Userdata ||
            value.AsUserdata().Payload is not LuaFfiBuffer buffer)
        {
            throw LuaLibraryHelpers.BadArgument(function, index, "native buffer expected");
        }

        if (buffer.IsClosed)
        {
            throw new LuaFfiException(LuaFfiErrorCode.BufferClosed, "The native buffer is closed.");
        }

        return buffer;
    }

    private void EnsureLibraryAllowed(string libraryName)
    {
        if (HasPathTraversal(libraryName) || !_allowedLibraries.Contains(libraryName))
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.LibraryNotAllowed,
                $"Native library '{libraryName}' is not allowlisted.");
        }
    }

    private static bool HasPathTraversal(string libraryName) =>
        libraryName.Split(['/', '\\'], StringSplitOptions.None)
            .Any(static segment => string.Equals(segment, "..", StringComparison.Ordinal));

    private void EnsureState(LuaState state)
    {
        if (_disposed != 0 || !ReferenceEquals(state, _state))
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.Disabled,
                "The native FFI context is no longer active.");
        }
    }

    internal void EnsureInvocationState(LuaState state) => EnsureState(state);

    private static ImmutableHashSet<string> Normalize(
        ImmutableArray<string> values,
        string parameterName)
    {
        if (values.IsDefault)
        {
            return ImmutableHashSet<string>.Empty;
        }

        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    $"Native FFI allowlist '{parameterName}' contains an empty value.",
                    parameterName);
            }

            if (value.Contains('\0'))
            {
                throw new ArgumentException(
                    $"Native FFI allowlist '{parameterName}' contains a NUL character.",
                    parameterName);
            }

            builder.Add(value);
        }

        return builder.ToImmutable();
    }

    private static string CheckText(
        ReadOnlySpan<LuaValue> arguments,
        int index,
        string function)
    {
        var bytes = LuaLibraryHelpers.CheckStringBytes(arguments, index, function);
        var value = Encoding.UTF8.GetString(bytes);
        if (value.Length == 0)
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.InvalidName,
                $"Native FFI argument #{index + 1} must not be empty.");
        }

        if (value.Contains('\0'))
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.InvalidName,
                $"Native FFI argument #{index + 1} must not contain a NUL character.");
        }

        return value;
    }

    private static int CheckOffset(
        ReadOnlySpan<LuaValue> arguments,
        int index,
        string function)
    {
        var value = LuaLibraryHelpers.Required(arguments, index, function);
        if (!value.TryGetInteger(out var offset) || offset < 0 || offset > int.MaxValue)
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.RangeExceeded,
                $"Native FFI argument #{index + 1} must be a non-negative integer.");
        }

        return (int)offset;
    }

    private static long ConvertInteger(LuaValue value, LuaFfiNativeType type)
    {
        if (!value.TryGetInteger(out var integer))
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.InvalidArgument,
                $"Native {type} arguments require an exact Lua integer.");
        }

        return integer;
    }

    private static byte ConvertBoolean(LuaValue value, LuaFfiNativeType type) =>
        value.Kind == LuaValueKind.Boolean
            ? (byte)(value.AsBoolean() ? 1 : 0)
            : ConvertInteger(value, type) == 0 ? (byte)0 : (byte)1;

    private static double ConvertNumber(LuaValue value, LuaFfiNativeType type) =>
        value.Kind switch
        {
            LuaValueKind.Integer => value.AsInteger(),
            LuaValueKind.Float => value.AsFloat(),
            _ => throw new LuaFfiException(
                LuaFfiErrorCode.InvalidArgument,
                $"Native {type} arguments require a Lua number."),
        };

    internal static IntPtr ConvertPointer(LuaValue value, LuaFfiNativeType type)
    {
        if (value.IsNil)
        {
            return IntPtr.Zero;
        }

        if (value.Kind != LuaValueKind.Userdata)
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.InvalidArgument,
                $"Native {type} arguments require a native buffer or pointer.");
        }

        return value.AsUserdata().Payload switch
        {
            LuaFfiBuffer buffer when !buffer.IsClosed => buffer.Address,
            LuaFfiPointer pointer when !pointer.IsClosed => pointer.Address,
            _ => throw new LuaFfiException(
                LuaFfiErrorCode.InvalidArgument,
                $"Native {type} arguments require a live native buffer or pointer."),
        };
    }

    private LuaFfiNativeInvoker CreateDynamicInvoker(IntPtr address, LuaFfiSignature signature)
    {
        var delegateType = GetDelegateType(signature);
        var native = Marshal.GetDelegateForFunctionPointer(address, delegateType);
        return arguments => native.DynamicInvoke(arguments.ToArray());
    }

    private Type GetDelegateType(LuaFfiSignature signature)
    {
        lock (_delegateTypeGate)
        {
            if (_delegateTypes.TryGetValue(signature, out var type))
            {
                return type;
            }

            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                throw new LuaFfiException(
                    LuaFfiErrorCode.DynamicCodeUnavailable,
                    "The runtime cannot create a dynamic native delegate type.");
            }

            var parameterTypes = signature.ParameterTypes
                .Select(ToClrType)
                .Append(ToClrType(signature.ReturnType))
                .ToArray();
            type = CreateDelegateType(parameterTypes, signature.CallingConvention);
            _delegateTypes.Add(signature, type);
            return type;
        }
    }

    private static Type CreateDelegateType(Type[] types, LuaFfiCallingConvention convention)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("Lunil.DynamicFfiDelegates"),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Lunil.DynamicFfiDelegates");
        var builder = module.DefineType(
            "FfiDelegate" + Guid.NewGuid().ToString("N"),
            TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Public,
            typeof(MulticastDelegate));
        var constructor = builder.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.HideBySig |
            MethodAttributes.RTSpecialName,
            CallingConventions.Standard,
            [typeof(object), typeof(IntPtr)]);
        constructor.SetImplementationFlags(
            MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
        var invoke = builder.DefineMethod(
            "Invoke",
            MethodAttributes.Public | MethodAttributes.HideBySig |
            MethodAttributes.NewSlot | MethodAttributes.Virtual,
            types[^1],
            types[..^1]);
        invoke.SetImplementationFlags(
            MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
        var attributeConstructor = typeof(UnmanagedFunctionPointerAttribute)
            .GetConstructor([typeof(CallingConvention)])!;
        var callingConvention = convention switch
        {
            LuaFfiCallingConvention.Cdecl => CallingConvention.Cdecl,
            LuaFfiCallingConvention.Stdcall => CallingConvention.StdCall,
            _ => CallingConvention.Winapi,
        };
        builder.SetCustomAttribute(new CustomAttributeBuilder(
            attributeConstructor,
            [callingConvention]));
        return builder.CreateType()!;
    }

    private static Type ToClrType(LuaFfiNativeType type) => type switch
    {
        LuaFfiNativeType.Void => typeof(void),
        LuaFfiNativeType.Boolean or LuaFfiNativeType.UInt8 => typeof(byte),
        LuaFfiNativeType.Int8 => typeof(sbyte),
        LuaFfiNativeType.Int16 => typeof(short),
        LuaFfiNativeType.UInt16 => typeof(ushort),
        LuaFfiNativeType.Int32 => typeof(int),
        LuaFfiNativeType.UInt32 => typeof(uint),
        LuaFfiNativeType.Int64 => typeof(long),
        LuaFfiNativeType.UInt64 => typeof(ulong),
        LuaFfiNativeType.IntPtr or LuaFfiNativeType.Pointer or LuaFfiNativeType.Utf8String =>
            typeof(IntPtr),
        LuaFfiNativeType.UIntPtr => typeof(UIntPtr),
        LuaFfiNativeType.Float => typeof(float),
        LuaFfiNativeType.Double => typeof(double),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            LuaFfiLibraryHandle[] libraries;
            lock (_libraries)
            {
                libraries = _libraries.Values.ToArray();
                _libraries.Clear();
            }

            foreach (var library in libraries)
            {
                library.ForceClose();
            }
        }
    }
}

internal sealed class LuaFfiLibraryHandle : IDisposable
{
    private readonly LuaFfiContext _context;
    private readonly IntPtr _nativeHandle;
    private int _leaseCount;
    private int _disposed;

    public LuaFfiLibraryHandle(LuaFfiContext context, string name, IntPtr nativeHandle)
    {
        _context = context;
        Name = name;
        _nativeHandle = nativeHandle;
    }

    public string Name { get; }

    public bool IsClosed => Volatile.Read(ref _disposed) != 0;

    internal void AddLease()
    {
        if (IsClosed)
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.LibraryClosed,
                $"Native library '{Name}' is closed.");
        }

        checked
        {
            _leaseCount++;
        }
    }

    internal bool RemoveLease()
    {
        if (_leaseCount <= 0)
        {
            return false;
        }

        _leaseCount--;
        return _leaseCount == 0;
    }

    internal LuaFfiLibraryLease AcquireLease() => _context.AcquireLease(this);

    internal void ReleaseLease() => _context.ReleaseLease(this);

    internal void CloseExplicit() => _context.CloseLibrary(this);

    public IntPtr GetExport(string symbolName)
    {
        if (IsClosed)
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.LibraryClosed,
                $"Native library '{Name}' is closed.");
        }

        if (_nativeHandle == IntPtr.Zero)
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.DynamicCodeUnavailable,
                $"Native library '{Name}' has no runtime handle; use its registered AOT bindings.");
        }

        try
        {
            return _context.GetExport(_nativeHandle, symbolName);
        }
        catch (LuaFfiException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.SymbolNotFound,
                $"Native symbol '{Name}!{symbolName}' was not found.",
                exception);
        }
    }

    internal void CloseNative()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            if (_nativeHandle != IntPtr.Zero)
            {
                try
                {
                    _context.Free(_nativeHandle);
                }
                catch
                {
                    // Native unload is also reached from the Lua GC finalizer path and must
                    // never escape into the collector. The handle is still retired locally.
                }
            }

        }
    }

    internal void ForceClose() => CloseNative();

    public void Dispose() => ForceClose();
}

internal sealed class LuaFfiLibraryLease : IDisposable
{
    private LuaFfiLibraryHandle? _handle;

    public LuaFfiLibraryLease(LuaFfiLibraryHandle handle)
    {
        _handle = handle;
    }

    public LuaFfiLibraryHandle Handle =>
        Volatile.Read(ref _handle) ??
        throw new LuaFfiException(
            LuaFfiErrorCode.LibraryClosed,
            "The native library lease is closed.");

    public bool IsClosed => Volatile.Read(ref _handle)?.IsClosed != false;

    public void Dispose() =>
        Interlocked.Exchange(ref _handle, null)?.ReleaseLease();

    public void CloseExplicit()
    {
        var handle = Volatile.Read(ref _handle);
        if (handle is null)
        {
            return;
        }

        Dispose();
        handle.CloseExplicit();
    }
}

internal sealed class LuaFfiNativeCall
{
    private readonly LuaFfiContext _context;
    private readonly LuaFfiLibraryHandle _library;
    private readonly string _symbolName;
    private readonly LuaFfiSignature _signature;
    private readonly LuaFfiNativeInvoker _invoker;

    public LuaFfiNativeCall(
        LuaFfiContext context,
        LuaFfiLibraryHandle library,
        string symbolName,
        LuaFfiSignature signature,
        LuaFfiNativeInvoker invoker)
    {
        _context = context;
        _library = library;
        _symbolName = symbolName;
        _signature = signature;
        _invoker = invoker;
    }

    public LuaValue[] Invoke(LuaState state, ReadOnlySpan<LuaValue> values)
    {
        try
        {
            _context.EnsureInvocationState(state);
        }
        catch (LuaFfiException exception)
        {
            throw LuaFfiContext.ToLuaException(exception);
        }

        if (_library.IsClosed)
        {
            throw new LuaRuntimeException(
                $"ffi {LuaFfiErrorCode.LibraryClosed}: native library '{_library.Name}' is closed.");
        }

        if (values.Length != _signature.ParameterTypes.Length)
        {
            throw new LuaRuntimeException(
                $"ffi {LuaFfiErrorCode.InvalidArgument}: native '{_symbolName}' expects " +
                $"{_signature.ParameterTypes.Length} argument(s).");
        }

        var arguments = new object?[values.Length];
        var temporary = new List<(IntPtr Address, int Size)>();
        try
        {
            for (var index = 0; index < arguments.Length; index++)
            {
                arguments[index] = _context.ConvertArgument(
                    state,
                    values[index],
                    _signature.ParameterTypes[index],
                    temporary);
            }

            object? result;
            try
            {
                result = _invoker(arguments);
            }
            catch (LuaFfiException)
            {
                throw;
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw new LuaFfiException(
                    LuaFfiErrorCode.NativeInvocationFailed,
                    $"Native invocation '{_library.Name}!{_symbolName}' failed.",
                    exception.InnerException);
            }
            catch (Exception exception)
            {
                throw new LuaFfiException(
                    LuaFfiErrorCode.NativeInvocationFailed,
                    $"Native invocation '{_library.Name}!{_symbolName}' failed.",
                    exception);
            }

            var converted = _context.ConvertReturn(
                state,
                _signature.ReturnType,
                result,
                _library);
            return _signature.ReturnType == LuaFfiNativeType.Void ? [] : [converted];
        }
        catch (LuaFfiException exception)
        {
            throw new LuaRuntimeException($"ffi {exception.Code}: {exception.Message}");
        }
        catch (OverflowException exception)
        {
            throw new LuaRuntimeException(
                $"ffi {LuaFfiErrorCode.RangeExceeded}: a native FFI value is outside its supported range.",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new LuaRuntimeException(
                $"ffi {LuaFfiErrorCode.InvalidArgument}: a native FFI value has an invalid representation.",
                exception);
        }
        catch (InvalidCastException exception)
        {
            throw new LuaRuntimeException(
                $"ffi {LuaFfiErrorCode.InvalidArgument}: a native FFI value has an invalid representation.",
                exception);
        }
        catch (FormatException exception)
        {
            throw new LuaRuntimeException(
                $"ffi {LuaFfiErrorCode.InvalidArgument}: a native FFI value has an invalid representation.",
                exception);
        }
        finally
        {
            foreach (var (address, size) in temporary)
            {
                Marshal.FreeHGlobal(address);
                _context.Release(size);
            }
        }
    }
}

internal sealed class LuaFfiBuffer : IDisposable
{
    private readonly LuaFfiContext _context;
    private readonly IntPtr _address;
    private int _disposed;

    public LuaFfiBuffer(LuaFfiContext context, int length)
    {
        _context = context;
        Length = length;
        _context.Reserve(length);
        try
        {
            _address = Marshal.AllocHGlobal(length);
            var zero = new byte[length];
            Marshal.Copy(zero, 0, _address, length);
        }
        catch
        {
            _context.Release(length);
            throw;
        }
    }

    public int Length { get; }

    public bool IsClosed => Volatile.Read(ref _disposed) != 0;

    internal IntPtr Address => IsClosed
        ? throw new LuaFfiException(LuaFfiErrorCode.BufferClosed, "The native buffer is closed.")
        : _address;

    public LuaValue Read(LuaState state, int offset, LuaFfiNativeType type)
    {
        if (type == LuaFfiNativeType.Utf8String)
        {
            EnsureRange(offset, 0);
            var maximumBytes = Math.Min(_context.MaximumStringBytes, Length - offset);
            return LuaValue.FromString(state.Strings.GetOrCreate(
                LuaFfiContext.ReadNullTerminated(IntPtr.Add(Address, offset), maximumBytes)));
        }

        var size = LuaFfiContext.SizeOf(type);
        EnsureRange(offset, size);
        return type switch
        {
            LuaFfiNativeType.Boolean => LuaValue.FromBoolean(Marshal.ReadByte(Address, offset) != 0),
            LuaFfiNativeType.Int8 => LuaValue.FromInteger((sbyte)Marshal.ReadByte(Address, offset)),
            LuaFfiNativeType.UInt8 => LuaValue.FromInteger(Marshal.ReadByte(Address, offset)),
            LuaFfiNativeType.Int16 => LuaValue.FromInteger(Marshal.ReadInt16(Address, offset)),
            LuaFfiNativeType.UInt16 => LuaValue.FromInteger((ushort)Marshal.ReadInt16(Address, offset)),
            LuaFfiNativeType.Int32 => LuaValue.FromInteger(Marshal.ReadInt32(Address, offset)),
            LuaFfiNativeType.UInt32 => LuaValue.FromInteger((uint)Marshal.ReadInt32(Address, offset)),
            LuaFfiNativeType.Int64 => LuaValue.FromInteger(Marshal.ReadInt64(Address, offset)),
            LuaFfiNativeType.UInt64 => ReadUInt64(offset),
            LuaFfiNativeType.IntPtr => LuaValue.FromInteger(Marshal.ReadIntPtr(Address, offset).ToInt64()),
            LuaFfiNativeType.UIntPtr => ReadUIntPtr(offset),
            LuaFfiNativeType.Pointer => PointerValue(state, Marshal.ReadIntPtr(Address, offset)),
            LuaFfiNativeType.Float => LuaValue.FromFloat(BitConverter.ToSingle(ReadBytes(offset, 4), 0)),
            LuaFfiNativeType.Double => LuaValue.FromFloat(BitConverter.ToDouble(ReadBytes(offset, 8), 0)),
            _ => throw new LuaFfiException(
                LuaFfiErrorCode.UnsupportedSignature,
                $"Native storage type '{type}' is not supported."),
        };
    }

    public void Write(
        LuaState state,
        int offset,
        LuaFfiNativeType type,
        LuaValue value)
    {
        if (type == LuaFfiNativeType.Utf8String)
        {
            var bytes = value.Kind == LuaValueKind.String
                ? value.AsString().ToArray()
                : throw new LuaFfiException(
                    LuaFfiErrorCode.InvalidArgument,
                    "A UTF-8 string is required for native buffer write.");
            if (bytes.Length > _context.MaximumStringBytes)
            {
                throw new LuaFfiException(
                    LuaFfiErrorCode.RangeExceeded,
                    "The UTF-8 string exceeds the configured length limit.");
            }

            EnsureRange(offset, checked(bytes.Length + 1));
            Marshal.Copy(bytes, 0, IntPtr.Add(Address, offset), bytes.Length);
            Marshal.WriteByte(Address, offset + bytes.Length, 0);
            return;
        }

        if (type == LuaFfiNativeType.Pointer)
        {
            EnsureRange(offset, IntPtr.Size);
            Marshal.WriteIntPtr(Address, offset, LuaFfiContext.ConvertPointer(value, type));
            return;
        }

        if (type == LuaFfiNativeType.IntPtr)
        {
            var integer = ConvertInteger(value, type);
            EnsureRange(offset, IntPtr.Size);
            if (IntPtr.Size == 4)
            {
                Marshal.WriteInt32(Address, offset, checked((int)integer));
            }
            else
            {
                Marshal.WriteInt64(Address, offset, integer);
            }

            return;
        }

        if (type == LuaFfiNativeType.UIntPtr)
        {
            var unsigned = checked((ulong)ConvertInteger(value, type));
            EnsureRange(offset, UIntPtr.Size);
            if (UIntPtr.Size == 4)
            {
                Marshal.WriteInt32(
                    Address,
                    offset,
                    unchecked((int)checked((uint)unsigned)));
            }
            else
            {
                Marshal.WriteInt64(Address, offset, unchecked((long)unsigned));
            }

            return;
        }

        var size = LuaFfiContext.SizeOf(type);
        EnsureRange(offset, size);
        var bytesToWrite = type switch
        {
            LuaFfiNativeType.Boolean => new[] { ConvertBoolean(value, type) },
            LuaFfiNativeType.Int8 => new[] { unchecked((byte)checked((sbyte)ConvertInteger(value, type))) },
            LuaFfiNativeType.UInt8 => new[] { checked((byte)ConvertInteger(value, type)) },
            LuaFfiNativeType.Int16 => BitConverter.GetBytes(checked((short)ConvertInteger(value, type))),
            LuaFfiNativeType.UInt16 => BitConverter.GetBytes(checked((ushort)ConvertInteger(value, type))),
            LuaFfiNativeType.Int32 => BitConverter.GetBytes(checked((int)ConvertInteger(value, type))),
            LuaFfiNativeType.UInt32 => BitConverter.GetBytes(checked((uint)ConvertInteger(value, type))),
            LuaFfiNativeType.Int64 => BitConverter.GetBytes(ConvertInteger(value, type)),
            LuaFfiNativeType.UInt64 => BitConverter.GetBytes(checked((ulong)ConvertInteger(value, type))),
            LuaFfiNativeType.Float => BitConverter.GetBytes(checked((float)ConvertNumber(value, type))),
            LuaFfiNativeType.Double => BitConverter.GetBytes(ConvertNumber(value, type)),
            _ => throw new LuaFfiException(
                LuaFfiErrorCode.UnsupportedSignature,
                $"Native storage type '{type}' is not supported."),
        };
        Marshal.Copy(bytesToWrite, 0, IntPtr.Add(Address, offset), bytesToWrite.Length);
    }

    private LuaValue ReadUInt64(int offset)
    {
        var value = unchecked((ulong)Marshal.ReadInt64(Address, offset));
        return value > long.MaxValue
            ? throw new LuaFfiException(
                LuaFfiErrorCode.RangeExceeded,
                "The native unsigned value cannot be represented by a Lua integer.")
            : LuaValue.FromInteger((long)value);
    }

    private LuaValue ReadUIntPtr(int offset)
    {
        var value = IntPtr.Size == 4
            ? unchecked((uint)Marshal.ReadInt32(Address, offset))
            : unchecked((ulong)Marshal.ReadInt64(Address, offset));
        return value > long.MaxValue
            ? throw new LuaFfiException(
                LuaFfiErrorCode.RangeExceeded,
                "The native unsigned value cannot be represented by a Lua integer.")
            : LuaValue.FromInteger((long)value);
    }

    private byte[] ReadBytes(int offset, int count)
    {
        var bytes = new byte[count];
        Marshal.Copy(IntPtr.Add(Address, offset), bytes, 0, count);
        return bytes;
    }

    private void EnsureRange(int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > Length - count)
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.RangeExceeded,
                "The native buffer access is outside its allocated range.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Marshal.FreeHGlobal(_address);
            _context.Release(Length);
        }
    }

    private static LuaValue PointerValue(LuaState state, IntPtr address) =>
        address == IntPtr.Zero
            ? LuaValue.Nil
            : LuaValue.FromUserdata(state.CreateUserdata(new LuaFfiPointer(address), 1, 32));

    private static long ConvertInteger(LuaValue value, LuaFfiNativeType type) =>
        value.TryGetInteger(out var integer)
            ? integer
            : throw new LuaFfiException(
                LuaFfiErrorCode.InvalidArgument,
                $"Native {type} values require an exact Lua integer.");

    private static byte ConvertBoolean(LuaValue value, LuaFfiNativeType type) =>
        value.Kind == LuaValueKind.Boolean
            ? (byte)(value.AsBoolean() ? 1 : 0)
            : ConvertInteger(value, type) == 0 ? (byte)0 : (byte)1;

    private static double ConvertNumber(LuaValue value, LuaFfiNativeType type) =>
        value.Kind switch
        {
            LuaValueKind.Integer => value.AsInteger(),
            LuaValueKind.Float => value.AsFloat(),
            _ => throw new LuaFfiException(
                LuaFfiErrorCode.InvalidArgument,
                $"Native {type} values require a Lua number."),
        };
}

internal sealed class LuaFfiPointer : IDisposable
{
    private readonly IntPtr _address;
    private readonly LuaFfiLibraryLease? _libraryLease;
    private int _disposed;

    public LuaFfiPointer(IntPtr address, LuaFfiLibraryLease? libraryLease = null)
    {
        _address = address;
        _libraryLease = libraryLease;
    }

    public bool IsClosed => Volatile.Read(ref _disposed) != 0;

    public IntPtr Address
    {
        get
        {
            if (IsClosed)
            {
                throw new LuaFfiException(
                    LuaFfiErrorCode.InvalidArgument,
                    "The native pointer is closed.");
            }

            if (_libraryLease?.IsClosed == true)
            {
                throw new LuaFfiException(
                    LuaFfiErrorCode.LibraryClosed,
                    "The native pointer's library is closed.");
            }

            return _address;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _libraryLease?.Dispose();
        }
    }
}
