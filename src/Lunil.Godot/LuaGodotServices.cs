using System.Collections.Concurrent;
using System.Text;
using Godot;
using Lunil.Hosting;
using Lunil.StandardLibrary;
using Lunil.Workspace;

namespace Lunil.Godot;

/// <summary>Owner-thread dispatcher drained by the Godot process loop.</summary>
public sealed class LuaGodotDispatcher : ILuaGameLoopDispatcher, IDisposable
{
    private readonly int _ownerThreadId = System.Environment.CurrentManagedThreadId;
    private readonly ConcurrentQueue<Action> _callbacks = new();
    private int _closed;

    public bool CheckAccess() => System.Environment.CurrentManagedThreadId == _ownerThreadId;

    public void Post(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        _callbacks.Enqueue(callback);
    }

    public int Drain(int maximumCallbacks)
    {
        if (!CheckAccess())
        {
            throw new InvalidOperationException(
                "The Godot dispatcher must be drained on its owner thread.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCallbacks);
        var count = 0;
        while (count < maximumCallbacks && _callbacks.TryDequeue(out var callback))
        {
            callback();
            count++;
        }

        return count;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        while (_callbacks.TryDequeue(out _))
        {
        }
    }
}

/// <summary>Monotonic Godot clock backed by engine tick microseconds.</summary>
public sealed class LuaGodotTimeProvider : TimeProvider
{
    public override long TimestampFrequency => 1_000_000L;

    public override long GetTimestamp() => checked((long)global::Godot.Time.GetTicksUsec());

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
}

/// <summary>UTF-8 console that emits complete lines through <see cref="GD"/>.</summary>
public sealed class LuaGodotConsole : ILuaConsole
{
    private readonly object _gate = new();
    private readonly List<byte> _output = [];
    private readonly List<byte> _error = [];

    public byte[] ReadStandardInput() => [];

    public void Write(ReadOnlyMemory<byte> bytes) => Append(_output, bytes);

    public void WriteError(ReadOnlyMemory<byte> bytes) => Append(_error, bytes);

    public void WriteLine()
    {
        lock (_gate)
        {
            Flush(_output, error: false);
            Flush(_error, error: true);
        }
    }

    private void Append(List<byte> target, ReadOnlyMemory<byte> bytes)
    {
        lock (_gate)
        {
            foreach (var value in bytes.Span)
            {
                target.Add(value);
            }
        }
    }

    private static void Flush(List<byte> stream, bool error)
    {
        if (stream.Count == 0)
        {
            return;
        }

        var text = Encoding.UTF8.GetString([.. stream]);
        stream.Clear();
        if (error)
        {
            GD.PushError(text);
        }
        else
        {
            GD.Print(text);
        }
    }
}

/// <summary>
/// Exact Godot ResourceLoader-backed asset, module, and virtual file-system resolver.
/// </summary>
public sealed class LuaGodotAssetResolver :
    ILuaGameLoopAssetResolver,
    ILuaModuleResolver,
    ILuaFileSystem
{
    private readonly Dictionary<string, Asset> _assets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Asset> _modules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Asset> _files = new(StringComparer.Ordinal);

    public LuaGodotAssetResolver(IEnumerable<LuaGodotScriptResource> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        foreach (var resource in resources)
        {
            if (resource is null)
            {
                continue;
            }

            Register(resource);
        }
    }

    public static LuaGodotAssetResolver Load(IEnumerable<string> resourcePaths)
    {
        ArgumentNullException.ThrowIfNull(resourcePaths);
        var resources = new List<LuaGodotScriptResource>();
        foreach (var path in resourcePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException(
                    "A Godot resource path cannot be empty.", nameof(resourcePaths));
            }

            var resource = ResourceLoader.Load<LuaGodotScriptResource>(path);
            if (resource is null)
            {
                throw new FileNotFoundException(
                    "The Lunil Godot script resource could not be loaded.", path);
            }

            resources.Add(resource);
        }

        return new LuaGodotAssetResolver(resources);
    }

    public ValueTask<LuaGameLoopReadResult> ResolveAsync(
        string assetId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_assets.TryGetValue(assetId, out var asset)
            ? LuaGameLoopReadResult.FromValue(asset.Bytes)
            : LuaGameLoopReadResult.Missing);
    }

