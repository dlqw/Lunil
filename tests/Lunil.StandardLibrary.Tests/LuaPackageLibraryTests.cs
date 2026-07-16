using System.Text;
using Lunil.Core.Text;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.StandardLibrary.Tests;

public sealed class LuaPackageLibraryTests
{
    [Fact]
    public void RequireUsesPreloadCacheLoaderDataAndNonYieldableBoundary()
    {
        var values = Execute(
            new Dictionary<string, byte[]>(),
            "local calls=0 package.preload.inner=function(n,d) return n..d end " +
            "package.preload.outer=function(n,d) calls=calls+1; return {v=require('inner')} end " +
            "local first,data=require('outer'); local second=require('outer') " +
            "package.preload.empty=function() end; local empty,emptyData=require('empty') " +
            "local co=coroutine.create(function() package.preload.y=function() coroutine.yield() end; require('y') end) " +
            "local ok,e=coroutine.resume(co) " +
            "return first.v,first==second,calls,data,empty,emptyData,ok,e~=nil");

        Assert.Equal("inner:preload:", values[0].AsString().ToString());
        Assert.True(values[1].AsBoolean());
        Assert.Equal(1, values[2].AsInteger());
        Assert.Equal(":preload:", values[3].AsString().ToString());
        Assert.True(values[4].AsBoolean());
        Assert.Equal(":preload:", values[5].AsString().ToString());
        Assert.False(values[6].AsBoolean());
        Assert.True(values[7].AsBoolean());
    }

    [Fact]
    public void LuaSearcherLoadsInjectedFilesAndSearchPathReportsEveryCandidate()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["mods/alpha.lua"] =
                Encoding.UTF8.GetBytes("local n,p=...; return {name=n,path=p}"),
        };
        var state = CreateState(files, new Dictionary<string, string?>());
        var values = Run(
            state,
            "package.path='missing/?.lua;mods/?.lua' " +
            "local m,data=require('alpha'); local cached=require('alpha') " +
            "local found=package.searchpath('alpha','missing/?.lua;mods/?.lua') " +
            "local absent,detail=package.searchpath('a.b','one/?.lua;two/?.lua') " +
            "local lf,le,where=package.loadlib('x','luaopen_x') " +
            "return m.name,m.path,data,m==cached,found,absent,detail,lf,le,where");

        const string expected = "mods/alpha.lua";
        Assert.Equal("alpha", values[0].AsString().ToString());
        Assert.Equal(expected, values[1].AsString().ToString());
        Assert.Equal(expected, values[2].AsString().ToString());
        Assert.True(values[3].AsBoolean());
        Assert.Equal(expected, values[4].AsString().ToString());
        Assert.True(values[5].IsNil);
        Assert.Contains($"one/a{Path.DirectorySeparatorChar}b.lua", values[6].AsString().ToString());
        Assert.Contains($"two/a{Path.DirectorySeparatorChar}b.lua", values[6].AsString().ToString());
        Assert.True(values[7].IsNil);
        Assert.Contains("dynamic libraries are not enabled", values[8].AsString().ToString());
        Assert.Equal("absent", values[9].AsString().ToString());

        Assert.True(state.TryGetModule("alpha", out var module));
        Assert.Equal(LuaModuleLoaderKind.LuaFile, module!.LoaderKind);
        Assert.Equal(expected, module.LoaderData.AsString().ToString());
        Assert.NotNull(module.Module);
        Assert.Equal(LuaValueKind.Table, module.CachedValue.Kind);
        Assert.Equal(["alpha"], state.GetLoadedModuleNames());
        var handlesWithRecord = state.Heap.HandleCount;

        _ = Run(state, "package.loaded.alpha=nil; assert(loadfile('mods/alpha.lua'))");

        Assert.False(state.TryGetModule("alpha", out _));
        Assert.Empty(state.GetLoadedModuleNames());
        Assert.Equal(handlesWithRecord - 3, state.Heap.HandleCount);
    }

    [Fact]
    public void PackageUsesVersionedEnvironmentPathsAndMissingRequireDiagnostics()
    {
        var state = CreateState(
            new Dictionary<string, byte[]>(),
            new Dictionary<string, string?>
            {
                ["LUA_PATH_5_4"] = "custom/?.lua;;tail/?.lua",
                ["LUA_PATH"] = "ignored/?.lua",
            });
        var values = Run(
            state,
            "local ok,e=pcall(require,'missing'); " +
            "return package.path,package.config,ok,e," +
            "package.loaded==debug_loaded_if_present");

        Assert.StartsWith("custom/?.lua;", values[0].AsString().ToString());
        Assert.Contains("tail/?.lua", values[0].AsString().ToString());
        Assert.EndsWith("\n", values[1].AsString().ToString());
        Assert.False(values[2].AsBoolean());
        Assert.Contains("module 'missing' not found:", values[3].AsString().ToString());
        Assert.Contains("no field package.preload['missing']", values[3].AsString().ToString());
    }

    private static LuaValue[] Execute(
        IReadOnlyDictionary<string, byte[]> files,
        string source) =>
        Run(CreateState(files, new Dictionary<string, string?>()), source);

    private static LuaState CreateState(
        IReadOnlyDictionary<string, byte[]> files,
        IReadOnlyDictionary<string, string?> environment)
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallBasic(
            state,
            new LuaStandardLibraryOptions
            {
                FileSystem = new MemoryFileSystem(files),
                Environment = new MemoryEnvironment(environment),
            });
        LuaStandardLibrary.InstallCoroutine(state);
        LuaStandardLibrary.InstallPackage(state);
        return state;
    }

    private static LuaValue[] Run(LuaState state, string source)
    {
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        return new LuaInterpreter()
            .Execute(state, state.CreateMainClosure(lowering.Module!))
            .Values
            .ToArray();
    }

    private sealed class MemoryFileSystem(IReadOnlyDictionary<string, byte[]> files)
        : ILuaFileSystem
    {
        public byte[] ReadAllBytes(string path) => files.TryGetValue(path, out var bytes)
            ? bytes.ToArray()
            : throw new FileNotFoundException(path);

        public bool FileExists(string path) => files.ContainsKey(path);
    }

    private sealed class MemoryEnvironment(IReadOnlyDictionary<string, string?> values)
        : ILuaEnvironment
    {
        public string? GetEnvironmentVariable(string name) =>
            values.GetValueOrDefault(name);
    }
}
