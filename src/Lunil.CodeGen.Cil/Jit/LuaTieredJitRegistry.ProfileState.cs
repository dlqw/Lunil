using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Lunil.CodeGen.Cil.Emission;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;

namespace Lunil.CodeGen.Cil.Jit;

internal sealed partial class LuaTieredJitRegistry
{
    public LuaJitFunctionState GetFunctionState(LuaIrModule module, int functionId)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (functionId < 0 || functionId >= module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        var key = new FunctionKey(
            GetModuleContentId(module),
            functionId,
            LuaCodegenAbiV3.RuntimeAbiVersion,
            CodegenVersion);
        if (!_entries.TryGetValue(key, out var entry))
        {
            return LuaJitFunctionState.Cold;
        }

        lock (entry.Gate)
        {
            return entry.State;
        }
    }

    public LuaJitFunctionEligibility GetFunctionEligibility(
        LuaIrModule module,
        int functionId)
    {
        ArgumentNullException.ThrowIfNull(module);
        if ((uint)functionId >= (uint)module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        var key = new FunctionKey(
            GetModuleContentId(module),
            functionId,
            LuaCodegenAbiV3.RuntimeAbiVersion,
            CodegenVersion);
        var entry = GetOrCreateEntry(key, module.Functions[functionId].ParameterCount);
        return EnsureEligibility(entry, module);
    }

    public LuaJitFunctionProfile GetFunctionProfile(LuaIrModule module, int functionId)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (functionId < 0 || functionId >= module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        var function = module.Functions[functionId];
        var key = new FunctionKey(
            GetModuleContentId(module),
            functionId,
            LuaCodegenAbiV3.RuntimeAbiVersion,
            CodegenVersion);
        return _entries.TryGetValue(key, out var entry)
            ? entry.Profile.Snapshot()
            : new LuaJitFunctionProfile(
                0,
                [.. Enumerable.Repeat(LuaJitValueKinds.None, function.ParameterCount)],
                []);
    }

    public LuaJitTier2Eligibility GetTier2PromotionEligibility(
        LuaIrModule module,
        int functionId)
    {
        ArgumentNullException.ThrowIfNull(module);
        if ((uint)functionId >= (uint)module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        var key = new FunctionKey(
            GetModuleContentId(module),
            functionId,
            LuaCodegenAbiV3.RuntimeAbiVersion,
            CodegenVersion);
        var entry = GetOrCreateEntry(key, module.Functions[functionId].ParameterCount);
        LuaJitFunctionProfile profile;
        lock (entry.Gate)
        {
            if (entry.Tier2Eligibility is { } cached &&
                cached.ProfileSamples == entry.Profile.Samples)
            {
                return cached;
            }

            profile = entry.Profile.Snapshot();
        }

        return ProfileGuidedLuaTier2Compiler.EvaluateAutoPromotionEligibility(
            module,
            functionId,
            profile,
            CancellationToken.None);
    }

    public void ImportProfile(LuaIrModule module, LuaJitModuleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(profile);
        foreach (var imported in profile.Functions)
        {
            var function = module.Functions[imported.FunctionId];
            var key = new FunctionKey(
                profile.ModuleContentId,
                imported.FunctionId,
                LuaCodegenAbiV3.RuntimeAbiVersion,
                CodegenVersion);
            var entry = GetOrCreateEntry(key, function.ParameterCount);
            entry.Profile.Merge(imported.Profile);
            lock (entry.Gate)
            {
                entry.Tier2Eligibility = null;
                entry.NextTier2EligibilitySample = 0;
                entry.NoNumericTier2EligibilityEvaluations = 0;
                if (IsTier2Enabled && entry.Tier2State == LuaJitTier2State.Ineligible)
                {
                    entry.Tier2State = LuaJitTier2State.Profiling;
                    Volatile.Write(ref entry.Tier2ProfilingActive, 1);
                    if (entry.ActiveTier == LuaJitCompilationTier.Tier1 &&
                        entry.Tier1Method is not null)
                    {
                        entry.Method = entry.Tier1Method;
                    }
                }
            }
            var entrySamples = imported.Profile.Sites
                .FirstOrDefault(static site => site.ProgramCounter == 0)?
                .Samples ?? 0;
            if (entrySamples > 0)
            {
                SetMaximum(ref entry.FunctionEntries, _options.FunctionEntryThreshold);
                SetMaximum(
                    ref entry.CompletedTier1Invocations,
                    Math.Min(entrySamples, _options.Tier2InvocationThreshold));
            }
        }
    }

    public LuaJitWarmupFunctionResult WarmupFunction(
        LuaIrModule module,
        int functionId,
        bool includeTier2,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var function = module.Functions[functionId];
        var key = new FunctionKey(
            GetModuleContentId(module),
            functionId,
            LuaCodegenAbiV3.RuntimeAbiVersion,
            CodegenVersion);
        var entry = GetOrCreateEntry(key, function.ParameterCount);
        var eligibility = EnsureEligibility(entry, module);
        var samples = entry.Profile.Samples;
        if (!eligibility.IsCompilable)
        {
            return new LuaJitWarmupFunctionResult(
                functionId,
                samples,
                LuaJitWarmupFunctionStatus.Ineligible,
                LuaJitCompilationTier.Interpreter,
                eligibility.DiagnosticCode);
        }

        var tier1Task = RequestCompilation(
            entry,
            module,
            compileSynchronously: true,
            cancellationToken);
        var tier1 = tier1Task?.WaitAsync(cancellationToken).GetAwaiter().GetResult() == true;
        if (!tier1)
        {
            lock (entry.Gate)
            {
                return new LuaJitWarmupFunctionResult(
                    functionId,
                    samples,
                    LuaJitWarmupFunctionStatus.Tier1Failed,
                    LuaJitCompilationTier.Interpreter,
                    entry.FailureCode);
            }
        }

        if (includeTier2 && IsTier2Enabled && samples > 0)
        {
            var tier2Task = RequestTier2Compilation(
                entry,
                module,
                compileSynchronously: true,
                cancellationToken);
            if (tier2Task is not null &&
                !tier2Task.WaitAsync(cancellationToken).GetAwaiter().GetResult())
            {
                lock (entry.Gate)
                {
                    if (entry.Tier2State == LuaJitTier2State.Failed)
                    {
                        return new LuaJitWarmupFunctionResult(
                            functionId,
                            samples,
                            LuaJitWarmupFunctionStatus.Tier2Failed,
                            LuaJitCompilationTier.Tier1,
                            entry.Tier2Eligibility?.DiagnosticCode ?? "JIT2001");
                    }
                }
            }
        }

        lock (entry.Gate)
        {
            var tier = entry.ActiveTier;
            return new LuaJitWarmupFunctionResult(
                functionId,
                samples,
                tier == LuaJitCompilationTier.Tier2
                    ? LuaJitWarmupFunctionStatus.ReadyTier2
                    : LuaJitWarmupFunctionStatus.ReadyTier1,
                tier,
                null);
        }
    }

    public LuaJitCompilationTier GetFunctionTier(LuaIrModule module, int functionId)
    {
        var entry = FindEntry(module, functionId);
        if (entry is null)
        {
            return LuaJitCompilationTier.Interpreter;
        }

        lock (entry.Gate)
        {
            return entry.ActiveTier;
        }
    }

    public LuaJitTier2State GetTier2State(LuaIrModule module, int functionId)
    {
        var entry = FindEntry(module, functionId);
        if (entry is null)
        {
            return IsTier2Enabled
                ? LuaJitTier2State.Profiling
                : LuaJitTier2State.Disabled;
        }

        lock (entry.Gate)
        {
            return entry.Tier2State;
        }
    }

    public LuaJitTier2Plan? GetTier2Plan(LuaIrModule module, int functionId)
    {
        var entry = FindEntry(module, functionId);
        if (entry is null)
        {
            return null;
        }

        lock (entry.Gate)
        {
            return entry.Tier2Plan;
        }
    }

    public IReadOnlyList<LuaJitLoopOsrPlan> GetLoopOsrPlans(
        LuaIrModule module,
        int functionId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(module);
        if ((uint)functionId >= (uint)module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        var function = module.Functions[functionId];
        var entry = FindEntry(module, functionId);
        if (entry is not null)
        {
            lock (entry.Gate)
            {
                if (entry.LoopOsrAnalyzed && entry.LoopOsrEntries.Count != 0)
                {
                    return entry.LoopOsrEntries.Values
                        .Select(static loop => loop.Plan)
                        .OrderBy(static plan => plan.HeaderProgramCounter)
                        .ThenBy(static plan => plan.BackedgeProgramCounter)
                        .ToArray();
                }
            }
        }

        return LuaLoopOsrAnalyzer.Analyze(module, functionId)
            .Select(plan => plan with
            {
                CodeKind = LuaLoopOsrEligibilityEvaluator.Evaluate(
                    function,
                    plan).ExpectedCodeKind,
            })
            .ToArray();
    }

    public LuaJitOsrState GetLoopOsrState(
        LuaIrModule module,
        int functionId,
        int headerProgramCounter,
        int backedgeProgramCounter)
    {
        ArgumentNullException.ThrowIfNull(module);
        if ((uint)functionId >= (uint)module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        if (!IsLoopOsrEnabled)
        {
            return LuaJitOsrState.Disabled;
        }

        var key = new FunctionKey(
            GetModuleContentId(module),
            functionId,
            LuaCodegenAbiV3.RuntimeAbiVersion,
            CodegenVersion);
        var entry = GetOrCreateEntry(key, module.Functions[functionId].ParameterCount);
        EnsureLoopOsrEntries(entry, module);
        lock (entry.Gate)
        {
            return entry.LoopOsrEntries.TryGetValue(
                new LoopKey(headerProgramCounter, backedgeProgramCounter),
                out var loop)
                ? loop.State
                : IsLoopOsrEnabled
                    ? LuaJitOsrState.Profiling
                    : LuaJitOsrState.Disabled;
        }
    }

    public LuaJitLoopOsrEligibility GetLoopOsrEligibility(
        LuaIrModule module,
        int functionId,
        int headerProgramCounter,
        int backedgeProgramCounter)
    {
        ArgumentNullException.ThrowIfNull(module);
        if ((uint)functionId >= (uint)module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        if (!IsLoopOsrEnabled)
        {
            var plan = LuaLoopOsrAnalyzer.Analyze(module, functionId).FirstOrDefault(candidate =>
                candidate.HeaderProgramCounter == headerProgramCounter &&
                candidate.BackedgeProgramCounter == backedgeProgramCounter) ??
                throw new ArgumentException(
                    "The requested edge is not a verified natural-loop backedge.",
                    nameof(backedgeProgramCounter));
            return LuaLoopOsrEligibilityEvaluator.Evaluate(
                module.Functions[functionId],
                plan);
        }

        var key = new FunctionKey(
            GetModuleContentId(module),
            functionId,
            LuaCodegenAbiV3.RuntimeAbiVersion,
            CodegenVersion);
        var entry = GetOrCreateEntry(key, module.Functions[functionId].ParameterCount);
        EnsureLoopOsrEntries(entry, module);
        lock (entry.Gate)
        {
            if (entry.LoopOsrEntries.TryGetValue(
                    new LoopKey(headerProgramCounter, backedgeProgramCounter),
                    out var loop))
            {
                return loop.Eligibility;
            }
        }

        throw new ArgumentException(
            "The requested edge is not a verified natural-loop backedge.",
            nameof(backedgeProgramCounter));
    }
}
