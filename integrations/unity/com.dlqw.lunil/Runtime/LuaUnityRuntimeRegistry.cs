using System;
using System.Collections.Generic;
using UnityEngine;

namespace Lunil.Unity
{
    /// <summary>Tracks active Unity adapters across disabled-domain-reload play sessions.</summary>
    public static class LuaUnityRuntimeRegistry
    {
        private static readonly object Gate = new object();
        private static readonly HashSet<LuaUnityGameLoop> Hosts = new HashSet<LuaUnityGameLoop>();

        public static int ActiveHostCount
        {
            get { lock (Gate) return Hosts.Count; }
        }

        internal static void Register(LuaUnityGameLoop host)
        {
            lock (Gate) Hosts.Add(host);
        }

        internal static void Unregister(LuaUnityGameLoop host)
        {
            lock (Gate) Hosts.Remove(host);
        }

        public static void DisposeAll()
        {
            LuaUnityGameLoop[] snapshot;
            lock (Gate) snapshot = new List<LuaUnityGameLoop>(Hosts).ToArray();
            foreach (var host in snapshot)
            {
                if (host != null) host.Shutdown();
            }
            lock (Gate) Hosts.RemoveWhere(item => item == null || !item.IsInitialized);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlayMode()
        {
            DisposeAll();
        }
    }
}
