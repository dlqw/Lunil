using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Lunil.CodeGen.Cil.Analysis;
using Lunil.CodeGen.Cil.Emission;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.CodeGen.Cil.Jit;

public enum LuaJitOptimizationKind : byte
{
    ConstantFold,
    DeadMove,
    NumericUnary,
    NumericBinary,
    BooleanBranch,
    TableGetPic,
    TableSetPic,
    KnownClosureCall,
    FixedResultWindowReuse,
}

public sealed record LuaJitOptimization(
    int ProgramCounter,
    LuaJitOptimizationKind Kind,
    int CanonicalInstructionCount,
    string Guard);

public sealed record LuaJitDeoptMapEntry(
    int ProgramCounter,
    ImmutableArray<int> MaterializedRegisters,
    bool FrameTopMaterialized,
    bool PendingTransformMaterialized);

public sealed record LuaJitTier2Plan(
    int FunctionId,
    ImmutableArray<LuaJitOptimization> Optimizations,
    ImmutableArray<LuaJitDeoptMapEntry> DeoptMap)
{
    public LuaJitTier2CodeKind CodeKind { get; init; } =
        LuaJitTier2CodeKind.ManagedProfileProgram;

    /// <summary>Number of verified natural-loop regions emitted by this plan.</summary>
    public int NumericRegionCount { get; init; }

    /// <summary>Number of numeric region value versions represented as CLR numeric locals.</summary>
    public int UnboxedNumericLocalCount { get; init; }

    /// <summary>Number of canonical numeric instructions emitted directly inside regions.</summary>
    public int DirectNumericInstructionCount { get; init; }

    /// <summary>Number of static backedge safepoint sites, not the dynamic poll count.</summary>
    public int NumericRegionSafepointCount { get; init; }

    /// <summary>
    /// Number of per-instruction instruction-budget comparisons emitted on the qualified hot
    /// numeric-region path. Cold exact-budget slow-tail checks are intentionally excluded.
    /// </summary>
    public int NumericRegionHotInstructionBudgetCheckCount { get; init; }
}

public enum LuaJitTier2CodeKind : byte
{
    ManagedProfileProgram,
    ExactNumericSpecializedCil,
    GuardedSpecializedCil,
}

internal sealed record LuaTier2CompilationResult(
    LuaCompiledMethod? Method,
    LuaJitTier2Plan? Plan,
    long EstimatedCodeBytes,
    ImmutableArray<string> Diagnostics,
    LuaJitTier2CompilationMetrics? Metrics = null)
{
    public bool Succeeded => Method is not null && Plan is not null && Diagnostics.IsEmpty;
}

internal interface ILuaTier2Compiler
{
    LuaTier2CompilationResult Compile(
        LuaIrModule module,
        int functionId,
        LuaJitFunctionProfile profile,
        CancellationToken cancellationToken);
}

internal sealed class ProfileGuidedLuaTier2Compiler : ILuaTier2Compiler
{
    private const int MinimumStraightLineTier2InstructionCount = 16;

