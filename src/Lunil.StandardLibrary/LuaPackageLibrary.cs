using System.Text;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.StandardLibrary;

internal static class LuaPackageLibrary
{
    private const string LoadedRegistryKey = "_LOADED";
    private const string PreloadRegistryKey = "_PRELOAD";
    private const string PackageRegistryKey = "_PACKAGE";
    private const string NativeLibraryError =
        "dynamic libraries are not enabled; check your Lua installation";

    private static readonly LuaNativeFunction RequireDescriptor =
        new("require", Require);
    private static readonly LuaNativeFunction PreloadSearcherDescriptor =
        new("package.searcher.preload", PreloadSearcher);
    private static readonly LuaNativeFunction LuaSearcherDescriptor =
        new("package.searcher.lua", LuaSearcher);
    private static readonly LuaNativeFunction NativeSearcherDescriptor =
        new("package.searcher.native", NativeSearcher);
    private static readonly LuaNativeFunction NativeRootSearcherDescriptor =
        new("package.searcher.native-root", NativeRootSearcher);

    public static LuaTable Install(LuaState state)
    {
        var loaded = GetOrCreateRegistryTable(state, LoadedRegistryKey);
        var preload = GetOrCreateRegistryTable(state, PreloadRegistryKey);
        var package = state.CreateTable(hashCapacity: 16);
        var searchers = state.CreateTable(arrayCapacity: 4);

        LuaLibraryHelpers.Set(state, package, "config", LuaLibraryHelpers.String(state, Config));
        LuaLibraryHelpers.Set(state, package, "loaded", LuaValue.FromTable(loaded));
        LuaLibraryHelpers.Set(state, package, "preload", LuaValue.FromTable(preload));
        LuaLibraryHelpers.Set(state, package, "path", LuaLibraryHelpers.String(
            state,
            GetPath(state, "LUA_PATH_5_4", "LUA_PATH", DefaultLuaPath)));
        LuaLibraryHelpers.Set(state, package, "cpath", LuaLibraryHelpers.String(
            state,
            GetPath(state, "LUA_CPATH_5_4", "LUA_CPATH", DefaultNativePath)));
        LuaLibraryHelpers.SetFunction(state, package, "loadlib", LoadLibrary);
        LuaLibraryHelpers.SetFunction(state, package, "searchpath", SearchPath);

        var packageValue = LuaValue.FromTable(package);
        state.Registry.Set(LuaLibraryHelpers.String(state, PackageRegistryKey), packageValue);
        searchers.Set(
            LuaValue.FromInteger(1),
            LuaValue.FromFunction(PreloadSearcherDescriptor));
        searchers.Set(
            LuaValue.FromInteger(2),
            LuaValue.FromFunction(LuaSearcherDescriptor));
        searchers.Set(
            LuaValue.FromInteger(3),
            LuaValue.FromFunction(NativeSearcherDescriptor));
        searchers.Set(
            LuaValue.FromInteger(4),
            LuaValue.FromFunction(NativeRootSearcherDescriptor));
        LuaLibraryHelpers.Set(state, package, "searchers", LuaValue.FromTable(searchers));

        var require = LuaValue.FromFunction(state.CreateNativeClosure(
            RequireDescriptor,
            [LuaValue.FromTable(loaded), LuaValue.FromTable(searchers)]));
        state.SetGlobal("package", packageValue);
        state.SetGlobal("require", require);
        loaded.Set(LuaLibraryHelpers.String(state, "package"), packageValue);
        return package;
    }

    private static string Config => string.Join(
        '\n',
        Path.DirectorySeparatorChar,
        ';',
        '?',
        '!',
        '-') + "\n";

    private static string DefaultLuaPath
    {
        get
        {
            var separator = Path.DirectorySeparatorChar;
            return $"?.lua;?{separator}init.lua";
        }
    }

    private static string DefaultNativePath =>
        OperatingSystem.IsWindows() ? "?.dll" : "?.so";

    private static LuaNativeStep Require(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        var loaded = context.Captures[0].AsTable();
        var searchers = context.Captures[1].AsTable();
        if (continuationId == 0)
        {
            var nameBytes = LuaLibraryHelpers.CheckStringBytes(values, 0, "require");
            var name = LuaValue.FromString(context.State.Strings.GetOrCreate(nameBytes));
            var cached = loaded.Get(name);
            if (cached.IsTruthy)
            {
                return LuaNativeStep.Completed(cached);
            }

            return CallSearcher(context, searchers, name, 1, string.Empty);
        }

        var state = context.InvocationState;
        var moduleName = state[0];
        if (continuationId == 1)
        {
            var index = checked((int)state[1].AsInteger());
            var errors = state[2].AsString().ToString();
            if (values.Length > 0 && values[0].Kind == LuaValueKind.Function)
            {
                var loaderData = values.Length > 1 ? values[1] : LuaValue.Nil;
                return LuaNativeStep.CallLua(
                    values[0],
                    [moduleName, loaderData],
                    continuationId: 2,
                    stateValues: [moduleName, loaderData],
                    callIsYieldable: false);
            }

            if (values.Length > 0 && values[0].Kind == LuaValueKind.String)
            {
                errors += "\n\t" + values[0].AsString();
            }

            return CallSearcher(context, searchers, moduleName, index + 1, errors);
        }

        var result = values.Length > 0 ? values[0] : LuaValue.Nil;
        if (!result.IsNil)
        {
            loaded.Set(moduleName, result);
        }

        var loadedResult = loaded.Get(moduleName);
        if (loadedResult.IsNil)
        {
            loadedResult = LuaValue.FromBoolean(true);
            loaded.Set(moduleName, loadedResult);
        }

        return LuaNativeStep.Completed(loadedResult, state[1]);
    }

