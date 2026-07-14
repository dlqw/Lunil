namespace Lunil.StandardLibrary;

public sealed record LuaStandardLibraryOptions
{
    public static LuaStandardLibraryOptions Default { get; } = new();

    public ILuaFileSystem FileSystem { get; init; } = SystemLuaFileSystem.Instance;

    public ILuaConsole Console { get; init; } = SystemLuaConsole.Instance;

    public ILuaEnvironment Environment { get; init; } = SystemLuaEnvironment.Instance;

    public ILuaOperatingSystem OperatingSystem { get; init; } = SystemLuaOperatingSystem.Instance;
}

public enum LuaFileMode
{
    Read,
    Write,
    Append,
    ReadUpdate,
    WriteUpdate,
    AppendUpdate,
}

public interface ILuaFileSystem
{
    byte[] ReadAllBytes(string path);

    bool FileExists(string path)
    {
        try
        {
            _ = ReadAllBytes(path);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    Stream Open(string path, LuaFileMode mode) =>
        throw new NotSupportedException("This file-system provider does not support open files.");

    Stream OpenTemporary(out string? path)
    {
        path = null;
        throw new NotSupportedException("This file-system provider does not support temporary files.");
    }

    string CreateTemporaryName() =>
        throw new NotSupportedException("This file-system provider does not support temporary names.");

    void Delete(string path) =>
        throw new NotSupportedException("This file-system provider does not support deletion.");

    void Move(string source, string destination) =>
        throw new NotSupportedException("This file-system provider does not support rename.");
}

public interface ILuaEnvironment
{
    string? GetEnvironmentVariable(string name);
}

public interface ILuaConsole
{
    byte[] ReadStandardInput();

    void Write(ReadOnlyMemory<byte> bytes);

    void WriteLine();

    Stream OpenStandardInput() => new MemoryStream(ReadStandardInput(), writable: false);

    Stream OpenStandardOutput() => new LuaConsoleWriteStream(this, error: false);

    Stream OpenStandardError() => new LuaConsoleWriteStream(this, error: true);

    void WriteError(ReadOnlyMemory<byte> bytes) => Write(bytes);
}

public readonly record struct LuaExecuteResult(bool Started, string Kind, int Status);

public interface ILuaPipeProcess : IDisposable
{
    LuaExecuteResult Wait();
}

public interface ILuaOperatingSystem
{
    double Clock { get; }

    DateTimeOffset Now { get; }

    TimeZoneInfo LocalTimeZone { get; }

    LuaExecuteResult Execute(string? command);

    Stream OpenPipe(string command, bool read, out ILuaPipeProcess process);

    void Terminate(int status, bool closeState);

    string? SetLocale(string? locale, string category);
}

public sealed class SystemLuaFileSystem : ILuaFileSystem
{
    public static SystemLuaFileSystem Instance { get; } = new();

    private SystemLuaFileSystem()
    {
    }

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public bool FileExists(string path) => File.Exists(path);

    public Stream Open(string path, LuaFileMode mode)
    {
        var (fileMode, access) = mode switch
        {
            LuaFileMode.Read => (FileMode.Open, FileAccess.Read),
            LuaFileMode.Write => (FileMode.Create, FileAccess.Write),
            LuaFileMode.Append => (FileMode.OpenOrCreate, FileAccess.Write),
            LuaFileMode.ReadUpdate => (FileMode.Open, FileAccess.ReadWrite),
            LuaFileMode.WriteUpdate => (FileMode.Create, FileAccess.ReadWrite),
            LuaFileMode.AppendUpdate => (FileMode.OpenOrCreate, FileAccess.ReadWrite),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        var stream = new FileStream(path, fileMode, access, FileShare.ReadWrite);
        if (mode is LuaFileMode.Append or LuaFileMode.AppendUpdate)
        {
            stream.Seek(0, SeekOrigin.End);
        }

        return stream;
    }

    public Stream OpenTemporary(out string? path)
    {
        path = Path.GetTempFileName();
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            4096,
            FileOptions.DeleteOnClose);
    }

    public string CreateTemporaryName()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return path;
            }
        }

