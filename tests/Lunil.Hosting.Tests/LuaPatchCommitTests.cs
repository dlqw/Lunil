using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Lunil.CodeGen.Cil.Jit;
using Lunil.Core;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.StandardLibrary;

namespace Lunil.Hosting.Tests;

public sealed class LuaPatchCommitTests
{
    [Fact]
    public async Task BackgroundPrepareCapturesRevisionsWithoutMutatingLiveState()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
            ["mods/b.lua"] = "return {value=2}",
        });
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; require('a'); require('b')").Succeeded);
        Assert.True(host.State.TryGetModule("a", out var a));
        Assert.True(host.State.TryGetModule("b", out var b));
        var bundle = CreateBundle(
            Entry("a", "return {value=10}"),
            Entry("b", "return {value=20}"));

        var result = await host.PreparePatchAsync(bundle);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(LuaPatchPrepareStatus.Ready, result.Status);
        Assert.Equal(2, result.PreparedPatch!.Modules.Length);
        Assert.Equal(a!.Revision, result.PreparedPatch.Modules.Single(
            static module => module.ModuleName == "a").ExpectedRevision);
        Assert.Equal(b!.Revision, result.PreparedPatch.Modules.Single(
            static module => module.ModuleName == "b").ExpectedRevision);
        Assert.True(host.State.TryGetModule("a", out var afterA));
        Assert.True(host.State.TryGetModule("b", out var afterB));
        Assert.Equal(a.Revision, afterA!.Revision);
        Assert.Equal(b.Revision, afterB!.Revision);
        var values = host.RunUtf8("return require('a').value,require('b').value")
            .Execution!.Values;
        Assert.Equal(1, values[0].AsInteger());
        Assert.Equal(2, values[1].AsInteger());
    }

    [Fact]
    public void CommitPublishesDependencyFirstCandidatesAtomically()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/shared.lua"] = "return {value=1}",
            ["mods/main.lua"] = "local s=require('shared'); return {value=s.value+1}",
        }, LuaHostExecutionBackend.Jit);
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; require('shared'); require('main')").Succeeded);
        Assert.True(host.State.TryGetModule("shared", out var oldShared));
        Assert.True(host.State.TryGetModule("main", out var oldMain));
        var bundle = CreateBundle(
            Entry("shared", "return {value=20}"),
            Entry(
                "main",
                "local s=require('shared'); return {value=s.value+2}",
                "shared"));
        var prepared = host.PreparePatch(bundle);
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.True(commit.Succeeded, commit.Message);
        Assert.All(commit.Modules, static module =>
            Assert.Equal(LuaPatchModuleCommitStatus.Committed, module.Status));
        Assert.True(host.State.TryGetModule("shared", out var currentShared));
        Assert.True(host.State.TryGetModule("main", out var currentMain));
        Assert.True(currentShared!.Revision > oldShared!.Revision);
        Assert.True(currentMain!.Revision > oldMain!.Revision);
        var values = host.RunUtf8("return require('shared').value,require('main').value")
            .Execution!.Values;
        Assert.Equal(20, values[0].AsInteger());
        Assert.Equal(22, values[1].AsInteger());
    }

    [Fact]
    public void RevisionConflictRejectsPatchBeforeCandidateExecution()
    {
        var files = new MutableMemoryFileSystem(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        using var host = CreateHost(files);
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        var prepared = host.PreparePatch(CreateBundle(Entry(
            "a",
            "patch_candidate_ran=true; return {value=9}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        files.Set("mods/a.lua", "return {value=2}");
        var reload = host.ReloadModule("a");
        Assert.True(reload.Succeeded, reload.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.Equal(LuaPatchCommitStatus.RevisionConflict, commit.Status);
        Assert.False(commit.SideEffectsMayHaveOccurred);
        var values = host.RunUtf8("return require('a').value,patch_candidate_ran")
            .Execution!.Values;
        Assert.Equal(2, values[0].AsInteger());
        Assert.True(values[1].IsNil);
    }

    [Fact]
    public void LaterExecutionFailureRestoresEveryTargetRevisionCacheAndClosureSlot()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "local n=0; local function next() n=n+1; return n end; return {next=next}",
            ["mods/b.lua"] = "return {value=1}",
        });
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; alias=require('a').next; require('b')").Succeeded);
        var alias = host.State.GetGlobal("alias").TryGetClosure()!;
        var oldVersion = alias.FunctionVersion;
        Assert.True(host.State.TryGetModule("a", out var oldA));
        Assert.True(host.State.TryGetModule("b", out var oldB));
        var prepared = host.PreparePatch(CreateBundle(
            Entry(
                "a",
                "local n=0; local function next() n=n+10; return n end; return {next=next}"),
            Entry("b", "error('candidate b failed')", "a")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.Equal(LuaPatchCommitStatus.ExecutionFailed, commit.Status);
        Assert.True(commit.SideEffectsMayHaveOccurred);
        Assert.Same(oldVersion, alias.FunctionVersion);
        Assert.True(host.State.TryGetModule("a", out var currentA));
        Assert.True(host.State.TryGetModule("b", out var currentB));
        Assert.Equal(oldA!.Revision, currentA!.Revision);
        Assert.Equal(oldB!.Revision, currentB!.Revision);
        var values = host.RunUtf8("return alias(),require('a').next(),require('b').value")
            .Execution!.Values;
        Assert.Equal(1, values[0].AsInteger());
        Assert.Equal(2, values[1].AsInteger());
        Assert.Equal(1, values[2].AsInteger());
    }

    [Fact]
    public void SuspendedFrameKeepsOldGenerationWhileNewEntrantsUseCommittedGeneration()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] =
                "local n=1; local function get() coroutine.yield('pause'); return n end; " +
                "return {get=get}",
        }, LuaHostExecutionBackend.Jit);
        var suspended = host.RunUtf8(
            "package.path='mods/?.lua'; alias=require('a').get; " +
            "co=coroutine.create(alias); return coroutine.resume(co)");
        Assert.True(suspended.Execution!.Values[0].AsBoolean());
        var alias = host.State.GetGlobal("alias").TryGetClosure()!;
        var oldVersion = alias.FunctionVersion;
        var prepared = host.PreparePatch(CreateBundle(Entry(
            "a",
            "local n=2; local function get() local _=coroutine; return n+10 end; " +
            "return {get=get}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.True(commit.Succeeded, commit.Message);
        Assert.NotSame(oldVersion, alias.FunctionVersion);
        Assert.Equal(oldVersion.Generation + 1, alias.FunctionVersion.Generation);
        var resumed = host.RunUtf8("return coroutine.resume(co)").Execution!.Values;
        Assert.True(resumed[0].AsBoolean());
        Assert.Equal(1, resumed[1].AsInteger());
        var current = host.RunUtf8("return alias(),require('a').get()")
            .Execution!.Values;
        Assert.Equal(11, current[0].AsInteger());
        Assert.Equal(12, current[1].AsInteger());
    }

    [Fact]
    public void ZeroPauseBudgetDefersWithoutExecutingOrPublishing()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        Assert.True(host.State.TryGetModule("a", out var old));
        var prepared = host.PreparePatch(CreateBundle(Entry(
            "a",
            "budget_candidate_ran=true; return {value=2}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(
                prepared.PreparedPatch!,
                opened.Window!,
                new LuaPatchCommitOptions { MaximumPauseDuration = TimeSpan.Zero });
        }

        Assert.Equal(LuaPatchCommitStatus.Deferred, commit.Status);
        Assert.False(commit.SideEffectsMayHaveOccurred);
        Assert.True(host.State.TryGetModule("a", out var current));
        Assert.Equal(old!.Revision, current!.Revision);
        var values = host.RunUtf8("return require('a').value,budget_candidate_ran")
            .Execution!.Values;
        Assert.Equal(1, values[0].AsInteger());
        Assert.True(values[1].IsNil);
    }

    [Fact]
    public void ReentrantUpdateWindowRequestIsDeferredUntilTheNextTick()
    {
        using var host = CreateHost(new Dictionary<string, string>());
        host.State.SetGlobal(
            "open_window",
            LuaValue.FromFunction(new LuaNativeFunction(
                "open_window",
                (_, _) =>
                {
                    var opened = host.TryOpenPatchUpdateWindow();
                    opened.Window?.Dispose();
                    return [LuaValue.FromInteger((long)opened.Status)];
                })));

        var status = Assert.Single(host.RunUtf8("return open_window()")
            .Execution!.Values);

        Assert.Equal((long)LuaPatchUpdateWindowStatus.Deferred, status.AsInteger());
    }

    [Fact]
    public async Task ConcurrentExecutionMakesZeroWaitUpdateWindowDefer()
    {
        using var host = CreateHost(new Dictionary<string, string>());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        host.State.SetGlobal(
            "hold_frame",
            LuaValue.FromFunction(new LuaNativeFunction(
                "hold_frame",
                (_, _) =>
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(5));
                    return [];
                })));
        var executing = Task.Run(() => host.RunUtf8("hold_frame()"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        var opened = host.TryOpenPatchUpdateWindow();
        release.Set();
        var execution = await executing;

        Assert.Equal(LuaPatchUpdateWindowStatus.Deferred, opened.Status);
        Assert.Null(opened.Window);
        Assert.True(execution.Succeeded);
    }

    [Fact]
    public void CancelledCommitDoesNotRunCandidateCode()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        Assert.True(host.RunUtf8("package.path='mods/?.lua'; require('a')").Succeeded);
        var prepared = host.PreparePatch(CreateBundle(Entry(
            "a",
            "cancel_candidate_ran=true; return {value=2}")));
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(
                prepared.PreparedPatch!,
                opened.Window!,
                cancellationToken: cancellation.Token);
        }

        Assert.Equal(LuaPatchCommitStatus.Cancelled, commit.Status);
        Assert.False(commit.SideEffectsMayHaveOccurred);
        var values = host.RunUtf8("return require('a').value,cancel_candidate_ran")
            .Execution!.Values;
        Assert.Equal(1, values[0].AsInteger());
        Assert.True(values[1].IsNil);
    }

    [Fact]
    public void AtomicTablePolicyPreservesIdentityAndRejectsOpaqueCallbacksAtPrepareTime()
    {
        using var host = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {keep=1,remove=2}",
        });
        Assert.True(host.RunUtf8(
            "package.path='mods/?.lua'; original=require('a')").Succeeded);
        var bundle = CreateBundle(Entry("a", "return {keep=3,added=4}"));
        var prepared = host.PreparePatch(bundle, new LuaPatchPrepareOptions
        {
            ModuleOptions = new Dictionary<string, LuaModuleReloadOptions>
            {
                ["a"] = new()
                {
                    CachePolicy = LuaModuleReloadCachePolicy.PatchExistingTable,
                },
            },
        });
        Assert.True(prepared.Succeeded, prepared.Message);
        var opened = host.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);

        LuaPatchCommitResult commit;
        using (opened.Window!)
        {
            commit = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
        }

        Assert.True(commit.Succeeded, commit.Message);
        var module = Assert.Single(commit.Modules);
        Assert.Equal(2, module.PatchedExportCount);
        Assert.Equal(1, module.RemovedExportCount);
        var values = host.RunUtf8(
            "local a=require('a'); return a==original,a.keep,a.remove,a.added")
            .Execution!.Values;
        Assert.True(values[0].AsBoolean());
        Assert.Equal(3, values[1].AsInteger());
        Assert.True(values[2].IsNil);
        Assert.Equal(4, values[3].AsInteger());

        var unsupported = host.PreparePatch(bundle, new LuaPatchPrepareOptions
        {
            ModuleOptions = new Dictionary<string, LuaModuleReloadOptions>
            {
                ["a"] = new()
                {
                    CachePolicy = LuaModuleReloadCachePolicy.Custom,
                    CustomCachePolicy = static context => context.CandidateValue,
                },
            },
        });
        Assert.Equal(LuaPatchPrepareStatus.UnsupportedCachePolicy, unsupported.Status);
        Assert.Null(unsupported.PreparedPatch);
    }

    private static LuaPatchEntry Entry(
        string moduleName,
        string source,
        params string[] dependencies) => new(
        $"modules/{moduleName}.lua",
        moduleName,
        LuaPatchEntryKind.Source,
        Encoding.UTF8.GetBytes(source),
        dependencies.Select(static dependency => new LuaPatchDependency(
            dependency,
            LuaPatchDependencyKind.Required)).ToImmutableArray());

    private static LuaPatchBundle CreateBundle(params LuaPatchEntry[] entries)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return LuaPatchBundle.Create(
            new LuaPatchManifest
            {
                PatchId = "alpha2-test",
                Channel = "test",
                TargetBuild = "test-2",
                BaseRevision = "test-1",
                TargetRevision = "test-2",
                LanguageVersion = LuaLanguageVersion.Lua54,
                RuntimeAbi = "lunil-0.12",
                CreatedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero),
                ExpiresAt = new DateTimeOffset(2026, 8, 22, 0, 0, 0, TimeSpan.Zero),
                Nonce = Guid.NewGuid().ToString("N"),
            },
            entries,
            new LuaPatchEcdsaSigner("test", key));
    }

    private static LuaHost CreateHost(
        IReadOnlyDictionary<string, string> files,
        LuaHostExecutionBackend backend = LuaHostExecutionBackend.Interpreter) =>
        CreateHost(new MutableMemoryFileSystem(files), backend);

    private static LuaHost CreateHost(
        MutableMemoryFileSystem files,
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
            StandardLibrary = LuaHostCapabilityProfiles.Create(LuaHostProfile.Restricted) with
            {
                FileSystem = files,
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
