using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Lunil.Core;

namespace Lunil.StandardLibrary;

/// <summary>Stable error categories reported by the opt-in native FFI boundary.</summary>
#pragma warning disable CA1720
public enum LuaFfiErrorCode : byte
{
    Disabled,
    InvalidName,
    LibraryNotAllowed,
    LibraryLoadFailed,
    LibraryClosed,
    SymbolNotAllowed,
    SymbolNotFound,
    InvalidSignature,
    UnsupportedSignature,
    InvalidArgument,
    RangeExceeded,
    NativeInvocationFailed,
    BufferClosed,
    AllocationLimitExceeded,
    ResourceLimitExceeded,
    DynamicCodeUnavailable,
    BindingConflict,
}

/// <summary>An exception with a stable native FFI error category.</summary>
public sealed class LuaFfiException : Exception
{
    public LuaFfiException(LuaFfiErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public LuaFfiException(LuaFfiErrorCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Gets the stable FFI error category.</summary>
    public LuaFfiErrorCode Code { get; }
}

/// <summary>Calling conventions admitted by the C ABI FFI surface.</summary>
public enum LuaFfiCallingConvention : byte
{
    /// <summary>Use the platform's default C ABI convention.</summary>
    PlatformDefault,

    /// <summary>Use the C declaration calling convention.</summary>
    Cdecl,

    /// <summary>Use the Windows stdcall convention where the runtime supports it.</summary>
    Stdcall,
}

/// <summary>Scalar and opaque boundary types supported by a native FFI signature.</summary>
public enum LuaFfiNativeType : byte
{
    Void,
    Boolean,
    Int8,
    UInt8,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    IntPtr,
    UIntPtr,
    Float,
    Double,
    Utf8String,
    Pointer,
}
#pragma warning restore CA1720

/// <summary>Immutable, validated signature for one non-variadic native function.</summary>
public sealed class LuaFfiSignature : IEquatable<LuaFfiSignature>
{
    public LuaFfiSignature(
        LuaFfiNativeType returnType,
        ImmutableArray<LuaFfiNativeType> parameterTypes,
        LuaFfiCallingConvention callingConvention = LuaFfiCallingConvention.PlatformDefault)
    {
        if (!LunilEnum.IsDefined(returnType))
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.InvalidSignature,
                "The native return type is not supported.");
        }

        if (parameterTypes.IsDefault)
        {
            parameterTypes = [];
        }

        if (parameterTypes.Any(type => !LunilEnum.IsDefined(type) || type == LuaFfiNativeType.Void))
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.InvalidSignature,
                "Native parameter types must be supported non-void types.");
        }

        if (!LunilEnum.IsDefined(callingConvention))
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.InvalidSignature,
                "The native calling convention is not supported.");
        }

        ReturnType = returnType;
        ParameterTypes = parameterTypes;
        CallingConvention = callingConvention;
    }

    public LuaFfiNativeType ReturnType { get; }

    public ImmutableArray<LuaFfiNativeType> ParameterTypes { get; }

    public LuaFfiCallingConvention CallingConvention { get; }

    /// <summary>
    /// Parses a compact declaration such as i32(i32, cstring). Platform-ambiguous C types
    /// (long, unsigned long) and C ABI varargs are intentionally not supported; use the
    /// pointer-sized aliases intptr_t, uintptr_t, or size_t instead.
    /// </summary>
    public static LuaFfiSignature Parse(
        string declaration,
        LuaFfiCallingConvention callingConvention = LuaFfiCallingConvention.PlatformDefault)
    {
        if (string.IsNullOrWhiteSpace(declaration))
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.InvalidSignature,
                "A native signature is required.");
        }

        declaration = declaration.Trim();

        var open = declaration.IndexOf('(');
        var close = declaration.LastIndexOf(')');
        if (open <= 0 || close != declaration.Length - 1 || close < open)
        {
            throw new LuaFfiException(
                LuaFfiErrorCode.InvalidSignature,
                "A native signature must have the form returnType(parameterTypes).");
        }

        var returnType = ParseType(declaration[..open]);
        var parameterText = declaration[(open + 1)..close].Trim();
        var parameters = parameterText.Length == 0
            ? ImmutableArray<LuaFfiNativeType>.Empty
            : parameterText
                .Split(',')
                .Select(static value => value.Trim())
                .Select(ParseType)
                .ToImmutableArray();
        return new LuaFfiSignature(returnType, parameters, callingConvention);
    }

    public bool Equals(LuaFfiSignature? other) => other is not null &&
        ReturnType == other.ReturnType &&
        CallingConvention == other.CallingConvention &&
        ParameterTypes.SequenceEqual(other.ParameterTypes);

    public override bool Equals(object? obj) => obj is LuaFfiSignature other && Equals(other);

    public override int GetHashCode()
    {
        var hash = HashCode.Combine(ReturnType, CallingConvention);
        foreach (var type in ParameterTypes)
        {
            hash = HashCode.Combine(hash, type);
        }

        return hash;
    }

    public override string ToString() =>
        $"{FormatType(ReturnType)}({string.Join(", ", ParameterTypes.Select(FormatType))})";

    private static LuaFfiNativeType ParseType(string text)
    {
        var normalized = text.Trim().ToLowerInvariant()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("const", string.Empty, StringComparison.Ordinal);
        normalized = normalized switch
        {
            "void*" or "ptr" or "pointer" => "pointer",
            "char*" or "constchar*" or "cstring" or "utf8" or "utf8string" => "cstring",
            "_bool" or "bool" or "bool8" => "bool",
            "signedchar" or "int8" or "int8_t" or "i8" => "i8",
            "unsignedchar" or "uint8" or "uint8_t" or "u8" => "u8",
            "short" or "int16" or "int16_t" or "i16" => "i16",
            "unsignedshort" or "uint16" or "uint16_t" or "u16" => "u16",
            "int" or "int32" or "int32_t" or "i32" => "i32",
            "unsigned" or "unsignedint" or "uint32" or "uint32_t" or "u32" => "u32",
            "longlong" or "int64" or "int64_t" or "i64" => "i64",
            "unsignedlonglong" or "uint64" or "uint64_t" or "u64" => "u64",
            "intptr" or "intptr_t" or "isize" => "isize",
            "uintptr" or "uintptr_t" or "size_t" or "usize" => "usize",
            "float" or "f32" => "f32",
            "double" or "f64" => "f64",
            _ => normalized,
        };

        return normalized switch
        {
            "void" => LuaFfiNativeType.Void,
            "bool" => LuaFfiNativeType.Boolean,
            "i8" => LuaFfiNativeType.Int8,
            "u8" => LuaFfiNativeType.UInt8,
            "i16" => LuaFfiNativeType.Int16,
            "u16" => LuaFfiNativeType.UInt16,
            "i32" => LuaFfiNativeType.Int32,
            "u32" => LuaFfiNativeType.UInt32,
            "i64" => LuaFfiNativeType.Int64,
            "u64" => LuaFfiNativeType.UInt64,
            "isize" => LuaFfiNativeType.IntPtr,
            "usize" => LuaFfiNativeType.UIntPtr,
            "f32" => LuaFfiNativeType.Float,
            "f64" => LuaFfiNativeType.Double,
            "cstring" => LuaFfiNativeType.Utf8String,
            "pointer" => LuaFfiNativeType.Pointer,
            _ => throw new LuaFfiException(
                LuaFfiErrorCode.InvalidSignature,
                $"Native type '{text}' is not supported."),
        };
    }

    private static string FormatType(LuaFfiNativeType type) => type switch
    {
        LuaFfiNativeType.Void => "void",
        LuaFfiNativeType.Boolean => "bool",
        LuaFfiNativeType.Int8 => "i8",
        LuaFfiNativeType.UInt8 => "u8",
        LuaFfiNativeType.Int16 => "i16",
        LuaFfiNativeType.UInt16 => "u16",
        LuaFfiNativeType.Int32 => "i32",
        LuaFfiNativeType.UInt32 => "u32",
        LuaFfiNativeType.Int64 => "i64",
        LuaFfiNativeType.UInt64 => "u64",
        LuaFfiNativeType.IntPtr => "isize",
        LuaFfiNativeType.UIntPtr => "usize",
        LuaFfiNativeType.Float => "f32",
        LuaFfiNativeType.Double => "f64",
        LuaFfiNativeType.Utf8String => "cstring",
        LuaFfiNativeType.Pointer => "pointer",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}

