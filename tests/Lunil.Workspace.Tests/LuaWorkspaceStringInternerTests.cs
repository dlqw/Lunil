using System.Runtime.CompilerServices;
using Lunil.Workspace;

namespace Lunil.Workspace.Tests;

/// <summary>
/// Coverage for the workspace string interner: canonical-instance reuse for string
/// and span lookups, weak reclamation of pooled entries, convergence of concurrent
/// producers, and snapshot rebuilds sharing interned instances across rebuilds.
/// </summary>
public sealed class LuaWorkspaceStringInternerTests
{
    [Fact]
    public void InternReturnsSharedInstanceForEqualContent()
    {
        var interner = new LuaWorkspaceStringInterner();
        var first = new string('a', 32);
        var second = new string('a', 32);
        Assert.NotSame(first, second);

        var internedFirst = interner.Intern(first);
        var internedSecond = interner.Intern(second);
        Assert.Same(first, internedFirst);
        Assert.Same(first, internedSecond);
        Assert.Equal(2, interner.LookupCount);
        Assert.Equal(1, interner.HitCount);
        Assert.Equal(1, interner.LiveEntryCount);
    }

    [Fact]
    public void InternKeepsDistinctContentDistinct()
    {
        var interner = new LuaWorkspaceStringInterner();
        var first = interner.Intern("alpha");
        var second = interner.Intern("beta");
        Assert.NotSame(first, second);
        Assert.Equal(2, interner.LiveEntryCount);
    }

    [Fact]
    public void InternEmptyReturnsSharedEmpty()
    {
        var interner = new LuaWorkspaceStringInterner();
        Assert.Same(string.Empty, interner.Intern(string.Empty));
        Assert.Same(string.Empty, interner.Intern(ReadOnlySpan<char>.Empty));
        Assert.Equal(0, interner.LiveEntryCount);
    }

    [Fact]
    public void SpanOverloadMatchesStringOverload()
    {
        var interner = new LuaWorkspaceStringInterner();
        var pooled = interner.Intern(new string('z', 16));
        var fromSpan = interner.Intern(new string('z', 16).ToCharArray().AsSpan());
        Assert.Same(pooled, fromSpan);
    }

    [Fact]
    public void ReclaimedEntriesLeaveThePool()
    {
        var interner = new LuaWorkspaceStringInterner();
        var track = new WeakReference<string>(string.Empty);
        PoolAndDrop(interner, track);
        // The probe runs in its own frame so the out temp cannot pin the target in
        // this method's conservative tier-0 liveness window.
        Assert.True(IsAlive(track));
        Assert.Equal(1, interner.LiveEntryCount);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(IsAlive(track));
        Assert.Equal(0, interner.LiveEntryCount);
    }

    private static bool IsAlive(WeakReference<string> track) => track.TryGetTarget(out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PoolAndDrop(LuaWorkspaceStringInterner interner, WeakReference<string> track)
    {
        var canonical = interner.Intern(new string('q', 64));
        track.SetTarget(canonical);
    }

    [Fact]
    public async Task ParallelInternsConvergeOnOneInstance()
    {
        var interner = new LuaWorkspaceStringInterner();
        var results = new string?[Environment.ProcessorCount * 4];
        await Task.WhenAll(Enumerable.Range(0, results.Length).Select(index => Task.Run(() =>
        {
            var fresh = new string('p', 24);
            results[index] = interner.Intern(fresh);
        })));

        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.Equal(1, interner.LiveEntryCount);
    }

    [Fact]
    public async Task RebuiltSnapshotsReuseInternedInstances()
    {
        using var workspace = new LuaWorkspace();
        var documents = Corpus();
        var first = await workspace.AnalyzeCompactAsync(documents);
        var firstName = MemberNameIn(first, "app");

        // Editing "app" forces its re-analysis: the fresh projection re-emits "value"
        // as a new string instance, but the interner maps it onto the canonical
        // instance the first snapshot still owns.
        var edited = Corpus();
        edited[1] = LuaWorkspaceDocument.FromUtf8(
            "app",
            "local a = require('a')\nlocal function go() return a.value + 0 end\nreturn { go = go }");
        var second = await workspace.AnalyzeCompactAsync(edited);
        var secondName = MemberNameIn(second, "app");

        Assert.Same(firstName, secondName);
        Assert.True(workspace.StringInterner.HitCount > 0);
    }

    private static string MemberNameIn(LuaWorkspaceCompactSnapshot snapshot, string moduleName)
    {
        var reference = Assert.Single(
            snapshot.FindMemberReferences("value"),
            candidate => candidate.Module.Name == moduleName);
        return reference.Name;
    }

    private static LuaWorkspaceDocument[] Corpus() =>
    [
        LuaWorkspaceDocument.FromUtf8("a", "return { value = 1 }"),
        LuaWorkspaceDocument.FromUtf8(
            "app",
            "local a = require('a')\nlocal function go() return a.value end\nreturn { go = go }"),
    ];
}
