namespace Lunil.Godot;

/// <summary>Tracks active Godot Node adapters for scene reload and test diagnostics.</summary>
public sealed class LuaGodotRuntimeRegistry
{
    /// <summary>
    /// Process-wide registry shared by engine-driven scene reloads. Hosts that isolate
    /// their loops can create a dedicated registry and assign it to each loop instead.
    /// </summary>
    public static readonly LuaGodotRuntimeRegistry Process = new();

    private readonly object _gate = new();
    private readonly HashSet<LuaGodotGameLoop> _hosts = [];

    public int ActiveHostCount
    {
        get
        {
            lock (_gate)
            {
                return _hosts.Count;
            }
        }
    }

    internal void Register(LuaGodotGameLoop host)
    {
        lock (_gate)
        {
            _hosts.Add(host);
        }
    }

    internal void Unregister(LuaGodotGameLoop host)
    {
        lock (_gate)
        {
            _hosts.Remove(host);
        }
    }

    public void DisposeAll()
    {
        LuaGodotGameLoop[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _hosts];
        }

        foreach (var host in snapshot)
        {
            host.Shutdown();
        }

        lock (_gate)
        {
            _hosts.RemoveWhere(static host => !host.IsInitialized);
        }
    }
}