        throw new IOException("Unable to create a unique temporary file name.");
    }

    public void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path);
        }
        else
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("No such file or directory", path);
            }

            File.Delete(path);
        }
    }

    public void Move(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }
}

public sealed class SystemLuaEnvironment : ILuaEnvironment
{
    public static SystemLuaEnvironment Instance { get; } = new();

    private SystemLuaEnvironment()
    {
    }

    public string? GetEnvironmentVariable(string name) =>
        global::System.Environment.GetEnvironmentVariable(name);
}

public sealed class SystemLuaConsole : ILuaConsole
{
    public static SystemLuaConsole Instance { get; } = new();

    private SystemLuaConsole()
    {
    }

    public byte[] ReadStandardInput()
    {
        using var buffer = new MemoryStream();
        Console.OpenStandardInput().CopyTo(buffer);
        return buffer.ToArray();
    }

    public void Write(ReadOnlyMemory<byte> bytes) =>
        Console.OpenStandardOutput().Write(bytes.Span);

    public void WriteLine() => Write("\n"u8.ToArray());

    public Stream OpenStandardInput() => Console.OpenStandardInput();

    public Stream OpenStandardOutput() => Console.OpenStandardOutput();

    public Stream OpenStandardError() => Console.OpenStandardError();

    public void WriteError(ReadOnlyMemory<byte> bytes) =>
        Console.OpenStandardError().Write(bytes.Span);
}

public sealed class SystemLuaOperatingSystem : ILuaOperatingSystem
{
    public static SystemLuaOperatingSystem Instance { get; } = new();

    private SystemLuaOperatingSystem()
    {
    }

    public double Clock
    {
        get
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.TotalProcessorTime.TotalSeconds;
        }
    }

    public DateTimeOffset Now => DateTimeOffset.Now;

    public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;

    public LuaExecuteResult Execute(string? command)
    {
        if (command is null)
        {
            return new LuaExecuteResult(true, "exit", 0);
        }

        using var process = StartShell(command, redirectOutput: false);
        process.WaitForExit();
        return new LuaExecuteResult(true, "exit", process.ExitCode);
    }

    public Stream OpenPipe(string command, bool read, out ILuaPipeProcess process)
    {
        var child = StartShell(command, redirectOutput: read, redirectInput: !read);
        process = new SystemLuaPipeProcess(child);
        return read ? child.StandardOutput.BaseStream : child.StandardInput.BaseStream;
    }

    public void Terminate(int status, bool closeState) => global::System.Environment.Exit(status);

    public string? SetLocale(string? locale, string category)
    {
        // The managed lexer, number conversion, collation, and formatting contracts are
        // deliberately culture-invariant. Advertising an arbitrary CLR culture here would
        // claim that every corresponding Lua locale category changed when none of them did,
        // as well as mutate process-wide host state. Lua only requires the portable C locale.
        return locale is null or "" or "C" ? "C" : null;
    }

    private static System.Diagnostics.Process StartShell(
        string command,
        bool redirectOutput,
        bool redirectInput = false)
    {
        var windows = OperatingSystem.IsWindows();
        var info = new System.Diagnostics.ProcessStartInfo
        {
            FileName = windows ? "cmd.exe" : "/bin/sh",
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardInput = redirectInput,
        };
        info.ArgumentList.Add(windows ? "/c" : "-c");
        info.ArgumentList.Add(command);
        return System.Diagnostics.Process.Start(info) ??
            throw new IOException("cannot start command processor");
    }
}

internal sealed class SystemLuaPipeProcess(System.Diagnostics.Process process) : ILuaPipeProcess
{
    public LuaExecuteResult Wait()
    {
        process.WaitForExit();
        return new LuaExecuteResult(true, "exit", process.ExitCode);
    }

    public void Dispose() => process.Dispose();
}

internal sealed class LuaConsoleWriteStream(ILuaConsole console, bool error) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        var bytes = new ReadOnlyMemory<byte>(buffer, offset, count);
        if (error)
        {
            console.WriteError(bytes);
        }
        else
        {
            console.Write(bytes);
        }
    }
}
