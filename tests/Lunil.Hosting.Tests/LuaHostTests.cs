using System.Text;
using Lunil.CodeGen.Cil.Jit;
using Lunil.IR.Lua54;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.StandardLibrary;
using Lunil.Workspace;

namespace Lunil.Hosting.Tests;

public sealed class LuaHostTests
{
    [Fact]
    public void RunCompilesAndExecutesThroughOnePublicBoundary()
    {
        using var host = new LuaHost();

        var result = host.RunUtf8(
            "local total=0; for i=1,10 do total=total+i end; return total",
            "@scripts/sum.lua");

        Assert.True(result.Succeeded);
        Assert.True(result.CompilationSucceeded);
        Assert.True(result.ExecutionStarted);
        Assert.Equal(LuaVmSignal.Completed, result.Execution!.Signal);
        Assert.Equal(55, Assert.Single(result.Execution.Values).AsInteger());
    }

    [Fact]
    public void ReusableHostPreservesStateAcrossCompilations()
    {
        using var host = new LuaHost();

        var first = host.RunUtf8("answer=41");
        var second = host.RunUtf8("return answer+1");

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(42, Assert.Single(second.Execution!.Values).AsInteger());
    }

    [Fact]
    public void CompileFailureDoesNotStartExecution()
    {
        using var host = new LuaHost();

        var result = host.RunUtf8("local =");

        Assert.False(result.Succeeded);
        Assert.False(result.CompilationSucceeded);
        Assert.False(result.ExecutionStarted);
        Assert.Null(result.Execution);
    }

    [Fact]
    public void RestrictedProfileDeniesFileSystemAndCapturesConsole()
    {
        using var host = new LuaHost(LuaHostOptions.Restricted);

        var result = host.RunUtf8(
            "local file,err=io.open('secret','r'); print('sandbox'); " +
            "return file==nil,err~=nil,os.getenv('HOME')==nil");

        Assert.True(result.Succeeded);
        Assert.All(result.Execution!.Values, value => Assert.True(value.AsBoolean()));
        Assert.NotNull(host.BufferedConsole);
        Assert.Equal("sandbox\n", Encoding.UTF8.GetString(
            host.BufferedConsole!.GetStandardOutput()));
    }

