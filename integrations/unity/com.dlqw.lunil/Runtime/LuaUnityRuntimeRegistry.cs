using System;
using System.Collections.Generic;
using UnityEngine;

namespace Lunil.Unity
{
    /// <summary>Tracks active Unity adapters across disabled-domain-reload play sessions.</summary>
    public sealed class LuaUnityRuntimeRegistry
    {
        /// <summary>
        /// Process-wide registry shared by play-mode resets and editor lifecycle shutdowns.
        /// Hosts that isolate their loops can create a dedicated registry and assign it to
        /// each loop instead.
        /// </summary>
        public static readonly LuaUnityRuntimeRegistry Process = new LuaUnityRuntimeRegistry();

        private readonly object _gate = new object();
        private readonly HashSet<LuaUnityGameLoop> _hosts = new HashSet<LuaUnityGameLoop>();

        public int ActiveHostCount
        {
            get { lock (_gate) return _hosts.Count; }
        }

        internal void Register(LuaUnityGameLoop host)
        {
            lock (_gate) _hosts.Add(host);
        }

        internal void Unregister(LuaUnityGameLoop host)
        {
            lock (_gate) _hosts.Remove(host);
        }

        public void DisposeAll()
        {
            LuaUnityGameLoop[] snapshot;
            lock (_gate) snapshot = new List<LuaUnityGameLoop>(_hosts).ToArray();
            foreach (var host in snapshot)
            {
                if (host != null) host.Shutdown();
            }
            lock (_gate) _hosts.RemoveWhere(item => item == null || !item.IsInitialized);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlayMode()
        {
            Process.DisposeAll();
        }
    }
}
