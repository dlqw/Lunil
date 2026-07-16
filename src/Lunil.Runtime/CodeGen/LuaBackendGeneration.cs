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
    private volatile bool _invalidating;

    public long Current => Interlocked.Read(ref _generation);

    public bool TryEnter(long expectedGeneration)
    {
        lock (_gate)
        {
            if (_invalidating || _generation != expectedGeneration)
            {
                return false;
            }

            _activeExecutions = checked(_activeExecutions + 1);
            return true;
        }
    }

    public void Exit()
    {
        lock (_gate)
        {
            if (_activeExecutions <= 0)
            {
                throw new InvalidOperationException("No backend execution lease is active.");
            }

            _activeExecutions--;
            if (_activeExecutions == 0)
            {
                Monitor.PulseAll(_gate);
            }
        }
    }

    public bool IsCurrent(long generation) =>
        !_invalidating && Interlocked.Read(ref _generation) == generation;

    public void BeginInvalidation()
    {
        lock (_gate)
        {
            while (_invalidating)
            {
                Monitor.Wait(_gate);
            }

            _invalidating = true;
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

            while (_activeExecutions != 0)
            {
                Monitor.Wait(_gate);
            }

            _invalidating = false;
            Monitor.PulseAll(_gate);
        }
    }
}
