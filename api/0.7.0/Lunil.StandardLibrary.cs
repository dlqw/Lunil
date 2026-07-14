// Target Frameworks: net10.0
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
        public static Lunil.Runtime.Values.LuaTable InstallIo(Lunil.Runtime.LuaState state, Lunil.StandardLibrary.LuaStandardLibraryOptions? options = null) => throw null;
        public static Lunil.Runtime.Values.LuaTable InstallOs(Lunil.Runtime.LuaState state, Lunil.StandardLibrary.LuaStandardLibraryOptions? options = null) => throw null;
        public static Lunil.Runtime.Values.LuaTable InstallDebug(Lunil.Runtime.LuaState state) => throw null;
        public static Lunil.Runtime.Values.LuaTable InstallCoroutine(Lunil.Runtime.LuaState state) => throw null;
    }

    public sealed class LuaStandardLibraryOptions : System.IEquatable<Lunil.StandardLibrary.LuaStandardLibraryOptions>
    {
        public static Lunil.StandardLibrary.LuaStandardLibraryOptions Default { get => throw null; }
        public Lunil.StandardLibrary.ILuaFileSystem FileSystem { get => throw null; init { } }
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
