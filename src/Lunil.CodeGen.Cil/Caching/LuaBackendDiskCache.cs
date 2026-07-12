using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lunil.CodeGen.Cil.Caching;

/// <summary>
/// Fail-soft persistent storage for verified backend data. Each entry is committed by an atomic
/// same-volume directory rename and protected by a cross-process file lock.
/// </summary>
public sealed class LuaBackendDiskCache
{
    private const string ManifestMagic = "LUNIL-BACKEND-CACHE-ENTRY";
    private const int StorageSchemaVersion = 1;
    private const int MaximumMetadataBytes = 64 * 1024;

    private readonly LuaBackendDiskCacheOptions _options;
    private readonly string _root;
    private readonly string _entriesRoot;
    private readonly string _temporaryRoot;
    private readonly string _locksRoot;
    private readonly string _quarantineRoot;
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public LuaBackendDiskCache(LuaBackendDiskCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumEntryBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumQuarantineBytes);
        if (options.LockTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.LockTimeout,
                "LockTimeout must be positive.");
        }

        if (options.LockRetryDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.LockRetryDelay,
                "LockRetryDelay must be positive.");
        }

        if (options.OrphanTemporaryEntryAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.OrphanTemporaryEntryAge,
                "OrphanTemporaryEntryAge cannot be negative.");
        }

        _options = options;
        _root = Path.GetFullPath(options.RootDirectory);
        _entriesRoot = Path.Combine(_root, $"entries-v{StorageSchemaVersion}");
        _temporaryRoot = Path.Combine(_root, "tmp");
        _locksRoot = Path.Combine(_root, "locks");
        _quarantineRoot = Path.Combine(_root, "quarantine");
    }

    public async ValueTask<LuaBackendCacheReadResult> TryReadAsync(
        LuaBackendCacheKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        try
        {
            EnsureLayout();
            await using var entryLock = await TryAcquireLockAsync(
                GetEntryLockPath(key.CacheId),
                cancellationToken).ConfigureAwait(false);
            if (entryLock is null)
            {
                return new LuaBackendCacheReadResult(
                    LuaBackendCacheReadStatus.Unavailable,
                    [],
                    LuaBackendCacheDiagnosticCodes.Unavailable);
            }

            var entryPath = GetEntryPath(key.CacheId);
            if (!Directory.Exists(entryPath))
            {
                return new LuaBackendCacheReadResult(LuaBackendCacheReadStatus.Miss, []);
            }

            try
            {
                var validated = await ValidateEntryAsync(
                    entryPath,
                    key,
                    cancellationToken).ConfigureAwait(false);
                TouchAccessFile(entryPath);
                return new LuaBackendCacheReadResult(
                    LuaBackendCacheReadStatus.Hit,
                    validated.Payload);
            }
            catch (InvalidDataException error)
            {
                TryQuarantine(entryPath, key.CacheId, error.Message);
                return new LuaBackendCacheReadResult(
                    LuaBackendCacheReadStatus.CorruptMiss,
                    [],
                    LuaBackendCacheDiagnosticCodes.CorruptEntry);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (IsCacheIoFailure(error))
        {
            return new LuaBackendCacheReadResult(
                LuaBackendCacheReadStatus.Unavailable,
                [],
                LuaBackendCacheDiagnosticCodes.Unavailable);
        }
    }

    public async ValueTask<LuaBackendCacheWriteResult> WriteAsync(
        LuaBackendCacheKey key,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var descriptor = key.SerializeCanonicalDescriptor();
        var payloadChecksum = ComputeHash(payload.Span);
        var estimatedManifest = SerializeManifest(new CacheEntryManifest(
            key.CacheId,
            descriptor.Length,
            ComputeHash(descriptor),
            payload.Length,
            payloadChecksum,
            DateTime.MaxValue.Ticks));
        if (payload.Length > _options.MaximumEntryBytes ||
            checked((long)payload.Length + descriptor.Length + estimatedManifest.Length) >
            _options.MaximumBytes)
        {
            return new LuaBackendCacheWriteResult(
                LuaBackendCacheWriteStatus.RejectedTooLarge,
                DiagnosticCode: LuaBackendCacheDiagnosticCodes.EntryTooLarge);
        }

        var writeStatus = LuaBackendCacheWriteStatus.Created;
        try
        {
            EnsureLayout();
            await using (var entryLock = await TryAcquireLockAsync(
                GetEntryLockPath(key.CacheId),
                cancellationToken).ConfigureAwait(false))
            {
                if (entryLock is null)
                {
                    return new LuaBackendCacheWriteResult(
                        LuaBackendCacheWriteStatus.Unavailable,
                        DiagnosticCode: LuaBackendCacheDiagnosticCodes.Unavailable);
                }

                var entryPath = GetEntryPath(key.CacheId);
                var commitEntry = true;
                if (Directory.Exists(entryPath))
                {
                    try
                    {
                        var existing = await ValidateEntryAsync(
                            entryPath,
                            key,
                            cancellationToken).ConfigureAwait(false);
                        if (string.Equals(
                            existing.Manifest.PayloadChecksum,
                            payloadChecksum,
                            StringComparison.Ordinal))
                        {
                            TouchAccessFile(entryPath);
                            writeStatus = LuaBackendCacheWriteStatus.AlreadyPresent;
                            commitEntry = false;
                        }
                        else
                        {
                            TryQuarantine(entryPath, key.CacheId, "payload-conflict");
                        }
                    }
                    catch (InvalidDataException error)
                    {
                        TryQuarantine(entryPath, key.CacheId, error.Message);
                    }
                }

                if (commitEntry)
                {
                    await CommitEntryAsync(
                        key,
                        descriptor,
                        payload,
                        payloadChecksum,
                        entryPath,
                        cancellationToken).ConfigureAwait(false);
                    writeStatus = LuaBackendCacheWriteStatus.Created;
                }
            }

            var trim = await TrimAsync(cancellationToken).ConfigureAwait(false);
            return new LuaBackendCacheWriteResult(
                writeStatus,
                trim.RemovedEntries,
                trim.Succeeded ? null : trim.DiagnosticCode);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (IsCacheIoFailure(error))
        {
            return new LuaBackendCacheWriteResult(
                LuaBackendCacheWriteStatus.Unavailable,
                DiagnosticCode: LuaBackendCacheDiagnosticCodes.Unavailable);
        }
    }

    public async ValueTask<bool> QuarantineAsync(
        LuaBackendCacheKey key,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        try
        {
            EnsureLayout();
            await using var entryLock = await TryAcquireLockAsync(
                GetEntryLockPath(key.CacheId),
                cancellationToken).ConfigureAwait(false);
            if (entryLock is null)
            {
                return false;
            }

            var entryPath = GetEntryPath(key.CacheId);
            if (!Directory.Exists(entryPath))
            {
                return true;
            }

            TryQuarantine(entryPath, key.CacheId, reason.Trim());
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (IsCacheIoFailure(error))
        {
            return false;
        }
    }

    public async ValueTask<LuaBackendCacheTrimResult> TrimAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureLayout();
            await using var quotaLock = await TryAcquireLockAsync(
                Path.Combine(_locksRoot, "quota.lock"),
                cancellationToken).ConfigureAwait(false);
            if (quotaLock is null)
            {
                return UnavailableTrim();
            }

            CleanupOrphanTemporaryDirectories();
            TrimQuarantine();
            var entries = EnumerateEntries()
                .OrderBy(static entry => entry.LastAccessUtc)
                .ThenBy(static entry => entry.Path, StringComparer.Ordinal)
                .ToList();
            var remainingBytes = entries.Sum(static entry => entry.Size);
            var removedEntries = 0;
            long removedBytes = 0;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (remainingBytes <= _options.MaximumBytes)
                {
                    break;
                }

                var cacheId = Path.GetFileName(entry.Path);
                await using var entryLock = await TryAcquireLockAsync(
                    GetEntryLockPath(cacheId),
                    cancellationToken).ConfigureAwait(false);
                if (entryLock is null || !Directory.Exists(entry.Path))
                {
                    continue;
                }

                var currentSize = GetDirectorySize(entry.Path);
                DeleteDirectorySafely(entry.Path);
                removedEntries++;
                removedBytes = checked(removedBytes + currentSize);
                remainingBytes = Math.Max(0, remainingBytes - currentSize);
            }

            return new LuaBackendCacheTrimResult(
                true,
                removedEntries,
                removedBytes,
                remainingBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (IsCacheIoFailure(error))
        {
            return UnavailableTrim();
        }
    }

    private async ValueTask CommitEntryAsync(
        LuaBackendCacheKey key,
        byte[] descriptor,
        ReadOnlyMemory<byte> payload,
        string payloadChecksum,
        string entryPath,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(
            _temporaryRoot,
            $"{key.CacheId}.{Guid.NewGuid():N}");
        EnsureWithinRoot(temporaryPath);
        Directory.CreateDirectory(temporaryPath);
        try
        {
            var createdUtcTicks = DateTime.UtcNow.Ticks;
            var manifest = new CacheEntryManifest(
                key.CacheId,
                descriptor.Length,
                ComputeHash(descriptor),
                payload.Length,
                payloadChecksum,
                createdUtcTicks);
            await WriteFileDurablyAsync(
                Path.Combine(temporaryPath, "descriptor.json"),
                descriptor,
                cancellationToken).ConfigureAwait(false);
            await WriteFileDurablyAsync(
                Path.Combine(temporaryPath, "payload.bin"),
                payload,
                cancellationToken).ConfigureAwait(false);
            await WriteFileDurablyAsync(
                Path.Combine(temporaryPath, "manifest.json"),
                SerializeManifest(manifest),
                cancellationToken).ConfigureAwait(false);
            await WriteFileDurablyAsync(
                Path.Combine(temporaryPath, "access"),
                ReadOnlyMemory<byte>.Empty,
                cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
            Directory.Move(temporaryPath, entryPath);
        }
        finally
        {
            if (Directory.Exists(temporaryPath))
            {
                DeleteDirectorySafely(temporaryPath);
            }
        }
    }

    private async ValueTask<ValidatedEntry> ValidateEntryAsync(
        string entryPath,
        LuaBackendCacheKey expectedKey,
        CancellationToken cancellationToken)
    {
        EnsureWithinRoot(entryPath);
        var manifestBytes = await ReadBoundedFileAsync(
            Path.Combine(entryPath, "manifest.json"),
            MaximumMetadataBytes,
            cancellationToken).ConfigureAwait(false);
        var manifest = DeserializeManifest(manifestBytes);
        if (!string.Equals(manifest.CacheId, expectedKey.CacheId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Cache entry id does not match its path.");
        }

        var descriptor = await ReadBoundedFileAsync(
            Path.Combine(entryPath, "descriptor.json"),
            MaximumMetadataBytes,
            cancellationToken).ConfigureAwait(false);
        if (descriptor.Length != manifest.DescriptorLength ||
            !string.Equals(
                ComputeHash(descriptor),
                manifest.DescriptorChecksum,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Cache key descriptor checksum is invalid.");
        }

        LuaBackendCacheKey actualKey;
        try
        {
            actualKey = LuaBackendCacheKey.ParseCanonicalDescriptor(descriptor);
        }
        catch (Exception error) when (error is ArgumentException or JsonException or
            InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Cache key descriptor is invalid.", error);
        }

        if (!string.Equals(actualKey.CacheId, manifest.CacheId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Cache key descriptor content address is invalid.");
        }

        var compatibility = expectedKey.GetCompatibility(actualKey);
        if (!compatibility.IsCompatible)
        {
            throw new InvalidDataException(
                $"Cache key is incompatible: {compatibility.Mismatches}.");
        }

        var accessInfo = new FileInfo(Path.Combine(entryPath, "access"));
        if (!accessInfo.Exists || accessInfo.Length != 0)
        {
            throw new InvalidDataException("Cache access marker is invalid.");
        }

        var payload = await ReadBoundedFileAsync(
            Path.Combine(entryPath, "payload.bin"),
            _options.MaximumEntryBytes,
            cancellationToken).ConfigureAwait(false);
        if (payload.Length != manifest.PayloadLength ||
            !string.Equals(
                ComputeHash(payload),
                manifest.PayloadChecksum,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Cache payload checksum is invalid.");
        }

        return new ValidatedEntry(manifest, [.. payload]);
    }

    private void EnsureLayout()
    {
        Directory.CreateDirectory(_entriesRoot);
        Directory.CreateDirectory(_temporaryRoot);
        Directory.CreateDirectory(_locksRoot);
        Directory.CreateDirectory(_quarantineRoot);
    }

    private string GetEntryPath(string cacheId)
    {
        var path = Path.Combine(_entriesRoot, cacheId[..2], cacheId);
        EnsureWithinRoot(path);
        return path;
    }

    private string GetEntryLockPath(string cacheId)
    {
        var path = Path.Combine(_locksRoot, $"{cacheId}.lock");
        EnsureWithinRoot(path);
        return path;
    }

    private async ValueTask<FileStream?> TryAcquireLockAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        EnsureWithinRoot(lockPath);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (stopwatch.Elapsed < _options.LockTimeout)
            {
                await Task.Delay(_options.LockRetryDelay, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    private void TryQuarantine(string entryPath, string cacheId, string reason)
    {
        if (!Directory.Exists(entryPath))
        {
            return;
        }

        EnsureWithinRoot(entryPath);
        var destination = Path.Combine(
            _quarantineRoot,
            $"{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-{cacheId}-{Guid.NewGuid():N}");
        EnsureWithinRoot(destination);
        Directory.Move(entryPath, destination);
        var reasonBytes = JsonEncodedText.Encode(reason).EncodedUtf8Bytes.ToArray();
        File.WriteAllBytes(Path.Combine(destination, "reason.txt"), reasonBytes);
    }

    private IEnumerable<EntryInfo> EnumerateEntries()
    {
        if (!Directory.Exists(_entriesRoot))
        {
            yield break;
        }

        foreach (var prefix in Directory.EnumerateDirectories(_entriesRoot))
        {
            foreach (var entryPath in Directory.EnumerateDirectories(prefix))
            {
                EnsureWithinRoot(entryPath);
                var accessPath = Path.Combine(entryPath, "access");
                var lastAccess = File.Exists(accessPath)
                    ? File.GetLastWriteTimeUtc(accessPath)
                    : Directory.GetLastWriteTimeUtc(entryPath);
                yield return new EntryInfo(
                    entryPath,
                    GetDirectorySize(entryPath),
                    lastAccess);
            }
        }
    }

    private void CleanupOrphanTemporaryDirectories()
    {
        var cutoff = DateTime.UtcNow - _options.OrphanTemporaryEntryAge;
        foreach (var path in Directory.EnumerateDirectories(_temporaryRoot))
        {
            EnsureWithinRoot(path);
            if (Directory.GetLastWriteTimeUtc(path) <= cutoff)
            {
                DeleteDirectorySafely(path);
            }
        }
    }

    private void TrimQuarantine()
    {
        var entries = Directory.EnumerateDirectories(_quarantineRoot)
            .Select(path => new EntryInfo(
                path,
                GetDirectorySize(path),
                Directory.GetCreationTimeUtc(path)))
            .OrderBy(static entry => entry.LastAccessUtc)
            .ThenBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToList();
        var size = entries.Sum(static entry => entry.Size);
        foreach (var entry in entries)
        {
            if (size <= _options.MaximumQuarantineBytes)
            {
                break;
            }

            DeleteDirectorySafely(entry.Path);
            size = Math.Max(0, size - entry.Size);
        }
    }

    private static async ValueTask WriteFileDurablyAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async ValueTask<byte[]> ReadBoundedFileAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 0 || info.Length > maximumBytes ||
            info.Length > int.MaxValue)
        {
            throw new InvalidDataException($"Cache file '{info.Name}' size is invalid.");
        }

        var content = new byte[checked((int)info.Length)];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException($"Cache file '{info.Name}' changed while reading.");
        }

        return content;
    }

    private static byte[] SerializeManifest(CacheEntryManifest manifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("magic", ManifestMagic);
            writer.WriteNumber("storageSchemaVersion", StorageSchemaVersion);
            writer.WriteString("cacheId", manifest.CacheId);
            writer.WriteNumber("descriptorLength", manifest.DescriptorLength);
            writer.WriteString("descriptorChecksum", manifest.DescriptorChecksum);
            writer.WriteNumber("payloadLength", manifest.PayloadLength);
            writer.WriteString("payloadChecksum", manifest.PayloadChecksum);
            writer.WriteNumber("createdUtcTicks", manifest.CreatedUtcTicks);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static CacheEntryManifest DeserializeManifest(ReadOnlySpan<byte> content)
    {
        try
        {
            using var document = JsonDocument.Parse(content.ToArray());
            var root = document.RootElement;
            if (!string.Equals(
                RequiredString(root, "magic"),
                ManifestMagic,
                StringComparison.Ordinal) ||
                RequiredInt32(root, "storageSchemaVersion") != StorageSchemaVersion)
            {
                throw new InvalidDataException("Cache entry manifest version is invalid.");
            }

            var manifest = new CacheEntryManifest(
                RequiredString(root, "cacheId"),
                RequiredInt32(root, "descriptorLength"),
                RequiredString(root, "descriptorChecksum"),
                RequiredInt64(root, "payloadLength"),
                RequiredString(root, "payloadChecksum"),
                RequiredInt64(root, "createdUtcTicks"));
            ValidateManifest(manifest);
            return manifest;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or
            FormatException or OverflowException)
        {
            throw new InvalidDataException("Cache entry manifest JSON is invalid.", error);
        }
    }

    private static void ValidateManifest(CacheEntryManifest manifest)
    {
        if (!IsHash(manifest.CacheId) || !IsHash(manifest.DescriptorChecksum) ||
            !IsHash(manifest.PayloadChecksum) || manifest.DescriptorLength <= 0 ||
            manifest.DescriptorLength > MaximumMetadataBytes || manifest.PayloadLength < 0 ||
            manifest.CreatedUtcTicks <= 0)
        {
            throw new InvalidDataException("Cache entry manifest fields are invalid.");
        }
    }

    private static string RequiredString(JsonElement element, string property) =>
        Required(element, property).GetString() ??
        throw new InvalidDataException($"Cache entry property '{property}' is null.");

    private static int RequiredInt32(JsonElement element, string property) =>
        Required(element, property).GetInt32();

    private static long RequiredInt64(JsonElement element, string property) =>
        Required(element, property).GetInt64();

    private static JsonElement Required(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value
            : throw new InvalidDataException($"Cache entry property '{property}' is missing.");

    private void TouchAccessFile(string entryPath)
    {
        var path = Path.Combine(entryPath, "access");
        EnsureWithinRoot(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
    }

    private long GetDirectorySize(string path)
    {
        EnsureWithinRoot(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            return 0;
        }

        long size = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                size = checked(size + new FileInfo(file).Length);
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                EnsureWithinRoot(child);
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(child);
                }
            }
        }

        return size;
    }

    private void DeleteDirectorySafely(string path)
    {
        EnsureWithinRoot(path);
        if (!Directory.Exists(path))
        {
            return;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path))
        {
            File.Delete(file);
        }

        foreach (var child in Directory.EnumerateDirectories(path))
        {
            DeleteDirectorySafely(child);
        }

        Directory.Delete(path);
    }

    private void EnsureWithinRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(_root, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", _pathComparison) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", _pathComparison))
        {
            throw new IOException("Cache path escaped the configured root.");
        }
    }

    private static string ComputeHash(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private static bool IsHash(string value) => value.Length == 64 &&
        value.All(static character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool IsCacheIoFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidDataException;

    private static LuaBackendCacheTrimResult UnavailableTrim() => new(
        false,
        0,
        0,
        0,
        LuaBackendCacheDiagnosticCodes.Unavailable);

    private sealed record CacheEntryManifest(
        string CacheId,
        int DescriptorLength,
        string DescriptorChecksum,
        long PayloadLength,
        string PayloadChecksum,
        long CreatedUtcTicks);

    private sealed record ValidatedEntry(
        CacheEntryManifest Manifest,
        ImmutableArray<byte> Payload);

    private sealed record EntryInfo(string Path, long Size, DateTime LastAccessUtc);
}
