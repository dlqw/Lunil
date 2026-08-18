namespace Lunil.Workspace;

/// <summary>
/// Thread-safe string interning pool shared across workspace snapshot rebuilds.
/// Every lookup returns one canonical instance per distinct content, so rebuilds
/// that re-emit the same names and symbol keys retain a single string instead of a
/// private copy per rebuild, and concurrent producers converge on shared instances.
/// The catalog is weak: canonical instances stay pooled only while a snapshot,
/// contribution, or cache still references them, so churn from edits and renames
/// cannot accumulate in a long-running process.
/// </summary>
public sealed class LuaWorkspaceStringInterner
{
    private readonly object _gate = new();
    private readonly Dictionary<int, List<WeakReference<string>>> _buckets = new();
    private long _lookupCount;
    private long _hitCount;

    /// <summary>Total intern attempts since construction, including hits.</summary>
    public long LookupCount => Interlocked.Read(ref _lookupCount);

    /// <summary>Attempts that returned an already-pooled canonical instance.</summary>
    public long HitCount => Interlocked.Read(ref _hitCount);

    /// <summary>
    /// Number of live canonical instances currently pooled. The scan prunes entries
    /// whose targets have been reclaimed, so it reflects only reachable instances.
    /// </summary>
    public int LiveEntryCount
    {
        get
        {
            lock (_gate)
            {
                var count = 0;
                List<int>? emptyBuckets = null;
                foreach (var pair in _buckets)
                {
                    var bucket = pair.Value;
                    var write = 0;
                    for (var read = 0; read < bucket.Count; read++)
                    {
                        if (bucket[read].TryGetTarget(out _))
                        {
                            bucket[write++] = bucket[read];
                            count++;
                        }
                    }

                    if (bucket.Count != write)
                    {
                        bucket.RemoveRange(write, bucket.Count - write);
                    }

                    if (bucket.Count == 0)
                    {
                        (emptyBuckets ??= []).Add(pair.Key);
                    }
                }

                if (emptyBuckets is not null)
                {
                    foreach (var hash in emptyBuckets)
                    {
                        _buckets.Remove(hash);
                    }
                }

                return count;
            }
        }
    }

    /// <summary>Returns the canonical instance for <paramref name="value"/>, pooling the given instance when none is live.</summary>
    public string Intern(string value)
    {
        LunilGuard.NotNull(value);
        if (value.Length == 0)
        {
            return string.Empty;
        }

        Interlocked.Increment(ref _lookupCount);
        var hash = Hash(value.AsSpan());
        lock (_gate)
        {
            var bucket = GetOrCreateBucket(hash);
            string found;
            if (ScanAndCompact(bucket, value, out found))
            {
                Interlocked.Increment(ref _hitCount);
                return found;
            }

            bucket.Add(new WeakReference<string>(value));
            return value;
        }
    }

    /// <summary>Returns the canonical instance equal to <paramref name="value"/> without allocating on the hit path.</summary>
    public string Intern(ReadOnlySpan<char> value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        Interlocked.Increment(ref _lookupCount);
        var hash = Hash(value);
        lock (_gate)
        {
            var bucket = GetOrCreateBucket(hash);
            if (ScanAndCompact(bucket, value, out var found))
            {
                Interlocked.Increment(ref _hitCount);
                return found;
            }

            var pooled = value.ToString();
            bucket.Add(new WeakReference<string>(pooled));
            return pooled;
        }
    }

    /// <summary>Drops every pooled entry; instances already handed out stay valid.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _buckets.Clear();
        }
    }

    private List<WeakReference<string>> GetOrCreateBucket(int hash)
    {
        if (!_buckets.TryGetValue(hash, out var bucket))
        {
            bucket = [];
            _buckets.Add(hash, bucket);
        }

        return bucket;
    }

    private static bool ScanAndCompact(List<WeakReference<string>> bucket, string value, out string found)
    {
        found = "";
        var write = 0;
        var matched = false;
        for (var read = 0; read < bucket.Count; read++)
        {
            if (!bucket[read].TryGetTarget(out var existing))
            {
                continue;
            }

            bucket[write++] = bucket[read];
            if (!matched && string.Equals(existing, value, StringComparison.Ordinal))
            {
                found = existing;
                matched = true;
            }
        }

        if (bucket.Count != write)
        {
            bucket.RemoveRange(write, bucket.Count - write);
        }

        return matched;
    }

    private static bool ScanAndCompact(List<WeakReference<string>> bucket, ReadOnlySpan<char> value, out string found)
    {
        found = "";
        var write = 0;
        var matched = false;
        for (var read = 0; read < bucket.Count; read++)
        {
            if (!bucket[read].TryGetTarget(out var existing))
            {
                continue;
            }

            bucket[write++] = bucket[read];
            if (!matched && existing.AsSpan().SequenceEqual(value))
            {
                found = existing;
                matched = true;
            }
        }

        if (bucket.Count != write)
        {
            bucket.RemoveRange(write, bucket.Count - write);
        }

        return matched;
    }

    private static int Hash(ReadOnlySpan<char> value)
    {
        uint hash = 2_166_136_261;
        foreach (var character in value)
        {
            hash = (hash ^ character) * 16_777_619;
        }

        return (int)hash;
    }
}
