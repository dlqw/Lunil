using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Lunil.CodeGen.Cil.Artifacts;
using Lunil.CodeGen.Cil.Emission;
using Lunil.IR.Canonical;

namespace Lunil.CodeGen.Cil.Jit;

internal interface ILuaDynamicCodeCapabilities
{
    bool IsDynamicCodeSupported { get; }

    bool IsDynamicCodeCompiled { get; }
}

internal sealed class RuntimeDynamicCodeCapabilities : ILuaDynamicCodeCapabilities
{
    public static RuntimeDynamicCodeCapabilities Instance { get; } = new();

    public bool IsDynamicCodeSupported => RuntimeFeature.IsDynamicCodeSupported;

    public bool IsDynamicCodeCompiled => RuntimeFeature.IsDynamicCodeCompiled;
}

internal sealed record LuaTier1CompilationResult(
    LuaCompiledMethod? Method,
    long EstimatedCodeBytes,
    ImmutableArray<string> Diagnostics,
    LuaJitCompilationMetrics? Metrics = null)
{
    public bool Succeeded => Method is not null && Diagnostics.IsEmpty;
}

internal interface ILuaTier1Compiler
{
    [RequiresDynamicCode("Tier 1 JIT compilation requires dynamic code support.")]
    LuaTier1CompilationResult Compile(
        LuaIrModule module,
        int functionId,
        CancellationToken cancellationToken);

    [RequiresDynamicCode("Tier 1 JIT compilation requires dynamic code support.")]
    LuaTier1CompilationResult Compile(
        LuaIrModule module,
        int functionId,
        bool includeInstructionObservation,
        CancellationToken cancellationToken) => Compile(module, functionId, cancellationToken);
}

internal sealed class ReflectionEmitLuaTier1Compiler : ILuaTier1Compiler
{
    public static ReflectionEmitLuaTier1Compiler Instance { get; } = new();
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "The JIT executor checks RuntimeFeature before preparing the compiler.")]
    private static readonly Lazy<bool> CompilerPrepared = new(
        PrepareCompilerCore,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static void PrepareCompiler() => _ = CompilerPrepared.Value;

    [RequiresDynamicCode("Tier 1 JIT compilation requires Reflection.Emit support.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "The JIT registry checks RuntimeFeature before reaching this compiler.")]
    public LuaTier1CompilationResult Compile(
        LuaIrModule module,
        int functionId,
        CancellationToken cancellationToken) => Compile(
            module,
            functionId,
            includeInstructionObservation: true,
            cancellationToken);

    [RequiresDynamicCode("Tier 1 JIT compilation requires Reflection.Emit support.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "The JIT registry checks RuntimeFeature before reaching this compiler.")]
    public LuaTier1CompilationResult Compile(
        LuaIrModule module,
        int functionId,
        bool includeInstructionObservation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var planning = LuaCilCodeGenerator.PlanFunction(
            module,
            functionId,
            includeInstructionObservation: includeInstructionObservation,
            cancellationToken: cancellationToken);
        if (!planning.Succeeded)
        {
            var failedPlanningMetrics = planning.Metrics;
            return new LuaTier1CompilationResult(
                null,
                0,
                [.. planning.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")],
                new LuaJitCompilationMetrics(
                    failedPlanningMetrics.CanonicalVerificationDuration,
                    failedPlanningMetrics.ControlFlowAnalysisDuration,
                    failedPlanningMetrics.MethodPlanBuildDuration,
                    failedPlanningMetrics.PlanVerificationDuration,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore),
                    0,
                    0,
                    0,
                    0,
                    0));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var plan = planning.Plan!;
        var emission = ReflectionEmitCilPlanSink.Compile(
            plan,
            planning.Verification!,
            cancellationToken);
        var estimatedCodeBytes = checked(plan.Instructions.Length * 8L);
        var planningMetrics = planning.Metrics;
        var emissionMetrics = emission.Metrics;
        var metrics = new LuaJitCompilationMetrics(
            planningMetrics.CanonicalVerificationDuration,
            planningMetrics.ControlFlowAnalysisDuration,
            planningMetrics.MethodPlanBuildDuration,
            planningMetrics.PlanVerificationDuration + emissionMetrics.PlanVerificationDuration,
            emissionMetrics.EmissionDuration,
            emissionMetrics.DelegateCreationDuration,
            Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore),
            plan.CanonicalInstructionCount,
            plan.DirectCanonicalInstructionCount,
            plan.SlowPathCanonicalInstructionCount,
            plan.Instructions.Length,
            estimatedCodeBytes);
        if (!emission.Succeeded)
        {
            return new LuaTier1CompilationResult(
                null,
                0,
                [.. emission.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")],
                metrics);
        }

        return new LuaTier1CompilationResult(
            emission.Method,
            estimatedCodeBytes,
            [],
            metrics);
    }

    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "The JIT executor checks RuntimeFeature before preparing the compiler.")]
    private static bool PrepareCompilerCore()
    {
        ReflectionEmitCilPlanSink.PrepareRuntimeAbi();
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));
        var module = new LuaIrModule
        {
            MainFunctionId = 0,
            Functions =
            [
                new LuaIrFunction
                {
                    Id = 0,
                    Span = default,
                    RegisterCount = 1,
                    Constants = [],
                    Instructions = instructions,
                    BasicBlocks = LuaIrControlFlow.Build(instructions),
                },
            ],
        };
        var result = Instance.Compile(
            module,
            0,
            includeInstructionObservation: false,
            CancellationToken.None);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Tier 1 compiler preparation failed: {string.Join("; ", result.Diagnostics)}");
        }

        return true;
    }
}

internal static class LuaJitModuleIdentity
{
    private static readonly ConditionalWeakTable<LuaIrModule, Identity> Identities = new();

    public static string Create(LuaIrModule module) => Identities.GetValue(
        module,
        static module => new Identity(LuaCanonicalModuleSerializer.Sha256Hex(
            LuaCanonicalModuleSerializer.Serialize(module))))
        .ContentId;

    private sealed record Identity(string ContentId);
}