    public static ProfileGuidedLuaTier2Compiler Instance { get; } = new();
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "The JIT executor checks RuntimeFeature before preparing the compiler.")]
    private static readonly Lazy<bool> CompilerPrepared = new(
        PrepareCompilerCore,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static void PrepareCompiler() => _ = CompilerPrepared.Value;

    public LuaTier2CompilationResult Compile(
        LuaIrModule module,
        int functionId,
        LuaJitFunctionProfile profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var verificationStarted = Stopwatch.GetTimestamp();
        var errors = LuaIrVerifier.Verify(module);
        var canonicalVerificationDuration = Stopwatch.GetElapsedTime(verificationStarted);
        if (!errors.IsEmpty || functionId < 0 || functionId >= module.Functions.Length)
        {
            return new LuaTier2CompilationResult(
                null,
                null,
                0,
                !errors.IsEmpty
                    ? [.. errors.Select(static error => error.Message)]
                    : ["Function id is outside the verified module."],
                new LuaJitTier2CompilationMetrics(
                    canonicalVerificationDuration,
                    TimeSpan.Zero,
                    LivenessCacheHit: false,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore),
                    LuaJitTier2CodeKind.ManagedProfileProgram,
                    OptimizationCount: 0,
                    SpecializedOptimizationCount: 0,
                    DeoptSiteCount: 0,
                    EstimatedCodeBytes: 0));
        }

        var function = module.Functions[functionId];
        var livenessStarted = Stopwatch.GetTimestamp();
        var liveness = LuaRegisterLiveness.AnalyzeCached(
            module,
            function,
            out var livenessCacheHit,
            cancellationToken);
        var livenessAnalysisDuration = Stopwatch.GetElapsedTime(livenessStarted);
        var optimizationStarted = Stopwatch.GetTimestamp();
        var optimized = BuildOptimizations(function, profile, liveness);
        var numericRegionPlans = BuildNumericRegionPlans(
            module,
            function,
            profile,
            cancellationToken);
        var optimizationDescriptions = optimized.Values
            .OrderBy(static item => item.ProgramCounter)
            .SelectMany(static item => item.ReusesFixedResultWindow
                ? new LuaJitOptimization[]
                {
                    item.ToDescription(),
                    new(
                        item.ProgramCounter,
                        LuaJitOptimizationKind.FixedResultWindowReuse,
                        item.InstructionCount,
                        "callee results remain in the verified caller stack interval"),
                }
                : [item.ToDescription()])
            .ToImmutableArray();
        var deoptMap = optimized.Values
            .Where(static item => item.HasGuard)
            .OrderBy(static item => item.ProgramCounter)
            .Select(item => new LuaJitDeoptMapEntry(
                item.ProgramCounter,
                liveness.LiveBefore[item.ProgramCounter],
                FrameTopMaterialized: true,
                PendingTransformMaterialized: true))
            .ToImmutableArray();
        var plan = new LuaJitTier2Plan(functionId, optimizationDescriptions, deoptMap);
        var program = new Tier2Program(function, optimized);
        var optimizationPlanningDuration = Stopwatch.GetElapsedTime(optimizationStarted);
        LuaCompiledMethod method = program.Execute;
        var estimatedCodeBytes = checked(
            function.Instructions.Length * 12L + optimized.Count * 64L);
        var emissionMetrics = default(LuaTier2EmissionMetrics);
        if (!numericRegionPlans.IsEmpty &&
            TryCompileNumericRegions(
                function,
                numericRegionPlans,
                cancellationToken,
                out var numericRegions) &&
            TryCompileNumericSpecializedCil(
                function,
                optimized,
                numericRegionPlans
                    .SelectMany(static region => region.Region.ProgramCounters)
                    .ToImmutableHashSet(),
                cancellationToken,
                out var specializedMethod,
                out var specializedCodeBytes,
                out var outerEmissionMetrics))
        {
            method = new NumericRegionTier2Program(specializedMethod, numericRegions).Execute;
            estimatedCodeBytes = checked(
                specializedCodeBytes + numericRegions.Sum(static region =>
                    region.EstimatedCodeBytes));
            emissionMetrics = new LuaTier2EmissionMetrics(
                outerEmissionMetrics.CilEmissionDuration + TimeSpan.FromTicks(
                    numericRegions.Sum(static region =>
                        region.Metrics.CilEmissionDuration.Ticks)),
                outerEmissionMetrics.DelegateCreationDuration + TimeSpan.FromTicks(
                    numericRegions.Sum(static region =>
                        region.Metrics.DelegateCreationDuration.Ticks)));
            plan = plan with
            {
                CodeKind = LuaJitTier2CodeKind.ExactNumericSpecializedCil,
                NumericRegionCount = numericRegions.Length,
                UnboxedNumericLocalCount = numericRegions.Sum(static region =>
                    region.Plan.Registers.Count(static register => register.Kind is
                        LuaNumericRegionValueKind.Integer or LuaNumericRegionValueKind.Float)),
                DirectNumericInstructionCount = numericRegions.Sum(static region =>
                    region.Plan.DirectNumericInstructionCount),
                NumericRegionSafepointCount = numericRegions.Sum(static region =>
                    region.Plan.BackedgeProgramCounters.Length),
                NumericRegionHotInstructionBudgetCheckCount = numericRegions.Sum(
                    static region => region.Plan.HotInstructionBudgetCheckCount),
            };
        }
        else if (TryCompileNumericSpecializedCil(
                function,
                optimized,
                [],
                cancellationToken,
                out var fallbackSpecializedMethod,
                out var fallbackSpecializedCodeBytes,
                out emissionMetrics))
        {
            method = fallbackSpecializedMethod;
            estimatedCodeBytes = fallbackSpecializedCodeBytes;
            plan = plan with
            {
                CodeKind = HasNonNumericSpecialization(optimized)
                    ? LuaJitTier2CodeKind.GuardedSpecializedCil
                    : LuaJitTier2CodeKind.ExactNumericSpecializedCil,
            };
        }

        var specializedOptimizationCount = plan.CodeKind !=
            LuaJitTier2CodeKind.ManagedProfileProgram
            ? optimized.Values.Count(static item => item.Kind is
                LuaJitOptimizationKind.NumericUnary or
                LuaJitOptimizationKind.NumericBinary or
                LuaJitOptimizationKind.TableGetPic or
                LuaJitOptimizationKind.TableSetPic or
                LuaJitOptimizationKind.KnownClosureCall)
            : 0;
        var metrics = new LuaJitTier2CompilationMetrics(
            canonicalVerificationDuration,
            livenessAnalysisDuration,
            livenessCacheHit,
            optimizationPlanningDuration,
            emissionMetrics.CilEmissionDuration,
            emissionMetrics.DelegateCreationDuration,
            Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore),
            plan.CodeKind,
            plan.Optimizations.Length,
            specializedOptimizationCount,
            plan.DeoptMap.Length,
            estimatedCodeBytes);

        return new LuaTier2CompilationResult(
            method,
            plan,
            estimatedCodeBytes,
            [],
            metrics);
    }

    internal static LuaJitTier2Eligibility EvaluateAutoPromotionEligibility(
        LuaIrModule module,
        int functionId,
        LuaJitFunctionProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(profile);
        if ((uint)functionId >= (uint)module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var function = module.Functions[functionId];
        var liveness = LuaRegisterLiveness.AnalyzeCached(
            module,
            function,
            out _,
            cancellationToken);
        var optimized = BuildOptimizations(function, profile, liveness);
        var numericOptimizationCount = optimized.Values.Count(static optimization =>
            optimization.Kind is LuaJitOptimizationKind.NumericUnary or
                LuaJitOptimizationKind.NumericBinary);
        var status = ReflectionEmitLuaTier2Compiler.EvaluateNumericSpecialization(
            function,
            optimized);
        if (status == LuaTier2NumericSpecializationStatus.Eligible)
        {
            var hasLoopCall = Enumerable.Range(0, function.Instructions.Length).Any(pc =>
                function.Instructions[pc].Opcode is (LuaIrOpcode.Call or LuaIrOpcode.TailCall) &&
                IsInsideLoop(function, pc) &&
                (!optimized.TryGetValue(pc, out var optimization) ||
                 optimization.Kind != LuaJitOptimizationKind.KnownClosureCall));
            var hasTablePic = optimized.Values.Any(static optimization =>
                optimization.Kind is LuaJitOptimizationKind.TableGetPic or
                    LuaJitOptimizationKind.TableSetPic);
            if (hasLoopCall)
            {
                status = LuaTier2NumericSpecializationStatus.HotLoopCallBoundary;
            }
            else if (!hasTablePic && !HasBackedge(function) &&
                     function.Instructions.Length < MinimumStraightLineTier2InstructionCount)
            {
                status = LuaTier2NumericSpecializationStatus.InsufficientTier2Work;
            }
        }

        var reason = status switch
        {
            LuaTier2NumericSpecializationStatus.Eligible =>
                LuaJitTier2EligibilityReason.Eligible,
            LuaTier2NumericSpecializationStatus.NoNumericHotspot =>
                LuaJitTier2EligibilityReason.NoNumericHotspot,
            LuaTier2NumericSpecializationStatus.PolymorphicNumericProfile =>
                LuaJitTier2EligibilityReason.PolymorphicNumericProfile,
            LuaTier2NumericSpecializationStatus.ManagedOptimizationRequired =>
                LuaJitTier2EligibilityReason.ManagedOptimizationRequired,
            LuaTier2NumericSpecializationStatus.UnsupportedInstruction =>
                LuaJitTier2EligibilityReason.UnsupportedInstruction,
            LuaTier2NumericSpecializationStatus.InsufficientTier2Work =>
                LuaJitTier2EligibilityReason.InsufficientTier2Work,
            LuaTier2NumericSpecializationStatus.HotLoopCallBoundary =>
                LuaJitTier2EligibilityReason.HotLoopCallBoundary,
            _ => throw new InvalidOperationException(
                $"Unknown Tier 2 specialization status {status}."),
        };
        var diagnosticCode = reason switch
        {
            LuaJitTier2EligibilityReason.Eligible => null,
            LuaJitTier2EligibilityReason.NoNumericHotspot =>
                LuaJitTier2DiagnosticCodes.NoNumericHotspot,
            LuaJitTier2EligibilityReason.PolymorphicNumericProfile =>
                LuaJitTier2DiagnosticCodes.PolymorphicNumericProfile,
            LuaJitTier2EligibilityReason.ManagedOptimizationRequired =>
                LuaJitTier2DiagnosticCodes.ManagedOptimizationRequired,
            LuaJitTier2EligibilityReason.ManagedSemanticBoundary =>
                LuaJitTier2DiagnosticCodes.ManagedSemanticBoundary,
            LuaJitTier2EligibilityReason.UnsupportedInstruction =>
                LuaJitTier2DiagnosticCodes.UnsupportedInstruction,
            LuaJitTier2EligibilityReason.InsufficientTier2Work =>
                LuaJitTier2DiagnosticCodes.InsufficientTier2Work,
            LuaJitTier2EligibilityReason.HotLoopCallBoundary =>
                LuaJitTier2DiagnosticCodes.HotLoopCallBoundary,
            _ => throw new InvalidOperationException(
                $"Unknown Tier 2 eligibility reason {reason}."),
        };

        return new LuaJitTier2Eligibility(
            status == LuaTier2NumericSpecializationStatus.Eligible,
            reason,
            diagnosticCode,
            profile.Samples,
            optimized.Count,
            numericOptimizationCount,
            status == LuaTier2NumericSpecializationStatus.Eligible
                ? HasNonNumericSpecialization(optimized)
                    ? LuaJitTier2CodeKind.GuardedSpecializedCil
                    : LuaJitTier2CodeKind.ExactNumericSpecializedCil
                : LuaJitTier2CodeKind.ManagedProfileProgram);
    }

    private static bool HasNonNumericSpecialization(
        ImmutableDictionary<int, OptimizedInstruction> optimized) =>
        optimized.Values.Any(static optimization => optimization.Kind is
            LuaJitOptimizationKind.TableGetPic or
            LuaJitOptimizationKind.TableSetPic or
            LuaJitOptimizationKind.KnownClosureCall);

    private static ImmutableArray<LuaNumericRegionPlan> BuildNumericRegionPlans(
        LuaIrModule module,
        LuaIrFunction function,
        LuaJitFunctionProfile profile,
        CancellationToken cancellationToken)
    {
        var candidates = ImmutableArray.CreateBuilder<LuaNumericRegionPlan>();
        foreach (var region in LuaNumericRegionAnalyzer.AnalyzeNaturalLoops(
            module,
            function.Id,
            out _,
            cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = LuaNumericRegionPlanner.TryCreate(
                function,
                region,
                BuildNumericRegionHints(function, region, profile),
                cancellationToken);
            if (plan is not null)
            {
                candidates.Add(plan);
            }
        }

        var occupied = new HashSet<int>();
        var selected = ImmutableArray.CreateBuilder<LuaNumericRegionPlan>();
        foreach (var candidate in candidates
            .OrderByDescending(static candidate => candidate.Region.ProgramCounters.Length)
            .ThenBy(static candidate => candidate.Region.HeaderProgramCounter)
            .ThenBy(static candidate => candidate.Region.BackedgeProgramCounter))
        {
            if (candidate.Region.ProgramCounters.Any(occupied.Contains))
            {
                continue;
            }

            selected.Add(candidate);
            occupied.UnionWith(candidate.Region.ProgramCounters);
        }

        return selected
            .OrderBy(static candidate => candidate.Region.HeaderProgramCounter)
            .ThenBy(static candidate => candidate.Region.BackedgeProgramCounter)
            .ToImmutableArray();
    }

    private static ImmutableArray<LuaNumericRegionTypeHint> BuildNumericRegionHints(
        LuaIrFunction function,
        LuaNaturalLoopRegion region,
        LuaJitFunctionProfile profile)
    {
        var result = ImmutableArray.CreateBuilder<LuaNumericRegionTypeHint>();
        for (var register = 0;
            register < Math.Min(function.ParameterCount, profile.ArgumentKinds.Length);
            register++)
        {
            var kind = ToNumericRegionKind(profile.ArgumentKinds[register]);
            if (kind is not LuaNumericRegionValueKind.Conflict)
            {
                result.Add(new LuaNumericRegionTypeHint(
                    region.HeaderProgramCounter,
                    register,
                    kind));
            }
        }

        foreach (var site in profile.Sites)
        {
            if (site.Samples <= 0 || !region.ProgramCounters.Contains(site.ProgramCounter) ||
                (uint)site.ProgramCounter >= (uint)function.Instructions.Length)
            {
                continue;
            }

            var instruction = function.Instructions[site.ProgramCounter];
            if (site.Opcode != instruction.Opcode)
            {
                continue;
            }

            switch (instruction.Opcode)
            {
                case LuaIrOpcode.Unary:
                    AddNumericRegionHint(
                        result,
                        site.ProgramCounter,
                        instruction.B,
                        site.FirstOperandKinds);
                    break;
                case LuaIrOpcode.Binary:
                    AddNumericRegionHint(
                        result,
                        site.ProgramCounter,
                        instruction.B,
                        site.FirstOperandKinds);
                    AddNumericRegionHint(
                        result,
                        site.ProgramCounter,
                        instruction.C,
                        site.SecondOperandKinds);
                    break;
                case LuaIrOpcode.JumpIfFalse:
                case LuaIrOpcode.JumpIfTrue:
                    AddNumericRegionHint(
                        result,
                        site.ProgramCounter,
                        instruction.A,
                        site.FirstOperandKinds);
                    break;
                case LuaIrOpcode.NumericForLoop:
                    AddNumericRegionHint(
                        result,
                        site.ProgramCounter,
                        instruction.A,
                        site.FirstOperandKinds);
                    AddNumericRegionHint(
                        result,
                        site.ProgramCounter,
                        instruction.A + 1,
                        site.SecondOperandKinds);
                    AddNumericRegionHint(
                        result,
                        site.ProgramCounter,
                        instruction.A + 2,
                        site.ThirdOperandKinds);
                    AddNumericRegionHint(
                        result,
                        site.ProgramCounter,
                        instruction.A + 3,
                        site.FirstOperandKinds);
                    break;
            }
        }

        return result.ToImmutable();
    }

    private static void AddNumericRegionHint(
        ImmutableArray<LuaNumericRegionTypeHint>.Builder hints,
        int programCounter,
        int register,
        LuaJitValueKinds kinds) => hints.Add(new LuaNumericRegionTypeHint(
            programCounter,
            register,
            ToNumericRegionKind(kinds)));

    private static LuaNumericRegionValueKind ToNumericRegionKind(LuaJitValueKinds kinds) =>
        kinds switch
        {
            LuaJitValueKinds.Integer => LuaNumericRegionValueKind.Integer,
            LuaJitValueKinds.Float => LuaNumericRegionValueKind.Float,
            LuaJitValueKinds.Boolean => LuaNumericRegionValueKind.Boolean,
            _ => LuaNumericRegionValueKind.Conflict,
        };

    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "Tier 2 compilation is reached only after the dynamic-code capability check.")]
    private static bool TryCompileNumericRegions(
        LuaIrFunction function,
        ImmutableArray<LuaNumericRegionPlan> plans,
        CancellationToken cancellationToken,
        out ImmutableArray<LuaCompiledNumericRegion> regions)
    {
        var result = ImmutableArray.CreateBuilder<LuaCompiledNumericRegion>(plans.Length);
        foreach (var plan in plans)
        {
            if (!ReflectionEmitLuaNumericRegionCompiler.TryCompile(
                function,
                plan,
                new LuaNumericRegionEmissionMode(
                    RequireLoopOsrEntry: false,
                    ObserveLoopOsrBackedge: false),
                cancellationToken,
                out var region))
            {
                regions = [];
                return false;
            }

            result.Add(region);
        }

        regions = result.MoveToImmutable();
        return true;
    }

    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "Tier 2 compilation is reached only after the dynamic-code capability check.")]
    private static bool TryCompileNumericSpecializedCil(
        LuaIrFunction function,
        ImmutableDictionary<int, OptimizedInstruction> optimized,
        ImmutableHashSet<int> numericRegionProgramCounters,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out LuaCompiledMethod? method,
        out long estimatedCodeBytes,
        out LuaTier2EmissionMetrics metrics) => ReflectionEmitLuaTier2Compiler.TryCompile(
            function,
            optimized,
            numericRegionProgramCounters,
            cancellationToken,
            out method,
            out estimatedCodeBytes,
            out metrics);

    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "The JIT executor checks RuntimeFeature before preparing the compiler.")]
    private static bool PrepareCompilerCore()
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                a: 2,
                b: 0,
                c: 1,
                d: (int)LuaIrBinaryOperator.LessThan),
            new LuaIrInstruction(LuaIrOpcode.JumpIfFalse, a: 2, b: 5),
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                a: 3,
                b: 3,
                c: 0,
                d: (int)LuaIrBinaryOperator.Add),
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                a: 0,
                b: 0,
                c: 4,
                d: (int)LuaIrBinaryOperator.Add),
            new LuaIrInstruction(LuaIrOpcode.Jump, b: 0, c: -1),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 3, b: 1));
        var module = new LuaIrModule
        {
            MainFunctionId = 0,
            Functions =
            [
                new LuaIrFunction
                {
                    Id = 0,
                    Span = default,
                    ParameterCount = 5,
                    RegisterCount = 5,
                    Constants = [],
                    Instructions = instructions,
                    BasicBlocks = LuaIrControlFlow.Build(instructions),
                },
            ],
        };
        var profile = new LuaJitFunctionProfile(
            Samples: 2,
            ArgumentKinds:
            [
                LuaJitValueKinds.Integer,
                LuaJitValueKinds.Integer,
                LuaJitValueKinds.Boolean,
                LuaJitValueKinds.Integer,
                LuaJitValueKinds.Integer,
            ],
            Sites:
            [
                new LuaJitSiteProfile(
                    ProgramCounter: 0,
                    Opcode: LuaIrOpcode.Binary,
                    Samples: 1,
                    FirstOperandKinds: LuaJitValueKinds.Integer,
                    SecondOperandKinds: LuaJitValueKinds.Integer,
                    ThirdOperandKinds: LuaJitValueKinds.None,
                    BranchTaken: 0,
                    BranchNotTaken: 0,
                    IsMegamorphic: false,
                    TableShapes: [],
                    CallTargets: []),
                new LuaJitSiteProfile(
                    ProgramCounter: 1,
                    Opcode: LuaIrOpcode.JumpIfFalse,
                    Samples: 2,
                    FirstOperandKinds: LuaJitValueKinds.Boolean,
                    SecondOperandKinds: LuaJitValueKinds.None,
                    ThirdOperandKinds: LuaJitValueKinds.None,
                    BranchTaken: 1,
                    BranchNotTaken: 1,
                    IsMegamorphic: false,
                    TableShapes: [],
                    CallTargets: []),
                new LuaJitSiteProfile(
                    ProgramCounter: 2,
                    Opcode: LuaIrOpcode.Binary,
                    Samples: 2,
                    FirstOperandKinds: LuaJitValueKinds.Integer,
                    SecondOperandKinds: LuaJitValueKinds.Integer,
                    ThirdOperandKinds: LuaJitValueKinds.None,
                    BranchTaken: 0,
                    BranchNotTaken: 0,
                    IsMegamorphic: false,
                    TableShapes: [],
                    CallTargets: []),
                new LuaJitSiteProfile(
                    ProgramCounter: 3,
                    Opcode: LuaIrOpcode.Binary,
                    Samples: 2,
                    FirstOperandKinds: LuaJitValueKinds.Integer,
                    SecondOperandKinds: LuaJitValueKinds.Integer,
                    ThirdOperandKinds: LuaJitValueKinds.None,
                    BranchTaken: 0,
                    BranchNotTaken: 0,
                    IsMegamorphic: false,
                    TableShapes: [],
                    CallTargets: []),
            ]);
        var result = Instance.Compile(module, 0, profile, CancellationToken.None);
        if (!result.Succeeded || result.Plan?.CodeKind !=
            LuaJitTier2CodeKind.ExactNumericSpecializedCil ||
            result.Plan.NumericRegionCount == 0)
        {
            throw new InvalidOperationException(
                $"Tier 2 compiler preparation failed: {string.Join("; ", result.Diagnostics)}");
        }

        return true;
    }

    private static ImmutableDictionary<int, OptimizedInstruction> BuildOptimizations(
        LuaIrFunction function,
        LuaJitFunctionProfile profile,
        LuaRegisterLivenessResult liveness)
    {
        var result = ImmutableDictionary.CreateBuilder<int, OptimizedInstruction>();
        AddConstantFolds(function, liveness, result);
        foreach (var site in profile.Sites)
        {
            if (result.ContainsKey(site.ProgramCounter) || result.Values.Any(
                optimization => optimization.Kind == LuaJitOptimizationKind.ConstantFold &&
                    site.ProgramCounter > optimization.ProgramCounter &&
                    site.ProgramCounter < optimization.ProgramCounter +
                        optimization.InstructionCount))
            {
                continue;
            }

            var instruction = function.Instructions[site.ProgramCounter];
            switch (instruction.Opcode)
            {
                case LuaIrOpcode.Move when
                    !liveness.LiveAfter[site.ProgramCounter].Contains(instruction.A):
                    result.Add(
                        site.ProgramCounter,
                        OptimizedInstruction.DeadMove(site.ProgramCounter));
                    break;
                case LuaIrOpcode.Unary when IsStableUnary(instruction, site):
                    result.Add(
                        site.ProgramCounter,
                        OptimizedInstruction.Unary(
                            site.ProgramCounter,
                            site.FirstOperandKinds));
                    break;
                case LuaIrOpcode.Binary when IsStableNumeric(site.FirstOperandKinds) &&
                    IsStableNumeric(site.SecondOperandKinds) &&
                    instruction.D != (int)LuaIrBinaryOperator.Concatenate:
                    result.Add(
                        site.ProgramCounter,
                        OptimizedInstruction.Binary(
                            site.ProgramCounter,
                            site.FirstOperandKinds,
                            site.SecondOperandKinds));
                    break;
                case LuaIrOpcode.JumpIfFalse:
                case LuaIrOpcode.JumpIfTrue:
                    if (site.Samples != 0 &&
                        (site.BranchTaken == site.Samples || site.BranchNotTaken == site.Samples))
                    {
                        result.Add(
                            site.ProgramCounter,
                            OptimizedInstruction.Branch(
                                site.ProgramCounter,
                                site.BranchTaken == site.Samples));
                    }

                    break;
                case LuaIrOpcode.GetTable when IsStableTablePic(site):
                    result.Add(
                        site.ProgramCounter,
                        OptimizedInstruction.Table(
                            site.ProgramCounter,
                            LuaJitOptimizationKind.TableGetPic,
                            site.TableShapes));
                    break;
                case LuaIrOpcode.SetTable when IsStableTablePic(site):
                    result.Add(
                        site.ProgramCounter,
                        OptimizedInstruction.Table(
                            site.ProgramCounter,
                            LuaJitOptimizationKind.TableSetPic,
                            site.TableShapes));
                    break;
                case LuaIrOpcode.Call:
                case LuaIrOpcode.TailCall:
                    if (!site.IsMegamorphic && site.CallTargets.Length == 1 &&
                        site.CallTargets[0].Kind == LuaJitCallTargetKind.Lua)
                    {
                        result.Add(
                            site.ProgramCounter,
                            OptimizedInstruction.KnownCall(
                                site.ProgramCounter,
                                site.CallTargets[0],
                                instruction.Opcode == LuaIrOpcode.Call && instruction.C >= 0));
                    }

                    break;
            }
        }

        return result.ToImmutable();
    }

    private static bool IsInsideLoop(LuaIrFunction function, int programCounter)
    {
        for (var backedge = programCounter; backedge < function.Instructions.Length; backedge++)
        {
            var instruction = function.Instructions[backedge];
            if (instruction.Opcode is
                    (LuaIrOpcode.Jump or LuaIrOpcode.JumpIfFalse or
                        LuaIrOpcode.JumpIfTrue or LuaIrOpcode.NumericForLoop) &&
                instruction.B <= programCounter && instruction.B <= backedge)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasBackedge(LuaIrFunction function)
    {
        for (var programCounter = 0; programCounter < function.Instructions.Length; programCounter++)
        {
            var instruction = function.Instructions[programCounter];
            if (instruction.Opcode is
                    (LuaIrOpcode.Jump or LuaIrOpcode.JumpIfFalse or
                        LuaIrOpcode.JumpIfTrue or LuaIrOpcode.NumericForLoop) &&
                instruction.B <= programCounter)
            {
                return true;
            }
        }

        return false;
    }

    private static void AddConstantFolds(
        LuaIrFunction function,
        LuaRegisterLivenessResult liveness,
        ImmutableDictionary<int, OptimizedInstruction>.Builder result)
    {
        var blocks = function.BasicBlocks.IsDefaultOrEmpty
            ? LuaIrControlFlow.Build(function.Instructions)
            : function.BasicBlocks;
        foreach (var block in blocks)
        {
            for (var pc = block.Start; pc + 2 < block.End; pc++)
            {
                var first = function.Instructions[pc];
                var second = function.Instructions[pc + 1];
                var binary = function.Instructions[pc + 2];
                if (first.Opcode != LuaIrOpcode.LoadConstant ||
                    second.Opcode != LuaIrOpcode.LoadConstant ||
                    binary.Opcode != LuaIrOpcode.Binary ||
                    first.A == second.A || binary.B != first.A || binary.C != second.A ||
                    liveness.LiveAfter[pc + 2].Contains(first.A) && first.A != binary.A ||
                    liveness.LiveAfter[pc + 2].Contains(second.A) && second.A != binary.A ||
                    !TryMaterializePrimitive(function.Constants[first.B], out var left) ||
                    !TryMaterializePrimitive(function.Constants[second.B], out var right) ||
                    !TryFoldBinary((LuaIrBinaryOperator)binary.D, left, right, out var value))
                {
                    continue;
                }

                result.Add(pc, OptimizedInstruction.ConstantFold(pc, binary.A, value));
                pc += 2;
            }
        }
    }

    private static bool TryMaterializePrimitive(LuaIrConstant constant, out LuaValue value)
    {
        value = constant.Kind switch
        {
            LuaIrConstantKind.Nil => LuaValue.Nil,
            LuaIrConstantKind.Boolean => LuaValue.FromBoolean(constant.Boolean),
            LuaIrConstantKind.Integer => LuaValue.FromInteger(constant.Integer),
            LuaIrConstantKind.Float => LuaValue.FromFloat(constant.Float),
            _ => LuaValue.Nil,
        };
        return constant.Kind != LuaIrConstantKind.String;
    }

    private static bool TryFoldBinary(
        LuaIrBinaryOperator operation,
        LuaValue left,
        LuaValue right,
        out LuaValue value)
    {
        if (operation == LuaIrBinaryOperator.Concatenate)
        {
            value = LuaValue.Nil;
            return false;
        }

        try
        {
            value = LuaValueOperations.Binary(null!, operation, left, right);
            return true;
        }
        catch (LuaRuntimeException)
        {
            value = LuaValue.Nil;
            return false;
        }
    }

    private static bool IsStableUnary(
        LuaIrInstruction instruction,
        LuaJitSiteProfile site) => (LuaIrUnaryOperator)instruction.C switch
        {
            LuaIrUnaryOperator.Negate or LuaIrUnaryOperator.BitwiseNot =>
                IsStableNumeric(site.FirstOperandKinds),
            LuaIrUnaryOperator.LogicalNot => site.FirstOperandKinds != LuaJitValueKinds.None,
            LuaIrUnaryOperator.Length => site.FirstOperandKinds is LuaJitValueKinds.String,
            _ => false,
        };

    private static bool IsStableNumeric(LuaJitValueKinds kinds) =>
        kinds != LuaJitValueKinds.None &&
        (kinds & ~(LuaJitValueKinds.Integer | LuaJitValueKinds.Float)) == 0;

    private static bool IsStableTablePic(LuaJitSiteProfile site) =>
        !site.IsMegamorphic &&
        site.FirstOperandKinds == LuaJitValueKinds.Table &&
        !site.TableShapes.IsEmpty;

    private sealed class NumericRegionTier2Program
    {
        private readonly LuaCompiledMethod _outerMethod;
        private readonly LuaCompiledNumericRegion?[] _regionsByProgramCounter;

        public NumericRegionTier2Program(
            LuaCompiledMethod outerMethod,
            ImmutableArray<LuaCompiledNumericRegion> regions)
        {
            _outerMethod = outerMethod;
            var length = regions
                .SelectMany(static region => region.Plan.Region.ProgramCounters)
                .DefaultIfEmpty(-1)
                .Max() + 1;
            _regionsByProgramCounter = new LuaCompiledNumericRegion?[length];
            foreach (var region in regions)
            {
                foreach (var programCounter in region.Plan.Region.ProgramCounters)
                {
                    if (_regionsByProgramCounter[programCounter] is not null)
                    {
                        throw new InvalidOperationException(
                            $"Numeric regions overlap at PC {programCounter}.");
                    }

                    _regionsByProgramCounter[programCounter] = region;
                }
            }
        }

        public LuaCompiledExit Execute(
            LuaExecutionContext context,
            LuaThread thread,
            LuaFrame frame)
        {
            while (true)
            {
                var region = FindRegion(frame.ProgramCounter);
                var exit = region is null
                    ? _outerMethod(context, thread, frame)
                    : region.Method(context, thread, frame);
                if (exit.Kind != LuaCompiledExitKind.Continue)
                {
                    return exit;
                }

                if (frame.ProgramCounter != exit.ProgramCounter)
                {
                    return LuaCompiledExit.Deopt(
                        exit.ProgramCounter,
                        context.InstructionsConsumed,
                        LuaCompiledExitReason.BackendInvalidated);
                }

                var nextRegion = FindRegion(exit.ProgramCounter);
                if (region is null ? nextRegion is null : ReferenceEquals(region, nextRegion))
                {
                    return LuaCompiledExit.Deopt(
                        exit.ProgramCounter,
                        context.InstructionsConsumed,
                        LuaCompiledExitReason.BackendInvalidated);
                }
            }
        }

        private LuaCompiledNumericRegion? FindRegion(int programCounter) =>
            (uint)programCounter < (uint)_regionsByProgramCounter.Length
                ? _regionsByProgramCounter[programCounter]
                : null;
    }

    private sealed class Tier2Program
    {
        private readonly LuaIrFunction _function;
        private readonly OptimizedInstruction?[] _optimized;
        private readonly LuaTier2RuntimeSites _runtimeSites;

        public Tier2Program(
            LuaIrFunction function,
            ImmutableDictionary<int, OptimizedInstruction> optimized)
        {
            _function = function;
            _optimized = new OptimizedInstruction?[function.Instructions.Length];
            _runtimeSites = new LuaTier2RuntimeSites(function.Instructions.Length);
            foreach (var pair in optimized)
            {
                _optimized[pair.Key] = pair.Value;
            }
        }

        public LuaCompiledExit Execute(
            LuaExecutionContext context,
            LuaThread thread,
            LuaFrame frame)
        {
            if (!LuaCodegenAbiV2.CanExecuteCompiledFrame(
                    context,
                    frame,
                    _function.Id,
                    _function.RegisterCount))
            {
                return LuaCompiledExit.Deopt(
                    frame.ProgramCounter,
                    context.InstructionsConsumed,
                    LuaCompiledExitReason.DebugModeChanged);
            }

            var safepointCountdown = LuaCodegenAbiV3.CompiledBackedgeSafepointQuantum;
            while ((uint)frame.ProgramCounter < (uint)_function.Instructions.Length)
            {
                var pc = frame.ProgramCounter;
                var instruction = _function.Instructions[pc];
                if (_optimized[pc] is { } optimization)
                {
                    var optimizedExit = ExecuteOptimized(
                        context,
                        thread,
                        frame,
                        instruction,
                        optimization,
                        _runtimeSites);
                    if (optimizedExit is { } optimizedResult)
                    {
                        return optimizedResult;
                    }

                    if (frame.ProgramCounter <= pc && --safepointCountdown == 0)
                    {
                        safepointCountdown = LuaCodegenAbiV3.CompiledBackedgeSafepointQuantum;
                        if (!LuaCodegenAbiV3.PollGcSafepoint(context, thread, frame))
                        {
                            return LuaCompiledExit.Poll(
                                frame.ProgramCounter,
                                context.InstructionsConsumed,
                                LuaCompiledExitReason.GarbageCollection);
                        }
                    }

                    continue;
                }

                var directExit = ExecuteDirect(context, thread, frame, instruction);
                if (directExit is { } directResult)
                {
                    return directResult;
                }

                if (frame.ProgramCounter <= pc && --safepointCountdown == 0)
                {
                    safepointCountdown = LuaCodegenAbiV3.CompiledBackedgeSafepointQuantum;
                    if (!LuaCodegenAbiV3.PollGcSafepoint(context, thread, frame))
                    {
                        return LuaCompiledExit.Poll(
                            frame.ProgramCounter,
                            context.InstructionsConsumed,
                            LuaCompiledExitReason.GarbageCollection);
                    }
                }
            }

            return LuaCompiledExit.Deopt(
                frame.ProgramCounter,
                context.InstructionsConsumed,
                LuaCompiledExitReason.BackendInvalidated);
        }

        private static LuaCompiledExit? ExecuteOptimized(
            LuaExecutionContext context,
            LuaThread thread,
            LuaFrame frame,
            LuaIrInstruction instruction,
            OptimizedInstruction optimization,
            LuaTier2RuntimeSites runtimeSites)
        {
            switch (optimization.Kind)
            {
                case LuaJitOptimizationKind.ConstantFold:
                    if (context.RemainingInstructionCount < optimization.InstructionCount)
                    {
                        return ExecuteDirect(context, thread, frame, instruction);
                    }

                    _ = context.TryReserveInstructions(optimization.InstructionCount);
                    LuaCodegenAbiV2.WriteRegisterUnchecked(
                        thread,
                        frame,
                        optimization.DestinationRegister,
                        optimization.FoldedValue);
                    frame.ProgramCounter += optimization.InstructionCount;
                    return null;
                case LuaJitOptimizationKind.DeadMove:
                    if (!Reserve(context, frame.ProgramCounter))
                    {
                        return BudgetPoll(context, frame.ProgramCounter);
                    }

                    // Dead SSA use does not remove the physical Lua stack slot from
                    // the heap root set. Clear it so a skipped copy cannot retain the
                    // previous object across a compiled GC safepoint.
                    LuaCodegenAbiV2.ClearRegistersUnchecked(
                        thread,
                        frame,
                        instruction.A,
                        1);
                    frame.ProgramCounter++;
                    return null;
                case LuaJitOptimizationKind.NumericUnary:
                    {
                        var operand = LuaCodegenAbiV2.ReadRegisterUnchecked(
                            thread,
                            frame,
                            instruction.B);
                        if (!optimization.MatchesFirst(operand))
                        {
                            return GuardFailure(context, frame.ProgramCounter);
                        }

                        if (!Reserve(context, frame.ProgramCounter))
                        {
                            return BudgetPoll(context, frame.ProgramCounter);
                        }

                        var operation = (LuaIrUnaryOperator)instruction.C;
                        var value = optimization.FirstKinds == LuaJitValueKinds.Integer &&
                            operation is LuaIrUnaryOperator.Negate or
                                LuaIrUnaryOperator.BitwiseNot
                            ? LuaValueOperations.UnaryIntegerSpecialized(operation, operand)
                            : LuaValueOperations.Unary(operation, operand);
                        LuaCodegenAbiV2.WriteRegisterUnchecked(
                            thread,
                            frame,
                            instruction.A,
                            value);
                        frame.ProgramCounter++;
                        return null;
                    }
                case LuaJitOptimizationKind.NumericBinary:
                    {
                        var left = LuaCodegenAbiV2.ReadRegisterUnchecked(
                            thread,
                            frame,
                            instruction.B);
                        var right = LuaCodegenAbiV2.ReadRegisterUnchecked(
                            thread,
                            frame,
                            instruction.C);
                        if (!optimization.MatchesFirst(left) ||
                            !optimization.MatchesSecond(right))
                        {
                            return GuardFailure(context, frame.ProgramCounter);
                        }

                        if (!Reserve(context, frame.ProgramCounter))
                        {
                            return BudgetPoll(context, frame.ProgramCounter);
                        }

                        var operation = (LuaIrBinaryOperator)instruction.D;
                        var value = optimization.FirstKinds == LuaJitValueKinds.Integer &&
                            optimization.SecondKinds == LuaJitValueKinds.Integer
                            ? LuaValueOperations.BinaryIntegerSpecialized(operation, left, right)
                            : LuaValueOperations.Binary(context.State, operation, left, right);
                        LuaCodegenAbiV2.WriteRegisterUnchecked(
                            thread,
                            frame,
                            instruction.A,
                            value);
                        frame.ProgramCounter++;
                        return null;
                    }
                case LuaJitOptimizationKind.BooleanBranch:
                    {
                        var truthy = LuaCodegenAbiV2.ReadRegisterUnchecked(
                            thread,
                            frame,
                            instruction.A).IsTruthy;
                        var taken = instruction.Opcode == LuaIrOpcode.JumpIfTrue
                            ? truthy
                            : !truthy;
                        if (taken != optimization.ExpectedBranchTaken)
                        {
                            return GuardFailure(context, frame.ProgramCounter);
                        }

                        if (!Reserve(context, frame.ProgramCounter))
                        {
                            return BudgetPoll(context, frame.ProgramCounter);
                        }

                        if (instruction.D != 0)
                        {
                            LuaCodegenAbiV2.SetFrameTopUnchecked(
                                thread,
                                frame,
                                instruction.C);
                        }

                        frame.ProgramCounter = taken ? instruction.B : frame.ProgramCounter + 1;
                        return null;
                    }
                case LuaJitOptimizationKind.TableGetPic:
                case LuaJitOptimizationKind.TableSetPic:
                    {
                        var isGet = optimization.Kind == LuaJitOptimizationKind.TableGetPic;
                        var cache = runtimeSites.GetTableSite(frame.ProgramCounter);
                        var result = isGet
                            ? LuaCodegenAbiV3.TryExecuteTableGetPic(
                                context,
                                thread,
                                frame,
                                cache,
                                instruction.A,
                                instruction.B,
                                instruction.C)
                            : LuaCodegenAbiV3.TryExecuteTableSetPic(
                                context,
                                thread,
                                frame,
                                cache,
                                instruction.A,
                                instruction.B,
                                instruction.C);
                        if (result == LuaCodegenPicExecutionResult.GuardFailure)
                        {
                            return GuardFailure(context, frame.ProgramCounter);
                        }

                        if (result == LuaCodegenPicExecutionResult.InstructionBudget)
                        {
                            return BudgetPoll(context, frame.ProgramCounter);
                        }

                        return null;
                    }
                case LuaJitOptimizationKind.KnownClosureCall:
                    {
                        var cache = runtimeSites.GetCallSite(frame.ProgramCounter);
                        if (!LuaCodegenAbiV3.CanExecuteKnownClosureCall(
                                thread,
                                frame,
                                cache,
                                instruction.A,
                                optimization.CallTarget!.FunctionId))
                        {
                            return GuardFailure(context, frame.ProgramCounter);
                        }

                        if (!Reserve(context, frame.ProgramCounter))
                        {
                            return BudgetPoll(context, frame.ProgramCounter);
                        }

                        if (instruction.Opcode == LuaIrOpcode.TailCall)
                        {
                            LuaCodegenAbiV3.ExecuteKnownClosureTailCall(
                                context,
                                thread,
                                frame,
                                instruction.A,
                                instruction.B);
                        }
                        else
                        {
                            if (LuaCodegenAbiV3.TryExecuteFramelessCall(
                                    context,
                                    thread,
                                    frame,
                                    instruction.A,
                                    instruction.B,
                                    instruction.C) != 0)
                            {
                                return LuaCodegenAbiV3.CanContinueAfterFramelessCall(
                                    context,
                                    thread,
                                    frame)
                                        ? null
                                        : LuaCompiledExit.Continue(
                                            frame.ProgramCounter,
                                            context.InstructionsConsumed);
                            }

                            LuaCodegenAbiV3.ExecuteKnownClosureCall(
                                context,
                                thread,
                                frame,
                                instruction.A,
                                instruction.B,
                                instruction.C);
                        }
                        return LuaCompiledExit.Continue(
                            frame.ProgramCounter,
                            context.InstructionsConsumed);
                    }
                default:
                    throw new InvalidOperationException(
                        $"Unknown Tier 2 optimization {optimization.Kind}.");
            }
        }

        private static LuaCompiledExit? ExecuteDirect(
            LuaExecutionContext context,
            LuaThread thread,
            LuaFrame frame,
            LuaIrInstruction instruction)
        {
            var pc = frame.ProgramCounter;
            switch (instruction.Opcode)
            {
                case LuaIrOpcode.LoadConstant:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV2.WriteRegisterUnchecked(
                        thread,
                        frame,
                        instruction.A,
                        LuaCodegenAbiV1.MaterializeConstant(
                            context,
                            frame,
                            instruction.B));
                    frame.ProgramCounter++;
                    return null;
                case LuaIrOpcode.LoadNil:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV2.ClearRegistersUnchecked(
                        thread,
                        frame,
                        instruction.A,
                        instruction.B);
                    frame.ProgramCounter++;
                    return null;
                case LuaIrOpcode.Move:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV2.WriteRegisterUnchecked(
                        thread,
                        frame,
                        instruction.A,
                        LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, instruction.B));
                    frame.ProgramCounter++;
                    return null;
                case LuaIrOpcode.GetUpvalue:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV2.WriteRegisterUnchecked(
                        thread,
                        frame,
                        instruction.A,
                        LuaCodegenAbiV1.ReadUpvalue(frame, instruction.B));
                    frame.ProgramCounter++;
                    return null;
                case LuaIrOpcode.SetUpvalue:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV1.WriteUpvalue(
                        frame,
                        instruction.A,
                        LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, instruction.B));
                    frame.ProgramCounter++;
                    return null;
                case LuaIrOpcode.NewTable:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV3.ExecuteNewTable(
                        context,
                        thread,
                        frame,
                        instruction.A,
                        instruction.B,
                        instruction.C);
                    return null;
                case LuaIrOpcode.GetTable:
                case LuaIrOpcode.SetTable:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    var completed = instruction.Opcode == LuaIrOpcode.GetTable
                        ? LuaCodegenAbiV3.ExecuteGetTable(
                            context,
                            thread,
                            frame,
                            instruction.A,
                            instruction.B,
                            instruction.C)
                        : LuaCodegenAbiV3.ExecuteSetTable(
                            context,
                            thread,
                            frame,
                            instruction.A,
                            instruction.B,
                            instruction.C);
                    return completed
                        ? null
                        : LuaCompiledExit.Continue(
                            frame.ProgramCounter,
                            context.InstructionsConsumed);
                case LuaIrOpcode.SetList:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV3.ExecuteSetList(
                        thread,
                        frame,
                        instruction.A,
                        instruction.B,
                        instruction.C,
                        instruction.D);
                    return null;
                case LuaIrOpcode.Closure:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV3.ExecuteClosure(
                        context,
                        thread,
                        frame,
                        instruction.A,
                        instruction.B);
                    return null;
                case LuaIrOpcode.VarArg:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV3.ExecuteVarArg(
                        thread,
                        frame,
                        instruction.A,
                        instruction.B);
                    return null;
                case LuaIrOpcode.SetTop:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV2.SetFrameTopUnchecked(thread, frame, instruction.A);
                    frame.ProgramCounter++;
                    return null;
                case LuaIrOpcode.Close when LuaCodegenAbiV2.CanSkipClose(
                    thread,
                    frame,
                    instruction.A):
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    frame.ProgramCounter++;
                    return null;
                case LuaIrOpcode.Jump when instruction.C < 0:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    frame.ProgramCounter = instruction.B;
                    return null;
                case LuaIrOpcode.JumpIfFalse:
                case LuaIrOpcode.JumpIfTrue:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    var truthy = instruction.D != 0
                        ? LuaCodegenAbiV2.ReadTruthyAndSetFrameTopUnchecked(
                            thread,
                            frame,
                            instruction.A,
                            instruction.C)
                        : LuaCodegenAbiV2.ReadRegisterUnchecked(
                            thread,
                            frame,
                            instruction.A).IsTruthy;
                    var taken = instruction.Opcode == LuaIrOpcode.JumpIfTrue
                        ? truthy
                        : !truthy;
                    frame.ProgramCounter = taken ? instruction.B : pc + 1;
                    return null;
                case LuaIrOpcode.Return:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    return LuaCompiledExit.Return(pc, context.InstructionsConsumed);
                case LuaIrOpcode.Call:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    return LuaCompiledExit.Call(pc, context.InstructionsConsumed);
                case LuaIrOpcode.TailCall:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    return LuaCompiledExit.TailCall(pc, context.InstructionsConsumed);
                default:
                    return ExecuteSlowPath(context, thread, frame);
            }
        }

        private static LuaCompiledExit ExecuteSlowPath(
            LuaExecutionContext context,
            LuaThread thread,
            LuaFrame frame)
        {
            LuaCodegenAbiV1.CommitProgramCounter(frame, frame.ProgramCounter);
            return LuaCodegenAbiV1.ExecuteCanonicalInstruction(
                context,
                thread,
                frame,
                frame.ProgramCounter);
        }

        private static bool Reserve(LuaExecutionContext context, int programCounter)
        {
            if (!context.TryReserveInstructions(1))
            {
                return false;
            }

            return true;
        }

        private static LuaCompiledExit BudgetPoll(
            LuaExecutionContext context,
            int programCounter) => LuaCompiledExit.Poll(
                programCounter,
                context.InstructionsConsumed,
                LuaCompiledExitReason.InstructionBudget);

        private static LuaCompiledExit GuardFailure(
            LuaExecutionContext context,
            int programCounter) => LuaCompiledExit.Deopt(
                programCounter,
                context.InstructionsConsumed,
                LuaCompiledExitReason.GuardFailure);
    }

    internal sealed record OptimizedInstruction
    {
        public required int ProgramCounter { get; init; }

        public required LuaJitOptimizationKind Kind { get; init; }

        public int InstructionCount { get; init; } = 1;

        public required string Guard { get; init; }

        public LuaJitValueKinds FirstKinds { get; init; }

        public LuaJitValueKinds SecondKinds { get; init; }

        public bool ExpectedBranchTaken { get; init; }

        public ImmutableArray<LuaJitTableShapeProfile> TableShapes { get; init; } = [];

        public LuaJitCallTargetProfile? CallTarget { get; init; }

        public bool ReusesFixedResultWindow { get; init; }

        public int DestinationRegister { get; init; }

        public LuaValue FoldedValue { get; init; }

        public bool HasGuard => Kind != LuaJitOptimizationKind.DeadMove;

        public LuaJitOptimization ToDescription() => new(
            ProgramCounter,
            Kind,
            InstructionCount,
            Guard);

        public bool MatchesFirst(LuaValue value) => Matches(FirstKinds, value);

        public bool MatchesSecond(LuaValue value) => Matches(SecondKinds, value);

        public static OptimizedInstruction DeadMove(int pc) => new()
        {
            ProgramCounter = pc,
            Kind = LuaJitOptimizationKind.DeadMove,
            Guard = "destination register is dead on every successor",
        };

        public static OptimizedInstruction ConstantFold(
            int pc,
            int destinationRegister,
            LuaValue value) => new()
            {
                ProgramCounter = pc,
                Kind = LuaJitOptimizationKind.ConstantFold,
                InstructionCount = 3,
                Guard = "verified primitive constants and side-effect-free binary operation",
                DestinationRegister = destinationRegister,
                FoldedValue = value,
            };

        public static OptimizedInstruction Unary(int pc, LuaJitValueKinds kinds) => new()
        {
            ProgramCounter = pc,
            Kind = LuaJitOptimizationKind.NumericUnary,
            Guard = $"operand kind in {kinds}",
            FirstKinds = kinds,
        };

        public static OptimizedInstruction Binary(
            int pc,
            LuaJitValueKinds first,
            LuaJitValueKinds second) => new()
            {
                ProgramCounter = pc,
                Kind = LuaJitOptimizationKind.NumericBinary,
                Guard = $"operand kinds in {first} x {second}",
                FirstKinds = first,
                SecondKinds = second,
            };

        public static OptimizedInstruction Branch(int pc, bool taken) => new()
        {
            ProgramCounter = pc,
            Kind = LuaJitOptimizationKind.BooleanBranch,
            Guard = taken ? "branch remains taken" : "branch remains not-taken",
            ExpectedBranchTaken = taken,
        };

        public static OptimizedInstruction Table(
            int pc,
            LuaJitOptimizationKind kind,
            ImmutableArray<LuaJitTableShapeProfile> shapes) => new()
            {
                ProgramCounter = pc,
                Kind = kind,
                Guard = $"table shape matches one of {shapes.Length} PIC entries",
                TableShapes = shapes,
            };

        public static OptimizedInstruction KnownCall(
            int pc,
            LuaJitCallTargetProfile target,
            bool reusesFixedResultWindow) => new()
            {
                ProgramCounter = pc,
                Kind = LuaJitOptimizationKind.KnownClosureCall,
                Guard = $"callee remains {target.ModuleContentId}/{target.FunctionId}",
                CallTarget = target,
                ReusesFixedResultWindow = reusesFixedResultWindow,
            };

        private static bool Matches(LuaJitValueKinds kinds, LuaValue value) => kinds switch
        {
            LuaJitValueKinds.Integer => value.IsInteger,
            LuaJitValueKinds.Float => value.IsFloat,
            _ => (kinds & ToKinds(value)) != 0,
        };

        private static LuaJitValueKinds ToKinds(LuaValue value) =>
            (LuaJitValueKinds)(1 << (int)value.Kind);
    }
}
