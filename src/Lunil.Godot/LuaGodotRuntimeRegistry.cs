namespace Lunil.Godot;

/// <summary>Tracks active Godot Node adapters for scene reload and test diagnostics.</summary>
public static class LuaGodotRuntimeRegistry
{
    private static readonly object Gate = new();
    private static readonly HashSet<LuaGodotGameLoop> Hosts = [];

    public static int ActiveHostCount
    {
        get
        {
            lock (Gate)
            {
                return Hosts.Count;
            }
        }
    }

    internal static void Register(LuaGodotGameLoop host)
    {
        lock (Gate)
        {
            Hosts.Add(host);
        }
    }

    internal static void Unregister(LuaGodotGameLoop host)
    {
        lock (Gate)
        {
            Hosts.Remove(host);
        }
    }

    public static void DisposeAll()
    {
        LuaGodotGameLoop[] snapshot;
        lock (Gate)
        {
            snapshot = [.. Hosts];
        }

        foreach (var host in snapshot)
        {
            host.Shutdown();
        }

        lock (Gate)
        {
            Hosts.RemoveWhere(static host => !host.IsInitialized);
        }
    }
}