/// <summary>Invokes one already-adapted native binding without reflection.</summary>
public delegate object? LuaFfiNativeInvoker(ReadOnlySpan<object?> arguments);

/// <summary>One exact, host-registered native binding for AOT and trimmed hosts.</summary>
public sealed record LuaFfiNativeBinding(
    string LibraryName,
    string SymbolName,
    LuaFfiSignature Signature,
    LuaFfiNativeInvoker Invoker);

/// <summary>Registry for exact native bindings that do not require runtime delegate generation.</summary>
public sealed class LuaFfiBindingRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LuaFfiNativeBinding> _bindings =
        new(StringComparer.Ordinal);

    /// <summary>Registers one exact library/symbol/signature tuple.</summary>
    public void Register(LuaFfiNativeBinding binding)
    {
        LunilGuard.NotNull(binding);
        if (string.IsNullOrWhiteSpace(binding.LibraryName) ||
            string.IsNullOrWhiteSpace(binding.SymbolName) ||
            binding.LibraryName.Contains('\0') ||
            binding.SymbolName.Contains('\0') ||
            binding.Signature is null ||
            binding.Invoker is null)
        {
            throw new ArgumentException(
                "Native binding names, signature, and invoker are required and cannot contain NUL characters.",
                nameof(binding));
        }

        var key = Key(binding.LibraryName, binding.SymbolName);
        lock (_gate)
        {
            if (_bindings.TryGetValue(key, out var existing) && !ReferenceEquals(existing, binding))
            {
                throw new LuaFfiException(
                    LuaFfiErrorCode.BindingConflict,
                    $"Native binding '{binding.LibraryName}!{binding.SymbolName}' is already registered.");
            }

            _bindings[key] = binding;
        }
    }

    /// <summary>Registers an exact binding using a compact signature declaration.</summary>
    public void Register(
        string libraryName,
        string symbolName,
        string signature,
        LuaFfiNativeInvoker invoker,
        LuaFfiCallingConvention callingConvention = LuaFfiCallingConvention.PlatformDefault) =>
        Register(new LuaFfiNativeBinding(
            libraryName,
            symbolName,
            LuaFfiSignature.Parse(signature, callingConvention),
            invoker ?? throw new ArgumentNullException(nameof(invoker))));

    /// <summary>Gets a deterministic snapshot of all registered bindings.</summary>
    public ImmutableArray<LuaFfiNativeBinding> GetBindings()
    {
        lock (_gate)
        {
            return _bindings.Values
                .OrderBy(static binding => binding.LibraryName, StringComparer.Ordinal)
                .ThenBy(static binding => binding.SymbolName, StringComparer.Ordinal)
                .ToImmutableArray();
        }
    }

    /// <summary>Attempts to retrieve an exact binding.</summary>
    internal bool TryGet(string libraryName, string symbolName, out LuaFfiNativeBinding? binding)
    {
        lock (_gate)
        {
            return _bindings.TryGetValue(Key(libraryName, symbolName), out binding);
        }
    }

    private static string Key(string libraryName, string symbolName) => libraryName + "\0" + symbolName;
}

