namespace Lunil.Runtime.CodeGen;

/// <summary>
/// Coordinates one backend module generation. Entry is rejected while invalidation is in
/// progress, and invalidation does not complete until every previously admitted delegate exits.
/// </summary>
internal sealed class LuaBackendGeneration
{
    private readonly object _gate = new();
    private long _generation = 1;
    private int _activeExecutions;
    private bool _invalidating;

    public long Current => Interlocked.Read(ref _generation);

    public bool TryEnter(long expectedGeneration)
    {
        if (Volatile.Read(ref _invalidating) ||
            Interlocked.Read(ref _generation) != expectedGeneration)
        {
            return false;
        }

        var activeExecutions = Interlocked.Increment(ref _activeExecutions);
        if (activeExecutions <= 0)
        {
            _ = Interlocked.Decrement(ref _activeExecutions);
            throw new OverflowException("Too many backend execution leases are active.");
        }

        if (!Volatile.Read(ref _invalidating) &&
            Interlocked.Read(ref _generation) == expectedGeneration)
        {
            return true;
        }

        // Invalidation may start after the optimistic checks but before admission is
        // published. Withdraw the lease before reporting failure so invalidation can
        // safely wait for every execution that observed the previous generation.
        Exit();
        return false;
    }

    public void Exit()
    {
        var remaining = Interlocked.Decrement(ref _activeExecutions);
        if (remaining < 0)
        {
            _ = Interlocked.Increment(ref _activeExecutions);
            throw new InvalidOperationException("No backend execution lease is active.");
        }

        if (remaining == 0 && Volatile.Read(ref _invalidating))
        {
            lock (_gate)
            {
                if (_activeExecutions == 0)
                {
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }

    public bool IsCurrent(long generation) =>
        !Volatile.Read(ref _invalidating) &&
        Interlocked.Read(ref _generation) == generation;

    public void BeginInvalidation()
    {
        lock (_gate)
        {
            while (_invalidating)
            {
                Monitor.Wait(_gate);
            }

            Volatile.Write(ref _invalidating, true);
            _ = checked(++_generation);
        }
    }

    public void CompleteInvalidation()
    {
        lock (_gate)
        {
            if (!_invalidating)
            {
                throw new InvalidOperationException("Backend invalidation has not begun.");
            }

            while (Volatile.Read(ref _activeExecutions) != 0)
            {
                Monitor.Wait(_gate);
            }

            Volatile.Write(ref _invalidating, false);
            Monitor.PulseAll(_gate);
        }
    }
}
