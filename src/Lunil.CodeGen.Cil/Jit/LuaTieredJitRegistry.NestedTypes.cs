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
    private readonly record struct FunctionKey(
        string ModuleContentId,
        int FunctionId,
        int RuntimeAbiVersion,
        int CodegenVersion);

    private readonly record struct CompiledEntryPoint(
        LuaCompiledMethod Method,
        LuaJitCompilationTier Tier,
        long Generation);

    private readonly record struct FunctionEntryFactoryState(
        LuaTieredJitRegistry Registry,
        int ParameterCount);

    private readonly record struct LoopKey(
        int HeaderProgramCounter,
        int BackedgeProgramCounter);

    private sealed class FunctionEntry(
        FunctionKey key,
        int parameterCount,
        int maximumPolymorphicShapes,
        bool enableTier2,
        bool enableLoopOsr)
    {
        public FunctionKey Key { get; } = key;

        public Lock Gate { get; } = new();

        public LuaJitFunctionState State { get; set; }

        public long InvalidatedStamp;

        public LuaJitCompilationTier ActiveTier { get; set; }

        public LuaJitTier2State Tier2State { get; set; } = enableTier2
            ? LuaJitTier2State.Profiling
            : LuaJitTier2State.Disabled;

        public LuaJitProfileAccumulator Profile { get; } = new(
            parameterCount,
            maximumPolymorphicShapes);

        public Dictionary<LoopKey, LoopOsrEntry> LoopOsrEntries { get; } = [];

        public Dictionary<int, List<LoopOsrEntry>> LoopOsrGuardSites { get; } = [];

        public bool LoopOsrAnalyzed { get; set; } = !enableLoopOsr;

        public int LoopOsrObservationState = enableLoopOsr ? 0 : -1;

        public int LoopOsrRuntimeQualificationPendingCount;

        public int LoopOsrStructuralRejectionPreflightState = enableLoopOsr ? 0 : -1;

        public LuaCompiledMethod? Method { get; set; }

        public LuaCompiledMethod? Tier1Method { get; set; }

        public LuaCompiledMethod? PlainTier1Method { get; set; }

        public LuaCompiledMethod? Tier2Method { get; set; }

        public LuaDirectCompiledMethod? DirectCallMethod { get; set; }

        public LuaJitTier2Plan? Tier2Plan { get; set; }

        public LuaJitTier2Eligibility? Tier2Eligibility { get; set; }

        public bool Tier2EligibilityEvaluationInProgress { get; set; }

        public long NextTier2EligibilitySample { get; set; }

        public int NoNumericTier2EligibilityEvaluations { get; set; }

        public LuaJitFunctionEligibility? Eligibility { get; set; }

        public TaskCompletionSource<bool>? Completion { get; set; }

        public TaskCompletionSource<bool>? Tier2Completion { get; set; }

        public long EstimatedCodeBytes { get; set; }

        public long Tier1EstimatedCodeBytes { get; set; }

        public long Tier2EstimatedCodeBytes { get; set; }

        public long LastAccessStamp;

        public long FunctionEntries;

        public long InstalledGeneration;

        public long Backedges;

        public long Tier1Invocations;

        public long CompletedTier1Invocations;

        public long Tier2GuardFailures;

        public Dictionary<string, int> Tier2GuardInvalidationsByPlan { get; } =
            new(StringComparer.Ordinal);

        public int Tier2ProfilingActive;

        public int DirectCallEligibility;

        public int CompilationAttempts { get; set; }

        public long RetryAfterTimestamp { get; set; }

        public int Tier2CompilationAttempts { get; set; }

        public long Tier2RetryAfterTimestamp { get; set; }

        public string? FailureCode { get; set; }

        public int LastFallbackTransition { get; set; } = -1;
    }

    private sealed class ModuleRouteCache(string moduleContentId, int functionCount)
    {
        private readonly FunctionRoute?[] _functions = new FunctionRoute[functionCount];

        public FunctionRoute GetFunctionRoute(int functionId)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(functionId);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                functionId,
                _functions.Length);
            if (Volatile.Read(ref _functions[functionId]) is { } cached)
            {
                return cached;
            }

            var route = new FunctionRoute(moduleContentId, functionId);
            return Interlocked.CompareExchange(
                ref _functions[functionId],
                route,
                null) ?? route;
        }
    }

    private sealed class FunctionRoute(string moduleContentId, int functionId)
    {
        public string ModuleContentId { get; } = moduleContentId;

        public int FunctionId { get; } = functionId;

        public FunctionEntry? Entry;

        public int TerminalInterpreterRoute;
    }

    private sealed record CompilationRequest(
        FunctionEntry Entry,
        LuaIrModule Module,
        long EnqueuedTimestamp,
        TaskCompletionSource<bool> Completion,
        LuaJitCompilationTier Tier,
        LuaJitFunctionProfile? Profile,
        LoopOsrEntry? LoopOsr,
        CancellationToken CancellationToken = default);

    private sealed class LoopOsrEntry(
        LoopKey key,
        LuaJitLoopOsrPlan plan,
        LuaJitLoopOsrEligibility eligibility,
        bool enableManagedFallback)
    {
        public LoopKey Key { get; } = key;

        public LuaJitLoopOsrPlan Plan { get; set; } = plan;

        public LuaJitLoopOsrEligibility StructuralEligibility { get; } = eligibility;

        public LuaJitLoopOsrEligibility Eligibility { get; set; } =
            eligibility.IsAutoEligible && !enableManagedFallback
                ? eligibility with
                {
                    IsAutoEligible = false,
                    Reason = LuaJitLoopOsrEligibilityReason.AwaitingExactNumericProfile,
                }
                : eligibility;

        public LuaJitOsrState State { get; set; } =
            eligibility.IsAutoEligible || enableManagedFallback
                ? LuaJitOsrState.Profiling
                : LuaJitOsrState.Ineligible;

        public LuaCompiledMethod? Method { get; set; }

        public TaskCompletionSource<bool>? Completion { get; set; }

        public long EstimatedCodeBytes { get; set; }

        public long Backedges;

        public long GuardFailures;

        public HashSet<int> PendingExactNumericGuardSites { get; } = [];

        public Dictionary<(int ProgramCounter, int Register), LuaJitValueKinds>
            ExactNumericTypes
        { get; } = [];

        public int ExactNumericQualificationState { get; set; } =
            eligibility.IsAutoEligible && !enableManagedFallback ? 0 : 1;

        public bool PreferManagedFallback { get; set; }

        public int CompilationAttempts { get; set; }

        public long RetryAfterTimestamp { get; set; }
    }

    private sealed class FunctionEntryObservation
    {
        private WeakReference<LuaClosure>? _closure;

        public Lock Gate { get; } = new();

        public int HasPendingLoop;

        public LoopKey? PendingLoop { get; set; }

        public FunctionEntry? Entry { get; set; }

        public FunctionRoute? Route { get; set; }

        public bool IsBoundTo(LuaClosure closure) =>
            _closure is not null &&
            _closure.TryGetTarget(out var observedClosure) &&
            ReferenceEquals(observedClosure, closure);

        public void Bind(
            LuaClosure closure,
            FunctionEntry entry,
            FunctionRoute route)
        {
            if (_closure is null)
            {
                _closure = new WeakReference<LuaClosure>(closure);
            }
            else
            {
                _closure.SetTarget(closure);
            }

            Entry = entry;
            Route = route;
        }
    }
}
