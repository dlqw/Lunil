using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lunil.Hosting;

public enum LuaPatchJournalErrorCode : byte
{
    InvalidEntry,
    Corrupted,
    HashMismatch,
    SequenceMismatch,
    InvalidTransition,
    ResourceLimitExceeded,
    IoFailure,
    WriterUnavailable,
}

public sealed class LuaPatchJournalException : Exception
{
    public LuaPatchJournalException(LuaPatchJournalErrorCode code, string message) : base(message)
    {
        Code = code;
    }

    public LuaPatchJournalException(
        LuaPatchJournalErrorCode code,
        string message,
        Exception innerException) : base(message, innerException)
    {
        Code = code;
    }

    public LuaPatchJournalErrorCode Code { get; }
}

public sealed record LuaPatchFileJournalOptions
{
    public static LuaPatchFileJournalOptions Default { get; } = new();

    public long MaximumBytes { get; init; } = 64L * 1024 * 1024;

    public int MaximumEntries { get; init; } = 1_000_000;

    public int MaximumLineBytes { get; init; } = 1024 * 1024;

    public bool CreateDirectory { get; init; } = true;

    /// <summary>
    /// Optional retention policy used when an append would exceed the byte or entry limit.
    /// A null value disables automatic compaction.
    /// </summary>
    public LuaPatchJournalCompactionOptions? AutomaticCompaction { get; init; }

    /// <summary>
    /// Maximum time a reader waits for an in-flight append to publish its terminating newline.
    /// </summary>
    public TimeSpan ConcurrentReadTimeout { get; init; } = TimeSpan.FromMilliseconds(250);
}

public sealed record LuaPatchJournalCompactionOptions
{
    public static LuaPatchJournalCompactionOptions Default { get; } = new();

    /// <summary>
    /// Number of most recently completed transactions whose complete phase history is retained.
    /// Incomplete transactions are always retained.
    /// </summary>
    public int RetainCompletedTransactions { get; init; } = 256;
}

public sealed record LuaPatchJournalCompactionResult(
    int OriginalEntryCount,
    int RetainedEntryCount,
    long OriginalBytes,
    long RetainedBytes,
    string? OriginalTailHash,
    string? RetainedTailHash)
{
    public int RemovedEntryCount => OriginalEntryCount - RetainedEntryCount;

    public bool Changed => RemovedEntryCount != 0;
}

public sealed record LuaPatchRecoveryRecord(
    string TransactionId,
    string RolloutId,
    string RingName,
    string PatchId,
    string TargetRevision,
    LuaPatchJournalPhase LastPhase,
    ImmutableArray<string> TargetIds,
    DateTimeOffset LastUpdatedAt);

public enum LuaPatchRecoveryResolution : byte
{
    Manual,
    Committed,
    RolledBack,
}

public interface ILuaPatchCrashRecoveryHandler
{
    LuaPatchRecoveryResolution Recover(LuaPatchRecoveryRecord record);
}

public sealed record LuaPatchRecoveryResult(
    LuaPatchRecoveryRecord Record,
    LuaPatchRecoveryResolution Resolution);

