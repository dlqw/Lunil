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

internal sealed partial class LuaTieredJitRegistry :
    ILuaInstructionExecutor,
    ILuaDirectCallExecutor,
    IDisposable
{
    private const int CodegenVersion = LuaJitProfileCodec.CurrentCodegenVersion;
    private const int MaximumNoNumericTier2EligibilityEvaluations = 2;
    internal const int DefaultMaximumTrackedFunctionEntries = 16_384;
    private readonly LuaJitExecutorOptions _options;
    private readonly ILuaDynamicCodeCapabilities _capabilities;
    private readonly ILuaTier1Compiler _compiler;
    private readonly ILuaTier2Compiler _tier2Compiler;
    private readonly ILuaLoopOsrCompiler _loopOsrCompiler;
    private readonly LuaDirectCallCounterSink _boundDirectCallCounters = new();
    private readonly LuaTablePicCounterSink _tablePicCounters = new();
    private readonly ConcurrentDictionary<FunctionKey, FunctionEntry> _entries = [];
    private readonly ConcurrentDictionary<string, LuaBackendGeneration> _moduleGenerations =
        new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<LuaIrModule, ModuleRouteCache> _moduleRoutes = new();
    private readonly ConditionalWeakTable<LuaFrame, FunctionEntryObservation> _observedFrames =
        new();
    private readonly Channel<CompilationRequest> _queue;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly AsyncLocal<bool> _workerCallContext = new();
    private readonly Task[] _workers;
    private readonly Lock _cacheGate = new();
    private long _accessStamp;
    private long _estimatedCodeBytes;
    private long _functionEntries;
    private long _backedges;
    private long _compilationQueued;
    private long _compilationStarted;
    private long _compilationCompleted;
    private long _compilationFailed;
    private long _queueRejected;
    private long _compiledInvocations;
    private long _interpreterFallbacks;
    private long _deoptimizations;
    private long _cacheEvictions;
    private long _invalidations;
    private long _invalidationStamp;
    private readonly int _maximumTrackedFunctionEntries = DefaultMaximumTrackedFunctionEntries;
    private long _totalQueueLatencyTicks;
    private long _totalCompilationTicks;
    private long _tier2CompilationQueued;
    private long _tier2CompilationStarted;
    private long _tier2CompilationCompleted;
    private long _tier2CompilationFailed;
    private long _tier2Invocations;
    private long _tier2CompletedInvocations;
    private long _tier2UnsupportedExits;
    private long _tier2GuardFailures;
    private long _tier2Invalidations;
    private long _loopOsrRequests;
    private long _loopOsrCompilationQueued;
    private long _loopOsrCompilationStarted;
    private long _loopOsrCompilationCompleted;
    private long _loopOsrCompilationFailed;
    private long _loopOsrEntries;
    private long _loopOsrExits;
    private long _loopOsrGuardFailures;
    private long _loopOsrInvalidations;
    private long _compiledCanonicalInstructions;
    private long _schedulerExits;
    private long _continueExits;
    private long _pollExits;
    private long _callExits;
    private long _tailCallExits;
    private long _returnExits;
    private long _instructionBudgetPolls;
    private long _garbageCollectionPolls;
    private long _debugModeDeoptimizations;
    private long _directCallEntries;
    private long _directCallCompletions;
    private long _directCallFallbacks;
    private long _directCallInvalidations;
    private long _schedulerExitsAvoided;
    private long _tier1CompileAllocatedBytes;
    private long _tier1DirectCanonicalInstructions;
    private long _tier1SlowPathCanonicalInstructions;
    private long _tier1PlanInstructions;
    private long _totalCanonicalVerificationTicks;
    private long _totalControlFlowAnalysisTicks;
    private long _totalMethodPlanBuildTicks;
    private long _totalPlanVerificationTicks;
    private long _totalReflectionEmitTicks;
    private long _totalDelegateCreationTicks;
    private long _eligibilityEvaluated;
    private long _eligibilityAccepted;
    private long _eligibilityRejected;
    private long _tier2EligibilityEvaluated;
    private long _tier2EligibilityAccepted;
    private long _tier2EligibilityRejected;
    private long _loopOsrEligibilityEvaluated;
    private long _loopOsrEligibilityAccepted;
    private long _loopOsrEligibilityRejected;
    private int _loopOsrCompilerPreparationState;
    private int _disposed;

    public LuaTieredJitRegistry(
        LuaJitExecutorOptions options,
        ILuaDynamicCodeCapabilities capabilities,
        ILuaTier1Compiler compiler,
        ILuaTier2Compiler tier2Compiler,
        ILuaLoopOsrCompiler loopOsrCompiler)
        : this(
            options,
            capabilities,
            compiler,
            tier2Compiler,
            loopOsrCompiler,
            DefaultMaximumTrackedFunctionEntries)
    {
    }

    internal LuaTieredJitRegistry(
        LuaJitExecutorOptions options,
        ILuaDynamicCodeCapabilities capabilities,
        ILuaTier1Compiler compiler,
        ILuaTier2Compiler tier2Compiler,
        ILuaLoopOsrCompiler loopOsrCompiler,
        int maximumTrackedFunctionEntries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumTrackedFunctionEntries, 1);

        _options = options;
        _capabilities = capabilities;
        _compiler = compiler;
        _tier2Compiler = tier2Compiler;
        _loopOsrCompiler = loopOsrCompiler;
        _maximumTrackedFunctionEntries = maximumTrackedFunctionEntries;
        _queue = Channel.CreateBounded<CompilationRequest>(new BoundedChannelOptions(
            options.CompilationQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = options.MaximumConcurrentCompilations == 1,
            SingleWriter = false,
        });
        _workers = options.SynchronousCompilation ||
            options.Policy is LuaJitPolicy.InterpreterOnly or LuaJitPolicy.RequireJit
            ? []
            : Enumerable.Range(0, options.MaximumConcurrentCompilations)
                .Select(_ => Task.Run(WorkerAsync))
                .ToArray();
    }

    public event EventHandler<LuaJitEvent>? EventOccurred;

    private bool IsLoopOsrEnabled => _options.EnableLoopOsr &&
        _capabilities.IsDynamicCodeSupported && _capabilities.IsDynamicCodeCompiled;

    private bool IsTier2Enabled => _options.EnableTier2 &&
        _capabilities.IsDynamicCodeSupported && _capabilities.IsDynamicCodeCompiled;

    public LuaFrameInstructionRoute GetInitialFrameInstructionRoute(LuaFrame frame)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return LuaFrameInstructionRoute.Interpreter;
        }

        var route = GetFunctionRoute(frame.Module, frame.Function.Id);
        return Volatile.Read(ref route.TerminalInterpreterRoute) != 0
            ? LuaFrameInstructionRoute.Interpreter
            : LuaFrameInstructionRoute.Backend;
    }

    public void CommitPendingBackedges(LuaFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.UnreportedBackendBackedgeCount == 0 ||
            !_observedFrames.TryGetValue(frame, out var observation) ||
            observation.Entry is not { } entry)
        {
            return;
        }

        CommitPendingBackedges(entry, frame);
    }
}
