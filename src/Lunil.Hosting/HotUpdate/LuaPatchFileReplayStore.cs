using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lunil.Hosting;

public enum LuaPatchReplayStoreErrorCode : byte
{
    InvalidRecord,
    Corrupted,
    HashMismatch,
    SequenceMismatch,
    ResourceLimitExceeded,
    IoFailure,
    WriterUnavailable,
}

public sealed class LuaPatchReplayStoreException : Exception
{
    public LuaPatchReplayStoreException(
        LuaPatchReplayStoreErrorCode code,
        string message) : base(message) => Code = code;

    public LuaPatchReplayStoreException(
        LuaPatchReplayStoreErrorCode code,
        string message,
        Exception innerException) : base(message, innerException) => Code = code;

    public LuaPatchReplayStoreErrorCode Code { get; }
}

public sealed record LuaPatchFileReplayStoreOptions
{
    public static LuaPatchFileReplayStoreOptions Default { get; } = new();

    public long MaximumBytes { get; init; } = 64L * 1024 * 1024;

    public int MaximumEntries { get; init; } = 1_000_000;

    public int MaximumLineBytes { get; init; } = 16 * 1024;

    public int MaximumIdentityCharacters { get; init; } = 1024;

    public bool CreateDirectory { get; init; } = true;

    public TimeSpan WriterLockTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan CommitLockTimeout { get; init; } = TimeSpan.Zero;
}

[JsonConverter(typeof(JsonStringEnumConverter<LuaPatchReplayRecordState>))]
public enum LuaPatchReplayRecordState : byte
{
    Reserved,
    Committed,
    Reopened,
}

/// <summary>One canonical audit event in a scoped replay-reservation state machine.</summary>
public sealed record LuaPatchReplayRecord
{
    public long Sequence { get; init; }

    public required string Scope { get; init; }

    public required string PatchId { get; init; }

    public required string Nonce { get; init; }

    public required string ReservationId { get; init; }

    public required LuaPatchReplayRecordState State { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public string? PreviousHash { get; init; }

    public required string Hash { get; init; }
}

/// <summary>
/// Append-only, hash-chained replay store with target-scoped, crash-resumable reservations.
/// Commit leases use per-reservation operating-system locks, so different rollout targets can
/// commit concurrently while the same target identity remains exclusive across processes.
/// </summary>
public sealed class LuaPatchFileReplayStore : ILuaPatchReplayStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly LuaPatchFileReplayStoreOptions _options;

