using Lunil.IR.Canonical;
using Lunil.IR.Lua54;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.CodeGen.Cil.Jit;

public sealed class LuaJitExecutor : IDisposable
{
    private readonly LuaTieredJitRegistry _registry;
    private readonly LuaExecutionEngine _engine;
    private int _disposed;

    public LuaJitExecutor(LuaJitExecutorOptions? options = null)
        : this(
            options ?? LuaJitExecutorOptions.Default,
            RuntimeDynamicCodeCapabilities.Instance,
            ReflectionEmitLuaTier1Compiler.Instance,
            ProfileGuidedLuaTier2Compiler.Instance,
            CanonicalLuaLoopOsrCompiler.Instance)
    {
    }

    internal LuaJitExecutor(
        LuaJitExecutorOptions options,
        ILuaDynamicCodeCapabilities capabilities,
        ILuaTier1Compiler compiler,
        ILuaTier2Compiler? tier2Compiler = null,
        ILuaLoopOsrCompiler? loopOsrCompiler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(compiler);
        ValidateOptions(options);
        Options = options;
        IsDynamicCodeAvailable = capabilities.IsDynamicCodeSupported &&
            capabilities.IsDynamicCodeCompiled;
        if (IsDynamicCodeAvailable && compiler is ReflectionEmitLuaTier1Compiler)
        {
            ReflectionEmitLuaTier1Compiler.PrepareCompiler();
        }

        var selectedTier2Compiler = tier2Compiler ?? ProfileGuidedLuaTier2Compiler.Instance;
        if (IsDynamicCodeAvailable &&
            options.EnableTier2 &&
            selectedTier2Compiler is ProfileGuidedLuaTier2Compiler)
        {
            ProfileGuidedLuaTier2Compiler.PrepareCompiler();
        }

        var selectedLoopOsrCompiler = loopOsrCompiler ?? CanonicalLuaLoopOsrCompiler.Instance;
        if (IsDynamicCodeAvailable &&
            options.EnableLoopOsr &&
            selectedLoopOsrCompiler is CanonicalLuaLoopOsrCompiler)
        {
            CanonicalLuaLoopOsrCompiler.PrepareCompiler();
        }

        _registry = new LuaTieredJitRegistry(
            options,
            capabilities,
            compiler,
            selectedTier2Compiler,
            selectedLoopOsrCompiler);
        _engine = new LuaExecutionEngine(options.Interpreter, _registry);
    }

    public LuaJitExecutorOptions Options { get; }

    public bool IsDynamicCodeAvailable { get; }

    public static LuaJitFunctionEligibility EvaluateFunctionEligibility(
        LuaIrModule module,
        int functionId,
        bool includeInstructionObservation = false)
    {
        ArgumentNullException.ThrowIfNull(module);
        if ((uint)functionId >= (uint)module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        return LuaTier1EligibilityEvaluator.Evaluate(
            module,
            functionId,
            includeInstructionObservation);
    }

    public static LuaJitTier2Eligibility EvaluateTier2PromotionEligibility(
        LuaIrModule module,
        int functionId,
        LuaJitFunctionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(profile);
        return ProfileGuidedLuaTier2Compiler.EvaluateAutoPromotionEligibility(
            module,
            functionId,
            profile,
            CancellationToken.None);
    }

    public static LuaJitLoopOsrEligibility EvaluateLoopOsrEligibility(
        LuaIrModule module,
        LuaJitLoopOsrPlan plan)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(plan);
        if ((uint)plan.FunctionId >= (uint)module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(plan));
        }

