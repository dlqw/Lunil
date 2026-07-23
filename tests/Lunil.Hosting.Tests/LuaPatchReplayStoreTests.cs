using System.Text;

namespace Lunil.Hosting.Tests;

public sealed class LuaPatchReplayStoreTests
{
    [Fact]
    public void ReservationsAreScopedResumableAndTerminalOnlyAfterCommit()
    {
        WithStorePath(path =>
        {
            var first = new LuaPatchFileReplayStore(path);
            var at = new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);

            var stateA = AssertReserved(first.TryReserve("state-a", "patch-1", "nonce-1", at));
            var resumed = AssertReserved(first.TryReserve("state-a", "patch-1", "nonce-1", at));
            var stateB = AssertReserved(first.TryReserve("state-b", "patch-1", "nonce-1", at));
            Assert.Equal(stateA, resumed);
            Assert.NotEqual(stateA.ReservationId, stateB.ReservationId);
            Assert.False(first.TryReserve("state-a", "patch-1", "nonce-2", at).Reserved);
            Assert.False(first.TryReserve("state-a", "patch-2", "nonce-1", at).Reserved);

            using (var lease = first.TryAcquireCommit(stateA, at.AddSeconds(1)))
            {
                Assert.NotNull(lease);
                lease.Complete(at.AddSeconds(2));
                Assert.True(lease.IsCompleted);
            }

            var reopened = new LuaPatchFileReplayStore(path);
            Assert.False(reopened.TryReserve("state-a", "patch-1", "nonce-1", at).Reserved);
            Assert.Equal(stateB, AssertReserved(reopened.TryReserve(
                "state-b", "patch-1", "nonce-1", at)));
            var records = reopened.ReadAll();
            Assert.Equal(3, records.Length);
            Assert.Equal(
                [
                    LuaPatchReplayRecordState.Reserved,
                    LuaPatchReplayRecordState.Reserved,
                    LuaPatchReplayRecordState.Committed,
                ],
                records.Select(static record => record.State).ToArray());
            Assert.Equal(records[0].Hash, records[1].PreviousHash);
            Assert.All(records, static record => Assert.Equal(TimeSpan.Zero, record.Timestamp.Offset));
        });
    }

    [Fact]
    public void IncompleteLeaseIsCrashResumableAndCompletedLeaseCanBeCompensated()
    {
        WithStorePath(path =>
        {
            var first = new LuaPatchFileReplayStore(path);
            var second = new LuaPatchFileReplayStore(path);
            var at = new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
            var reservation = AssertReserved(first.TryReserve(
                "state-a", "patch-1", "nonce-1", at));

            using (var lease = first.TryAcquireCommit(reservation, at)!)
            {
                Assert.Null(second.TryAcquireCommit(reservation, at));
            }

            using (var recovered = second.TryAcquireCommit(reservation, at.AddSeconds(1))!)
            {
                recovered.Complete(at.AddSeconds(2));
                recovered.Reopen(at.AddSeconds(3));
                Assert.False(recovered.IsCompleted);
            }

            using var retry = first.TryAcquireCommit(reservation, at.AddSeconds(4));
            Assert.NotNull(retry);
            retry.Complete(at.AddSeconds(5));
            Assert.Null(second.TryAcquireCommit(reservation, at.AddSeconds(6)));
            Assert.Equal(
                [
                    LuaPatchReplayRecordState.Reserved,
                    LuaPatchReplayRecordState.Committed,
                    LuaPatchReplayRecordState.Reopened,
                    LuaPatchReplayRecordState.Committed,
                ],
                first.ReadAll().Select(static record => record.State).ToArray());
        });
    }

    [Fact]
    public async Task IndependentInstancesSerializeConcurrentCommitOwnership()
    {
        await WithStorePathAsync(async path =>
        {
            var first = new LuaPatchFileReplayStore(path);
            var second = new LuaPatchFileReplayStore(path);
            var at = new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
            var reservation = AssertReserved(first.TryReserve(
                "state-a", "patch-1", "nonce-1", at));
            using var start = new ManualResetEventSlim();
            var attempts = new[] { first, second }.Select(store => Task.Run(() =>
            {
                start.Wait();
                return store.TryAcquireCommit(reservation, at);
            })).ToArray();

            start.Set();
            var leases = await Task.WhenAll(attempts);
            var lease = Assert.Single(leases, static candidate => candidate is not null)!;
            Assert.Single(leases, static candidate => candidate is null);
            lease.Dispose();
        });
    }

    [Fact]
    public void TamperingAndTruncatedTailFailClosed()
    {
        WithStorePath(path =>
        {
            var store = new LuaPatchFileReplayStore(path);
            _ = AssertReserved(store.TryReserve(
                "state-a",
                "patch-1",
                "nonce-1",
                new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero)));
            var original = File.ReadAllBytes(path);

            var text = File.ReadAllText(path, Encoding.UTF8);
            File.WriteAllText(
                path,
                text.Replace("patch-1", "patch-x", StringComparison.Ordinal),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var tampered = Assert.Throws<LuaPatchReplayStoreException>(() => store.ReadAll());
            Assert.Equal(LuaPatchReplayStoreErrorCode.HashMismatch, tampered.Code);

            File.WriteAllBytes(path, original[..^1]);
            var truncated = Assert.Throws<LuaPatchReplayStoreException>(() => store.ReadAll());
            Assert.Equal(LuaPatchReplayStoreErrorCode.Corrupted, truncated.Code);
        });
    }

    [Fact]
    public void ResourceLimitAndWriterContentionFailClosed()
    {
        WithStorePath(path =>
        {
            var limited = new LuaPatchFileReplayStore(path, new LuaPatchFileReplayStoreOptions
            {
                MaximumEntries = 1,
            });
            var at = new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
            _ = AssertReserved(limited.TryReserve("state-a", "patch-1", "nonce-1", at));
            var limit = Assert.Throws<LuaPatchReplayStoreException>(() =>
                limited.TryReserve("state-b", "patch-1", "nonce-1", at));
            Assert.Equal(LuaPatchReplayStoreErrorCode.ResourceLimitExceeded, limit.Code);

            using var held = new FileStream(
                limited.WriterLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            var contended = new LuaPatchFileReplayStore(path, new LuaPatchFileReplayStoreOptions
            {
                WriterLockTimeout = TimeSpan.FromMilliseconds(20),
            });
            var unavailable = Assert.Throws<LuaPatchReplayStoreException>(() => contended.ReadAll());
            Assert.Equal(LuaPatchReplayStoreErrorCode.WriterUnavailable, unavailable.Code);

            var identityLimited = new LuaPatchFileReplayStore(
                path + ".identity-limit",
                new LuaPatchFileReplayStoreOptions { MaximumIdentityCharacters = 4 });
            Assert.Throws<ArgumentOutOfRangeException>(() => identityLimited.TryReserve(
                "state-a", "p", "n", at));
        });
    }

    private static LuaPatchReplayReservation AssertReserved(
        LuaPatchReplayReservationResult result)
    {
        Assert.True(result.Reserved, result.Message);
        return result.Reservation!;
    }

    private static void WithStorePath(Action<string> action)
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lunil-replay-store-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            action(System.IO.Path.Combine(directory, "accepted.ndjson"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task WithStorePathAsync(Func<string, Task> action)
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "lunil-replay-store-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            await action(System.IO.Path.Combine(directory, "accepted.ndjson"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
