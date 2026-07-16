using System.Text;
using Lunil.Compiler;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.StandardLibrary;

namespace Lunil.Hosting;

public sealed partial class LuaHost
{
    /// <summary>
    /// Compiles or re-executes a previously required module while the host state is idle, then
    /// commits the selected cache policy only after candidate execution succeeds.
    /// </summary>
    public LuaModuleReloadResult ReloadModule(
        string name,
        LuaModuleReloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        options ??= LuaModuleReloadOptions.Default;
        ValidateReloadOptions(options);

        lock (_executionGate)
        {
            ThrowIfDisposed();
            if (!State.IsIdle)
            {
                return ReloadFailure(
                    name,
                    LuaModuleReloadStatus.StateBusy,
                    null,
                    "The Lua state is currently executing.");
            }

            if (!State.TryGetModule(name, out var previous))
            {
                return ReloadFailure(
                    name,
                    LuaModuleReloadStatus.NotLoaded,
                    null,
                    "No current require record matches package.loaded.");
            }

            return ReloadModuleCore(name, previous!, options, cancellationToken);
        }
    }

    private LuaModuleReloadResult ReloadModuleCore(
        string name,
        LuaModuleRecord previous,
        LuaModuleReloadOptions options,
        CancellationToken cancellationToken)
    {
        LuaCompilationResult? compilation = null;
        LuaValue candidateLoader;
        LuaValue candidateLoaderData;
        LuaIrModule? candidateModule;
        switch (previous.LoaderKind)
        {
            case LuaModuleLoaderKind.LuaFile:
                var sourcePath = options.SourcePath ?? GetRecordedSourcePath(previous);
                if (sourcePath is null)
                {
                    return ReloadFailure(
                        name,
                        LuaModuleReloadStatus.UnsupportedLoader,
                        previous,
                        "The recorded Lua file loader has no source path.");
                }

                byte[] source;
                try
                {
                    source = (StandardLibraryOptions?.FileSystem ??
                        SystemLuaFileSystem.Instance).ReadAllBytes(sourcePath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    return ReloadFailure(
                        name,
                        LuaModuleReloadStatus.SourceReadFailed,
                        previous,
                        exception.Message);
                }

                compilation = Compiler.CompileBytes(
                    source,
                    "@" + sourcePath,
                    cancellationToken);
                if (!compilation.Succeeded || compilation.Module is null)
                {
                    return ReloadFailure(
                        name,
                        LuaModuleReloadStatus.CompilationFailed,
                        previous,
                        "The replacement module did not compile.",
                        compilation: compilation);
                }

                candidateModule = compilation.Module;
                candidateLoader = LuaValue.FromFunction(State.CreateMainClosure(candidateModule));
                candidateLoaderData = LuaValue.FromString(State.Strings.GetOrCreate(
                    Encoding.UTF8.GetBytes(sourcePath)));
                break;
            case LuaModuleLoaderKind.Preload:
            case LuaModuleLoaderKind.CustomSearcher:
                if (options.SourcePath is not null)
                {
                    return ReloadFailure(
                        name,
                        LuaModuleReloadStatus.UnsupportedLoader,
                        previous,
                        "A source path override is valid only for built-in Lua file loaders.");
                }

                candidateLoader = previous.Loader;
                candidateLoaderData = previous.LoaderData;
                candidateModule = previous.Module;
                break;
            default:
                return ReloadFailure(
                    name,
                    LuaModuleReloadStatus.UnsupportedLoader,
                    previous,
                    "The recorded module loader kind is unsupported.");
        }

        if (candidateLoader.Kind != LuaValueKind.Function)
        {
            return ReloadFailure(
                name,
                LuaModuleReloadStatus.UnsupportedLoader,
                previous,
                "The recorded module loader is no longer callable.",
                compilation: compilation);
        }

        var upvalues = ReuseCompatibleLoaderUpvalues(previous.Loader, candidateLoader);
        var previousCache = previous.CachedValue;
        LuaExecutionResult execution;
        LuaValue cacheAfterExecution;
        try
        {
            execution = ExecuteLoader(name, candidateLoader, candidateLoaderData);
            cacheAfterExecution = State.GetLoadedModuleCacheValue(name);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            State.SetLoadedModuleCacheValue(name, previousCache);
            State.RestoreLoadedModule(previous);
            RestoreUpvalues(upvalues.Snapshots);
            return ReloadFailure(
                name,
                LuaModuleReloadStatus.ExecutionFailed,
                previous,
                exception.Message,
                compilation,
                sideEffectsMayHaveOccurred: true,
                reusedUpvalueCount: upvalues.Snapshots.Count,
                upvalueMismatchCount: upvalues.MismatchCount);
        }

        State.SetLoadedModuleCacheValue(name, previousCache);
        if (execution.Signal != LuaVmSignal.Completed)
        {
            State.RestoreLoadedModule(previous);
            RestoreUpvalues(upvalues.Snapshots);
            return ReloadFailure(
                name,
                LuaModuleReloadStatus.ExecutionFailed,
                previous,
                execution.Values.IsEmpty ? "The replacement loader failed." :
                    execution.Values[0].ToString(),
                compilation,
                execution,
                sideEffectsMayHaveOccurred: true,
                reusedUpvalueCount: upvalues.Snapshots.Count,
                upvalueMismatchCount: upvalues.MismatchCount);
        }

        var candidateValue = execution.Values.IsEmpty ? LuaValue.Nil : execution.Values[0];
        if (candidateValue.IsNil)
        {
            candidateValue = cacheAfterExecution != previousCache && !cacheAfterExecution.IsNil
                ? cacheAfterExecution
                : LuaValue.FromBoolean(true);
        }

        LuaValue committedValue;
        LuaTablePatch? tablePatch = null;
        try
        {
            switch (options.CachePolicy)
            {
                case LuaModuleReloadCachePolicy.ReplaceCache:
                    committedValue = candidateValue;
                    break;
                case LuaModuleReloadCachePolicy.PatchExistingTable:
                    if (previousCache.Kind != LuaValueKind.Table ||
                        candidateValue.Kind != LuaValueKind.Table)
                    {
                        throw new InvalidOperationException(
                            "PatchExistingTable requires both cache values to be tables.");
                    }

                    tablePatch = LuaTablePatch.Apply(
                        previousCache.AsTable(),
                        candidateValue.AsTable());
                    committedValue = previousCache;
                    break;
                case LuaModuleReloadCachePolicy.Custom:
                    committedValue = options.CustomCachePolicy!(new LuaModuleReloadContext(
                        name,
                        previous,
                        candidateValue,
                        candidateLoader,
                        candidateModule));
                    if (committedValue.IsNil)
                    {
                        committedValue = LuaValue.FromBoolean(true);
                    }

                    State.SetLoadedModuleCacheValue(name, previousCache);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(options));
            }

            State.SetLoadedModuleCacheValue(name, committedValue);
            var current = State.RegisterLoadedModule(
                name,
                previous.LoaderKind,
                candidateLoader,
                candidateLoaderData,
                committedValue,
                candidateModule);
            if (previous.Module is not null)
            {
                _jitExecutor?.Invalidate(previous.Module);
            }

            return new LuaModuleReloadResult(
                name,
                LuaModuleReloadStatus.Reloaded,
                previous,
                current,
                compilation,
                execution,
                null,
                SideEffectsMayHaveOccurred: false,
                upvalues.Snapshots.Count,
                upvalues.MismatchCount,
                tablePatch?.PatchedExportCount ?? 0,
                tablePatch?.RemovedExportCount ?? 0);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            State.SetLoadedModuleCacheValue(name, previousCache);
            tablePatch?.Rollback();
            State.RestoreLoadedModule(previous);
            RestoreUpvalues(upvalues.Snapshots);
            return ReloadFailure(
                name,
                LuaModuleReloadStatus.CachePolicyFailed,
                previous,
                exception.Message,
                compilation,
                execution,
                sideEffectsMayHaveOccurred: true,
                reusedUpvalueCount: upvalues.Snapshots.Count,
                upvalueMismatchCount: upvalues.MismatchCount);
        }
    }

