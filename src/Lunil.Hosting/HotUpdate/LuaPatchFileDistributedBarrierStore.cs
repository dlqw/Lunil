using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lunil.Hosting;

public enum LuaPatchDistributedBarrierErrorCode : byte
{
    InvalidRequest,
    Conflict,
    Corrupted,
    HashMismatch,
    ResourceLimitExceeded,
    IoFailure,
    WriterUnavailable,
}

public sealed class LuaPatchDistributedBarrierException : Exception
{
    public LuaPatchDistributedBarrierException(
        LuaPatchDistributedBarrierErrorCode code,
        string message) : base(message)
    {
        Code = code;
    }

    public LuaPatchDistributedBarrierException(
        LuaPatchDistributedBarrierErrorCode code,
        string message,
        Exception innerException) : base(message, innerException)
    {
        Code = code;
    }

    public LuaPatchDistributedBarrierErrorCode Code { get; }
}

public sealed record LuaPatchFileDistributedBarrierStoreOptions
{
    public static LuaPatchFileDistributedBarrierStoreOptions Default { get; } = new();

    public long MaximumStateBytes { get; init; } = 1024 * 1024;

    public int MaximumParticipantCount { get; init; } = 1024;

    public int MaximumBarrierCount { get; init; } = 10_000;

    public int MaximumMessageBytes { get; init; } = 16 * 1024;

    public int MaximumIdentityBytes { get; init; } = 4096;

    public TimeSpan WriterLockTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public bool CreateDirectory { get; init; } = true;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public sealed record LuaPatchDistributedBarrierPruneResult(
    int ScannedBarrierCount,
    int RemovedBarrierCount,
    int RemovedTemporaryFileCount,
    int RemovedOrphanLockCount);

/// <summary>
/// Crash-safe file-backed distributed barrier store. Each rollout ring owns one atomically
/// replaced, hash-protected state file and one operating-system writer lock. All participating
/// processes must observe the same directory through a file system that preserves exclusive file
/// locks and atomic same-directory rename.
/// </summary>
public sealed class LuaPatchFileDistributedBarrierStore : ILuaPatchDistributedBarrierStore
{
    private static readonly TimeSpan MaximumBarrierTimeout = TimeSpan.FromDays(1);
    private readonly string _directory;
    private readonly LuaPatchFileDistributedBarrierStoreOptions _options;

    public LuaPatchFileDistributedBarrierStore(
        string directory,
        LuaPatchFileDistributedBarrierStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _options = options ?? LuaPatchFileDistributedBarrierStoreOptions.Default;
        ArgumentNullException.ThrowIfNull(_options.TimeProvider);
        if (_options.MaximumStateBytes <= 0 || _options.MaximumStateBytes > int.MaxValue ||
            _options.MaximumParticipantCount <= 0 || _options.MaximumBarrierCount <= 0 ||
            _options.MaximumMessageBytes <= 0 ||
            _options.MaximumIdentityBytes <= 0 ||
            _options.MaximumMessageBytes > _options.MaximumStateBytes ||
            !IsValidLockTimeout(_options.WriterLockTimeout))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Distributed barrier file limits and writer timeout are invalid.");
        }
    }

    public string DirectoryPath => _directory;

