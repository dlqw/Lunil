using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lunil.Hosting;
using Lunil.StandardLibrary;
using Lunil.Workspace;
using UnityEngine;

namespace Lunil.Unity
{
    /// <summary>Owner-thread dispatcher drained by the Unity player loop.</summary>
    public sealed class LuaUnityDispatcher : ILuaGameLoopDispatcher
    {
        private readonly int _ownerThreadId;
        private readonly ConcurrentQueue<Action> _callbacks = new ConcurrentQueue<Action>();
        private int _closed;

        public LuaUnityDispatcher()
        {
            _ownerThreadId = Environment.CurrentManagedThreadId;
        }

        public bool CheckAccess()
        {
            return Environment.CurrentManagedThreadId == _ownerThreadId;
        }

        public void Post(Action callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (Volatile.Read(ref _closed) != 0)
                throw new ObjectDisposedException(nameof(LuaUnityDispatcher));
            _callbacks.Enqueue(callback);
        }

        public int Drain(int maximumCallbacks)
        {
            if (!CheckAccess())
                throw new InvalidOperationException("The Unity dispatcher must be drained on its owner thread.");
            if (maximumCallbacks <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumCallbacks));

            var count = 0;
            Action callback;
            while (count < maximumCallbacks && _callbacks.TryDequeue(out callback))
            {
                callback();
                count++;
            }
            return count;
        }

