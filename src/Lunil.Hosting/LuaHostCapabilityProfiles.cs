using System.Diagnostics;
using Lunil.StandardLibrary;

namespace Lunil.Hosting;

/// <summary>Creates fresh standard-library capability sets for built-in host profiles.</summary>
public static class LuaHostCapabilityProfiles
{
    public static LuaStandardLibraryOptions Create(LuaHostProfile profile) => profile switch
    {
        LuaHostProfile.Trusted => LuaStandardLibraryOptions.Default,
        LuaHostProfile.Restricted => new LuaStandardLibraryOptions
        {
            FileSystem = DeniedFileSystem.Instance,
            Console = new LuaBufferedConsole(),
            Environment = EmptyEnvironment.Instance,
            OperatingSystem = new RestrictedOperatingSystem(),
        },
        LuaHostProfile.Deterministic => new LuaStandardLibraryOptions
        {
            FileSystem = DeniedFileSystem.Instance,
            Console = new LuaBufferedConsole(),
            Environment = EmptyEnvironment.Instance,
            OperatingSystem = DeterministicOperatingSystem.Instance,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    private sealed class DeniedFileSystem : ILuaFileSystem
    {
        public static DeniedFileSystem Instance { get; } = new();

        public byte[] ReadAllBytes(string path) => throw Denied(path);

        public bool FileExists(string path) => false;

        public Stream Open(string path, LuaFileMode mode) => throw Denied(path);

        public Stream OpenTemporary(out string? path)
        {
            path = null;
            throw Denied("temporary file");
        }

        public string CreateTemporaryName() => throw Denied("temporary name");

        public void Delete(string path) => throw Denied(path);

        public void Move(string source, string destination) => throw Denied(source);

        private static UnauthorizedAccessException Denied(string resource) =>
            new($"Lua host profile denies file-system access to '{resource}'.");
    }

    private sealed class EmptyEnvironment : ILuaEnvironment
    {
        public static EmptyEnvironment Instance { get; } = new();

        public string? GetEnvironmentVariable(string name) => null;
    }

    private sealed class RestrictedOperatingSystem : ILuaOperatingSystem
    {
        private readonly long _started = Stopwatch.GetTimestamp();

        public double Clock => Stopwatch.GetElapsedTime(_started).TotalSeconds;

        public DateTimeOffset Now => DateTimeOffset.Now;

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;

        public LuaExecuteResult Execute(string? command) => throw Denied("process execution");

        public Stream OpenPipe(string command, bool read, out ILuaPipeProcess process) =>
            throw Denied("process pipes");

        public void Terminate(int status, bool closeState) =>
            throw Denied("process termination");

        public string? SetLocale(string? locale, string category) =>
            locale is null or "C" or "POSIX" ? "C" : null;
    }

    private sealed class DeterministicOperatingSystem : ILuaOperatingSystem
    {
        public static DeterministicOperatingSystem Instance { get; } = new();

        public double Clock => 0;

        public DateTimeOffset Now => DateTimeOffset.UnixEpoch;

        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public LuaExecuteResult Execute(string? command) => throw Denied("process execution");

        public Stream OpenPipe(string command, bool read, out ILuaPipeProcess process) =>
            throw Denied("process pipes");

        public void Terminate(int status, bool closeState) =>
            throw Denied("process termination");

        public string? SetLocale(string? locale, string category) =>
            locale is null or "C" or "POSIX" ? "C" : null;
    }

    private static UnauthorizedAccessException Denied(string capability) =>
        new($"Lua host profile denies {capability}.");
}
