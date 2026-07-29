using System.Text;
using Lunil.Compiler;
using Lunil.Gameplay.Fixture;
using Lunil.Hosting;
using Lunil.StandardLibrary;
using Lunil.Workspace;

var resolver = new MemoryResolver(new Dictionary<string, byte[]>
{
    [SharedGameplayFixture.ModulePath] =
        Encoding.UTF8.GetBytes(SharedGameplayFixture.InitialRulesSource),
});
using var gameLoop = new LuaGameLoopHost(new LuaGameLoopHostOptions
{
    HostOptions = LuaHostOptions.Default with
    {
        ExecutionBackend = LuaHostExecutionBackend.Interpreter,
        ModuleResolver = resolver,
        StandardLibrary = LuaHostCapabilityProfiles.Create(LuaHostProfile.Trusted) with
        {
            FileSystem = resolver,
        },
    },
    ModuleResolver = resolver,
    AssetResolver = resolver,
    PersistentStore = new MemoryStore(),
});
var soakSeconds = ReadDoubleArgument(args, "--soak-seconds=");
if (soakSeconds > 0.0)
{
    var warmupSeconds = ReadDoubleArgument(args, "--warmup-seconds=", Math.Min(soakSeconds / 3.0, 1800.0));
    var sampleSeconds = ReadDoubleArgument(args, "--sample-seconds=", Math.Max(1.0, Math.Min(300.0, (soakSeconds - warmupSeconds) / 6.0)));
    var targetTicksPerSecond = ReadDoubleArgument(args, "--target-ticks-per-second=", 1000.0);
    var soak = new SharedEngineSoakSession(
        gameLoop,
        gameLoop.Tick,
        "plain",
        TimeSpan.FromSeconds(soakSeconds),
        TimeSpan.FromSeconds(warmupSeconds),
        TimeSpan.FromSeconds(sampleSeconds));
    SharedEngineSoakResult? soakResult = null;
    var pacing = System.Diagnostics.Stopwatch.StartNew();
    while (soakResult is null)
    {
        soakResult = soak.Tick();
        if (targetTicksPerSecond > 0.0)
        {
            var target = TimeSpan.FromSeconds(soak.TickCount / targetTicksPerSecond);
            var remaining = target - pacing.Elapsed;
            if (remaining > TimeSpan.FromMilliseconds(1.0))
                Thread.Sleep(remaining > TimeSpan.FromMilliseconds(10.0) ? 10 : 1);
        }
    }
    Console.WriteLine(soakResult.ToMarker());
}
else
{
    var result = SharedGameplayFixture.Run(
        gameLoop,
        fixedTick => fixedTick ? gameLoop.TickFixed() : gameLoop.Tick(),
        "plain");
    Console.WriteLine(result.ToMarker());
}
return 0;

static double ReadDoubleArgument(
    string[] arguments,
    string prefix,
    double defaultValue = 0.0)
{
    var argument = arguments.LastOrDefault(value =>
        value.StartsWith(prefix, StringComparison.Ordinal));
    if (argument is null) return defaultValue;
    return double.Parse(argument[prefix.Length..],
        System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class MemoryResolver(IReadOnlyDictionary<string, byte[]> files) :
    ILuaGameLoopAssetResolver,
    ILuaModuleResolver,
    ILuaFileSystem
{
    public byte[] ReadAllBytes(string path) => files.TryGetValue(Normalize(path), out var bytes)
        ? bytes.ToArray()
        : throw new FileNotFoundException(path);

    public bool FileExists(string path) => files.ContainsKey(Normalize(path));

    public ValueTask<LuaGameLoopReadResult> ResolveAsync(
        string assetId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(files.TryGetValue(assetId, out var bytes)
            ? LuaGameLoopReadResult.FromValue(bytes)
            : LuaGameLoopReadResult.Missing);
    }

    public ValueTask<LuaWorkspaceDocument?> ResolveAsync(
        LuaModuleResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.RequestedName.Replace('.', '/') + ".lua";
        return ValueTask.FromResult(files.TryGetValue(path, out var bytes)
            ? LuaWorkspaceDocument.FromBytes(request.RequestedName, bytes, "@" + path)
            : null);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}

internal sealed class MemoryStore : ILuaGameLoopPersistentStore
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

    public ValueTask<LuaGameLoopReadResult> ReadAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_values.TryGetValue(key, out var value)
            ? LuaGameLoopReadResult.FromValue(value)
            : LuaGameLoopReadResult.Missing);
    }

    public ValueTask WriteAsync(
        string key,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values[key] = value.ToArray();
        return ValueTask.CompletedTask;
    }
}
