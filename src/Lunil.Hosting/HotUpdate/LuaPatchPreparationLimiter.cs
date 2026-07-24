using System.Diagnostics.CodeAnalysis;

namespace Lunil.Hosting;

/// <summary>
/// Bounds concurrent isolated patch preparation and the number of callers waiting for capacity.
/// One limiter can be shared by every host participating in a rollout.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "SemaphoreSlim does not allocate an unmanaged wait handle unless AvailableWaitHandle is used, which this limiter never does.")]
public sealed class LuaPatchPreparationLimiter
{
    private readonly SemaphoreSlim _slots;
    private int _activeCount;
    private int _queuedCount;

    public LuaPatchPreparationLimiter(int maximumConcurrency, int maximumQueueLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumQueueLength);
        MaximumConcurrency = maximumConcurrency;
        MaximumQueueLength = maximumQueueLength;
        _slots = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
    }

    public int MaximumConcurrency { get; }

    public int MaximumQueueLength { get; }

    public int ActiveCount => Volatile.Read(ref _activeCount);

    public int QueuedCount => Volatile.Read(ref _queuedCount);

    internal async ValueTask<Admission> AcquireAsync(
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        ValidateWaitTimeout(waitTimeout);
        cancellationToken.ThrowIfCancellationRequested();
        if (_slots.Wait(0, cancellationToken))
        {
            return Acquired();
        }

        if (waitTimeout == TimeSpan.Zero)
        {
            return Admission.Saturated;
        }

        if (!TryEnterQueue())
        {
            return Admission.Saturated;
        }

        try
        {
            var entered = waitTimeout == Timeout.InfiniteTimeSpan
                ? await WaitWithoutTimeoutAsync(cancellationToken).ConfigureAwait(false)
                : await _slots.WaitAsync(waitTimeout, cancellationToken).ConfigureAwait(false);
            return entered ? Acquired() : Admission.TimedOut;
        }
        finally
        {
            Interlocked.Decrement(ref _queuedCount);
        }
    }

    internal static void ValidateWaitTimeout(TimeSpan waitTimeout)
    {
        if (waitTimeout < TimeSpan.Zero && waitTimeout != Timeout.InfiniteTimeSpan ||
            waitTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(waitTimeout),
                waitTimeout,
                "A preparation wait timeout must be infinite or between zero and Int32.MaxValue milliseconds.");
        }
    }

    private bool TryEnterQueue()
    {
        while (true)
        {
            var queued = Volatile.Read(ref _queuedCount);
            if (queued >= MaximumQueueLength)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _queuedCount, queued + 1, queued) == queued)
            {
                return true;
            }
        }
    }

    private async ValueTask<bool> WaitWithoutTimeoutAsync(
        CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private Admission Acquired()
    {
        Interlocked.Increment(ref _activeCount);
        return new Admission(new Lease(this), AdmissionFailure.None);
    }

    private void Release()
    {
        Interlocked.Decrement(ref _activeCount);
        _slots.Release();
    }

    internal enum AdmissionFailure : byte
    {
        None,
        Saturated,
        TimedOut,
    }

    internal readonly record struct Admission(Lease? Lease, AdmissionFailure Failure)
    {
        public static Admission Saturated { get; } = new(null, AdmissionFailure.Saturated);

        public static Admission TimedOut { get; } = new(null, AdmissionFailure.TimedOut);

        public bool Succeeded => Lease is not null;
    }

    internal sealed class Lease : IDisposable
    {
        private LuaPatchPreparationLimiter? _owner;

        public Lease(LuaPatchPreparationLimiter owner)
        {
            _owner = owner;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
