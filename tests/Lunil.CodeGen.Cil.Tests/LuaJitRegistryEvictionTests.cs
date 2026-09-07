using System.Collections.Immutable;
using Lunil.CodeGen.Cil.Jit;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.CodeGen.Cil.Tests;

public sealed class LuaJitRegistryEvictionTests
{
    [Fact]
    public void InvalidationKeepsTheObservableTombstoneWithoutGrowingBoundlessly()
    {
        var executor = CreateExecutor();
        var module = Compile("local t = {10, 20}; return t[1] + t[2]");

        for (var index = 0; index < 4; index++)
        {
            ExecuteFresh(executor, module);
        }

        var entriesBeforeInvalidation = executor.TrackedEntryCount;
        Assert.True(entriesBeforeInvalidation > 0, "entries after run");

        executor.Invalidate(module);

        // The tombstone keeps the observable Invalidated state for function-state queries
        // and route guards, so it is retained rather than dropped.
        Assert.Equal(entriesBeforeInvalidation, executor.TrackedEntryCount);
        Assert.Equal(
            LuaJitFunctionState.Invalidated,
            executor.GetFunctionState(module, functionId: 0));

        // The invalidated module instance keeps executing correctly on its terminal
        // interpreter route; a patched module arrives as a new module instance.
        var result = ExecuteFresh(executor, module);
        Assert.True(result.Values.SequenceEqual([LuaValue.FromInteger(30)]));
    }

    [Fact]
    public void TombstoneSweepBoundsTheEntryTableDuringReloadStorms()
    {
        const int maximumTrackedEntries = 8;
        var executor = new LuaJitExecutor(
            LuaJitExecutorOptions.Default with
            {
                Policy = LuaJitPolicy.PreferJit,
                FunctionEntryThreshold = 1,
                BackedgeThreshold = 1,
                SynchronousCompilation = true,
                EnableTier2 = false,
            },
            new TestDynamicCodeCapabilities(true, true),
            ReflectionEmitLuaTier1Compiler.Instance,
            ProfileGuidedLuaTier2Compiler.Instance,
            CanonicalLuaLoopOsrCompiler.Instance,
            maximumTrackedEntries);
        var liveModule = Compile("return 1");
        ExecuteFresh(executor, liveModule);

        for (var index = 0; index < 16; index++)
        {
            var module = Compile($"return {index + 2}");
            ExecuteFresh(executor, module);
            executor.Invalidate(module);
        }

        // The sweep reclaims the oldest invalidated tombstones; the entry table stays at
        // or below the configured bound while live entries are never swept.
        Assert.True(
            executor.TrackedEntryCount <= maximumTrackedEntries,
            $"entries={executor.TrackedEntryCount}");
        Assert.True(executor.TrackedModuleGenerationCount <= 16 + 1);
    }

    private static LuaJitExecutor CreateExecutor() => new(
        LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            FunctionEntryThreshold = 1,
            BackedgeThreshold = 1,
            SynchronousCompilation = true,
            EnableTier2 = false,
        },
        new TestDynamicCodeCapabilities(true, true),
        ReflectionEmitLuaTier1Compiler.Instance,
        ProfileGuidedLuaTier2Compiler.Instance,
        CanonicalLuaLoopOsrCompiler.Instance);

    private static LuaExecutionResult ExecuteFresh(
        LuaJitExecutor executor,
        LuaIrModule module)
    {
        var state = new LuaState();
        return executor.Execute(state, state.CreateMainClosure(module));
    }

    private static LuaIrModule Compile(string source)
    {
        var parsing = LuaParser.Parse(SourceText.FromUtf8(source));
        var binding = LuaBinder.Bind(parsing);
        var lowering = LuaLowerer.Lower(binding);
        Assert.True(lowering.Succeeded, string.Join(
            "; ",
            lowering.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return Assert.IsType<LuaIrModule>(lowering.Module);
    }

    private sealed record TestDynamicCodeCapabilities(
        bool IsDynamicCodeSupported,
        bool IsDynamicCodeCompiled) : ILuaDynamicCodeCapabilities;
}