    private LuaExecutionResult ExecuteLoader(
        string name,
        LuaValue loader,
        LuaValue loaderData)
    {
        var thread = State.CreateThread(loader);
        var moduleName = LuaValue.FromString(State.Strings.GetOrCreate(
            Encoding.UTF8.GetBytes(name)));
        LuaValue[] arguments = [moduleName, loaderData];
        return SelectedExecutionBackend == LuaHostExecutionBackend.Jit
            ? GetJitExecutor().Start(State, thread, arguments)
            : _interpreterExecutor.Start(State, thread, arguments);
    }

    private static string? GetRecordedSourcePath(LuaModuleRecord record) =>
        record.LoaderData.Kind == LuaValueKind.String
            ? record.LoaderData.AsString().ToString()
            : null;

    private static UpvalueReuseResult ReuseCompatibleLoaderUpvalues(
        LuaValue previousLoader,
        LuaValue candidateLoader)
    {
        var previous = previousLoader.TryGetClosure();
        var candidate = candidateLoader.TryGetClosure();
        if (previous is null || candidate is null || ReferenceEquals(previous, candidate))
        {
            return new UpvalueReuseResult([], 0);
        }

        var snapshots = new List<UpvalueSnapshot>();
        var previousLayout = previous.Function.Upvalues;
        var candidateLayout = candidate.Function.Upvalues;
        var sharedCount = Math.Min(previousLayout.Length, candidateLayout.Length);
        for (var index = 0; index < sharedCount; index++)
        {
            var oldUpvalue = previousLayout[index];
            var newUpvalue = candidateLayout[index];
            if (!string.Equals(oldUpvalue.Name, newUpvalue.Name, StringComparison.Ordinal) ||
                oldUpvalue.SourceKind != newUpvalue.SourceKind ||
                oldUpvalue.SourceIndex != newUpvalue.SourceIndex ||
                oldUpvalue.Kind != newUpvalue.Kind)
            {
                continue;
            }

            var cell = previous.GetUpvalue(index);
            snapshots.Add(new UpvalueSnapshot(cell, cell.Value));
            candidate.JoinUpvalue(index, previous, index);
        }

        return new UpvalueReuseResult(
            snapshots,
            Math.Max(previousLayout.Length, candidateLayout.Length) - snapshots.Count);
    }