    [Fact]
    public void DeterministicProfileFixesTimeZoneClockAndHashSeed()
    {
        using var first = new LuaHost(LuaHostOptions.Deterministic);
        using var second = new LuaHost(LuaHostOptions.Deterministic);

        var result = first.RunUtf8(
            "return os.clock(),os.time(),os.date('!%Y-%m-%d %H:%M:%S',os.time())");

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Execution!.Values[0].AsFloat());
        Assert.Equal(0, result.Execution.Values[1].AsInteger());
        Assert.Equal("1970-01-01 00:00:00", result.Execution.Values[2].AsString().ToString());
        Assert.Equal(0, first.State.Heap.HashSeed);
        Assert.Equal(first.State.Heap.HashSeed, second.State.Heap.HashSeed);
    }

    [Fact]
    public void CustomCapabilitiesOverrideProfileDefaults()
    {
        var console = new LuaBufferedConsole();
        var options = LuaHostOptions.Deterministic with
        {
            StandardLibrary = LuaHostCapabilityProfiles.Create(LuaHostProfile.Restricted) with
            {
                Console = console,
            },
        };
        using var host = new LuaHost(options);

        var result = host.RunUtf8("print('custom')");

        Assert.True(result.Succeeded);
        Assert.Same(console, host.BufferedConsole);
        Assert.Equal("custom\n", Encoding.UTF8.GetString(console.GetStandardOutput()));
    }

    [Fact]
    public void AnnotationsAreErasedFromRuntimeExecution()
    {
        using var host = new LuaHost();

        var result = host.RunUtf8("---@type string\nreturn 42");

        Assert.True(result.Succeeded);
        Assert.Equal(42, Assert.Single(result.Execution!.Values).AsInteger());
        Assert.Single(result.Compilation.Annotations.Annotations);
        Assert.Single(result.Compilation.Analysis.Functions);
        Assert.NotEmpty(result.Compilation.Analysis.Expressions);
    }

    [Fact]
    public async Task HostPublishesReusableWorkspaceAnalysisBoundary()
    {
        using var host = new LuaHost();

        var result = await host.AnalyzeWorkspaceAsync([
            LuaWorkspaceDocument.FromUtf8(
                "app",
                "local dep = require('dep')\nreturn dep.value + 1"),
            LuaWorkspaceDocument.FromUtf8("dep", "return { value = 41 }"),
        ]);

        Assert.True(result.Succeeded);
        Assert.Equal("integer", result.GetModule("app")!.ExportedType.DisplayName);
        Assert.Equal(2, result.Graph.Nodes.Length);
    }

    [Fact]
    public void InterpreterBackendRemainsAnExplicitDeterministicOptOut()
    {
        using var host = new LuaHost(LuaHostOptions.Default with
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
        });

        var result = host.RunUtf8("return 6 * 7");

        Assert.True(result.Succeeded);
        Assert.Equal(LuaHostExecutionBackend.Interpreter, host.SelectedExecutionBackend);
        Assert.Null(host.JitStatistics);
        Assert.Equal(42, Assert.Single(result.Execution!.Values).AsInteger());
    }

    [Fact]
    public void QualifiedJitBackendCompilesEligibleCodeAndPreservesBudgets()
    {
        using var host = new LuaHost(LuaHostOptions.Default with
        {
            ExecutionBackend = LuaHostExecutionBackend.Jit,
            Execution = LuaInterpreterOptions.Default with
            {
                MaximumInstructionCount = 10_000,
            },
            Jit = LuaJitExecutorOptions.Default with
            {
                FunctionEntryThreshold = 1,
                BackedgeThreshold = 1,
                SynchronousCompilation = true,
                EnableTier2 = false,
                EnableLoopOsr = false,
            },
        });
        Assert.Null(host.JitStatistics);

        var result = host.RunUtf8(
            "local total=0; for i=1,100 do total=total+i end; return total");

        Assert.True(result.Succeeded);
        Assert.Equal(LuaHostExecutionBackend.Jit, host.SelectedExecutionBackend);
        Assert.True(host.IsDynamicCodeAvailable);
        Assert.NotNull(host.JitStatistics);
        Assert.True(host.JitStatistics!.CompilationCompleted > 0);
        Assert.True(host.JitStatistics.CompiledInvocations > 0);
        Assert.Equal(5_050, Assert.Single(result.Execution!.Values).AsInteger());
        Assert.Throws<Lunil.Runtime.LuaRuntimeException>(() => host.RunUtf8("while true do end"));
    }

    [Fact]
    public void DumpLoadPreservesConstantLeftMetamethodOperandOrder()
    {
        const string source = """
            local rhs
            local mt = {
                __add = function(left, right)
                    return left == 5.25 and right == rhs
                end,
            }
            rhs = setmetatable({}, mt)
            return 5.25 + rhs
            """;
        using var host = new LuaHost(LuaHostOptions.Default with
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
        });
        var compilation = host.CompileUtf8(source, "@metamethod-order.lua");
        Assert.True(compilation.Succeeded);
        var module = Assert.IsType<Lunil.IR.Canonical.LuaIrModule>(compilation.Module);
        var binary = Lua54CanonicalPrototypeWriter.Write(module, module.MainFunctionId);

        var result = host.ExecuteBinaryChunk(binary);

        Assert.Equal(LuaVmSignal.Completed, result.Signal);
        Assert.True(Assert.Single(result.Values).AsBoolean());
    }

    [Fact]
    public void ReloadModuleRecompilesFileAndReplacesCacheAfterSuccessfulExecution()
    {
        var files = new MutableMemoryFileSystem(new Dictionary<string, string>
        {
            ["mods/value.lua"] = "local name,path=...; return {value=1,name=name,path=path}",
        });
        using var host = CreateReloadHost(files, LuaHostExecutionBackend.Jit);
        var initial = host.RunUtf8(
            "package.path='mods/?.lua'; original=require('value'); return original.value");
        Assert.True(initial.Succeeded);
        Assert.True(host.State.TryGetModule("value", out var previous));

        files.Set("mods/value.lua", "local name,path=...; return {value=2,name=name,path=path}");
        var reload = host.ReloadModule("value");
        var current = host.RunUtf8(
            "local value=require('value'); return value.value,value==original,value.path");

        Assert.True(reload.Succeeded, reload.Message);
        Assert.Equal(LuaModuleReloadStatus.Reloaded, reload.Status);
        Assert.NotNull(reload.Compilation);
        Assert.Equal(LuaVmSignal.Completed, reload.Execution!.Signal);
        Assert.True(reload.CurrentRecord!.Revision > previous!.Revision);
        Assert.NotSame(previous.Module, reload.CurrentRecord.Module);
        Assert.Equal(LuaModuleLoaderKind.LuaFile, reload.CurrentRecord.LoaderKind);
        Assert.Equal("mods/value.lua", reload.CurrentRecord.LoaderData.AsString().ToString());
        Assert.Equal(1, reload.ReusedUpvalueCount);
        Assert.Equal(0, reload.UpvalueMismatchCount);
        Assert.Equal(2, current.Execution!.Values[0].AsInteger());
        Assert.False(current.Execution.Values[1].AsBoolean());
        Assert.Equal("mods/value.lua", current.Execution.Values[2].AsString().ToString());
    }

    [Fact]
    public void ReloadModuleCanPatchExistingTableIdentityAndRemoveOldExports()
    {
        var files = new MutableMemoryFileSystem(new Dictionary<string, string>
        {
            ["mods/value.lua"] = "return {keep=1,remove=2}",
        });
        using var host = CreateReloadHost(files);
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; original=require('value')").Succeeded);
        files.Set("mods/value.lua", "return {keep=3,added=4}");

        var reload = host.ReloadModule("value", new LuaModuleReloadOptions
        {
            CachePolicy = LuaModuleReloadCachePolicy.PatchExistingTable,
        });
        var current = host.RunUtf8(
            "local value=require('value'); " +
            "return value==original,value.keep,value.remove,value.added");

        Assert.True(reload.Succeeded, reload.Message);
        Assert.Equal(2, reload.PatchedExportCount);
        Assert.Equal(1, reload.RemovedExportCount);
        Assert.True(current.Execution!.Values[0].AsBoolean());
        Assert.Equal(3, current.Execution.Values[1].AsInteger());
        Assert.True(current.Execution.Values[2].IsNil);
        Assert.Equal(4, current.Execution.Values[3].AsInteger());
    }

    [Fact]
    public void ReloadModuleMigratesCompatibleExportedClosureSlotsAndPreservesUpvalues()
    {
        var files = new MutableMemoryFileSystem(new Dictionary<string, string>
        {
            ["mods/value.lua"] =
                "local state=0; local function next() state=state+1; return state end; " +
                "return {next=next}",
        });
        using var host = CreateReloadHost(files, LuaHostExecutionBackend.Jit);
        var initial = host.RunUtf8(
            "package.path='mods/?.lua'; local m=require('value'); alias=m.next; return alias()");
        Assert.Equal(1, initial.Execution!.Values[0].AsInteger());
        var alias = host.State.GetGlobal("alias").TryGetClosure()!;
        var previousVersion = alias.FunctionVersion;
        files.Set(
            "mods/value.lua",
            "local state=0; local function next() state=state+2; return state end; " +
            "return {next=next}");

        var reload = host.ReloadModule("value");
        var current = host.RunUtf8("return alias(),require('value').next()");

        Assert.True(reload.Succeeded, reload.Message);
        var migration = Assert.Single(reload.FunctionMigrations);
        Assert.Equal(LuaFunctionMigrationStatus.Updated, migration.Status);
        Assert.Equal(1, reload.UpdatedFunctionCount);
        Assert.Equal(0, reload.IncompatibleFunctionCount);
        Assert.Same(alias, host.State.GetGlobal("alias").TryGetClosure());
        Assert.NotSame(previousVersion, alias.FunctionVersion);
        Assert.Equal(previousVersion.Generation + 1, alias.FunctionVersion.Generation);
        Assert.Equal(3, current.Execution!.Values[0].AsInteger());
        Assert.Equal(2, current.Execution.Values[1].AsInteger());
    }

    [Fact]
    public void ReloadModuleReportsIncompatibleExportedClosureUpvalues()
    {
        var files = new MutableMemoryFileSystem(new Dictionary<string, string>
        {
            ["mods/value.lua"] =
                "local state=1; local function get() return state end; return {get=get}",
        });
        using var host = CreateReloadHost(files);
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; local m=require('value'); alias=m.get").Succeeded);
        files.Set(
            "mods/value.lua",
            "local state,extra=2,3; local function get() return state+extra end; " +
            "return {get=get}");

        var reload = host.ReloadModule("value");
        var current = host.RunUtf8("return alias(),require('value').get()");

        Assert.True(reload.Succeeded, reload.Message);
        var migration = Assert.Single(reload.FunctionMigrations);
        Assert.Equal(LuaFunctionMigrationStatus.UpvalueLayoutMismatch, migration.Status);
        Assert.Equal(0, reload.UpdatedFunctionCount);
        Assert.Equal(1, reload.IncompatibleFunctionCount);
        Assert.Equal(1, current.Execution!.Values[0].AsInteger());
        Assert.Equal(5, current.Execution.Values[1].AsInteger());
    }

    [Fact]
    public void SuspendedFrameKeepsOldFunctionVersionAfterCompatibleSlotMigration()
    {
        var files = new MutableMemoryFileSystem(new Dictionary<string, string>
        {
            ["mods/value.lua"] =
                "local state=1; local function get() coroutine.yield('pause'); return state end; " +
                "return {get=get}",
        });
        using var host = CreateReloadHost(files);
        var suspended = host.RunUtf8(
            "package.path='mods/?.lua'; local m=require('value'); alias=m.get; " +
            "co=coroutine.create(alias); return coroutine.resume(co)");
        Assert.True(suspended.Execution!.Values[0].AsBoolean());
        Assert.Equal("pause", suspended.Execution.Values[1].AsString().ToString());
        var alias = host.State.GetGlobal("alias").TryGetClosure()!;
        files.Set(
            "mods/value.lua",
            "local state=2; local function get() local _=coroutine; return state+10 end; " +
            "return {get=get}");

        var reload = host.ReloadModule("value");
        var resumed = host.RunUtf8("return coroutine.resume(co)");
        Assert.Same(reload.CurrentRecord!.Module, alias.FunctionVersion.Module);
        var current = host.RunUtf8("return alias(),require('value').get()");

        Assert.True(reload.Succeeded, reload.Message);
        Assert.Equal(LuaFunctionMigrationStatus.Updated, Assert.Single(
            reload.FunctionMigrations).Status);
        Assert.True(resumed.Execution!.Values[0].AsBoolean());
        Assert.Equal(1, resumed.Execution.Values[1].AsInteger());
        Assert.Equal(11, current.Execution!.Values[0].AsInteger());
        Assert.Equal(12, current.Execution.Values[1].AsInteger());
    }

    [Fact]
    public void ReloadModuleUsesCandidateLoadedValueWhenLoaderReturnsNil()
    {
        var files = new MutableMemoryFileSystem(new Dictionary<string, string>
        {
            ["mods/value.lua"] = "return {value=1}",
        });
        using var host = CreateReloadHost(files);
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; original=require('value')").Succeeded);
        files.Set(
            "mods/value.lua",
            "local name=...; assert(package.loaded[name]==original); " +
            "package.loaded[name]={value=3}; return nil");

        var reload = host.ReloadModule("value");
        var current = host.RunUtf8(
            "local value=require('value'); return value.value,value==original");

        Assert.True(reload.Succeeded, reload.Message);
        Assert.Equal(3, reload.CurrentRecord!.CachedValue.AsTable().Get(
            LuaValue.FromString(host.State.Strings.GetOrCreate("value"u8))).AsInteger());
        Assert.Equal(3, current.Execution!.Values[0].AsInteger());
        Assert.False(current.Execution.Values[1].AsBoolean());
    }

    [Fact]
    public void ReloadModuleSupportsCustomCachePolicyAndSourcePathOverride()
    {
        var files = new MutableMemoryFileSystem(new Dictionary<string, string>
        {
            ["mods/value.lua"] = "return {value=1}",
            ["updates/value.lua"] = "return {value=2}",
        });
        using var host = CreateReloadHost(files);
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; original=require('value')").Succeeded);
        var callbackCalled = false;

        var reload = host.ReloadModule("value", new LuaModuleReloadOptions
        {
            SourcePath = "updates/value.lua",
            CachePolicy = LuaModuleReloadCachePolicy.Custom,
            CustomCachePolicy = context =>
            {
                callbackCalled = true;
                var previous = context.PreviousRecord.CachedValue.AsTable();
                var key = LuaValue.FromString(host.State.Strings.GetOrCreate("value"u8));
                previous.Set(key, context.CandidateValue.AsTable().Get(key));
                return context.PreviousRecord.CachedValue;
            },
        });
        var current = host.RunUtf8(
            "local value=require('value'); return value==original,value.value");

        Assert.True(reload.Succeeded, reload.Message);
        Assert.True(callbackCalled);
        Assert.Equal("updates/value.lua", reload.CurrentRecord!.LoaderData.AsString().ToString());
        Assert.True(current.Execution!.Values[0].AsBoolean());
        Assert.Equal(2, current.Execution.Values[1].AsInteger());
    }

    [Fact]
    public void ReloadModuleFailureRetainsOldCacheAndReportsUnrollbackableSideEffects()
    {
        var files = new MutableMemoryFileSystem(new Dictionary<string, string>
        {
            ["mods/value.lua"] = "return {value=1}",
        });
        using var host = CreateReloadHost(files);
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; original=require('value')").Succeeded);
        Assert.True(host.State.TryGetModule("value", out var previous));

        files.Set("mods/value.lua", "local =");
        var compileFailure = host.ReloadModule("value");
        Assert.Equal(LuaModuleReloadStatus.CompilationFailed, compileFailure.Status);
        Assert.False(compileFailure.SideEffectsMayHaveOccurred);

        files.Set(
            "mods/value.lua",
            "reload_side_effect=(reload_side_effect or 0)+1; error('replacement failed')");
        var executionFailure = host.ReloadModule("value");
        var current = host.RunUtf8(
            "local value=require('value'); " +
            "return value==original,value.value,reload_side_effect");

        Assert.Equal(LuaModuleReloadStatus.ExecutionFailed, executionFailure.Status);
        Assert.True(executionFailure.SideEffectsMayHaveOccurred);
        Assert.True(host.State.TryGetModule("value", out var retained));
        Assert.Equal(previous!.Revision, retained!.Revision);
        Assert.True(current.Execution!.Values[0].AsBoolean());
        Assert.Equal(1, current.Execution.Values[1].AsInteger());
        Assert.Equal(1, current.Execution.Values[2].AsInteger());

        files.Set("mods/value.lua", "return 7");
        var policyFailure = host.ReloadModule("value", new LuaModuleReloadOptions
        {
            CachePolicy = LuaModuleReloadCachePolicy.PatchExistingTable,
        });

        Assert.Equal(LuaModuleReloadStatus.CachePolicyFailed, policyFailure.Status);
        Assert.True(policyFailure.SideEffectsMayHaveOccurred);
        Assert.True(host.State.TryGetModule("value", out retained));
        Assert.Equal(previous.Revision, retained!.Revision);
    }

    [Fact]
    public void ReloadModuleDistinguishesPreloadCustomSearcherAndCacheEviction()
    {
        using var host = CreateReloadHost(new MutableMemoryFileSystem(
            new Dictionary<string, string>()));
        var initial = host.RunUtf8("""
            local preload_count=0
            package.preload.pre=function(name,data)
                preload_count=preload_count+1
                return {count=preload_count,data=data}
            end
            local pre=require('pre')
            package.searchers[1]=function(name)
                if name~='custom' then return '\n\tno custom loader' end
                return function(_,data)
                    custom_count=(custom_count or 0)+1
                    return {count=custom_count,data=data}
                end,':custom:'
            end
            local custom=require('custom')
            return pre.count,custom.count
            """);
        Assert.True(initial.Succeeded);
        Assert.True(host.State.TryGetModule("pre", out var pre));
        Assert.True(host.State.TryGetModule("custom", out var custom));
        Assert.Equal(LuaModuleLoaderKind.Preload, pre!.LoaderKind);
        Assert.Equal(LuaModuleLoaderKind.CustomSearcher, custom!.LoaderKind);

        var preReload = host.ReloadModule("pre");
        var customReload = host.ReloadModule("custom");
        var values = host.RunUtf8(
            "return require('pre').count,require('custom').count").Execution!.Values;

        Assert.True(preReload.Succeeded, preReload.Message);
        Assert.True(customReload.Succeeded, customReload.Message);
        Assert.Equal(2, values[0].AsInteger());
        Assert.Equal(2, values[1].AsInteger());
        Assert.Equal(
            LuaModuleReloadStatus.UnsupportedLoader,
            host.ReloadModule("pre", new LuaModuleReloadOptions
            {
                SourcePath = "pre.lua",
            }).Status);

        Assert.True(host.RunUtf8("package.loaded.custom=nil").Succeeded);
        Assert.Equal(LuaModuleReloadStatus.NotLoaded, host.ReloadModule("custom").Status);
        Assert.DoesNotContain("custom", host.State.GetLoadedModuleNames());
    }

    [Fact]
    public void ReloadModuleAcceptsEmptyModuleName()
    {
        using var host = CreateReloadHost(new MutableMemoryFileSystem(
            new Dictionary<string, string>()));
        var initial = host.RunUtf8(
            "local count=0; package.preload['']=function(name,data) " +
            "count=count+1; return {count=count,name=name,data=data} end; return require('')");
        Assert.True(initial.Succeeded);
        Assert.True(host.State.TryGetModule(string.Empty, out var previous));

        var reload = host.ReloadModule(string.Empty);

        Assert.True(reload.Succeeded, reload.Message);
        Assert.True(reload.CurrentRecord!.Revision > previous!.Revision);
        Assert.Contains(string.Empty, host.State.GetLoadedModuleNames());
        var value = host.RunUtf8("return require('').count").Execution!.Values[0];
        Assert.Equal(2, value.AsInteger());
    }

    [Fact]
    public void ReloadModuleRejectsReentrantExecutionAsBusy()
    {
        var files = new MutableMemoryFileSystem(new Dictionary<string, string>
        {
            ["mods/value.lua"] = "return {value=1}",
        });
        using var host = CreateReloadHost(files);
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; require('value')").Succeeded);
        host.State.SetGlobal(
            "reload_now",
            LuaValue.FromFunction(new LuaNativeFunction(
                "reload_now",
                (_, _) =>
                [
                    LuaValue.FromInteger((long)host.ReloadModule("value").Status),
                ])));

        var status = Assert.Single(host.RunUtf8("return reload_now()").Execution!.Values);

        Assert.Equal((long)LuaModuleReloadStatus.StateBusy, status.AsInteger());
    }

    private static LuaHost CreateReloadHost(
        MutableMemoryFileSystem fileSystem,
        LuaHostExecutionBackend backend = LuaHostExecutionBackend.Interpreter) => new(
            LuaHostOptions.Default with
            {
                ExecutionBackend = backend,
                Jit = LuaJitExecutorOptions.Default with
                {
                    FunctionEntryThreshold = 1,
                    BackedgeThreshold = 1,
                    SynchronousCompilation = true,
                    EnableTier2 = false,
                    EnableLoopOsr = false,
                },
                StandardLibrary = LuaHostCapabilityProfiles.Create(
                    LuaHostProfile.Restricted) with
                {
                    FileSystem = fileSystem,
                },
            });

    private sealed class MutableMemoryFileSystem(IReadOnlyDictionary<string, string> files)
        : ILuaFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = files.ToDictionary(
            static pair => pair.Key,
            static pair => Encoding.UTF8.GetBytes(pair.Value),
            StringComparer.Ordinal);

        public byte[] ReadAllBytes(string path) => _files.TryGetValue(path, out var bytes)
            ? bytes.ToArray()
            : throw new FileNotFoundException(path);

        public bool FileExists(string path) => _files.ContainsKey(path);

        public void Set(string path, string source) =>
            _files[path] = Encoding.UTF8.GetBytes(source);
    }
}