    public LuaPatchDistributedBarrierSnapshot Advance(
        LuaPatchDistributedBarrierRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = Normalize(request);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDirectory();
        var path = GetStatePath(normalized.RolloutId, normalized.RingName);
        using var catalogLock = AcquireWriterLock(
            Path.Combine(_directory, ".catalog.lock"),
            cancellationToken);
        var stateExists = File.Exists(path);
        if (!stateExists && Directory.EnumerateFiles(_directory, "*.json")
            .Take(_options.MaximumBarrierCount)
            .Count() >= _options.MaximumBarrierCount)
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.ResourceLimitExceeded,
                "The distributed barrier store reached its configured barrier-count limit.");
        }

        using var writerLock = AcquireWriterLock(path + ".lock", cancellationToken);
        var stored = Read(path);
        var observedNow = _options.TimeProvider.GetUtcNow();
        var now = stored is not null && observedNow < stored.UpdatedAt
            ? stored.UpdatedAt
            : observedNow;
        var state = stored is null
            ? CreateState(normalized, now)
            : ValidatePinned(stored, normalized);
        var updated = Apply(state, normalized, now);
        if (!Equals(state, updated) || stored is null)
        {
            Write(path, updated with { Hash = ComputeHash(updated with { Hash = null }) });
        }

        return Snapshot(updated);
    }

    /// <summary>Removes terminal barrier state older than the supplied retention interval.</summary>
    public LuaPatchDistributedBarrierPruneResult PruneCompleted(
        TimeSpan minimumAge,
        CancellationToken cancellationToken = default)
    {
        if (minimumAge < TimeSpan.Zero || minimumAge > TimeSpan.FromDays(3650))
        {
            throw new ArgumentOutOfRangeException(nameof(minimumAge));
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureDirectory();
        using var catalogLock = AcquireWriterLock(
            Path.Combine(_directory, ".catalog.lock"),
            cancellationToken);
        var cutoff = _options.TimeProvider.GetUtcNow() - minimumAge;
        var scanned = 0;
        var removed = 0;
        var removedTemporary = 0;
        var removedOrphanLocks = 0;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;
            var lockPath = path + ".lock";
            var removeLock = false;
            using (AcquireWriterLock(lockPath, cancellationToken))
            {
                var state = Read(path);
                if (state is null || state.UpdatedAt > cutoff ||
                    state.Decision is not
                        (LuaPatchDistributedBarrierDecision.Commit or
                            LuaPatchDistributedBarrierDecision.Rollback))
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                    LuaPatchDurableFileSystem.FlushDirectory(_directory);
                    removeLock = true;
                    removed++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw Error(
                        LuaPatchDistributedBarrierErrorCode.IoFailure,
                        "A terminal distributed barrier state could not be pruned.",
                        exception);
                }
            }

            if (removeLock)
            {
                try
                {
                    File.Delete(lockPath);
                    LuaPatchDurableFileSystem.FlushDirectory(_directory);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw Error(
                        LuaPatchDistributedBarrierErrorCode.IoFailure,
                        "A terminal distributed barrier lock could not be pruned.",
                        exception);
                }
            }
        }

        foreach (var temporaryPath in Directory.EnumerateFiles(_directory, "*.tmp"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeletePrunedFile(temporaryPath, "temporary state");
            removedTemporary++;
        }

        foreach (var lockPath in Directory.EnumerateFiles(_directory, "*.json.lock"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var statePath = lockPath[..^".lock".Length];
            if (File.Exists(statePath))
            {
                continue;
            }

            DeletePrunedFile(lockPath, "orphan barrier lock");
            removedOrphanLocks++;
        }

        return new LuaPatchDistributedBarrierPruneResult(
            scanned,
            removed,
            removedTemporary,
            removedOrphanLocks);

        void DeletePrunedFile(string path, string kind)
        {
            try
            {
                File.Delete(path);
                LuaPatchDurableFileSystem.FlushDirectory(_directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw Error(
                    LuaPatchDistributedBarrierErrorCode.IoFailure,
                    $"A distributed barrier {kind} could not be pruned.",
                    exception);
            }
        }
    }

    private LuaPatchDistributedBarrierRequest Normalize(LuaPatchDistributedBarrierRequest request)
    {
        ValidateText(request.RolloutId, nameof(request.RolloutId));
        ValidateText(request.RingName, nameof(request.RingName));
        ValidateText(request.PatchId, nameof(request.PatchId));
        ValidateText(request.TargetRevision, nameof(request.TargetRevision));
        ValidateText(request.ParticipantId, nameof(request.ParticipantId));
        var manifestIdentity = NormalizeManifestIdentity(request.PatchManifestIdentity);
        if (!Enum.IsDefined(request.Signal))
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.InvalidRequest,
                "The distributed barrier signal is invalid.");
        }

        if (request.Participants.IsDefaultOrEmpty ||
            request.Participants.Any(static participant => string.IsNullOrWhiteSpace(participant)))
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.InvalidRequest,
                "A distributed barrier requires non-empty participant identities.");
        }

        foreach (var participant in request.Participants)
        {
            ValidateText(participant, nameof(request.Participants));
        }

        var participants = request.Participants
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        if (participants.Length != request.Participants.Length)
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.InvalidRequest,
                "Distributed barrier participant identities must be unique.");
        }

        if (participants.Length > _options.MaximumParticipantCount)
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.ResourceLimitExceeded,
                "The distributed barrier participant count exceeds its configured limit.");
        }
        if (!participants.Contains(request.ParticipantId, StringComparer.Ordinal))
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.InvalidRequest,
                "The reporting participant is not a member of the pinned barrier.");
        }

        var required = request.RequiredParticipantCount == 0
            ? participants.Length
            : request.RequiredParticipantCount;
        if (required <= 0 || required > participants.Length ||
            !IsValidBarrierTimeout(request.PreparationTimeout) ||
            !IsValidBarrierTimeout(request.HealthTimeout))
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.InvalidRequest,
                "Distributed barrier quorum and timeout values are invalid.");
        }

        if (request.Message is not null &&
            Encoding.UTF8.GetByteCount(request.Message) > _options.MaximumMessageBytes)
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.ResourceLimitExceeded,
                "The distributed barrier message exceeds its configured byte limit.");
        }

        return request with
        {
            PatchManifestIdentity = manifestIdentity,
            Participants = participants,
            RequiredParticipantCount = required,
        };
    }

    private static LuaPatchDistributedBarrierFileState CreateState(
        LuaPatchDistributedBarrierRequest request,
        DateTimeOffset now) => new()
        {
            RolloutId = request.RolloutId,
            RingName = request.RingName,
            PatchId = request.PatchId,
            TargetRevision = request.TargetRevision,
            PatchManifestIdentity = request.PatchManifestIdentity,
            Participants = request.Participants,
            RequiredParticipantCount = request.RequiredParticipantCount,
            PreparationTimeoutTicks = request.PreparationTimeout.Ticks,
            HealthTimeoutTicks = request.HealthTimeout.Ticks,
            CreatedAt = now,
            UpdatedAt = now,
            PreparationDeadline = now + request.PreparationTimeout,
            Decision = LuaPatchDistributedBarrierDecision.Waiting,
        };

    private static LuaPatchDistributedBarrierFileState ValidatePinned(
        LuaPatchDistributedBarrierFileState state,
        LuaPatchDistributedBarrierRequest request)
    {
        if (!string.Equals(state.RolloutId, request.RolloutId, StringComparison.Ordinal) ||
            !string.Equals(state.RingName, request.RingName, StringComparison.Ordinal) ||
            !string.Equals(state.PatchId, request.PatchId, StringComparison.Ordinal) ||
            !string.Equals(state.TargetRevision, request.TargetRevision, StringComparison.Ordinal) ||
            !string.Equals(
                state.PatchManifestIdentity,
                request.PatchManifestIdentity,
                StringComparison.Ordinal) ||
            state.RequiredParticipantCount != request.RequiredParticipantCount ||
            state.PreparationTimeoutTicks != request.PreparationTimeout.Ticks ||
            state.HealthTimeoutTicks != request.HealthTimeout.Ticks ||
            !state.Participants.SequenceEqual(request.Participants, StringComparer.Ordinal))
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.Conflict,
                "The rollout ring is already pinned to a different patch, membership, quorum, or timeout policy.");
        }

        return state;
    }

    private static LuaPatchDistributedBarrierFileState Apply(
        LuaPatchDistributedBarrierFileState state,
        LuaPatchDistributedBarrierRequest request,
        DateTimeOffset now)
    {
        if (state.Decision is LuaPatchDistributedBarrierDecision.Commit or
            LuaPatchDistributedBarrierDecision.Rollback)
        {
            return state;
        }

        var prepared = state.PreparedParticipants.ToHashSet(StringComparer.Ordinal);
        var healthy = state.HealthyParticipants.ToHashSet(StringComparer.Ordinal);
        var failed = state.FailedParticipants.ToHashSet(StringComparer.Ordinal);
        var message = state.Message;
        switch (request.Signal)
        {
            case LuaPatchDistributedBarrierSignal.Observe:
                break;
            case LuaPatchDistributedBarrierSignal.Prepared:
                if (failed.Contains(request.ParticipantId))
                {
                    throw Error(
                        LuaPatchDistributedBarrierErrorCode.Conflict,
                        "A failed participant cannot later report prepared.");
                }

                if (state.Decision == LuaPatchDistributedBarrierDecision.Waiting)
                {
                    prepared.Add(request.ParticipantId);
                }

                break;
            case LuaPatchDistributedBarrierSignal.PreparationFailed:
                if (state.Decision == LuaPatchDistributedBarrierDecision.Waiting)
                {
                    prepared.Remove(request.ParticipantId);
                }

                failed.Add(request.ParticipantId);
                message = request.Message ?? "A participant failed local barrier preparation.";
                break;
            case LuaPatchDistributedBarrierSignal.Healthy:
                EnsureSelected(state, request.ParticipantId, "healthy");
                if (failed.Contains(request.ParticipantId))
                {
                    throw Error(
                        LuaPatchDistributedBarrierErrorCode.Conflict,
                        "An unhealthy participant cannot later report healthy.");
                }

                healthy.Add(request.ParticipantId);
                break;
            case LuaPatchDistributedBarrierSignal.Unhealthy:
                if (state.Decision == LuaPatchDistributedBarrierDecision.Apply)
                {
                    EnsureSelected(state, request.ParticipantId, "unhealthy");
                }

                if (state.Decision == LuaPatchDistributedBarrierDecision.Waiting)
                {
                    prepared.Remove(request.ParticipantId);
                }

                healthy.Remove(request.ParticipantId);
                failed.Add(request.ParticipantId);
                message = request.Message ?? "A selected participant failed publication or health validation.";
                break;
        }

        var decision = state.Decision;
        var selected = state.SelectedParticipants;
        var healthDeadline = state.HealthDeadline;
        if (decision == LuaPatchDistributedBarrierDecision.Waiting)
        {
            if (failed.Count > state.Participants.Length - state.RequiredParticipantCount)
            {
                decision = LuaPatchDistributedBarrierDecision.Rollback;
                message ??= "The prepared-participant quorum is no longer reachable.";
            }
            else if (now >= state.PreparationDeadline)
            {
                decision = LuaPatchDistributedBarrierDecision.Rollback;
                message ??= "The distributed preparation barrier expired before reaching quorum.";
            }
            else if (prepared.Count >= state.RequiredParticipantCount)
            {
                selected = prepared.Order(StringComparer.Ordinal)
                    .Take(state.RequiredParticipantCount)
                    .ToImmutableArray();
                decision = LuaPatchDistributedBarrierDecision.Apply;
                healthDeadline = now + TimeSpan.FromTicks(state.HealthTimeoutTicks);
                message = null;
            }
        }

        if (decision == LuaPatchDistributedBarrierDecision.Apply)
        {
            if (selected.Any(failed.Contains))
            {
                decision = LuaPatchDistributedBarrierDecision.Rollback;
                message ??= "A selected participant failed publication or health validation.";
            }
            else if (healthDeadline is { } deadline && now >= deadline)
            {
                decision = LuaPatchDistributedBarrierDecision.Rollback;
                message ??= "The distributed health barrier expired before every selected participant acknowledged health.";
            }
            else if (selected.All(healthy.Contains))
            {
                decision = LuaPatchDistributedBarrierDecision.Commit;
                message = null;
            }
        }

        return state with
        {
            PreparedParticipants = prepared.Order(StringComparer.Ordinal).ToImmutableArray(),
            SelectedParticipants = selected,
            HealthyParticipants = healthy.Order(StringComparer.Ordinal).ToImmutableArray(),
            FailedParticipants = failed.Order(StringComparer.Ordinal).ToImmutableArray(),
            Decision = decision,
            UpdatedAt = now,
            HealthDeadline = healthDeadline,
            Message = message,
            Hash = null,
        };
    }

    private static void EnsureSelected(
        LuaPatchDistributedBarrierFileState state,
        string participantId,
        string signal)
    {
        if (state.Decision != LuaPatchDistributedBarrierDecision.Apply ||
            !state.SelectedParticipants.Contains(participantId, StringComparer.Ordinal))
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.Conflict,
                $"Only a selected participant may report {signal} after the apply decision.");
        }
    }

    private LuaPatchDistributedBarrierFileState? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length <= 1 || stream.Length > _options.MaximumStateBytes ||
                stream.Length > int.MaxValue)
            {
                throw Error(
                    LuaPatchDistributedBarrierErrorCode.ResourceLimitExceeded,
                    "The distributed barrier state exceeds its configured byte limit.");
            }

            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (bytes[^1] != (byte)'\n')
            {
                throw Error(
                    LuaPatchDistributedBarrierErrorCode.Corrupted,
                    "The distributed barrier state is not newline terminated.");
            }

            var state = JsonSerializer.Deserialize(
                bytes.AsSpan(0, bytes.Length - 1),
                LuaPatchJsonContext.Default.LuaPatchDistributedBarrierFileState) ?? throw Error(
                    LuaPatchDistributedBarrierErrorCode.Corrupted,
                    "The distributed barrier state is empty.");
            ValidateStoredState(state);
            var expected = ComputeHash(state with { Hash = null });
            if (!string.Equals(state.Hash, expected, StringComparison.Ordinal))
            {
                throw Error(
                    LuaPatchDistributedBarrierErrorCode.HashMismatch,
                    "The distributed barrier state hash does not match its contents.");
            }

            if (state.Participants.Length > _options.MaximumParticipantCount)
            {
                throw Error(
                    LuaPatchDistributedBarrierErrorCode.ResourceLimitExceeded,
                    "The distributed barrier participant count exceeds its configured limit.");
            }
            return state;
        }
        catch (LuaPatchDistributedBarrierException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.Corrupted,
                "The distributed barrier state is not valid JSON.",
                exception);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.IoFailure,
                "The distributed barrier state could not be read.",
                exception);
        }
    }

    private void Write(string path, LuaPatchDistributedBarrierFileState state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            state,
            LuaPatchJsonContext.Default.LuaPatchDistributedBarrierFileState);
        if (bytes.LongLength + 1 > _options.MaximumStateBytes)
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.ResourceLimitExceeded,
                "The distributed barrier state exceeds its configured byte limit.");
        }

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
            LuaPatchDurableFileSystem.FlushDirectory(_directory);
        }
        catch (LuaPatchDistributedBarrierException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.IoFailure,
                "The distributed barrier state could not be durably replaced.",
                exception);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private FileStream AcquireWriterLock(string path, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        IOException? lastException = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = LuaPatchFileLock.TryOpenExclusive(
                path,
                out var exception,
                out var contention);
            if (stream is not null)
            {
                return stream;
            }

            lastException = exception;
            if (!contention || Stopwatch.GetElapsedTime(started) >= _options.WriterLockTimeout)
            {
                throw Error(
                    LuaPatchDistributedBarrierErrorCode.WriterUnavailable,
                    "The distributed barrier writer lock is unavailable.",
                    lastException!);
            }

            if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(10)))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private void EnsureDirectory()
    {
        if (System.IO.Directory.Exists(_directory))
        {
            return;
        }

        if (!_options.CreateDirectory)
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.IoFailure,
                "The distributed barrier directory does not exist.");
        }

        try
        {
            System.IO.Directory.CreateDirectory(_directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.IoFailure,
                "The distributed barrier directory could not be created.",
                exception);
        }
    }

    private string GetStatePath(string rolloutId, string ringName)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            rolloutId + "\0" + ringName)));
        return Path.Combine(_directory, key + ".json");
    }

    private static string ComputeHash(LuaPatchDistributedBarrierFileState state) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            state,
            LuaPatchJsonContext.Default.LuaPatchDistributedBarrierFileState)));

    private static LuaPatchDistributedBarrierSnapshot Snapshot(
        LuaPatchDistributedBarrierFileState state) => new()
        {
            RolloutId = state.RolloutId,
            RingName = state.RingName,
            PatchId = state.PatchId,
            TargetRevision = state.TargetRevision,
            PatchManifestIdentity = state.PatchManifestIdentity,
            Participants = state.Participants,
            RequiredParticipantCount = state.RequiredParticipantCount,
            PreparedParticipants = state.PreparedParticipants,
            SelectedParticipants = state.SelectedParticipants,
            HealthyParticipants = state.HealthyParticipants,
            FailedParticipants = state.FailedParticipants,
            Decision = state.Decision,
            CreatedAt = state.CreatedAt,
            UpdatedAt = state.UpdatedAt,
            PreparationDeadline = state.PreparationDeadline,
            HealthDeadline = state.HealthDeadline,
            Message = state.Message,
        };

    private void ValidateStoredState(LuaPatchDistributedBarrierFileState state)
    {
        if (!IsValidStoredText(state.RolloutId) || !IsValidStoredText(state.RingName) ||
            !IsValidStoredText(state.PatchId) || !IsValidStoredText(state.TargetRevision) ||
            !IsCanonicalManifestIdentity(state.PatchManifestIdentity) ||
            state.Participants.IsDefaultOrEmpty ||
            state.Participants.Distinct(StringComparer.Ordinal).Count() != state.Participants.Length ||
            !IsOrdinallySorted(state.Participants) ||
            state.Participants.Any(participant => !IsValidStoredText(participant)) ||
            state.RequiredParticipantCount <= 0 ||
            state.RequiredParticipantCount > state.Participants.Length ||
            !Enum.IsDefined(state.Decision) || state.CreatedAt > state.UpdatedAt ||
            !IsValidBarrierTimeout(TimeSpan.FromTicks(state.PreparationTimeoutTicks)) ||
            !IsValidBarrierTimeout(TimeSpan.FromTicks(state.HealthTimeoutTicks)) ||
            state.PreparationDeadline - state.CreatedAt !=
                TimeSpan.FromTicks(state.PreparationTimeoutTicks) ||
            state.HealthDeadline < state.CreatedAt ||
            state.Message is not null &&
                Encoding.UTF8.GetByteCount(state.Message) > _options.MaximumMessageBytes ||
            !AllMembers(state.PreparedParticipants, state.Participants) ||
            !AllMembers(state.SelectedParticipants, state.Participants) ||
            !AllMembers(state.HealthyParticipants, state.SelectedParticipants) ||
            !AllMembers(state.FailedParticipants, state.Participants) ||
            !IsOrdinallySorted(state.PreparedParticipants) ||
            !IsOrdinallySorted(state.SelectedParticipants) ||
            !IsOrdinallySorted(state.HealthyParticipants) ||
            !IsOrdinallySorted(state.FailedParticipants) ||
            state.SelectedParticipants.Except(state.PreparedParticipants, StringComparer.Ordinal).Any() ||
            state.HealthyParticipants.Intersect(state.FailedParticipants, StringComparer.Ordinal).Any() ||
            !IsValidDecisionState(state))
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.Corrupted,
                "The distributed barrier state contains invalid fields or transitions.");
        }
    }

    private static bool IsValidDecisionState(LuaPatchDistributedBarrierFileState state) =>
        state.Decision switch
        {
            LuaPatchDistributedBarrierDecision.Waiting =>
                state.SelectedParticipants.IsEmpty && state.HealthyParticipants.IsEmpty &&
                state.HealthDeadline is null,
            LuaPatchDistributedBarrierDecision.Apply =>
                state.SelectedParticipants.Length == state.RequiredParticipantCount &&
                state.HealthDeadline is not null &&
                !state.SelectedParticipants.Intersect(
                    state.FailedParticipants,
                    StringComparer.Ordinal).Any(),
            LuaPatchDistributedBarrierDecision.Commit =>
                state.SelectedParticipants.Length == state.RequiredParticipantCount &&
                state.HealthDeadline is not null &&
                state.SelectedParticipants.All(participant =>
                    state.HealthyParticipants.Contains(participant, StringComparer.Ordinal)) &&
                !state.SelectedParticipants.Intersect(
                    state.FailedParticipants,
                    StringComparer.Ordinal).Any(),
            LuaPatchDistributedBarrierDecision.Rollback => true,
            _ => false,
        };

    private static bool AllMembers(
        ImmutableArray<string> values,
        ImmutableArray<string> members) =>
        !values.IsDefault && values.Distinct(StringComparer.Ordinal).Count() == values.Length &&
        values.All(value => members.Contains(value, StringComparer.Ordinal));

    private bool IsValidStoredText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Contains('\0') &&
        Encoding.UTF8.GetByteCount(value) <= _options.MaximumIdentityBytes;

    private static bool IsOrdinallySorted(ImmutableArray<string> values) =>
        !values.IsDefault && values.SequenceEqual(values.Order(StringComparer.Ordinal));

    private static bool IsCanonicalManifestIdentity(string? value) =>
        value is not null && value.Length == SHA256.HashSizeInBytes * 2 &&
        value.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private void ValidateText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.InvalidRequest,
                $"Distributed barrier field '{name}' is empty or invalid.");
        }


        if (Encoding.UTF8.GetByteCount(value) > _options.MaximumIdentityBytes)
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.ResourceLimitExceeded,
                $"Distributed barrier field '{name}' exceeds its configured byte limit.");
        }
    }

    private static string NormalizeManifestIdentity(string value)
    {
        if (value is null)
        {
            throw Error(
                LuaPatchDistributedBarrierErrorCode.InvalidRequest,
                "The patch manifest identity must be a 32-byte SHA-256 value in hexadecimal form.");
        }

        try
        {
            var bytes = Convert.FromHexString(value);
            if (bytes.Length == SHA256.HashSizeInBytes)
            {
                return Convert.ToHexString(bytes);
            }
        }
        catch (FormatException)
        {
        }

        throw Error(
            LuaPatchDistributedBarrierErrorCode.InvalidRequest,
            "The patch manifest identity must be a 32-byte SHA-256 value in hexadecimal form.");
    }

    private static bool IsValidBarrierTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero && timeout <= MaximumBarrierTimeout;

    private static bool IsValidLockTimeout(TimeSpan timeout) =>
        timeout >= TimeSpan.Zero && timeout <= TimeSpan.FromMinutes(1);

    private static LuaPatchDistributedBarrierException Error(
        LuaPatchDistributedBarrierErrorCode code,
        string message) => new(code, message);

    private static LuaPatchDistributedBarrierException Error(
        LuaPatchDistributedBarrierErrorCode code,
        string message,
        Exception innerException) => new(code, message, innerException);
}

internal sealed record LuaPatchDistributedBarrierFileState
{
    public required string RolloutId { get; init; }

    public required string RingName { get; init; }

    public required string PatchId { get; init; }

    public required string TargetRevision { get; init; }

    public required string PatchManifestIdentity { get; init; }

    public ImmutableArray<string> Participants { get; init; } = [];

    public int RequiredParticipantCount { get; init; }

    public long PreparationTimeoutTicks { get; init; }

    public long HealthTimeoutTicks { get; init; }

    public ImmutableArray<string> PreparedParticipants { get; init; } = [];

    public ImmutableArray<string> SelectedParticipants { get; init; } = [];

    public ImmutableArray<string> HealthyParticipants { get; init; } = [];

    public ImmutableArray<string> FailedParticipants { get; init; } = [];

    public LuaPatchDistributedBarrierDecision Decision { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset PreparationDeadline { get; init; }

    public DateTimeOffset? HealthDeadline { get; init; }

    public string? Message { get; init; }

    public string? Hash { get; init; }
}
