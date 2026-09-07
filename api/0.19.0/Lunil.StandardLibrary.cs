// Target Frameworks: net10.0, netstandard2.1
#nullable enable

namespace Lunil.StandardLibrary
{
    public interface ILuaConsole
    {
        byte[] ReadStandardInput();
        void Write(System.ReadOnlyMemory<byte> bytes);
        void WriteLine();
        public System.IO.Stream OpenStandardInput() => throw null;
        public System.IO.Stream OpenStandardOutput() => throw null;
        public System.IO.Stream OpenStandardError() => throw null;
        public void WriteError(System.ReadOnlyMemory<byte> bytes) { }
    }

    public interface ILuaEnvironment
    {
        string? GetEnvironmentVariable(string name);
    }

    public interface ILuaFfiLibraryLoader
    {
        nint Load(string libraryName);
        nint GetExport(nint libraryHandle, string symbolName);
        void Free(nint libraryHandle);
    }

    public interface ILuaFileSystem
    {
        byte[] ReadAllBytes(string path);
        public bool FileExists(string path) => throw null;
        public System.IO.Stream Open(string path, Lunil.StandardLibrary.LuaFileMode mode) => throw null;
        public System.IO.Stream OpenTemporary(out string? path) => throw null;
        public string CreateTemporaryName() => throw null;
        public void Delete(string path) { }
        public void Move(string source, string destination) { }
    }

    public interface ILuaOperatingSystem
    {
        double Clock { get; }
        System.DateTimeOffset Now { get; }
        System.TimeZoneInfo LocalTimeZone { get; }
        Lunil.StandardLibrary.LuaExecuteResult Execute(string? command);
        System.IO.Stream OpenPipe(string command, bool read, out Lunil.StandardLibrary.ILuaPipeProcess process);
        void Terminate(int status, bool closeState);
        string? SetLocale(string? locale, string category);
    }

    public interface ILuaPipeProcess : System.IDisposable
    {
        Lunil.StandardLibrary.LuaExecuteResult Wait();
    }