    private static LuaNativeStep CallSearcher(
        LuaNativeCallContext context,
        LuaTable searchers,
        LuaValue moduleName,
        int index,
        string errors)
    {
        var searcher = searchers.Get(LuaValue.FromInteger(index));
        if (searcher.IsNil)
        {
            throw new LuaRuntimeException(
                $"module '{moduleName.AsString()}' not found:{errors}");
        }

        if (searcher.Kind != LuaValueKind.Function)
        {
            throw new LuaRuntimeException("'package.searchers' must be a table of functions");
        }

        return LuaNativeStep.CallLua(
            searcher,
            [moduleName],
            continuationId: 1,
            stateValues:
            [
                moduleName,
                LuaValue.FromInteger(index),
                LuaLibraryHelpers.String(context.State, errors),
            ],
            callIsYieldable: false);
    }

    private static LuaValue[] PreloadSearcher(LuaState state, ReadOnlySpan<LuaValue> values)
    {
        var name = LuaLibraryHelpers.Required(values, 0, "package.searcher.preload");
        var loader = GetRegistryTable(state, PreloadRegistryKey).Get(name);
        return loader.IsNil
            ? [LuaLibraryHelpers.String(state, $"no field package.preload['{name.AsString()}']")]
            :
            [
                loader,
                LuaLibraryHelpers.String(state, ":preload:"),
            ];
    }

    private static LuaValue[] LuaSearcher(LuaState state, ReadOnlySpan<LuaValue> values)
    {
        var name = LuaLibraryHelpers.CheckStringBytes(values, 0, "package.searcher.lua");
        var path = GetPackageString(state, GetPackage(state), "path");
        var found = FindPath(state, name, path, "."u8, DirectoryReplacement);
        if (found.Path is null)
        {
            return [LuaLibraryHelpers.String(state, found.Error)];
        }

        LuaValue[] loaded;
        try
        {
            var bytes = LuaStandardLibraryContext.Get(state).Options.FileSystem.ReadAllBytes(found.Path);
            loaded = LuaBasicLibrary.FinishLoad(
                state,
                bytes,
                Encoding.UTF8.GetBytes("@" + found.Path),
                "bt"u8.ToArray(),
                hasEnvironment: false,
                LuaValue.Nil).Values;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new LuaRuntimeException(
                $"error loading module '{Encoding.UTF8.GetString(name)}' from file '{found.Path}':\n\t{exception.Message}");
        }

        if (loaded.Length > 0 && loaded[0].Kind == LuaValueKind.Function)
        {
            return [loaded[0], LuaLibraryHelpers.String(state, found.Path)];
        }

        var detail = loaded.Length > 1 && loaded[1].Kind == LuaValueKind.String
            ? loaded[1].AsString().ToString()
            : "unknown load error";
        throw new LuaRuntimeException(
            $"error loading module '{Encoding.UTF8.GetString(name)}' from file '{found.Path}':\n\t{detail}");
    }

    private static LuaValue[] NativeSearcher(LuaState state, ReadOnlySpan<LuaValue> values) =>
        SearchNative(state, values, rootOnly: false);

    private static LuaValue[] NativeRootSearcher(LuaState state, ReadOnlySpan<LuaValue> values) =>
        SearchNative(state, values, rootOnly: true);

    private static LuaValue[] SearchNative(
        LuaState state,
        ReadOnlySpan<LuaValue> values,
        bool rootOnly)
    {
        var name = LuaLibraryHelpers.CheckStringBytes(values, 0, "package.searcher.native");
        if (rootOnly)
        {
            var dot = name.AsSpan().IndexOf((byte)'.');
            if (dot < 0)
            {
                return [LuaValue.Nil];
            }

            name = name.AsSpan(0, dot).ToArray();
        }

        var cpath = GetPackageString(state, GetPackage(state), "cpath");
        var found = FindPath(state, name, cpath, "."u8, DirectoryReplacement);
        return
        [
            LuaLibraryHelpers.String(
            state,
            found.Path is null
                ? found.Error
                : $"no native module loader is available for file '{found.Path}'"),
        ];
    }