    public LuaPatchFileReplayStore(
        string path,
        LuaPatchFileReplayStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        _options = options ?? LuaPatchFileReplayStoreOptions.Default;
        if (_options.MaximumBytes <= 0 ||
            _options.MaximumEntries <= 0 ||
            _options.MaximumLineBytes <= 0 ||
            _options.MaximumIdentityCharacters <= 0 ||
            _options.MaximumLineBytes > _options.MaximumBytes ||
            !IsValidTimeout(_options.WriterLockTimeout) ||
            !IsValidTimeout(_options.CommitLockTimeout))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Replay-store limits must be positive and lock timeouts must be non-negative.");
        }
    }

    public string Path => _path;

    public string WriterLockPath => _path + ".writer.lock";

    public string CommitLockDirectory => _path + ".commit-locks";

    public LuaPatchReplayReservationResult TryReserve(
        string scope,
        string patchId,
        string nonce,
        DateTimeOffset reservedAt)
    {
        ValidateIdentity(scope, patchId, nonce, reservedAt);
        lock (_gate)
        {
            using var writerLock = AcquireWriterLock();
            var snapshot = ReadAndVerify();
            if (TryFindCollision(snapshot.States, scope, patchId, nonce, out var existing))
            {
                if (existing is not null &&
                    string.Equals(existing.PatchId, patchId, StringComparison.Ordinal) &&
                    string.Equals(existing.Nonce, nonce, StringComparison.Ordinal) &&
                    existing.State is not LuaPatchReplayRecordState.Committed)
                {
                    return Reserved(existing.ToReservation());
                }

                return Replayed(existing?.State is LuaPatchReplayRecordState.Committed
                    ? "The scoped patch identity was already committed."
                    : "The scoped patch id or nonce conflicts with another reservation.");
            }

            var reservation = new LuaPatchReplayReservation(
                scope,
                patchId,
                nonce,
                Guid.NewGuid().ToString("N"),
                reservedAt.ToUniversalTime());
            AppendEvent(snapshot, reservation, LuaPatchReplayRecordState.Reserved, reservedAt);
            return Reserved(reservation);
        }
    }

    public ILuaPatchReplayCommitLease? TryAcquireCommit(
        LuaPatchReplayReservation reservation,
        DateTimeOffset acquiredAt)
    {
        ValidateReservation(reservation);
        ArgumentOutOfRangeException.ThrowIfEqual(acquiredAt, default, nameof(acquiredAt));
        var commitLock = TryAcquireCommitLock(reservation);
        if (commitLock is null)
        {
            return null;
        }

        try
        {
            lock (_gate)
            {
                using var writerLock = AcquireWriterLock();
                var snapshot = ReadAndVerify();
                if (!snapshot.States.TryGetValue(Key(reservation.Scope, reservation.ReservationId), out var state) ||
                    !Matches(state, reservation) ||
                    state.State is LuaPatchReplayRecordState.Committed)
                {
                    commitLock.Dispose();
                    return null;
                }
            }

            return new CommitLease(this, reservation, commitLock);
        }
        catch
        {
            commitLock.Dispose();
            throw;
        }
    }

    public ImmutableArray<LuaPatchReplayRecord> ReadAll()
    {
        lock (_gate)
        {
            using var writerLock = AcquireWriterLock();
            return ReadAndVerify().Entries;
        }
    }

    private void Complete(LuaPatchReplayReservation reservation, DateTimeOffset committedAt)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(committedAt, default, nameof(committedAt));
        lock (_gate)
        {
            using var writerLock = AcquireWriterLock();
            var snapshot = ReadAndVerify();
            var state = GetExpectedState(snapshot, reservation);
            if (state.State is LuaPatchReplayRecordState.Committed)
            {
                return;
            }

            AppendEvent(snapshot, reservation, LuaPatchReplayRecordState.Committed, committedAt);
        }
    }

    private void Reopen(LuaPatchReplayReservation reservation, DateTimeOffset reopenedAt)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(reopenedAt, default, nameof(reopenedAt));
        lock (_gate)
        {
            using var writerLock = AcquireWriterLock();
            var snapshot = ReadAndVerify();
            var state = GetExpectedState(snapshot, reservation);
            if (state.State is not LuaPatchReplayRecordState.Committed)
            {
                return;
            }

            AppendEvent(snapshot, reservation, LuaPatchReplayRecordState.Reopened, reopenedAt);
        }
    }

    private static ReplayState GetExpectedState(
        VerifiedReplayStore snapshot,
        LuaPatchReplayReservation reservation)
    {
        if (!snapshot.States.TryGetValue(Key(reservation.Scope, reservation.ReservationId), out var state) ||
            !Matches(state, reservation))
        {
            throw Error(
                LuaPatchReplayStoreErrorCode.InvalidRecord,
                "The replay reservation is not present in this store.");
        }

        return state;
    }

    private void AppendEvent(
        VerifiedReplayStore snapshot,
        LuaPatchReplayReservation reservation,
        LuaPatchReplayRecordState state,
        DateTimeOffset timestamp)
    {
        if (snapshot.Entries.Length >= _options.MaximumEntries)
        {
            throw Error(
                LuaPatchReplayStoreErrorCode.ResourceLimitExceeded,
                "The replay store reached its configured entry limit.");
        }

        var record = StoreRecord(
            snapshot.Entries.Length + 1L,
            reservation,
            state,
            timestamp.ToUniversalTime(),
            snapshot.Entries.IsEmpty ? null : snapshot.Entries[^1].Hash);
        var line = JsonSerializer.SerializeToUtf8Bytes(
            record,
            LuaPatchJsonContext.Default.LuaPatchReplayRecord);
        if (line.Length > _options.MaximumLineBytes ||
            snapshot.Length > _options.MaximumBytes - line.Length - 1L)
        {
            throw Error(
                LuaPatchReplayStoreErrorCode.ResourceLimitExceeded,
                "The replay store reached its configured byte limit.");
        }

        Append(snapshot.Length, line);
    }

    private FileStream AcquireWriterLock() => AcquireFileLock(
        WriterLockPath,
        _options.WriterLockTimeout,
        LuaPatchReplayStoreErrorCode.WriterUnavailable,
        "The replay-store writer lock is unavailable.");

    private HeldCommitLock? TryAcquireCommitLock(LuaPatchReplayReservation reservation)
    {
        EnsureDirectory(CommitLockDirectory);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            reservation.Scope + "\0" + reservation.ReservationId)));
        var path = System.IO.Path.Combine(CommitLockDirectory, name + ".lock");
        var started = Stopwatch.GetTimestamp();
        var stream = TryAcquireFileLock(path, _options.CommitLockTimeout, started);
        return stream is null ? null : new HeldCommitLock(stream);
    }

    private FileStream AcquireFileLock(
        string path,
        TimeSpan timeout,
        LuaPatchReplayStoreErrorCode errorCode,
        string message)
    {
        EnsureParentDirectory(path);
        var started = Stopwatch.GetTimestamp();
        IOException? lastException = null;
        while (true)
        {
            var stream = TryOpenLockedFile(path, out var exception, out var contention);
            if (stream is not null)
            {
                return stream;
            }

            lastException = exception;
            if (!contention)
            {
                throw new LuaPatchReplayStoreException(errorCode, message, lastException!);
            }

            if (Elapsed(started, timeout))
            {
                throw new LuaPatchReplayStoreException(errorCode, message, lastException!);
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(10));
        }
    }

    private FileStream? TryAcquireFileLock(string path, TimeSpan timeout, long started)
    {
        EnsureParentDirectory(path);
        while (true)
        {
            var stream = TryOpenLockedFile(path, out var exception, out var contention);
            if (stream is not null)
            {
                return stream;
            }

            if (!contention)
            {
                throw new LuaPatchReplayStoreException(
                    LuaPatchReplayStoreErrorCode.IoFailure,
                    "The replay commit lock could not be opened.",
                    exception!);
            }

            if (Elapsed(started, timeout))
            {
                return null;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(10));
        }
    }

    private static FileStream? TryOpenLockedFile(
        string path,
        out IOException? exception,
        out bool contention) => LuaPatchFileLock.TryOpenExclusive(
            path,
            out exception,
            out contention);

    private VerifiedReplayStore ReadAndVerify()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new VerifiedReplayStore([], 0, new Dictionary<(string, string), ReplayState>());
            }

            using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length > _options.MaximumBytes || stream.Length > int.MaxValue)
            {
                throw Error(
                    LuaPatchReplayStoreErrorCode.ResourceLimitExceeded,
                    "The replay store exceeds its configured byte limit.");
            }

            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (bytes.Length == 0)
            {
                return new VerifiedReplayStore([], 0, new Dictionary<(string, string), ReplayState>());
            }

            if (bytes[^1] != (byte)'\n')
            {
                throw Error(
                    LuaPatchReplayStoreErrorCode.Corrupted,
                    "The replay store has an incomplete final record.");
            }

            var entries = ImmutableArray.CreateBuilder<LuaPatchReplayRecord>();
            var states = new Dictionary<(string, string), ReplayState>();
            var patchIds = new Dictionary<(string, string), (string, string)>();
            var nonces = new Dictionary<(string, string), (string, string)>();
            var offset = 0;
            long expectedSequence = 1;
            string? previousHash = null;
            while (offset < bytes.Length)
            {
                var length = bytes.AsSpan(offset).IndexOf((byte)'\n');
                if (length <= 0 || length > _options.MaximumLineBytes)
                {
                    throw Error(
                        LuaPatchReplayStoreErrorCode.Corrupted,
                        "A replay-store record has an invalid length.");
                }

                if (entries.Count >= _options.MaximumEntries)
                {
                    throw Error(
                        LuaPatchReplayStoreErrorCode.ResourceLimitExceeded,
                        "The replay store exceeds its configured entry limit.");
                }

                LuaPatchReplayRecord record;
                try
                {
                    record = JsonSerializer.Deserialize(
                        bytes.AsSpan(offset, length),
                        LuaPatchJsonContext.Default.LuaPatchReplayRecord) ??
                        throw Error(
                            LuaPatchReplayStoreErrorCode.Corrupted,
                            "A replay-store record is empty.");
                }
                catch (JsonException exception)
                {
                    throw new LuaPatchReplayStoreException(
                        LuaPatchReplayStoreErrorCode.Corrupted,
                        "A replay-store record is invalid JSON.",
                        exception);
                }

                ValidateRecord(record);
                if (record.Sequence != expectedSequence)
                {
                    throw Error(
                        LuaPatchReplayStoreErrorCode.SequenceMismatch,
                        "The replay-store sequence is not contiguous.");
                }

                if (!string.Equals(record.PreviousHash, previousHash, StringComparison.Ordinal) ||
                    !string.Equals(Hash(ToPayload(record)), record.Hash, StringComparison.Ordinal))
                {
                    throw Error(
                        LuaPatchReplayStoreErrorCode.HashMismatch,
                        "The replay-store hash chain or record hash is invalid.");
                }

                var canonical = JsonSerializer.SerializeToUtf8Bytes(
                    record,
                    LuaPatchJsonContext.Default.LuaPatchReplayRecord);
                if (!bytes.AsSpan(offset, length).SequenceEqual(canonical))
                {
                    throw Error(
                        LuaPatchReplayStoreErrorCode.Corrupted,
                        "A replay-store record is not canonically encoded.");
                }

                ApplyState(record, states, patchIds, nonces);
                entries.Add(record);
                previousHash = record.Hash;
                expectedSequence++;
                offset += length + 1;
            }

            return new VerifiedReplayStore(entries.ToImmutable(), bytes.LongLength, states);
        }
        catch (LuaPatchReplayStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LuaPatchReplayStoreException(
                LuaPatchReplayStoreErrorCode.IoFailure,
                "The replay store could not be read.",
                exception);
        }
    }

    private void Append(long expectedLength, ReadOnlySpan<byte> line)
    {
        try
        {
            using var stream = new FileStream(
                _path,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            if (stream.Length != expectedLength)
            {
                throw Error(
                    LuaPatchReplayStoreErrorCode.IoFailure,
                    "The replay store changed while its writer lock was held.");
            }

            stream.Position = stream.Length;
            stream.Write(line);
            stream.WriteByte((byte)'\n');
            stream.Flush(flushToDisk: true);
            if (expectedLength == 0)
            {
                FlushContainingDirectory();
            }
        }
        catch (LuaPatchReplayStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LuaPatchReplayStoreException(
                LuaPatchReplayStoreErrorCode.IoFailure,
                "The replay event could not be durably appended.",
                exception);
        }
    }

    private void FlushContainingDirectory()
        => LuaPatchDurableFileSystem.FlushDirectory(System.IO.Path.GetDirectoryName(_path)!);

    private static void ApplyState(
        LuaPatchReplayRecord record,
        Dictionary<(string, string), ReplayState> states,
        Dictionary<(string, string), (string, string)> patchIds,
        Dictionary<(string, string), (string, string)> nonces)
    {
        var key = Key(record.Scope, record.ReservationId);
        var patchKey = Key(record.Scope, record.PatchId);
        var nonceKey = Key(record.Scope, record.Nonce);
        if (!states.TryGetValue(key, out var previous))
        {
            if (record.State is not LuaPatchReplayRecordState.Reserved ||
                patchIds.ContainsKey(patchKey) || nonces.ContainsKey(nonceKey))
            {
                throw Error(
                    LuaPatchReplayStoreErrorCode.InvalidRecord,
                    "A replay reservation starts with an invalid state or scoped identity collision.");
            }

            states.Add(key, ReplayState.From(record));
            patchIds.Add(patchKey, key);
            nonces.Add(nonceKey, key);
            return;
        }

        if (!string.Equals(previous.PatchId, record.PatchId, StringComparison.Ordinal) ||
            !string.Equals(previous.Nonce, record.Nonce, StringComparison.Ordinal) ||
            record.Timestamp < previous.LastTimestamp ||
            record.State switch
            {
                LuaPatchReplayRecordState.Committed =>
                    previous.State is LuaPatchReplayRecordState.Committed,
                LuaPatchReplayRecordState.Reopened =>
                    previous.State is not LuaPatchReplayRecordState.Committed,
                _ => true,
            })
        {
            throw Error(
                LuaPatchReplayStoreErrorCode.InvalidRecord,
                "A replay reservation contains an invalid state transition.");
        }

        states[key] = ReplayState.From(record, previous.ReservedAt);
    }

    private static bool TryFindCollision(
        IReadOnlyDictionary<(string, string), ReplayState> states,
        string scope,
        string patchId,
        string nonce,
        out ReplayState? collision)
    {
        collision = states.Values.FirstOrDefault(state =>
            string.Equals(state.Scope, scope, StringComparison.Ordinal) &&
            (string.Equals(state.PatchId, patchId, StringComparison.Ordinal) ||
                string.Equals(state.Nonce, nonce, StringComparison.Ordinal)));
        return collision is not null;
    }

    private static LuaPatchReplayRecord StoreRecord(
        long sequence,
        LuaPatchReplayReservation reservation,
        LuaPatchReplayRecordState state,
        DateTimeOffset timestamp,
        string? previousHash)
    {
        var payload = new LuaPatchReplayHashPayload(
            sequence,
            reservation.Scope,
            reservation.PatchId,
            reservation.Nonce,
            reservation.ReservationId,
            state,
            timestamp,
            previousHash);
        return new LuaPatchReplayRecord
        {
            Sequence = sequence,
            Scope = reservation.Scope,
            PatchId = reservation.PatchId,
            Nonce = reservation.Nonce,
            ReservationId = reservation.ReservationId,
            State = state,
            Timestamp = timestamp,
            PreviousHash = previousHash,
            Hash = Hash(payload),
        };
    }

    private static LuaPatchReplayHashPayload ToPayload(LuaPatchReplayRecord record) => new(
        record.Sequence,
        record.Scope,
        record.PatchId,
        record.Nonce,
        record.ReservationId,
        record.State,
        record.Timestamp,
        record.PreviousHash);

    private static string Hash(LuaPatchReplayHashPayload payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            LuaPatchJsonContext.Default.LuaPatchReplayHashPayload);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private void ValidateRecord(LuaPatchReplayRecord record)
    {
        if (record.Sequence <= 0 ||
            string.IsNullOrWhiteSpace(record.Scope) ||
            string.IsNullOrWhiteSpace(record.PatchId) ||
            string.IsNullOrWhiteSpace(record.Nonce) ||
            string.IsNullOrWhiteSpace(record.ReservationId) ||
            !Enum.IsDefined(record.State) ||
            record.Timestamp == default ||
            !IsSha256(record.Hash) ||
            record.PreviousHash is not null && !IsSha256(record.PreviousHash))
        {
            throw Error(
                LuaPatchReplayStoreErrorCode.InvalidRecord,
                "A replay-store record is invalid.");
        }

        if (record.Scope.Length > _options.MaximumIdentityCharacters ||
            record.PatchId.Length > _options.MaximumIdentityCharacters ||
            record.Nonce.Length > _options.MaximumIdentityCharacters ||
            record.ReservationId.Length > _options.MaximumIdentityCharacters)
        {
            throw Error(
                LuaPatchReplayStoreErrorCode.ResourceLimitExceeded,
                "A replay-store identity exceeds the configured character limit.");
        }
    }

    private void ValidateIdentity(
        string scope,
        string patchId,
        string nonce,
        DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(patchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        ArgumentOutOfRangeException.ThrowIfEqual(timestamp, default, nameof(timestamp));
        ValidateIdentityLength(scope, nameof(scope));
        ValidateIdentityLength(patchId, nameof(patchId));
        ValidateIdentityLength(nonce, nameof(nonce));
    }

    private void ValidateReservation(LuaPatchReplayReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ValidateIdentity(
            reservation.Scope,
            reservation.PatchId,
            reservation.Nonce,
            reservation.ReservedAt);
        ArgumentException.ThrowIfNullOrWhiteSpace(reservation.ReservationId);
        ValidateIdentityLength(reservation.ReservationId, nameof(reservation));
    }

    private void ValidateIdentityLength(string value, string parameterName)
    {
        if (value.Length > _options.MaximumIdentityCharacters)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "The replay identity exceeds the configured character limit.");
        }
    }

    private static bool Matches(ReplayState state, LuaPatchReplayReservation reservation) =>
        string.Equals(state.Scope, reservation.Scope, StringComparison.Ordinal) &&
        string.Equals(state.PatchId, reservation.PatchId, StringComparison.Ordinal) &&
        string.Equals(state.Nonce, reservation.Nonce, StringComparison.Ordinal) &&
        string.Equals(state.ReservationId, reservation.ReservationId, StringComparison.Ordinal) &&
        state.ReservedAt == reservation.ReservedAt.ToUniversalTime();

    private static LuaPatchReplayReservationResult Reserved(LuaPatchReplayReservation reservation) =>
        new(LuaPatchReplayReservationStatus.Reserved, reservation, null);

    private static LuaPatchReplayReservationResult Replayed(string message) =>
        new(LuaPatchReplayReservationStatus.ReplayDetected, null, message);

    private static (string, string) Key(string left, string right) => (left, right);

    private static bool IsValidTimeout(TimeSpan timeout) =>
        timeout >= TimeSpan.Zero && timeout <= TimeSpan.FromMinutes(1);

    private static bool Elapsed(long started, TimeSpan timeout) =>
        timeout == TimeSpan.Zero || Stopwatch.GetElapsedTime(started) >= timeout;

    private void EnsureParentDirectory(string path) =>
        EnsureDirectory(System.IO.Path.GetDirectoryName(path)!);

    private void EnsureDirectory(string directory)
    {
        if (_options.CreateDirectory)
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static bool IsSha256(string? value) =>
        value is not null &&
        value.Length == SHA256.HashSizeInBytes * 2 &&
        value.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static LuaPatchReplayStoreException Error(
        LuaPatchReplayStoreErrorCode code,
        string message) => new(code, message);

    private sealed class CommitLease : ILuaPatchReplayCommitLease
    {
        private readonly LuaPatchFileReplayStore _store;
        private HeldCommitLock? _lock;

        public CommitLease(
            LuaPatchFileReplayStore store,
            LuaPatchReplayReservation reservation,
            HeldCommitLock heldLock)
        {
            _store = store;
            Reservation = reservation;
            _lock = heldLock;
        }

        public LuaPatchReplayReservation Reservation { get; }

        public bool IsCompleted { get; private set; }

        public void Complete(DateTimeOffset committedAt)
        {
            ObjectDisposedException.ThrowIf(_lock is null, this);
            _store.Complete(Reservation, committedAt);
            IsCompleted = true;
        }

        public void Reopen(DateTimeOffset reopenedAt)
        {
            ObjectDisposedException.ThrowIf(_lock is null, this);
            if (!IsCompleted)
            {
                return;
            }

            _store.Reopen(Reservation, reopenedAt);
            IsCompleted = false;
        }

        public void Dispose()
        {
            _lock?.Dispose();
            _lock = null;
        }
    }

    private sealed class HeldCommitLock : IDisposable
    {
        private FileStream? _stream;

        public HeldCommitLock(FileStream stream) => _stream = stream;

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    private sealed record VerifiedReplayStore(
        ImmutableArray<LuaPatchReplayRecord> Entries,
        long Length,
        IReadOnlyDictionary<(string, string), ReplayState> States);

    private sealed record ReplayState(
        string Scope,
        string PatchId,
        string Nonce,
        string ReservationId,
        DateTimeOffset ReservedAt,
        DateTimeOffset LastTimestamp,
        LuaPatchReplayRecordState State)
    {
        public static ReplayState From(
            LuaPatchReplayRecord record,
            DateTimeOffset? reservedAt = null) => new(
                record.Scope,
                record.PatchId,
                record.Nonce,
                record.ReservationId,
                reservedAt ?? record.Timestamp,
                record.Timestamp,
                record.State);

        public LuaPatchReplayReservation ToReservation() => new(
            Scope,
            PatchId,
            Nonce,
            ReservationId,
            ReservedAt);
    }
}

internal sealed record LuaPatchReplayHashPayload(
    long Sequence,
    string Scope,
    string PatchId,
    string Nonce,
    string ReservationId,
    LuaPatchReplayRecordState State,
    DateTimeOffset Timestamp,
    string? PreviousHash);