        return LuaLoopOsrEligibilityEvaluator.Evaluate(
            module.Functions[plan.FunctionId],
            plan);
    }

    public LuaJitStatistics Statistics => _registry.GetStatistics();

    public event EventHandler<LuaJitEvent>? EventOccurred
    {
        add => _registry.EventOccurred += value;
        remove => _registry.EventOccurred -= value;
    }

    public LuaExecutionResult Execute(
        LuaState state,
        LuaClosure closure,
        ReadOnlySpan<LuaValue> arguments = default)
    {
        ThrowIfDisposed();
        return _engine.Execute(state, closure, arguments);
    }

    public LuaExecutionResult ExecuteBinaryChunk(
        LuaState state,
        ReadOnlySpan<byte> binaryChunk,
        ReadOnlySpan<LuaValue> arguments = default,
        Lua54ChunkReaderOptions? readerOptions = null)
    {
        ThrowIfDisposed();
        return _engine.ExecuteBinaryChunk(state, binaryChunk, arguments, readerOptions);
    }

    public LuaExecutionResult Start(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default)
    {
        ThrowIfDisposed();
        return _engine.Start(state, thread, arguments);
    }

    public LuaExecutionResult Resume(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default)
    {
        ThrowIfDisposed();
        return _engine.Resume(state, thread, arguments);
    }

    public LuaExecutionResult Close(LuaState state, LuaThread thread)
    {
        ThrowIfDisposed();
        return _engine.Close(state, thread);
    }

    public LuaJitFunctionState GetFunctionState(LuaIrModule module, int functionId)
    {
        ThrowIfDisposed();
        return _registry.GetFunctionState(module, functionId);
    }

    public LuaJitFunctionEligibility GetFunctionEligibility(
        LuaIrModule module,
        int functionId)
    {
        ThrowIfDisposed();
        return _registry.GetFunctionEligibility(module, functionId);
    }

    public LuaJitFunctionProfile GetFunctionProfile(LuaIrModule module, int functionId)
    {
        ThrowIfDisposed();
        return _registry.GetFunctionProfile(module, functionId);
    }

    public LuaJitTier2Eligibility GetTier2PromotionEligibility(
        LuaIrModule module,
        int functionId)
    {
        ThrowIfDisposed();
        return _registry.GetTier2PromotionEligibility(module, functionId);
    }

    public byte[] ExportProfile(LuaIrModule module)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(module);
        var profiles = module.Functions
            .Select(function => _registry.GetFunctionProfile(module, function.Id))
            .ToArray();
        return LuaJitProfileCodec.Serialize(module, profiles);
    }

    public LuaJitProfileImportResult ImportProfile(
        LuaIrModule module,
        ReadOnlySpan<byte> payload)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(module);
        if (!Options.EnableTier2 || !IsDynamicCodeAvailable)
        {
            return new LuaJitProfileImportResult(LuaJitProfileImportStatus.Disabled);
        }

        try
        {
            var profile = LuaJitProfileCodec.Deserialize(module, payload);
            _registry.ImportProfile(module, profile);
            return new LuaJitProfileImportResult(LuaJitProfileImportStatus.Imported);
        }
        catch (LuaJitProfileCodec.ProfileFormatException exception)
        {
            return new LuaJitProfileImportResult(
                exception.DiagnosticCode == LuaJitProfileDiagnosticCodes.Incompatible
                    ? LuaJitProfileImportStatus.Incompatible
                    : LuaJitProfileImportStatus.Rejected,
                exception.DiagnosticCode,
                exception.Message);
        }
    }

    public LuaJitCompilationTier GetFunctionTier(LuaIrModule module, int functionId)
    {
        ThrowIfDisposed();
        return _registry.GetFunctionTier(module, functionId);
    }

    public LuaJitTier2State GetTier2State(LuaIrModule module, int functionId)
    {
        ThrowIfDisposed();
        return _registry.GetTier2State(module, functionId);
    }

    public LuaJitTier2Plan? GetTier2Plan(LuaIrModule module, int functionId)
    {
        ThrowIfDisposed();
        return _registry.GetTier2Plan(module, functionId);
    }

    public IReadOnlyList<LuaJitLoopOsrPlan> GetLoopOsrPlans(
        LuaIrModule module,
        int functionId)
    {
        ThrowIfDisposed();
        return _registry.GetLoopOsrPlans(module, functionId);
    }

    public LuaJitOsrState GetLoopOsrState(
        LuaIrModule module,
        int functionId,
        int headerProgramCounter,
        int backedgeProgramCounter)
    {
        ThrowIfDisposed();
        return _registry.GetLoopOsrState(
            module,
            functionId,
            headerProgramCounter,
            backedgeProgramCounter);
    }

    public LuaJitLoopOsrEligibility GetLoopOsrEligibility(
        LuaIrModule module,
        int functionId,
        int headerProgramCounter,
        int backedgeProgramCounter)
    {
        ThrowIfDisposed();
        return _registry.GetLoopOsrEligibility(
            module,
            functionId,
            headerProgramCounter,
            backedgeProgramCounter);
    }

    public void Invalidate(LuaIrModule module)
    {
        ThrowIfDisposed();
        _registry.Invalidate(module);
    }

    public void ClearCache()
    {
        ThrowIfDisposed();
        _registry.ClearCache();
    }

    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _registry.WaitForIdleAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _registry.Dispose();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static void ValidateOptions(LuaJitExecutorOptions options)
    {
        if (!Enum.IsDefined(options.Policy))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The JIT policy is invalid.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.FunctionEntryThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.BackedgeThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.CompilationQueueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumConcurrentCompilations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumCompilationAttempts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumPolymorphicShapes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Tier2InvocationThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Tier2BackedgeThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumTier2GuardFailures);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.LoopOsrBackedgeThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumLoopOsrGuardFailures);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumCodeCacheBytes);
        ArgumentNullException.ThrowIfNull(options.Interpreter);
        if (options.CompilationRetryBackoff < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "CompilationRetryBackoff cannot be negative.");
        }
    }
}