    public ValueTask<LuaWorkspaceDocument?> ResolveAsync(
        LuaModuleResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_modules.TryGetValue(request.RequestedName, out var asset)
            ? LuaWorkspaceDocument.FromBytes(asset.ModuleName, asset.Bytes.Span, asset.AssetId)
            : null);
    }

    public byte[] ReadAllBytes(string path)
    {
        if (!_files.TryGetValue(NormalizePath(path), out var asset))
        {
            throw new FileNotFoundException(
                "The Lua Godot resource path is not registered.", path);
        }

        return asset.Bytes.ToArray();
    }

    public bool FileExists(string path) => _files.ContainsKey(NormalizePath(path));

    private void Register(LuaGodotScriptResource resource)
    {
        var assetId = resource.GetEffectiveAssetId();
        if (string.IsNullOrWhiteSpace(resource.ModuleName))
        {
            throw new ArgumentException(
                "A Lunil Godot script resource must have a ModuleName.", nameof(resource));
        }

        var asset = new Asset(assetId, resource.ModuleName, resource.GetBytes());
        if (!_assets.TryAdd(asset.AssetId, asset))
        {
            throw new ArgumentException(
                "Duplicate Lunil Godot asset identity: " + asset.AssetId, nameof(resource));
        }

        if (!_modules.TryAdd(asset.ModuleName, asset))
        {
            throw new ArgumentException(
                "Duplicate Lunil Godot module name: " + asset.ModuleName, nameof(resource));
        }

        AddFileAlias(asset.ModuleName.Replace('.', '/') + ".lua", asset);
        if (asset.AssetId.StartsWith('@'))
        {
            AddFileAlias(asset.AssetId[1..], asset);
        }
    }

    private void AddFileAlias(string path, Asset asset)
    {
        var normalized = NormalizePath(path);
        if (_files.TryGetValue(normalized, out var existing) && !ReferenceEquals(existing, asset))
        {
            throw new ArgumentException("Duplicate Lua file path: " + normalized);
        }

        _files[normalized] = asset;
    }

    private static string NormalizePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private sealed record Asset(string AssetId, string ModuleName, ReadOnlyMemory<byte> Bytes);
}

/// <summary>Godot user-data store with confined keys and atomic same-directory publication.</summary>
public sealed class LuaGodotPersistentStore : ILuaGameLoopPersistentStore
{
    private readonly string _root;

    public LuaGodotPersistentStore(string subdirectory = "Lunil")
    {
        if (string.IsNullOrWhiteSpace(subdirectory) ||
            subdirectory.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            subdirectory.Contains('/') || subdirectory.Contains('\\'))
        {
            throw new ArgumentException(
                "The persistent-store directory must be one safe segment.",
                nameof(subdirectory));
        }

        _root = ProjectSettings.GlobalizePath("user://" + subdirectory);
        Directory.CreateDirectory(_root);
    }

    public ValueTask<LuaGameLoopReadResult> ReadAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(key);
        return ValueTask.FromResult(System.IO.File.Exists(path)
            ? LuaGameLoopReadResult.FromValue(System.IO.File.ReadAllBytes(path))
            : LuaGameLoopReadResult.Missing);
    }

    public ValueTask WriteAsync(
        string key,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(key);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        System.IO.File.WriteAllBytes(temporary, value.ToArray());
        try
        {
            System.IO.File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (System.IO.File.Exists(temporary))
            {
                System.IO.File.Delete(temporary);
            }
        }

        return ValueTask.CompletedTask;
    }

    private string GetPath(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A persistent-store key is required.", nameof(key));
        }

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(key))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Path.Combine(_root, encoded + ".bin");
    }
}

/// <summary>
/// Host-facing defaults shared by the Godot game-loop adapter.
/// </summary>
internal static class LuaGodotServices
{
    /// <summary>
    /// Default capabilities for scripts loaded from project assets: file access stays
    /// inside the registered resources, while shell execution, process termination,
    /// and environment variables remain denied. Trusted access requires an explicit
    /// <c>ConfigureHostOptions</c> opt-in.
    /// </summary>
    public static LuaStandardLibraryOptions CreateDefaultStandardLibrary(ILuaFileSystem fileSystem) =>
        LuaHostCapabilityProfiles.Create(LuaHostProfile.Restricted) with
        {
            FileSystem = fileSystem,
        };
}