    private static void RestoreUpvalues(IEnumerable<UpvalueSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            snapshot.Cell.Value = snapshot.Value;
        }
    }

    private static LuaModuleReloadResult ReloadFailure(
        string name,
        LuaModuleReloadStatus status,
        LuaModuleRecord? previous,
        string message,
        LuaCompilationResult? compilation = null,
        LuaExecutionResult? execution = null,
        bool sideEffectsMayHaveOccurred = false,
        int reusedUpvalueCount = 0,
        int upvalueMismatchCount = 0) => new(
            name,
            status,
            previous,
            previous,
            compilation,
            execution,
            message,
            sideEffectsMayHaveOccurred,
            reusedUpvalueCount,
            upvalueMismatchCount,
            PatchedExportCount: 0,
            RemovedExportCount: 0);

    private static void ValidateReloadOptions(LuaModuleReloadOptions options)
    {
        if (!Enum.IsDefined(options.CachePolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (options.SourcePath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.SourcePath);
        }

        if ((options.CachePolicy == LuaModuleReloadCachePolicy.Custom) !=
            (options.CustomCachePolicy is not null))
        {
            throw new ArgumentException(
                "CustomCachePolicy must be supplied if and only if CachePolicy is Custom.",
                nameof(options));
        }
    }

    private sealed class LuaTablePatch
    {
        private readonly LuaTable _target;
        private readonly List<KeyValuePair<LuaValue, LuaValue>> _previousEntries;
        private readonly LuaTable? _previousMetatable;

        private LuaTablePatch(
            LuaTable target,
            List<KeyValuePair<LuaValue, LuaValue>> previousEntries,
            LuaTable? previousMetatable,
            int patchedExportCount,
            int removedExportCount)
        {
            _target = target;
            _previousEntries = previousEntries;
            _previousMetatable = previousMetatable;
            PatchedExportCount = patchedExportCount;
            RemovedExportCount = removedExportCount;
        }

        public int PatchedExportCount { get; }

        public int RemovedExportCount { get; }

        public static LuaTablePatch Apply(LuaTable target, LuaTable replacement)
        {
            var previousEntries = Snapshot(target);
            var replacementEntries = Snapshot(replacement);
            var patch = new LuaTablePatch(
                target,
                previousEntries,
                target.Metatable,
                replacementEntries.Count,
                previousEntries.Count(previous => !replacementEntries.Any(
                    replacement => replacement.Key == previous.Key)));
            try
            {
                Replace(target, replacementEntries, replacement.Metatable);
                return patch;
            }
            catch
            {
                patch.Rollback();
                throw;
            }
        }

        public void Rollback() => Replace(_target, _previousEntries, _previousMetatable);

        private static void Replace(
            LuaTable target,
            IReadOnlyList<KeyValuePair<LuaValue, LuaValue>> entries,
            LuaTable? metatable)
        {
            foreach (var existing in Snapshot(target))
            {
                target.Set(existing.Key, LuaValue.Nil);
            }

            foreach (var entry in entries)
            {
                target.Set(entry.Key, entry.Value);
            }

            target.SetMetatable(metatable);
        }

        private static List<KeyValuePair<LuaValue, LuaValue>> Snapshot(LuaTable table)
        {
            var entries = new List<KeyValuePair<LuaValue, LuaValue>>();
            var key = LuaValue.Nil;
            while (table.Next(key, out var nextKey, out var value))
            {
                entries.Add(new KeyValuePair<LuaValue, LuaValue>(nextKey, value));
                key = nextKey;
            }

            return entries;
        }
    }

    private sealed record UpvalueReuseResult(
        List<UpvalueSnapshot> Snapshots,
        int MismatchCount);

    private readonly record struct UpvalueSnapshot(LuaUpvalue Cell, LuaValue Value);
}