/// <summary>Host abstraction for loading libraries and resolving exported symbols.</summary>
public interface ILuaFfiLibraryLoader
{
    IntPtr Load(string libraryName);

    IntPtr GetExport(IntPtr libraryHandle, string symbolName);

    void Free(IntPtr libraryHandle);
}

/// <summary>System loader backed by NativeLibrary.</summary>
public sealed class SystemLuaFfiLibraryLoader : ILuaFfiLibraryLoader
{
    public static SystemLuaFfiLibraryLoader Instance { get; } = new();

    private SystemLuaFfiLibraryLoader()
    {
    }

#pragma warning disable CA2101
#if NET10_0_OR_GREATER
    public IntPtr Load(string libraryName) => NativeLibrary.Load(libraryName);

    public IntPtr GetExport(IntPtr libraryHandle, string symbolName) =>
        NativeLibrary.GetExport(libraryHandle, symbolName);

    public void Free(IntPtr libraryHandle)
    {
        if (libraryHandle != IntPtr.Zero)
        {
            NativeLibrary.Free(libraryHandle);
        }
    }
#else
    public IntPtr Load(string libraryName)
    {
        var handle = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? LoadLibrary(libraryName)
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? MacDlopen(libraryName, RtldNow)
                : UnixDlopenWithFallback(libraryName, RtldNow);
        return handle == IntPtr.Zero
            ? throw new DllNotFoundException(
                $"Native library '{libraryName}' could not be loaded: {DescribeLoaderError()}")
            : handle;
    }

    public IntPtr GetExport(IntPtr libraryHandle, string symbolName)
    {
        var address = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? GetProcAddress(libraryHandle, symbolName)
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? MacDlsym(libraryHandle, symbolName)
                : UnixDlsym(libraryHandle, symbolName);
        return address == IntPtr.Zero
            ? throw new EntryPointNotFoundException(
                $"Native symbol '{symbolName}' was not found: {DescribeLoaderError()}")
            : address;
    }