/// <summary>
/// Hash-chained NDJSON deployment journal. Every append is flushed to stable storage before it
/// returns. One writer instance owns a journal file; readers may inspect it concurrently, and
/// completed history can be compacted under an explicit retention policy.
/// </summary>
public sealed class LuaPatchFileJournal : ILuaPatchDeploymentJournal, IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly LuaPatchFileJournalOptions _options;
    private bool _initialized;
    private long _nextSequence = 1;
    private string? _lastHash;
    private ImmutableArray<LuaPatchJournalEntry> _entries = [];
    private long _currentBytes;
    private bool _writeFaulted;
    private FileStream? _writerLock;
    private bool _disposed;

    public LuaPatchFileJournal(string path, LuaPatchFileJournalOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        _options = options ?? LuaPatchFileJournalOptions.Default;
        if (_options.MaximumBytes <= 0 || _options.MaximumEntries <= 0 ||
            _options.MaximumLineBytes <= 0 ||
            _options.MaximumLineBytes > _options.MaximumBytes ||
            _options.ConcurrentReadTimeout < TimeSpan.Zero ||
            _options.ConcurrentReadTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Journal resource limits must be positive and internally consistent.");
        }

        ValidateCompactionOptions(_options.AutomaticCompaction, nameof(options));
    }

    public string Path => _path;

    public string WriterLockPath => _path + ".writer.lock";

    public void Append(LuaPatchJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfWriteFaulted();
            EnsureWriterOwnership();
            EnsureInitialized();
            ValidateEntry(entry, requireStoredFields: false);
            AppendCore(entry, allowAutomaticCompaction: true);
        }
    }

    public LuaPatchJournalCompactionResult Compact(
        LuaPatchJournalCompactionOptions? options = null)
    {
        options ??= LuaPatchJournalCompactionOptions.Default;
        ValidateCompactionOptions(options, nameof(options));
        lock (_gate)
        {
            ThrowIfDisposed();
            ThrowIfWriteFaulted();
            EnsureWriterOwnership();
            EnsureInitialized();
            return CompactCore(options);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writerLock?.Dispose();
            _writerLock = null;
        }
    }

    public ImmutableArray<LuaPatchJournalEntry> ReadAll()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var snapshot = ReadAndVerify();
            _nextSequence = snapshot.Entries.Length + 1L;
            _lastHash = snapshot.Entries.IsEmpty ? null : snapshot.Entries[^1].Hash;
            _entries = snapshot.Entries;
            _currentBytes = snapshot.Length;
            _initialized = true;
            return snapshot.Entries;
        }
    }

    public ImmutableArray<LuaPatchRecoveryRecord> GetIncompleteTransactions()
    {
        var entries = ReadAll();
        return entries.GroupBy(static entry => entry.TransactionId, StringComparer.Ordinal)
            .Select(static group => group.OrderBy(static entry => entry.Sequence).Last())
            .Where(static entry => entry.Phase is LuaPatchJournalPhase.Started or
                LuaPatchJournalPhase.Prepared or LuaPatchJournalPhase.Publishing or
                LuaPatchJournalPhase.Restoring)
            .OrderBy(static entry => entry.Sequence)
            .Select(static entry => new LuaPatchRecoveryRecord(
                entry.TransactionId,
                entry.RolloutId,
                entry.RingName,
                entry.PatchId,
                entry.TargetRevision,
                entry.Phase,
                entry.TargetIds,
                entry.Timestamp))
            .ToImmutableArray();
    }

    public ImmutableArray<LuaPatchRecoveryResult> RecoverIncomplete(
        ILuaPatchCrashRecoveryHandler handler,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        timeProvider ??= TimeProvider.System;
        var records = GetIncompleteTransactions();
        var results = ImmutableArray.CreateBuilder<LuaPatchRecoveryResult>(records.Length);
        foreach (var record in records)
        {
            using var activity = LuaPatchTelemetry.Start(
                "lunil.patch.recover",
                record.PatchId,
                record.RolloutId,
                record.RingName);
            activity?.SetTag("lunil.patch.transaction_id", record.TransactionId);
            LuaPatchRecoveryResolution resolution;
            try
            {
                resolution = handler.Recover(record);
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException and
                not StackOverflowException and
                not AccessViolationException)
            {
                LuaPatchTelemetry.Failed(activity, exception);
                LuaPatchTelemetry.RecordRecovery("Exception");
                throw;
            }

            if (!Enum.IsDefined(resolution))
            {
                var exception = new InvalidOperationException(
                    "The crash recovery handler returned an invalid result.");
                LuaPatchTelemetry.Failed(activity, exception);
                LuaPatchTelemetry.RecordRecovery("Exception");
                throw exception;
            }

            results.Add(new LuaPatchRecoveryResult(record, resolution));
            if (resolution == LuaPatchRecoveryResolution.Manual)
            {
                activity?.SetTag("lunil.patch.status", resolution.ToString());
                LuaPatchTelemetry.RecordRecovery(resolution.ToString());
                continue;
            }

            try
            {
                Append(new LuaPatchJournalEntry
                {
                    Timestamp = timeProvider.GetUtcNow(),
                    TransactionId = record.TransactionId,
                    RolloutId = record.RolloutId,
                    RingName = record.RingName,
                    PatchId = record.PatchId,
                    TargetRevision = record.TargetRevision,
                    Phase = resolution == LuaPatchRecoveryResolution.Committed
                        ? LuaPatchJournalPhase.RecoveredCommitted
                        : LuaPatchJournalPhase.RecoveredRolledBack,
                    TargetIds = record.TargetIds,
                    Message = "Crash recovery resolution recorded by the host.",
                });
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException and
                not StackOverflowException and
                not AccessViolationException)
            {
                LuaPatchTelemetry.Failed(activity, exception);
                LuaPatchTelemetry.RecordRecovery("Exception");
                throw;
            }

            var status = resolution == LuaPatchRecoveryResolution.Committed
                ? LuaPatchJournalPhase.RecoveredCommitted.ToString()
                : LuaPatchJournalPhase.RecoveredRolledBack.ToString();
            LuaPatchTelemetry.Complete(activity, status);
            LuaPatchTelemetry.RecordRecovery(status);
        }

        return results.ToImmutable();
    }

    private void AppendCore(LuaPatchJournalEntry entry, bool allowAutomaticCompaction)
    {
        var automaticCompactionAttempted = false;
        while (true)
        {
            ValidateNextEntry(_entries, entry);
            if (_nextSequence > _options.MaximumEntries)
            {
                if (TryAutomaticCompaction(
                    allowAutomaticCompaction,
                    ref automaticCompactionAttempted))
                {
                    continue;
                }

                throw Error(
                    LuaPatchJournalErrorCode.ResourceLimitExceeded,
                    "The journal entry limit was exceeded.");
            }

            var stored = StoreEntry(entry, _nextSequence, _lastHash);
            var line = JsonSerializer.SerializeToUtf8Bytes(
                stored,
                LuaPatchJsonContext.Default.LuaPatchJournalEntry);
            if (line.Length > _options.MaximumLineBytes)
            {
                throw Error(
                    LuaPatchJournalErrorCode.ResourceLimitExceeded,
                    "A journal entry exceeds the configured line limit.");
            }

            var record = new byte[line.Length + 1];
            line.CopyTo(record, 0);
            record[^1] = (byte)'\n';
            if (_currentBytes > _options.MaximumBytes - record.LongLength)
            {
                if (TryAutomaticCompaction(
                    allowAutomaticCompaction,
                    ref automaticCompactionAttempted))
                {
                    continue;
                }

                throw Error(
                    LuaPatchJournalErrorCode.ResourceLimitExceeded,
                    "The journal byte limit was exceeded.");
            }

            try
            {
                using var stream = new FileStream(
                    _path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.Read | FileShare.Delete,
                    bufferSize: 4096,
                    FileOptions.WriteThrough);
                if (stream.Length != _currentBytes)
                {
                    _writeFaulted = true;
                    throw Error(
                        LuaPatchJournalErrorCode.Corrupted,
                        "The journal changed outside its owning writer.");
                }

                stream.Seek(0, SeekOrigin.End);
                stream.Write(record);
                stream.Flush(flushToDisk: true);
            }
            catch (LuaPatchJournalException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                if (TryConfirmAppend(stored))
                {
                    return;
                }

                _writeFaulted = true;
                throw new LuaPatchJournalException(
                    LuaPatchJournalErrorCode.IoFailure,
                    "The deployment journal could not be appended.",
                    exception);
            }

            _lastHash = stored.Hash;
            _nextSequence++;
            _entries = _entries.Add(stored);
            _currentBytes += record.LongLength;
            return;
        }
    }

    private bool TryAutomaticCompaction(bool allowed, ref bool attempted)
    {
        if (!allowed || attempted || _options.AutomaticCompaction is null)
        {
            return false;
        }

        attempted = true;
        return CompactCore(_options.AutomaticCompaction).Changed;
    }

    private LuaPatchJournalCompactionResult CompactCore(
        LuaPatchJournalCompactionOptions options)
    {
        var original = _entries;
        var originalBytes = _currentBytes;
        var grouped = original
            .GroupBy(static entry => entry.TransactionId, StringComparer.Ordinal)
            .Select(static group => new
            {
                TransactionId = group.Key,
                Last = group.OrderBy(static entry => entry.Sequence).Last(),
            })
            .ToArray();
        var retainedIds = grouped
            .Where(static group => IsIncomplete(group.Last.Phase))
            .Select(static group => group.TransactionId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var completed in grouped
            .Where(static group => !IsIncomplete(group.Last.Phase))
            .OrderByDescending(static group => group.Last.Sequence)
            .Take(options.RetainCompletedTransactions))
        {
            retainedIds.Add(completed.TransactionId);
        }

        var retainedSource = original
            .Where(entry => retainedIds.Contains(entry.TransactionId))
            .ToImmutableArray();
        if (retainedSource.Length == original.Length)
        {
            return new LuaPatchJournalCompactionResult(
                original.Length,
                original.Length,
                originalBytes,
                originalBytes,
                original.IsEmpty ? null : original[^1].Hash,
                original.IsEmpty ? null : original[^1].Hash);
        }

        var replacement = BuildJournal(retainedSource);
        ReplaceJournal(replacement.Bytes, replacement.Entries);
        _entries = replacement.Entries;
        _currentBytes = replacement.Bytes.LongLength;
        _nextSequence = replacement.Entries.Length + 1L;
        _lastHash = replacement.Entries.IsEmpty ? null : replacement.Entries[^1].Hash;
        _initialized = true;
        return new LuaPatchJournalCompactionResult(
            original.Length,
            replacement.Entries.Length,
            originalBytes,
            replacement.Bytes.LongLength,
            original.IsEmpty ? null : original[^1].Hash,
            replacement.Entries.IsEmpty ? null : replacement.Entries[^1].Hash);
    }

    private JournalReplacement BuildJournal(ImmutableArray<LuaPatchJournalEntry> source)
    {
        if (source.Length > _options.MaximumEntries)
        {
            throw Error(
                LuaPatchJournalErrorCode.ResourceLimitExceeded,
                "The retained journal entries exceed the configured entry limit.");
        }

        using var buffer = new MemoryStream();
        var entries = ImmutableArray.CreateBuilder<LuaPatchJournalEntry>(source.Length);
        string? previousHash = null;
        long sequence = 1;
        foreach (var entry in source)
        {
            var unstored = entry with { Sequence = 0, PreviousHash = null, Hash = null };
            ValidateNextEntry(entries, unstored);
            var stored = StoreEntry(unstored, sequence, previousHash);
            var line = JsonSerializer.SerializeToUtf8Bytes(
                stored,
                LuaPatchJsonContext.Default.LuaPatchJournalEntry);
            if (line.Length > _options.MaximumLineBytes ||
                buffer.Length > _options.MaximumBytes - line.LongLength - 1L)
            {
                throw Error(
                    LuaPatchJournalErrorCode.ResourceLimitExceeded,
                    "The retained journal entries exceed the configured byte or line limit.");
            }

            buffer.Write(line);
            buffer.WriteByte((byte)'\n');
            entries.Add(stored);
            previousHash = stored.Hash;
            sequence++;
        }

        return new JournalReplacement(entries.ToImmutable(), buffer.ToArray());
    }

    private void ReplaceJournal(
        byte[] bytes,
        ImmutableArray<LuaPatchJournalEntry> expectedEntries)
    {
        var temporaryPath = _path + ".compact.tmp";
        var replacementReturned = false;
        try
        {
            File.Delete(temporaryPath);
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                File.Replace(temporaryPath, _path, destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _path);
            }

            replacementReturned = true;
            FlushContainingDirectory();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (!replacementReturned &&
                TryConfirmReplacement(expectedEntries, bytes.LongLength))
            {
                return;
            }

            _writeFaulted = true;
            throw new LuaPatchJournalException(
                LuaPatchJournalErrorCode.IoFailure,
                "The deployment journal could not be compacted atomically.",
                exception);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception) when (_writeFaulted)
            {
                // Preserve the primary failure. The fixed temporary path is retried after reopen.
            }
        }
    }

    private void FlushContainingDirectory()
        => LuaPatchDurableFileSystem.FlushDirectory(System.IO.Path.GetDirectoryName(_path)!);

    private void EnsureWriterOwnership()
    {
        if (_writerLock is not null)
        {
            return;
        }

        EnsureDirectory();
        var writerLock = LuaPatchFileLock.TryOpenExclusive(
            WriterLockPath,
            out var exception,
            out _);
        if (writerLock is null)
        {
            throw new LuaPatchJournalException(
                LuaPatchJournalErrorCode.WriterUnavailable,
                "Another journal writer owns this path, or the writer lock cannot be opened.",
                exception!);
        }

        _writerLock = writerLock;
        _initialized = false;
    }

    private void EnsureDirectory()
    {
        var directory = System.IO.Path.GetDirectoryName(_path)!;
        if (Directory.Exists(directory))
        {
            return;
        }

        if (!_options.CreateDirectory)
        {
            throw Error(
                LuaPatchJournalErrorCode.IoFailure,
                "The journal directory does not exist.");
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new LuaPatchJournalException(
                LuaPatchJournalErrorCode.IoFailure,
                "The journal directory could not be created.",
                exception);
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        var snapshot = ReadAndVerify();
        _nextSequence = snapshot.Entries.Length + 1L;
        _lastHash = snapshot.Entries.IsEmpty ? null : snapshot.Entries[^1].Hash;
        _entries = snapshot.Entries;
        _currentBytes = snapshot.Length;
        _initialized = true;
    }

    private VerifiedJournal ReadAndVerify()
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return ReadAndVerifyOnce();
            }
            catch (IncompleteJournalTailException)
            {
                if (Stopwatch.GetElapsedTime(started) >= _options.ConcurrentReadTimeout)
                {
                    throw Error(
                        LuaPatchJournalErrorCode.Corrupted,
                        "The journal ends with an incomplete record.");
                }

                Thread.Sleep(1);
            }
            catch (LuaPatchJournalException exception) when (
                exception.Code == LuaPatchJournalErrorCode.IoFailure &&
                exception.InnerException is IOException)
            {
                if (Stopwatch.GetElapsedTime(started) >= _options.ConcurrentReadTimeout)
                {
                    throw;
                }

                Thread.Sleep(1);
            }
        }
    }

    private VerifiedJournal ReadAndVerifyOnce()
    {
        if (!File.Exists(_path))
        {
            return new VerifiedJournal([], 0);
        }

        try
        {
            using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            var lengthOnDisk = stream.Length;
            if (lengthOnDisk > _options.MaximumBytes || lengthOnDisk > int.MaxValue)
            {
                throw Error(
                    LuaPatchJournalErrorCode.ResourceLimitExceeded,
                    "The journal exceeds the configured byte limit.");
            }

            var bytes = GC.AllocateUninitializedArray<byte>((int)lengthOnDisk);
            stream.ReadExactly(bytes);

            if (bytes.Length != 0 && bytes[^1] != (byte)'\n')
            {
                throw new IncompleteJournalTailException();
            }

            var entries = ImmutableArray.CreateBuilder<LuaPatchJournalEntry>();
            var verifiedEntries = new List<LuaPatchJournalEntry>();
            var offset = 0;
            string? previousHash = null;
            long expectedSequence = 1;
            while (offset < bytes.Length)
            {
                var newline = Array.IndexOf(bytes, (byte)'\n', offset);
                var length = newline - offset;
                if (length <= 0 || length > _options.MaximumLineBytes)
                {
                    throw Error(
                        LuaPatchJournalErrorCode.ResourceLimitExceeded,
                        "A journal record has an invalid length.");
                }

                if (entries.Count >= _options.MaximumEntries)
                {
                    throw Error(
                        LuaPatchJournalErrorCode.ResourceLimitExceeded,
                        "The journal entry limit was exceeded.");
                }

                LuaPatchJournalEntry entry;
                try
                {
                    entry = JsonSerializer.Deserialize(
                        bytes.AsSpan(offset, length),
                        LuaPatchJsonContext.Default.LuaPatchJournalEntry) ??
                        throw Error(
                            LuaPatchJournalErrorCode.Corrupted,
                            "A journal record is empty.");
                }
                catch (JsonException exception)
                {
                    throw new LuaPatchJournalException(
                        LuaPatchJournalErrorCode.Corrupted,
                        "A journal record is invalid JSON.",
                        exception);
                }

                ValidateEntry(entry, requireStoredFields: true);
                ValidateNextEntry(verifiedEntries, entry);
                if (entry.Sequence != expectedSequence)
                {
                    throw Error(
                        LuaPatchJournalErrorCode.SequenceMismatch,
                        "The journal sequence is not contiguous.");
                }

                if (!string.Equals(entry.PreviousHash, previousHash, StringComparison.Ordinal))
                {
                    throw Error(
                        LuaPatchJournalErrorCode.HashMismatch,
                        "The journal hash chain is broken.");
                }

                var payload = ToPayload(entry, entry.Sequence, entry.PreviousHash);
                var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(
                    payload,
                    LuaPatchJsonContext.Default.LuaPatchJournalHashPayload);
                var expectedHash = Convert.ToHexString(SHA256.HashData(payloadBytes));
                if (!string.Equals(expectedHash, entry.Hash, StringComparison.Ordinal))
                {
                    throw Error(
                        LuaPatchJournalErrorCode.HashMismatch,
                        "A journal record hash is invalid.");
                }

                var canonical = JsonSerializer.SerializeToUtf8Bytes(
                    entry,
                    LuaPatchJsonContext.Default.LuaPatchJournalEntry);
                if (!bytes.AsSpan(offset, length).SequenceEqual(canonical))
                {
                    throw Error(
                        LuaPatchJournalErrorCode.Corrupted,
                        "A journal record is not canonically encoded.");
                }

                entries.Add(entry);
                verifiedEntries.Add(entry);
                previousHash = entry.Hash;
                expectedSequence++;
                offset = newline + 1;
            }

            return new VerifiedJournal(entries.ToImmutable(), bytes.LongLength);
        }
        catch (IncompleteJournalTailException)
        {
            throw;
        }
        catch (LuaPatchJournalException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new LuaPatchJournalException(
                LuaPatchJournalErrorCode.IoFailure,
                "The deployment journal could not be read.",
                exception);
        }
    }

    private static void ValidateEntry(LuaPatchJournalEntry entry, bool requireStoredFields)
    {
        if (entry.Timestamp == default ||
            string.IsNullOrWhiteSpace(entry.TransactionId) ||
            string.IsNullOrWhiteSpace(entry.RolloutId) ||
            string.IsNullOrWhiteSpace(entry.RingName) ||
            string.IsNullOrWhiteSpace(entry.PatchId) ||
            string.IsNullOrWhiteSpace(entry.TargetRevision) ||
            !Enum.IsDefined(entry.Phase) ||
            entry.TargetIds.IsDefaultOrEmpty ||
            entry.TargetIds.Any(string.IsNullOrWhiteSpace) ||
            !entry.TargetIds.SequenceEqual(entry.TargetIds.Order(StringComparer.Ordinal)) ||
            entry.TargetIds.Distinct(StringComparer.Ordinal).Count() != entry.TargetIds.Length ||
            requireStoredFields && (entry.Sequence <= 0 || string.IsNullOrWhiteSpace(entry.Hash)) ||
            !requireStoredFields && (entry.Sequence != 0 || entry.PreviousHash is not null ||
                entry.Hash is not null) ||
            entry.Hash is not null && !IsSha256(entry.Hash) ||
            entry.PreviousHash is not null && !IsSha256(entry.PreviousHash))
        {
            throw Error(
                LuaPatchJournalErrorCode.InvalidEntry,
                "A deployment journal entry is invalid.");
        }
    }

    private bool TryConfirmAppend(LuaPatchJournalEntry expected)
    {
        try
        {
            var snapshot = ReadAndVerify();
            if (snapshot.Entries.Length == expected.Sequence &&
                snapshot.Entries[^1].Sequence == expected.Sequence &&
                string.Equals(snapshot.Entries[^1].Hash, expected.Hash, StringComparison.Ordinal))
            {
                _entries = snapshot.Entries;
                _lastHash = expected.Hash;
                _nextSequence = expected.Sequence + 1;
                _currentBytes = snapshot.Length;
                _initialized = true;
                return true;
            }

            return false;
        }
        catch (LuaPatchJournalException)
        {
            return false;
        }
    }

    private bool TryConfirmReplacement(
        ImmutableArray<LuaPatchJournalEntry> expected,
        long expectedLength)
    {
        try
        {
            var snapshot = ReadAndVerify();
            return snapshot.Length == expectedLength &&
                snapshot.Entries.SequenceEqual(expected);
        }
        catch (LuaPatchJournalException)
        {
            return false;
        }
    }

    private static void ValidateNextEntry(
        IReadOnlyCollection<LuaPatchJournalEntry> entries,
        LuaPatchJournalEntry entry)
    {
        var previous = entries.LastOrDefault(candidate => string.Equals(
            candidate.TransactionId,
            entry.TransactionId,
            StringComparison.Ordinal));
        if (previous is null)
        {
            if (entry.Phase != LuaPatchJournalPhase.Started)
            {
                throw Error(
                    LuaPatchJournalErrorCode.InvalidTransition,
                    "A journal transaction must begin with Started.");
            }

            return;
        }

        if (!string.Equals(previous.RolloutId, entry.RolloutId, StringComparison.Ordinal) ||
            !string.Equals(previous.RingName, entry.RingName, StringComparison.Ordinal) ||
            !string.Equals(previous.PatchId, entry.PatchId, StringComparison.Ordinal) ||
            !string.Equals(previous.TargetRevision, entry.TargetRevision, StringComparison.Ordinal) ||
            !previous.TargetIds.SequenceEqual(entry.TargetIds, StringComparer.Ordinal))
        {
            throw Error(
                LuaPatchJournalErrorCode.InvalidTransition,
                "Journal transaction metadata changed between phases.");
        }

        var valid = previous.Phase switch
        {
            LuaPatchJournalPhase.Started => entry.Phase is
                LuaPatchJournalPhase.Prepared or
                LuaPatchJournalPhase.Failed or
                LuaPatchJournalPhase.RolledBack or
                LuaPatchJournalPhase.RecoveredCommitted or
                LuaPatchJournalPhase.RecoveredRolledBack,
            LuaPatchJournalPhase.Prepared => entry.Phase is
                LuaPatchJournalPhase.Publishing or
                LuaPatchJournalPhase.RolledBack or
                LuaPatchJournalPhase.RecoveredCommitted or
                LuaPatchJournalPhase.RecoveredRolledBack,
            LuaPatchJournalPhase.Publishing => entry.Phase is
                LuaPatchJournalPhase.Committed or
                LuaPatchJournalPhase.Restoring or
                LuaPatchJournalPhase.RolledBack or
                LuaPatchJournalPhase.RecoveredCommitted or
                LuaPatchJournalPhase.RecoveredRolledBack,
            LuaPatchJournalPhase.Restoring => entry.Phase is
                LuaPatchJournalPhase.Committed or
                LuaPatchJournalPhase.RecoveredCommitted or
                LuaPatchJournalPhase.RecoveredRolledBack,
            _ => false,
        };
        if (!valid)
        {
            throw Error(
                LuaPatchJournalErrorCode.InvalidTransition,
                $"Journal phase transition '{previous.Phase}' -> '{entry.Phase}' is invalid.");
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == SHA256.HashSizeInBytes * 2 &&
        value.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool IsIncomplete(LuaPatchJournalPhase phase) =>
        phase is LuaPatchJournalPhase.Started or
            LuaPatchJournalPhase.Prepared or
            LuaPatchJournalPhase.Publishing or
            LuaPatchJournalPhase.Restoring;

    private static LuaPatchJournalEntry StoreEntry(
        LuaPatchJournalEntry entry,
        long sequence,
        string? previousHash)
    {
        var payload = ToPayload(entry, sequence, previousHash);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            LuaPatchJsonContext.Default.LuaPatchJournalHashPayload);
        return entry with
        {
            Sequence = sequence,
            PreviousHash = previousHash,
            Hash = Convert.ToHexString(SHA256.HashData(payloadBytes)),
        };
    }

    private static void ValidateCompactionOptions(
        LuaPatchJournalCompactionOptions? options,
        string parameterName)
    {
        if (options is not null && options.RetainCompletedTransactions < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The retained completed transaction count cannot be negative.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ThrowIfWriteFaulted()
    {
        if (_writeFaulted)
        {
            throw Error(
                LuaPatchJournalErrorCode.IoFailure,
                "The journal writer is faulted; reopen the journal after inspecting its tail.");
        }
    }

    private static LuaPatchJournalHashPayload ToPayload(
        LuaPatchJournalEntry entry,
        long sequence,
        string? previousHash) => new()
        {
            Sequence = sequence,
            Timestamp = entry.Timestamp,
            TransactionId = entry.TransactionId,
            RolloutId = entry.RolloutId,
            RingName = entry.RingName,
            PatchId = entry.PatchId,
            TargetRevision = entry.TargetRevision,
            Phase = entry.Phase,
            TargetIds = entry.TargetIds,
            Message = entry.Message,
            PreviousHash = previousHash,
        };

    private static LuaPatchJournalException Error(
        LuaPatchJournalErrorCode code,
        string message) => new(code, message);

    private readonly record struct VerifiedJournal(
        ImmutableArray<LuaPatchJournalEntry> Entries,
        long Length);

    private readonly record struct JournalReplacement(
        ImmutableArray<LuaPatchJournalEntry> Entries,
        byte[] Bytes);

    private sealed class IncompleteJournalTailException : Exception;
}

internal sealed record LuaPatchJournalHashPayload
{
    public required long Sequence { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required string TransactionId { get; init; }

    public required string RolloutId { get; init; }

    public required string RingName { get; init; }

    public required string PatchId { get; init; }

    public required string TargetRevision { get; init; }

    public required LuaPatchJournalPhase Phase { get; init; }

    public required ImmutableArray<string> TargetIds { get; init; }

    public string? Message { get; init; }

    public string? PreviousHash { get; init; }
}
