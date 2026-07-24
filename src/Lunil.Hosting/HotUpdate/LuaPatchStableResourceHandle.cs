namespace Lunil.Hosting;

/// <summary>
/// Stable owner for a host or native resource retained across a patch generation. Active leases
/// defer owned-resource disposal until the last in-flight host operation has completed.
/// </summary>
public sealed class LuaPatchStableResourceHandle : IDisposable
{
    private readonly object _gate = new();
    private object? _resource;
    private int _activeLeaseCount;
    private bool _disposed;

    public LuaPatchStableResourceHandle(
        string resourceId,
        object resource,
        bool ownsResource = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        if (resourceId.Length > 4096 || resourceId.Contains('\0'))
        {
            throw new ArgumentOutOfRangeException(
                nameof(resourceId),
                "A stable patch resource identity must contain 1 to 4096 non-null characters.");
        }

        ArgumentNullException.ThrowIfNull(resource);
        ResourceId = resourceId;
        _resource = resource;
        OwnsResource = ownsResource;
    }

    public string ResourceId { get; }

    public bool OwnsResource { get; }

    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    public int ActiveLeaseCount
    {
        get
        {
            lock (_gate)
            {
                return _activeLeaseCount;
            }
        }
    }

    public bool IsActive => ActiveLeaseCount != 0;

    public object Resource
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _resource!;
            }
        }
    }

    public T GetResource<T>() where T : class => Resource as T ??
        throw new InvalidCastException(
            $"Stable patch resource '{ResourceId}' is not a {typeof(T).FullName}.");

    public LuaPatchStableResourceLease AcquireLease()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeLeaseCount = checked(_activeLeaseCount + 1);
            return new LuaPatchStableResourceLease(this, _resource!);
        }
    }

    public void Dispose()
    {
        object? resource = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_activeLeaseCount == 0)
            {
                resource = DetachOwnedResource();
            }
        }

        DisposeResource(resource);
    }

    internal void ReleaseLease()
    {
        object? resource = null;
        lock (_gate)
        {
            if (_activeLeaseCount <= 0)
            {
                throw new InvalidOperationException(
                    "A stable patch resource lease was released without an active acquisition.");
            }

            _activeLeaseCount--;
            if (_disposed && _activeLeaseCount == 0)
            {
                resource = DetachOwnedResource();
            }
        }

        DisposeResource(resource);
    }

    private object? DetachOwnedResource()
    {
        var resource = _resource;
        _resource = null;
        return OwnsResource ? resource : null;
    }

    private static void DisposeResource(object? resource)
    {
        if (resource is IDisposable disposable)
        {
            disposable.Dispose();
        }
        else if (resource is IAsyncDisposable asyncDisposable)
        {
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

/// <summary>One idempotently released active lease on a stable patch resource.</summary>
public sealed class LuaPatchStableResourceLease : IDisposable
{
    private readonly object _gate = new();
    private LuaPatchStableResourceHandle? _owner;
    private object? _resource;

    internal LuaPatchStableResourceLease(LuaPatchStableResourceHandle owner, object resource)
    {
        _owner = owner;
        _resource = resource;
    }

    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _owner is null;
            }
        }
    }

    public object Resource
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_owner is null, this);
                return _resource!;
            }
        }
    }

    public T GetResource<T>() where T : class => Resource as T ??
        throw new InvalidCastException(
            $"The leased patch resource is not a {typeof(T).FullName}.");

    public void Dispose()
    {
        LuaPatchStableResourceHandle? owner;
        lock (_gate)
        {
            owner = _owner;
            if (owner is null)
            {
                return;
            }

            _owner = null;
            _resource = null;
        }

        owner.ReleaseLease();
    }
}