    private static LuaValue[] SearchPath(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var name = LuaLibraryHelpers.CheckStringBytes(arguments, 0, "searchpath");
        var path = LuaLibraryHelpers.CheckStringBytes(arguments, 1, "searchpath");
        var separator = arguments.Length > 2 && !arguments[2].IsNil
            ? LuaLibraryHelpers.CheckStringBytes(arguments, 2, "searchpath")
            : "."u8.ToArray();
        var replacement = arguments.Length > 3 && !arguments[3].IsNil
            ? LuaLibraryHelpers.CheckStringBytes(arguments, 3, "searchpath")
            : DirectoryReplacement;
        var found = FindPath(state, name, path, separator, replacement);
        return found.Path is null
            ? [LuaValue.Nil, LuaLibraryHelpers.String(state, found.Error)]
            : [LuaLibraryHelpers.String(state, found.Path)];
    }

    private static LuaValue[] LoadLibrary(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        _ = LuaLibraryHelpers.CheckStringBytes(arguments, 0, "loadlib");
        _ = LuaLibraryHelpers.CheckStringBytes(arguments, 1, "loadlib");
        return
        [
            LuaValue.Nil,
            LuaLibraryHelpers.String(state, NativeLibraryError),
            LuaLibraryHelpers.String(state, "absent"),
        ];
    }

    private static SearchResult FindPath(
        LuaState state,
        ReadOnlySpan<byte> name,
        ReadOnlySpan<byte> path,
        ReadOnlySpan<byte> separator,
        ReadOnlySpan<byte> replacement)
    {
        var transformedName = Replace(name, separator, replacement);
        var pathText = Encoding.UTF8.GetString(path);
        var nameText = Encoding.UTF8.GetString(transformedName);
        var errors = new StringBuilder();
        foreach (var template in pathText.Split(';'))
        {
            var candidate = template.Replace("?", nameText, StringComparison.Ordinal);
            if (LuaStandardLibraryContext.Get(state).Options.FileSystem.FileExists(candidate))
            {
                return new SearchResult(candidate, string.Empty);
            }

            if (errors.Length > 0)
            {
                errors.Append("\n\t");
            }

            errors.Append("no file '").Append(candidate).Append('\'');
        }

        return new SearchResult(null, errors.ToString());
    }

    private static byte[] Replace(
        ReadOnlySpan<byte> source,
        ReadOnlySpan<byte> search,
        ReadOnlySpan<byte> replacement)
    {
        if (search.IsEmpty)
        {
            return source.ToArray();
        }

        using var output = new MemoryStream();
        var remaining = source;
        while (true)
        {
            var index = remaining.IndexOf(search);
            if (index < 0)
            {
                output.Write(remaining);
                return output.ToArray();
            }

            output.Write(remaining[..index]);
            output.Write(replacement);
            remaining = remaining[(index + search.Length)..];
        }
    }

    private static byte[] DirectoryReplacement =>
        Encoding.UTF8.GetBytes(Path.DirectorySeparatorChar.ToString());

    private static byte[] GetPackageString(LuaState state, LuaTable package, string field)
    {
        var value = package.Get(LuaLibraryHelpers.String(state, field));
        if (value.Kind != LuaValueKind.String)
        {
            throw new LuaRuntimeException($"'package.{field}' must be a string");
        }

        return value.AsString().ToArray();
    }

    private static LuaTable GetOrCreateRegistryTable(LuaState state, string name)
    {
        var key = LuaLibraryHelpers.String(state, name);
        var existing = state.Registry.Get(key);
        if (existing.Kind == LuaValueKind.Table)
        {
            return existing.AsTable();
        }

        var table = state.CreateTable();
        state.Registry.Set(key, LuaValue.FromTable(table));
        return table;
    }

    private static LuaTable GetRegistryTable(LuaState state, string name) =>
        state.Registry.Get(LuaLibraryHelpers.String(state, name)).AsTable();

    private static LuaTable GetPackage(LuaState state) =>
        state.Registry.Get(LuaLibraryHelpers.String(state, PackageRegistryKey)).AsTable();

    private static string GetPath(
        LuaState state,
        string versionedName,
        string fallbackName,
        string defaultPath)
    {
        var noEnvironment = state.Registry.Get(LuaLibraryHelpers.String(state, "LUA_NOENV")).IsTruthy;
        var environment = LuaStandardLibraryContext.Get(state).Options.Environment;
        var configured = noEnvironment
            ? null
            : environment.GetEnvironmentVariable(versionedName) ??
                environment.GetEnvironmentVariable(fallbackName);
        if (configured is null)
        {
            return defaultPath;
        }

        var marked = ";" + configured + ";";
        var expanded = marked.Replace(";;", $";{defaultPath};", StringComparison.Ordinal);
        return expanded[1..^1];
    }

    private readonly record struct SearchResult(string? Path, string Error);
}