    public void Free(IntPtr libraryHandle)
    {
        if (libraryHandle == IntPtr.Zero)
        {
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _ = FreeLibrary(libraryHandle);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            _ = MacDlclose(libraryHandle);
        }
        else
        {
            _ = UnixDlclose(libraryHandle);
        }
    }

    private const int RtldNow = 2;

    private static IntPtr UnixDlopenWithFallback(string name, int flags)
    {
        try
        {
            return UnixDlopen(name, flags);
        }
        catch (DllNotFoundException)
        {
            // musl 系发行版（如 Alpine）把 dl 接口并入 libc，只保留 libdl.so 兼容名；
            // glibc 发行版以 libdl.so.2 为主，因此先主后备对两类系统都成立。
            return UnixDlopenCompat(name, flags);
        }
    }

    private static string DescribeLoaderError()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }

        var message = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? Marshal.PtrToStringAnsi(MacDlerror())
            : Marshal.PtrToStringAnsi(UnixDlerrorWithFallback());
        return string.IsNullOrEmpty(message) ? "unknown native loader error" : message;
    }

    private static IntPtr UnixDlerrorWithFallback()
    {
        try
        {
            return UnixDlerror();
        }
        catch (DllNotFoundException)
        {
            return UnixDlerrorCompat();
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr LoadLibrary(
        [MarshalAs(UnmanagedType.LPWStr)] string name);

    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", SetLastError = true)]
    private static extern IntPtr GetProcAddress(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport("kernel32.dll", EntryPoint = "FreeLibrary", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr handle);

    [DllImport("libdl.so.2", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
    private static extern IntPtr UnixDlopen(
        [MarshalAs(UnmanagedType.LPStr)] string name,
        int flags);

    [DllImport("libdl.so.2", EntryPoint = "dlsym", CharSet = CharSet.Ansi)]
    private static extern IntPtr UnixDlsym(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport("libdl.so.2", EntryPoint = "dlclose")]
    private static extern int UnixDlclose(IntPtr handle);

    [DllImport("libdl.so.2", EntryPoint = "dlerror", CharSet = CharSet.Ansi)]
    private static extern IntPtr UnixDlerror();

    [DllImport("libdl.so", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
    private static extern IntPtr UnixDlopenCompat(
        [MarshalAs(UnmanagedType.LPStr)] string name,
        int flags);

    [DllImport("libdl.so", EntryPoint = "dlerror", CharSet = CharSet.Ansi)]
    private static extern IntPtr UnixDlerrorCompat();

    [DllImport("libSystem.B.dylib", EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
    private static extern IntPtr MacDlopen(
        [MarshalAs(UnmanagedType.LPStr)] string name,
        int flags);

    [DllImport("libSystem.B.dylib", EntryPoint = "dlsym", CharSet = CharSet.Ansi)]
    private static extern IntPtr MacDlsym(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport("libSystem.B.dylib", EntryPoint = "dlclose")]
    private static extern int MacDlclose(IntPtr handle);

    [DllImport("libSystem.B.dylib", EntryPoint = "dlerror", CharSet = CharSet.Ansi)]
    private static extern IntPtr MacDlerror();
#endif
#pragma warning restore CA2101
}

/// <summary>Options and limits for the disabled-by-default native FFI surface.</summary>
public sealed record LuaFfiOptions
{
    public static LuaFfiOptions Disabled { get; } = new();

    /// <summary>Gets whether the host grants native FFI access.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets exact library identities accepted by ffi.load.</summary>
    public ImmutableArray<string> AllowedLibraryNames { get; init; } = [];

    /// <summary>
    /// Gets exact symbol entries in the form libraryName!symbolName accepted by ffi.bind.
    /// </summary>
    public ImmutableArray<string> AllowedSymbolNames { get; init; } = [];

    /// <summary>Gets optional AOT/trim-safe exact native bindings.</summary>
    public LuaFfiBindingRegistry? BindingRegistry { get; init; }

    /// <summary>Gets the host-controlled native library loader.</summary>
    public ILuaFfiLibraryLoader LibraryLoader { get; init; } = SystemLuaFfiLibraryLoader.Instance;

    public int MaximumOpenLibraries { get; init; } = 32;

    public int MaximumSignatureLength { get; init; } = 256;

    public int MaximumArgumentCount { get; init; } = 8;

    public int MaximumStringBytes { get; init; } = 1024 * 1024;

    public long MaximumAllocationBytes { get; init; } = 16 * 1024 * 1024;

    public int MaximumBufferBytes { get; init; } = 16 * 1024 * 1024;

    public LuaFfiCallingConvention DefaultCallingConvention { get; init; } =
        LuaFfiCallingConvention.PlatformDefault;
}
