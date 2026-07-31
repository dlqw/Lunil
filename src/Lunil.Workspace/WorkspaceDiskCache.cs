using System.Text;

namespace Lunil.Workspace;

internal sealed class WorkspaceDiskCache
{
    private const string Header = "LUNIL-WORKSPACE-CACHE-V1";
    private readonly string _root;
    private readonly long _maximumBytes;

    public WorkspaceDiskCache(string root, long maximumBytes)
    {
        _root = Path.GetFullPath(root);
        _maximumBytes = maximumBytes;
        Directory.CreateDirectory(Path.Combine(_root, "v1"));
    }

    public bool TryRead(string cacheKey, string moduleName, string contentHash)
    {
        var path = GetPath(cacheKey);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > 64 * 1024)
            {
                return false;
            }

            var lines = File.ReadAllLines(path, Encoding.UTF8);
            var valid = lines.Length == 8 &&
                string.Equals(lines[0], Header, StringComparison.Ordinal) &&
                string.Equals(lines[1], cacheKey, StringComparison.Ordinal) &&
                string.Equals(lines[2], moduleName, StringComparison.Ordinal) &&
                string.Equals(lines[3], contentHash, StringComparison.Ordinal) &&
                lines.Skip(4).All(IsLowerHexHash);
            if (valid)
            {
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
                return true;
            }

            TryDelete(path);
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException)
        {
            return false;
        }
    }

    public void Write(string cacheKey, LuaWorkspaceModuleResult result)
    {
        var path = GetPath(cacheKey);
        var directory = Path.GetDirectoryName(path)!;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllLines(temporary,
            [
                Header,
                cacheKey,
                result.Identity.Name,
                result.ContentHash,
                result.ExportHash,
                result.ExportSymbolHash,
                result.FunctionSummaryHash,
                result.DependencySummaryHash,
            ], Encoding.UTF8);
            MoveAtomically(temporary, path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException)
        {
            TryDelete(temporary);
        }
    }

    private static void MoveAtomically(string temporary, string path)
    {
        if (!File.Exists(path))
        {
            try
            {
                File.Move(temporary, path);
                return;
            }
            catch (IOException) when (File.Exists(path))
            {
                // A concurrent writer won the create race; replace its complete entry atomically.
            }
        }

        File.Replace(temporary, path, destinationBackupFileName: null);
    }

    public int Prune()
    {
        try
        {
            var files = Directory.EnumerateFiles(Path.Combine(_root, "v1"), "*.lunilcache",
                    SearchOption.AllDirectories)
                .Select(static path => new FileInfo(path))
                .OrderBy(static info => info.LastAccessTimeUtc)
                .ThenBy(static info => info.FullName, StringComparer.Ordinal)
                .ToArray();
            var total = files.Sum(static info => info.Exists ? info.Length : 0);
            var removed = 0;
            foreach (var file in files)
            {
                if (total <= _maximumBytes)
                {
                    break;
                }

                var length = file.Exists ? file.Length : 0;
                if (TryDelete(file.FullName))
                {
                    total -= length;
                    removed++;
                }
            }

            foreach (var temporary in Directory.EnumerateFiles(Path.Combine(_root, "v1"), "*.tmp",
                         SearchOption.AllDirectories))
            {
                TryDelete(temporary);
            }

            return removed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            DirectoryNotFoundException)
        {
            return 0;
        }
    }

    private string GetPath(string cacheKey)
    {
        if (!IsLowerHexHash(cacheKey))
        {
            throw new ArgumentException("Disk cache key is invalid.", nameof(cacheKey));
        }

        return Path.Combine(_root, "v1", cacheKey[..2], cacheKey + ".lunilcache");
    }

    private static bool IsLowerHexHash(string value) =>
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException)
        {
            return false;
        }
    }
}
