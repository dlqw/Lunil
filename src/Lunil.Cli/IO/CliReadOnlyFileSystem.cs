using Lunil.StandardLibrary;

namespace Lunil.Cli.IO;

internal sealed class CliReadOnlyFileSystem : ILuaFileSystem
{
    private readonly string _currentDirectory;
    private readonly string[] _roots;
    private readonly long _maximumFileBytes;

    public CliReadOnlyFileSystem(
        string currentDirectory,
        IEnumerable<string> roots,
        long maximumFileBytes)
    {
        _currentDirectory = Path.GetFullPath(currentDirectory);
        _roots = roots.Select(root => Path.GetFullPath(root, _currentDirectory))
            .Distinct(GetPathComparer())
            .OrderBy(static root => root, GetPathComparer())
            .ToArray();
        if (_roots.Length == 0)
        {
            throw new ArgumentException("At least one sandbox root is required.", nameof(roots));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileBytes);
        _maximumFileBytes = maximumFileBytes;
    }

    public byte[] ReadAllBytes(string path)
    {
        var resolved = Resolve(path);
        var info = new FileInfo(resolved);
        if (!info.Exists)
        {
            throw new FileNotFoundException("No such file or directory", resolved);
        }

        if (info.Length > _maximumFileBytes)
        {
            throw new IOException(
                $"File '{resolved}' exceeds the {_maximumFileBytes}-byte sandbox limit.");
        }

        var bytes = File.ReadAllBytes(resolved);
        if (bytes.LongLength > _maximumFileBytes)
        {
            throw new IOException(
                $"File '{resolved}' exceeds the {_maximumFileBytes}-byte sandbox limit.");
        }

        return bytes;
    }

    public bool FileExists(string path)
    {
        try
        {
            return File.Exists(Resolve(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public Stream Open(string path, LuaFileMode mode)
    {
        if (mode != LuaFileMode.Read)
        {
            throw Denied(path);
        }

        return new FileStream(Resolve(path), FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public Stream OpenTemporary(out string? path)
    {
        path = null;
        throw Denied("temporary file");
    }

    public string CreateTemporaryName() => throw Denied("temporary name");

    public void Delete(string path) => throw Denied(path);

    public void Move(string source, string destination) => throw Denied(source);

    private string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            throw Denied(path);
        }

        var candidate = Path.GetFullPath(path, _currentDirectory);
        var root = _roots.FirstOrDefault(item => IsWithinRoot(item, candidate));
        if (root is null)
        {
            throw Denied(path);
        }

        RejectReparseTraversal(root, candidate);
        return candidate;
    }

    private static void RejectReparseTraversal(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (relative == ".")
        {
            return;
        }

        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException(
                    $"Sandbox path '{candidate}' crosses a symbolic link or reparse point.");
            }
        }
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private static UnauthorizedAccessException Denied(string? path) =>
        new($"Sandbox denies file-system access to '{path}'.");

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