        public void Close()
        {
            Interlocked.Exchange(ref _closed, 1);
            Action ignored;
            while (_callbacks.TryDequeue(out ignored)) { }
        }
    }

    /// <summary>Monotonic Unity realtime clock for timers and game-loop scheduling.</summary>
    public sealed class LuaUnityTimeProvider : TimeProvider
    {
        public override long TimestampFrequency
        {
            get { return 1000000L; }
        }

        public override long GetTimestamp()
        {
            return checked((long)(Time.realtimeSinceStartupAsDouble * TimestampFrequency));
        }

        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.UtcNow;
        }
    }

    /// <summary>UTF-8 console that emits complete lines through the Unity console.</summary>
    public sealed class LuaUnityConsole : ILuaConsole
    {
        private readonly object _gate = new object();
        private readonly MemoryStream _output = new MemoryStream();
        private readonly MemoryStream _error = new MemoryStream();

        public byte[] ReadStandardInput()
        {
            return new byte[0];
        }

        public void Write(ReadOnlyMemory<byte> bytes)
        {
            Append(_output, bytes);
        }

        public void WriteError(ReadOnlyMemory<byte> bytes)
        {
            Append(_error, bytes);
        }

        public void WriteLine()
        {
            lock (_gate)
            {
                Flush(_output, false);
                Flush(_error, true);
            }
        }

        private void Append(MemoryStream target, ReadOnlyMemory<byte> bytes)
        {
            lock (_gate)
            {
                var copy = bytes.ToArray();
                target.Write(copy, 0, copy.Length);
            }
        }

        private static void Flush(MemoryStream stream, bool error)
        {
            var text = Encoding.UTF8.GetString(stream.ToArray());
            stream.SetLength(0);
            if (error) Debug.LogError(text); else Debug.Log(text);
        }
    }

    /// <summary>Exact in-memory Unity asset and workspace module resolver.</summary>
    public sealed class LuaUnityAssetResolver : ILuaGameLoopAssetResolver, ILuaModuleResolver, ILuaFileSystem
    {
        private readonly Dictionary<string, LuaScriptAsset> _assets;
        private readonly Dictionary<string, LuaScriptAsset> _modules;
        private readonly Dictionary<string, LuaScriptAsset> _files;

        public LuaUnityAssetResolver(IEnumerable<LuaScriptAsset> assets)
        {
            if (assets == null) throw new ArgumentNullException(nameof(assets));
            _assets = new Dictionary<string, LuaScriptAsset>(StringComparer.Ordinal);
            _modules = new Dictionary<string, LuaScriptAsset>(StringComparer.Ordinal);
            _files = new Dictionary<string, LuaScriptAsset>(StringComparer.Ordinal);
            foreach (var asset in assets)
            {
                if (asset == null) continue;
                if (_assets.ContainsKey(asset.AssetId))
                    throw new ArgumentException("Duplicate Lua asset identity: " + asset.AssetId, nameof(assets));
                if (_modules.ContainsKey(asset.ModuleName))
                    throw new ArgumentException("Duplicate Lua module name: " + asset.ModuleName, nameof(assets));
                _assets.Add(asset.AssetId, asset);
                _modules.Add(asset.ModuleName, asset);
                AddFileAlias(asset.ModuleName.Replace('.', '/') + ".lua", asset);
                if (asset.AssetId.StartsWith("@", StringComparison.Ordinal))
                    AddFileAlias(asset.AssetId.Substring(1), asset);
            }
        }

        public ValueTask<LuaGameLoopReadResult> ResolveAsync(
            string assetId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LuaScriptAsset asset;
            return new ValueTask<LuaGameLoopReadResult>(
                _assets.TryGetValue(assetId, out asset)
                    ? LuaGameLoopReadResult.FromValue(asset.Bytes)
                    : LuaGameLoopReadResult.Missing);
        }

        public ValueTask<LuaWorkspaceDocument> ResolveAsync(
            LuaModuleResolutionRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            cancellationToken.ThrowIfCancellationRequested();
            LuaScriptAsset asset;
            if (!_modules.TryGetValue(request.RequestedName, out asset))
                return new ValueTask<LuaWorkspaceDocument>((LuaWorkspaceDocument)null);
            return new ValueTask<LuaWorkspaceDocument>(LuaWorkspaceDocument.FromBytes(
                asset.ModuleName, asset.Bytes.Span, asset.AssetId));
        }

        public byte[] ReadAllBytes(string path)
        {
            LuaScriptAsset asset;
            if (!_files.TryGetValue(NormalizePath(path), out asset))
                throw new FileNotFoundException("The Lua asset path is not registered.", path);
            return asset.Bytes.ToArray();
        }

        public bool FileExists(string path)
        {
            return _files.ContainsKey(NormalizePath(path));
        }

        private void AddFileAlias(string path, LuaScriptAsset asset)
        {
            var normalized = NormalizePath(path);
            LuaScriptAsset existing;
            if (_files.TryGetValue(normalized, out existing) && existing != asset)
                throw new ArgumentException("Duplicate Lua file path: " + normalized, "assets");
            _files[normalized] = asset;
        }

        private static string NormalizePath(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            var normalized = path.Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal))
                normalized = normalized.Substring(2);
            return normalized;
        }
    }

    /// <summary>Persistent-data store with key confinement and same-volume atomic replacement.</summary>
    public sealed class LuaUnityPersistentStore : ILuaGameLoopPersistentStore
    {
        private readonly string _root;

        public LuaUnityPersistentStore(string subdirectory = "Lunil")
        {
            if (string.IsNullOrWhiteSpace(subdirectory))
                throw new ArgumentException("A persistent-store directory is required.", nameof(subdirectory));
            if (subdirectory.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                subdirectory.Contains("/") || subdirectory.Contains("\\"))
                throw new ArgumentException("The persistent-store directory must be one safe segment.", nameof(subdirectory));
            _root = Path.Combine(Application.persistentDataPath, subdirectory);
            Directory.CreateDirectory(_root);
        }

        public ValueTask<LuaGameLoopReadResult> ReadAsync(
            string key,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = GetPath(key);
            return new ValueTask<LuaGameLoopReadResult>(File.Exists(path)
                ? LuaGameLoopReadResult.FromValue(File.ReadAllBytes(path))
                : LuaGameLoopReadResult.Missing);
        }

        public ValueTask WriteAsync(
            string key,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = GetPath(key);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, value.ToArray());
            try
            {
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return default(ValueTask);
        }

        private string GetPath(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("A persistent-store key is required.", nameof(key));
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(key))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return Path.Combine(_root, encoded + ".bin");
        }
    }
}