    public readonly struct LuaExecuteResult : System.IEquatable<Lunil.StandardLibrary.LuaExecuteResult>
    {
        public bool Started { get => throw null; init { } }
        public string Kind { get => throw null; init { } }
        public int Status { get => throw null; init { } }
        public LuaExecuteResult(bool Started, string Kind, int Status) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.StandardLibrary.LuaExecuteResult left, Lunil.StandardLibrary.LuaExecuteResult right) => throw null;
        public static bool operator ==(Lunil.StandardLibrary.LuaExecuteResult left, Lunil.StandardLibrary.LuaExecuteResult right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object obj) => throw null;
        public bool Equals(Lunil.StandardLibrary.LuaExecuteResult other) => throw null;
        public void Deconstruct(out bool Started, out string Kind, out int Status) => throw null;
    }

    public sealed class LuaFfiBindingRegistry
    {
        public void Register(Lunil.StandardLibrary.LuaFfiNativeBinding binding) { }
        public void Register(string libraryName, string symbolName, string signature, Lunil.StandardLibrary.LuaFfiNativeInvoker invoker, Lunil.StandardLibrary.LuaFfiCallingConvention callingConvention = 0) { }
        public System.Collections.Immutable.ImmutableArray<Lunil.StandardLibrary.LuaFfiNativeBinding> GetBindings() => throw null;
    }

    public enum LuaFfiCallingConvention
    {
        PlatformDefault = 0,
        Cdecl = 1,
        Stdcall = 2
    }

    public enum LuaFfiErrorCode
    {
        Disabled = 0,
        InvalidName = 1,
        LibraryNotAllowed = 2,
        LibraryLoadFailed = 3,
        LibraryClosed = 4,
        SymbolNotAllowed = 5,
        SymbolNotFound = 6,
        InvalidSignature = 7,
        UnsupportedSignature = 8,
        InvalidArgument = 9,
        RangeExceeded = 10,
        NativeInvocationFailed = 11,
        BufferClosed = 12,
        AllocationLimitExceeded = 13,
        ResourceLimitExceeded = 14,
        DynamicCodeUnavailable = 15,
        BindingConflict = 16
    }

    public sealed class LuaFfiException : System.Exception
    {
        public Lunil.StandardLibrary.LuaFfiErrorCode Code { get => throw null; }
        public LuaFfiException(Lunil.StandardLibrary.LuaFfiErrorCode code, string message) { }
        public LuaFfiException(Lunil.StandardLibrary.LuaFfiErrorCode code, string message, System.Exception innerException) { }
    }

    public sealed class LuaFfiNativeBinding : System.IEquatable<Lunil.StandardLibrary.LuaFfiNativeBinding>
    {
        public string LibraryName { get => throw null; init { } }
        public string SymbolName { get => throw null; init { } }
        public Lunil.StandardLibrary.LuaFfiSignature Signature { get => throw null; init { } }
        public Lunil.StandardLibrary.LuaFfiNativeInvoker Invoker { get => throw null; init { } }
        public LuaFfiNativeBinding(string LibraryName, string SymbolName, Lunil.StandardLibrary.LuaFfiSignature Signature, Lunil.StandardLibrary.LuaFfiNativeInvoker Invoker) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.StandardLibrary.LuaFfiNativeBinding? left, Lunil.StandardLibrary.LuaFfiNativeBinding? right) => throw null;
        public static bool operator ==(Lunil.StandardLibrary.LuaFfiNativeBinding? left, Lunil.StandardLibrary.LuaFfiNativeBinding? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.StandardLibrary.LuaFfiNativeBinding? other) => throw null;
        public void Deconstruct(out string LibraryName, out string SymbolName, out Lunil.StandardLibrary.LuaFfiSignature Signature, out Lunil.StandardLibrary.LuaFfiNativeInvoker Invoker) => throw null;
    }

    public delegate object? LuaFfiNativeInvoker(System.ReadOnlySpan<object?> arguments);

    public enum LuaFfiNativeType
    {
        Void = 0,
        Boolean = 1,
        Int8 = 2,
        UInt8 = 3,
        Int16 = 4,
        UInt16 = 5,
        Int32 = 6,
        UInt32 = 7,
        Int64 = 8,
        UInt64 = 9,
        IntPtr = 10,
        UIntPtr = 11,
        Float = 12,
        Double = 13,
        Utf8String = 14,
        Pointer = 15
    }

    public sealed class LuaFfiOptions : System.IEquatable<Lunil.StandardLibrary.LuaFfiOptions>
    {
        public static Lunil.StandardLibrary.LuaFfiOptions Disabled { get => throw null; }
        public bool Enabled { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<string> AllowedLibraryNames { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<string> AllowedSymbolNames { get => throw null; init { } }
        public Lunil.StandardLibrary.LuaFfiBindingRegistry? BindingRegistry { get => throw null; init { } }
        public Lunil.StandardLibrary.ILuaFfiLibraryLoader LibraryLoader { get => throw null; init { } }
        public int MaximumOpenLibraries { get => throw null; init { } }
        public int MaximumSignatureLength { get => throw null; init { } }
        public int MaximumArgumentCount { get => throw null; init { } }
        public int MaximumStringBytes { get => throw null; init { } }
        public long MaximumAllocationBytes { get => throw null; init { } }
        public int MaximumBufferBytes { get => throw null; init { } }
        public Lunil.StandardLibrary.LuaFfiCallingConvention DefaultCallingConvention { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.StandardLibrary.LuaFfiOptions? left, Lunil.StandardLibrary.LuaFfiOptions? right) => throw null;
        public static bool operator ==(Lunil.StandardLibrary.LuaFfiOptions? left, Lunil.StandardLibrary.LuaFfiOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.StandardLibrary.LuaFfiOptions? other) => throw null;
    }

    public sealed class LuaFfiSignature : System.IEquatable<Lunil.StandardLibrary.LuaFfiSignature>
    {
        public Lunil.StandardLibrary.LuaFfiNativeType ReturnType { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.StandardLibrary.LuaFfiNativeType> ParameterTypes { get => throw null; }
        public Lunil.StandardLibrary.LuaFfiCallingConvention CallingConvention { get => throw null; }
        public LuaFfiSignature(Lunil.StandardLibrary.LuaFfiNativeType returnType, System.Collections.Immutable.ImmutableArray<Lunil.StandardLibrary.LuaFfiNativeType> parameterTypes, Lunil.StandardLibrary.LuaFfiCallingConvention callingConvention = 0) { }
        public static Lunil.StandardLibrary.LuaFfiSignature Parse(string declaration, Lunil.StandardLibrary.LuaFfiCallingConvention callingConvention = 0) => throw null;
        public bool Equals(Lunil.StandardLibrary.LuaFfiSignature? other) => throw null;
        public override bool Equals(object? obj) => throw null;
        public override int GetHashCode() => throw null;
        public override string ToString() => throw null;
    }

    public enum LuaFileMode
    {
        Read = 0,
        Write = 1,
        Append = 2,
        ReadUpdate = 3,
        WriteUpdate = 4,
        AppendUpdate = 5
    }

    public static class LuaStandardLibrary
    {
        public static Lunil.Runtime.Values.LuaTable InstallAll(Lunil.Runtime.LuaState state, Lunil.StandardLibrary.LuaStandardLibraryOptions? options = null) => throw null;
        public static Lunil.Runtime.Values.LuaTable InstallBasic(Lunil.Runtime.LuaState state, Lunil.StandardLibrary.LuaStandardLibraryOptions? options = null) => throw null;
        public static Lunil.Runtime.Values.LuaTable InstallMath(Lunil.Runtime.LuaState state) => throw null;
        public static Lunil.Runtime.Values.LuaTable InstallUtf8(Lunil.Runtime.LuaState state) => throw null;
        public static Lunil.Runtime.Values.LuaTable InstallTable(Lunil.Runtime.LuaState state) => throw null;
        public static Lunil.Runtime.Values.LuaTable InstallString(Lunil.Runtime.LuaState state) => throw null;
        public static Lunil.Runtime.Values.LuaTable InstallPackage(Lunil.Runtime.LuaState state) => throw null;
        public static Lunil.Runtime.Values.LuaTable InstallPackage(Lunil.Runtime.LuaState state, Lunil.StandardLibrary.LuaStandardLibraryOptions? options) => throw null;
        public static Lunil.Runtime.Values.LuaTable InstallFfi(Lunil.Runtime.LuaState state, Lunil.StandardLibrary.LuaStandardLibraryOptions? options = null) => throw null;
        public static Lunil.Runtime.Values.LuaTable InstallIo(Lunil.Runtime.LuaState state, Lunil.StandardLibrary.LuaStandardLibraryOptions? options = null) => throw null;
        public static Lunil.Runtime.Values.LuaTable InstallOs(Lunil.Runtime.LuaState state, Lunil.StandardLibrary.LuaStandardLibraryOptions? options = null) => throw null;
        public static Lunil.Runtime.Values.LuaTable InstallDebug(Lunil.Runtime.LuaState state) => throw null;
        public static Lunil.Runtime.Values.LuaTable InstallCoroutine(Lunil.Runtime.LuaState state) => throw null;
    }

    public sealed class LuaStandardLibraryOptions : System.IEquatable<Lunil.StandardLibrary.LuaStandardLibraryOptions>
    {
        public static Lunil.StandardLibrary.LuaStandardLibraryOptions Default { get => throw null; }
        public Lunil.StandardLibrary.ILuaFileSystem FileSystem { get => throw null; init { } }
        public Lunil.StandardLibrary.LuaFfiOptions Ffi { get => throw null; init { } }
        public Lunil.StandardLibrary.ILuaConsole Console { get => throw null; init { } }
        public Lunil.StandardLibrary.ILuaEnvironment Environment { get => throw null; init { } }
        public Lunil.StandardLibrary.ILuaOperatingSystem OperatingSystem { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.StandardLibrary.LuaStandardLibraryOptions? left, Lunil.StandardLibrary.LuaStandardLibraryOptions? right) => throw null;
        public static bool operator ==(Lunil.StandardLibrary.LuaStandardLibraryOptions? left, Lunil.StandardLibrary.LuaStandardLibraryOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.StandardLibrary.LuaStandardLibraryOptions? other) => throw null;
    }

    public sealed class SystemLuaConsole : Lunil.StandardLibrary.ILuaConsole
    {
        public static Lunil.StandardLibrary.SystemLuaConsole Instance { get => throw null; }
        public byte[] ReadStandardInput() => throw null;
        public void Write(System.ReadOnlyMemory<byte> bytes) { }
        public void WriteLine() { }
        public System.IO.Stream OpenStandardInput() => throw null;
        public System.IO.Stream OpenStandardOutput() => throw null;
        public System.IO.Stream OpenStandardError() => throw null;
        public void WriteError(System.ReadOnlyMemory<byte> bytes) { }
    }

    public sealed class SystemLuaEnvironment : Lunil.StandardLibrary.ILuaEnvironment
    {
        public static Lunil.StandardLibrary.SystemLuaEnvironment Instance { get => throw null; }
        public string? GetEnvironmentVariable(string name) => throw null;
    }

    public sealed class SystemLuaFfiLibraryLoader : Lunil.StandardLibrary.ILuaFfiLibraryLoader
    {
        public static Lunil.StandardLibrary.SystemLuaFfiLibraryLoader Instance { get => throw null; }
        public nint Load(string libraryName) => throw null;
        public nint GetExport(nint libraryHandle, string symbolName) => throw null;
        public void Free(nint libraryHandle) { }
    }

    public sealed class SystemLuaFileSystem : Lunil.StandardLibrary.ILuaFileSystem
    {
        public static Lunil.StandardLibrary.SystemLuaFileSystem Instance { get => throw null; }
        public byte[] ReadAllBytes(string path) => throw null;
        public bool FileExists(string path) => throw null;
        public System.IO.Stream Open(string path, Lunil.StandardLibrary.LuaFileMode mode) => throw null;
        public System.IO.Stream OpenTemporary(out string? path) => throw null;
        public string CreateTemporaryName() => throw null;
        public void Delete(string path) { }
        public void Move(string source, string destination) { }
    }

    public sealed class SystemLuaOperatingSystem : Lunil.StandardLibrary.ILuaOperatingSystem
    {
        public static Lunil.StandardLibrary.SystemLuaOperatingSystem Instance { get => throw null; }
        public double Clock { get => throw null; }
        public System.DateTimeOffset Now { get => throw null; }
        public System.TimeZoneInfo LocalTimeZone { get => throw null; }
        public Lunil.StandardLibrary.LuaExecuteResult Execute(string? command) => throw null;
        public System.IO.Stream OpenPipe(string command, bool read, out Lunil.StandardLibrary.ILuaPipeProcess process) => throw null;
        public void Terminate(int status, bool closeState) { }
        public string? SetLocale(string? locale, string category) => throw null;
    }
}
