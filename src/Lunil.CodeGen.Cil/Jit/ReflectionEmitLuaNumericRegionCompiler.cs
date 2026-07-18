using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Lunil.CodeGen.Cil.Emission;
using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.CodeGen.Cil.Jit;

internal readonly record struct LuaNumericRegionEmissionMode(
    bool RequireLoopOsrEntry,
    bool ObserveLoopOsrBackedge);

internal readonly record struct LuaNumericRegionEmissionMetrics(
    TimeSpan CilEmissionDuration,
    TimeSpan DelegateCreationDuration);

internal sealed record LuaCompiledNumericRegion(
    LuaNumericRegionPlan Plan,
    LuaCompiledMethod Method,
    long EstimatedCodeBytes,
    LuaNumericRegionEmissionMetrics Metrics,
    LuaTier2RuntimeSites RuntimeSites);

internal static class LuaNumericRegionRuntime
{
    public static long Shift(long value, long count, bool left) =>
        LuaCodegenAbiV4.Shift(value, count, left);

    public static double FloatingModulo(double dividend, double divisor) =>
        LuaCodegenAbiV4.FloatingModulo(dividend, divisor);

    public static bool CompareMixed(
        long integerValue,
        double floatingPoint,
        bool integerOnLeft,
        LuaIrBinaryOperator operation) => LuaCodegenAbiV4.CompareMixed(
            integerValue,
            floatingPoint,
            integerOnLeft,
            (int)operation);
}

/// <summary>
/// Emits a verified natural loop as raw CLR numeric locals. The frame is touched only at entry,
/// side exits, deoptimization, and backedge safepoints; canonical instruction accounting is kept
/// in locals and committed atomically at those same boundaries.
/// </summary>
internal static class ReflectionEmitLuaNumericRegionCompiler
{
    // Touching this method runs the type initializer and resolves the shared ABI MethodInfo set
    // before a timed Tier 2 or loop-OSR compilation. Both entry modes use this one emitter.
    internal static void PrepareRuntimeAbi() => _ = CanExecuteCompiledFrame;

    private delegate LuaCompiledExit LuaCompiledNumericRegionMethodWithSites(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        LuaTier2RuntimeSites runtimeSites);

    private const int MaximumBackedgePollInterval = 1024;

    private enum NumericExecutionPath : byte
    {
        HotQuantum,
        ColdSlowTail,
    }

    private readonly record struct NumericDirtyState(
        LuaNumericIlLocal Dirty,
        LuaNumericIlLocal ActiveKind);

    private readonly struct NumericRegionLabelMap(
        ImmutableArray<int> programCounters,
        LuaNumericIlLabel[] labels)
    {
        public LuaNumericIlLabel this[int programCounter] =>
            TryGetValue(programCounter, out var label)
                ? label
                : throw new InvalidOperationException(
                    $"Numeric region has no label for PC {programCounter}.");

        public bool TryGetValue(int programCounter, out LuaNumericIlLabel label)
        {
            var index = programCounters.BinarySearch(programCounter);
            if (index >= 0 && labels[index].Id >= 0)
            {
                label = labels[index];
                return true;
            }

            label = default;
            return false;
        }

        public LuaNumericIlLabel GetValueOrDefault(int programCounter) =>
            TryGetValue(programCounter, out var label) ? label : new(-1);
    }

    private static readonly Type[] CompiledMethodParameters =
    [
        typeof(LuaExecutionContext),
        typeof(LuaThread),
        typeof(LuaFrame),
        typeof(LuaTier2RuntimeSites),
    ];

    private static readonly MethodInfo CanExecuteCompiledFrame = Method(
        typeof(LuaCodegenAbiV2),
        nameof(LuaCodegenAbiV2.CanExecuteCompiledFrame),
        [typeof(LuaExecutionContext), typeof(LuaFrame), typeof(int), typeof(int)]);
    private static readonly MethodInfo CanExecuteKnownClosureValue = Method(
        typeof(LuaCodegenAbiV4),
        nameof(LuaCodegenAbiV4.CanExecuteKnownClosureValue),
        [typeof(LuaValue), typeof(LuaCodegenCallSiteCache), typeof(int)]);
    private static readonly MethodInfo GetCallSite = Method(
        typeof(LuaTier2RuntimeSites),
        nameof(LuaTier2RuntimeSites.GetCallSite),
        [typeof(int), typeof(string)]);
    private static readonly MethodInfo GetTableSite = Method(
        typeof(LuaTier2RuntimeSites),
        nameof(LuaTier2RuntimeSites.GetTableSite),
        [typeof(int)]);
    private static readonly MethodInfo TryGetCompilerProvenIntegerTableValue = Method(
        typeof(LuaCodegenAbiV5),
        nameof(LuaCodegenAbiV5.TryGetCompilerProvenIntegerTableValue),
        [
            typeof(LuaTable).MakeByRefType(),
            typeof(LuaValue),
            typeof(LuaCodegenTableSiteCache),
            typeof(long),
            typeof(LuaValue).MakeByRefType(),
        ]);
    private static readonly MethodInfo TrySetCompilerProvenIntegerTableValue = Method(
        typeof(LuaCodegenAbiV5),
        nameof(LuaCodegenAbiV5.TrySetCompilerProvenIntegerTableValue),
        [
            typeof(LuaTable).MakeByRefType(),
            typeof(LuaValue),
            typeof(LuaCodegenTableSiteCache),
            typeof(long),
            typeof(LuaValue),
        ]);
    private static readonly MethodInfo TrySetCompilerProvenIntegerTableIntegerValue = Method(
        typeof(LuaCodegenAbiV5),
        nameof(LuaCodegenAbiV5.TrySetCompilerProvenIntegerTableIntegerValue),
        [
            typeof(LuaTable).MakeByRefType(),
            typeof(LuaValue),
            typeof(LuaCodegenTableSiteCache),
            typeof(long),
            typeof(long),
        ]);
    private static readonly MethodInfo TrySetCompilerProvenIntegerTableFloatValue = Method(
        typeof(LuaCodegenAbiV5),
        nameof(LuaCodegenAbiV5.TrySetCompilerProvenIntegerTableFloatValue),
        [
            typeof(LuaTable).MakeByRefType(),
            typeof(LuaValue),
            typeof(LuaCodegenTableSiteCache),
            typeof(long),
            typeof(double),
        ]);
    private static readonly MethodInfo TrySetCompilerProvenIntegerTableBooleanValue = Method(
        typeof(LuaCodegenAbiV5),
        nameof(LuaCodegenAbiV5.TrySetCompilerProvenIntegerTableBooleanValue),
        [
            typeof(LuaTable).MakeByRefType(),
            typeof(LuaValue),
            typeof(LuaCodegenTableSiteCache),
            typeof(long),
            typeof(bool),
        ]);
    private static readonly MethodInfo TryGetCompilerProvenStringTableValue = Method(
        typeof(LuaCodegenAbiV5),
        nameof(LuaCodegenAbiV5.TryGetCompilerProvenStringTableValue),
        [
            typeof(LuaTable).MakeByRefType(),
            typeof(LuaValue),
            typeof(LuaCodegenTableSiteCache),
            typeof(LuaCodegenTableRegionSite).MakeByRefType(),
            typeof(LuaValue),
            typeof(LuaValue).MakeByRefType(),
        ]);
    private static readonly MethodInfo TrySetCompilerProvenStringTableValue = Method(
        typeof(LuaCodegenAbiV5),
        nameof(LuaCodegenAbiV5.TrySetCompilerProvenStringTableValue),
        [
            typeof(LuaTable).MakeByRefType(),
            typeof(LuaValue),
            typeof(LuaCodegenTableSiteCache),
            typeof(LuaCodegenTableRegionSite).MakeByRefType(),
            typeof(LuaValue),
            typeof(LuaValue),
        ]);
    private static readonly MethodInfo TrySetCompilerProvenStringTableIntegerValue = Method(
        typeof(LuaCodegenAbiV5),
        nameof(LuaCodegenAbiV5.TrySetCompilerProvenStringTableIntegerValue),
        [
            typeof(LuaTable).MakeByRefType(),
            typeof(LuaValue),
            typeof(LuaCodegenTableSiteCache),
            typeof(LuaCodegenTableRegionSite).MakeByRefType(),
            typeof(LuaValue),
            typeof(long),
        ]);
    private static readonly MethodInfo TrySetCompilerProvenStringTableFloatValue = Method(
        typeof(LuaCodegenAbiV5),
        nameof(LuaCodegenAbiV5.TrySetCompilerProvenStringTableFloatValue),
        [
            typeof(LuaTable).MakeByRefType(),
            typeof(LuaValue),
            typeof(LuaCodegenTableSiteCache),
            typeof(LuaCodegenTableRegionSite).MakeByRefType(),
            typeof(LuaValue),
            typeof(double),
        ]);
    private static readonly MethodInfo TrySetCompilerProvenStringTableBooleanValue = Method(
        typeof(LuaCodegenAbiV5),
        nameof(LuaCodegenAbiV5.TrySetCompilerProvenStringTableBooleanValue),
        [
            typeof(LuaTable).MakeByRefType(),
            typeof(LuaValue),
            typeof(LuaCodegenTableSiteCache),
            typeof(LuaCodegenTableRegionSite).MakeByRefType(),
            typeof(LuaValue),
            typeof(bool),
        ]);
    private static readonly MethodInfo RecordInlineDirectCallCompletion = Method(
        typeof(LuaTier2RuntimeSites),
        nameof(LuaTier2RuntimeSites.RecordInlineDirectCallCompletion),
        []);
    private static readonly MethodInfo RecordInlineDirectCallFallback = Method(
        typeof(LuaTier2RuntimeSites),
        nameof(LuaTier2RuntimeSites.RecordInlineDirectCallFallback),
        []);
    private static readonly MethodInfo CanEnterLoopOsr = Method(
        typeof(LuaCodegenAbiV2),
        nameof(LuaCodegenAbiV2.CanEnterLoopOsr),
        [
            typeof(LuaExecutionContext),
            typeof(LuaThread),
            typeof(LuaFrame),
            typeof(int),
            typeof(int),
            typeof(int),
        ]);
    private static readonly MethodInfo CheckLoopHeader = Method(
        typeof(LuaCodegenAbiV2),
        nameof(LuaCodegenAbiV2.CheckLoopOsrHeader),
        [typeof(LuaExecutionContext), typeof(LuaThread), typeof(LuaFrame)]);
    private static readonly MethodInfo ReadRegister = Method(
        typeof(LuaCodegenAbiV2),
        nameof(LuaCodegenAbiV2.ReadRegisterUnchecked),
        [typeof(LuaThread), typeof(LuaFrame), typeof(int)]);
    private static readonly MethodInfo MaterializeConstant = Method(
        typeof(LuaCodegenAbiV1),
        nameof(LuaCodegenAbiV1.MaterializeConstant),
        [typeof(LuaExecutionContext), typeof(LuaFrame), typeof(int)]);
    private static readonly MethodInfo WriteRegister = Method(
        typeof(LuaCodegenAbiV2),
        nameof(LuaCodegenAbiV2.WriteRegisterUnchecked),
        [typeof(LuaThread), typeof(LuaFrame), typeof(int), typeof(LuaValue)]);
    private static readonly MethodInfo SetFrameTop = Method(
        typeof(LuaCodegenAbiV2),
        nameof(LuaCodegenAbiV2.SetFrameTopUnchecked),
        [typeof(LuaThread), typeof(LuaFrame), typeof(int)]);
    private static readonly MethodInfo ObserveLoopOsrBackedges = Method(
        typeof(LuaCodegenAbiV1),
        nameof(LuaCodegenAbiV1.ObserveLoopOsrBackedges),
        [typeof(LuaExecutionContext), typeof(LuaFrame), typeof(int), typeof(int)]);
    private static readonly MethodInfo TryReserveInstructions = Method(
        typeof(LuaExecutionContext),
        nameof(LuaExecutionContext.TryReserveInstructions),
        [typeof(int)]);
    private static readonly MethodInfo GetRemainingInstructionCount = PropertyGetter(
        typeof(LuaExecutionContext),
        nameof(LuaExecutionContext.RemainingInstructionCount));
    private static readonly MethodInfo GetInstructionsConsumed = Method(
        typeof(LuaCodegenAbiV4),
        nameof(LuaCodegenAbiV4.GetInstructionsConsumed),
        [typeof(LuaExecutionContext)]);
    private static readonly MethodInfo GetProgramCounter = PropertyGetter(
        typeof(LuaFrame),
        nameof(LuaFrame.ProgramCounter));
    private static readonly MethodInfo SetProgramCounter = Method(
        typeof(LuaCodegenAbiV4),
        nameof(LuaCodegenAbiV4.SetProgramCounter),
        [typeof(LuaFrame), typeof(int)]);
    private static readonly MethodInfo GetKind = PropertyGetter(
        typeof(LuaValue),
        nameof(LuaValue.Kind));
    private static readonly MethodInfo AsInteger = Method(
        typeof(LuaValue),
        nameof(LuaValue.AsInteger),
        []);
    private static readonly MethodInfo AsFloat = Method(
        typeof(LuaValue),
        nameof(LuaValue.AsFloat),
        []);
    private static readonly MethodInfo AsBoolean = Method(
        typeof(LuaValue),
        nameof(LuaValue.AsBoolean),
        []);
    private static readonly MethodInfo FromInteger = Method(
        typeof(LuaValue),
        nameof(LuaValue.FromInteger),
        [typeof(long)]);
    private static readonly MethodInfo FromFloat = Method(
        typeof(LuaValue),
        nameof(LuaValue.FromFloat),
        [typeof(double)]);
    private static readonly MethodInfo FromBoolean = Method(
        typeof(LuaValue),
        nameof(LuaValue.FromBoolean),
        [typeof(bool)]);
    private static readonly MethodInfo MathFloor = Method(
        typeof(Math),
        nameof(Math.Floor),
        [typeof(double)]);
    private static readonly MethodInfo MathPow = Method(
        typeof(Math),
        nameof(Math.Pow),
        [typeof(double), typeof(double)]);
    private static readonly MethodInfo Shift = Method(
        typeof(LuaCodegenAbiV4),
        nameof(LuaCodegenAbiV4.Shift),
        [typeof(long), typeof(long), typeof(bool)]);
    private static readonly MethodInfo FloatingModulo = Method(
        typeof(LuaCodegenAbiV4),
        nameof(LuaCodegenAbiV4.FloatingModulo),
        [typeof(double), typeof(double)]);
    private static readonly MethodInfo CompareMixed = Method(
        typeof(LuaCodegenAbiV4),
        nameof(LuaCodegenAbiV4.CompareMixed),
        [typeof(long), typeof(double), typeof(bool), typeof(int)]);
    private static readonly MethodInfo ContinueExit = Method(
        typeof(LuaCompiledExit),
        nameof(LuaCompiledExit.Continue),
        [typeof(int), typeof(long)]);
    private static readonly MethodInfo PollExit = Method(
        typeof(LuaCompiledExit),
        nameof(LuaCompiledExit.Poll),
        [typeof(int), typeof(long), typeof(LuaCompiledExitReason)]);
    private static readonly MethodInfo DeoptExit = Method(
        typeof(LuaCompiledExit),
        nameof(LuaCompiledExit.Deopt),
        [typeof(int), typeof(long), typeof(LuaCompiledExitReason)]);
    private static readonly ConstructorInfo InvalidOperationExceptionConstructor =
        typeof(InvalidOperationException).GetConstructor([typeof(string)]) ??
        throw new MissingMethodException(
            typeof(InvalidOperationException).FullName,
            ".ctor(string)");

    [RequiresDynamicCode("Linear numeric regions require Reflection.Emit support.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "The caller checks dynamic-code support before requesting compilation.")]
    public static bool TryCompile(
        LuaIrFunction function,
        LuaNumericRegionPlan plan,
        LuaNumericRegionEmissionMode mode,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out LuaCompiledNumericRegion? result) => TryCompile(
            function,
            plan,
            mode,
            boundDirectCalls: null,
            cancellationToken,
            out result);

    public static bool TryCompile(
        LuaIrFunction function,
        LuaNumericRegionPlan plan,
        LuaNumericRegionEmissionMode mode,
        IReadOnlyDictionary<int, LuaBoundDirectCall>? boundDirectCalls,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out LuaCompiledNumericRegion? result)
    {
        result = null;
        if (!RuntimeFeature.IsDynamicCodeSupported || plan.Registers.IsEmpty)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var emissionStarted = Stopwatch.GetTimestamp();
        var dynamicMethod = new DynamicMethod(
            $"lunil_numeric_region_f{function.Id}_h{plan.Region.HeaderProgramCounter}",
            typeof(LuaCompiledExit),
            CompiledMethodParameters,
            typeof(ReflectionEmitLuaNumericRegionCompiler).Module,
            skipVisibility: true);
        var generator = new ReflectionEmitLuaNumericRegionIlGenerator(
            dynamicMethod.GetILGenerator());
        Emit(function, plan, mode, generator, boundDirectCalls, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var cilEmissionDuration = Stopwatch.GetElapsedTime(emissionStarted);
        var delegateStarted = Stopwatch.GetTimestamp();
        var compiledWithSites = (LuaCompiledNumericRegionMethodWithSites)dynamicMethod.CreateDelegate(
            typeof(LuaCompiledNumericRegionMethodWithSites));
        var runtimeSites = new LuaTier2RuntimeSites(
            function.Instructions.Length,
            boundDirectCalls);
        LuaCompiledMethod method = (context, thread, frame) =>
            compiledWithSites(context, thread, frame, runtimeSites);
        var delegateCreationDuration = Stopwatch.GetElapsedTime(delegateStarted);
        var estimatedCodeBytes = checked(
            plan.Region.ProgramCounters.Length * 48L +
            plan.Registers.Length * 96L +
            plan.DirectNumericInstructionCount * 48L +
            plan.BackedgeProgramCounters.Length * 96L);
        result = new LuaCompiledNumericRegion(
            plan,
            method,
            estimatedCodeBytes,
            new LuaNumericRegionEmissionMetrics(
                cilEmissionDuration,
                delegateCreationDuration),
            runtimeSites);
        return true;
    }

    internal static void Emit(
        LuaIrFunction function,
        LuaNumericRegionPlan plan,
        LuaNumericRegionEmissionMode mode,
        LuaNumericRegionIlGenerator generator,
        IReadOnlyDictionary<int, LuaBoundDirectCall>? boundDirectCalls,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(generator);
        cancellationToken.ThrowIfCancellationRequested();
        var valueLocals = plan.Registers.ToDictionary(
            static register => (register.Register, register.Kind),
            register => generator.DeclareLocal(LocalType(register.Kind)));
        var dirtyLocals = plan.Registers
            .Select(static register => register.Register)
            .Distinct()
            .ToDictionary(
            static register => register,
            _ => new NumericDirtyState(
                generator.DeclareLocal(typeof(bool)),
                generator.DeclareLocal(typeof(int))));
        var tableSiteLocals = plan.TableSites.ToDictionary(
            static site => site.ProgramCounter,
            _ => generator.DeclareLocal(typeof(LuaCodegenTableSiteCache)));
        var tableRegionSiteLocals = plan.TableSites.ToDictionary(
            static site => site.ProgramCounter,
            _ => generator.DeclareLocal(typeof(LuaCodegenTableRegionSite)));
        var tableDefinitionLocals = plan.TableSites
            .Select(static site => site.TableDefinitionProgramCounter)
            .Distinct()
            .ToDictionary(
                static programCounter => programCounter,
                _ => generator.DeclareLocal(typeof(LuaTable)));
        var taggedConstantLocals = plan.Region.ProgramCounters
            .Select(pc => function.Instructions[pc])
            .Where(instruction => instruction.Opcode == LuaIrOpcode.LoadConstant &&
                function.Constants[instruction.B].Kind == LuaIrConstantKind.String)
            .Select(static instruction => instruction.B)
            .Distinct()
            .ToDictionary(
                static constant => constant,
                _ => generator.DeclareLocal(typeof(LuaValue)));
        var taggedValue = generator.DeclareLocal(typeof(LuaValue));
        var remaining = generator.DeclareLocal(typeof(long));
        var pending = generator.DeclareLocal(typeof(int));
        var boundaryProgramCounter = generator.DeclareLocal(typeof(int));
        var backedgeCountdown = generator.DeclareLocal(typeof(int));
        var observedBackedges = mode.ObserveLoopOsrBackedge
            ? plan.BackedgeProgramCounters.ToDictionary(
                static pc => pc,
                _ => generator.DeclareLocal(typeof(int)))
            : [];
        var minimumTop = generator.DeclareLocal(typeof(int));
        var desiredTop = generator.DeclareLocal(typeof(int));
        var topDirty = generator.DeclareLocal(typeof(bool));
        var headerReason = generator.DeclareLocal(typeof(LuaCompiledExitReason));
        var integerTemporary = generator.DeclareLocal(typeof(long));
        var integerRemainder = generator.DeclareLocal(typeof(long));
        var floatingTemporary = generator.DeclareLocal(typeof(double));
        var hotBodyLabels = DefineLabels(generator, plan.Region.ProgramCounters);
        var hotChargeLabels = DefineLabels(generator, plan.Region.ProgramCounters);
        var coldSlowTailLabels = DefineLabels(generator, plan.Region.ProgramCounters);
        var resumeLabels = DefineLabels(generator, plan.Region.ProgramCounters);
        var entryLabels = DefineLabels(generator, plan.Region.ProgramCounters);
        var budgetLabels = DefineLabels(generator, plan.Region.ProgramCounters);
        var guardLabels = DefineLabels(generator, plan.Region.ProgramCounters);
        bool RequiresSemanticDeoptimization(int pc) =>
            function.Instructions[pc].Opcode is LuaIrOpcode.Binary or
                LuaIrOpcode.GetTable or LuaIrOpcode.SetTable or LuaIrOpcode.Call;
        bool IsBoundDirectCall(int pc) =>
            function.Instructions[pc].Opcode == LuaIrOpcode.Call &&
            boundDirectCalls?.ContainsKey(pc) == true;
        var hotSemanticDeoptLabels = DefineLabels(
            generator,
            plan.Region.ProgramCounters,
            RequiresSemanticDeoptimization);
        var coldSemanticDeoptLabels = DefineLabels(
            generator,
            plan.Region.ProgramCounters,
            RequiresSemanticDeoptimization);
        var hotDirectBudgetLabels = DefineLabels(
            generator,
            plan.Region.ProgramCounters,
            IsBoundDirectCall);
        var coldDirectBudgetLabels = DefineLabels(
            generator,
            plan.Region.ProgramCounters,
            IsBoundDirectCall);
        var hotDirectSafepointLabels = DefineLabels(
            generator,
            plan.Region.ProgramCounters,
            IsBoundDirectCall);
        var coldDirectSafepointLabels = DefineLabels(
            generator,
            plan.Region.ProgramCounters,
            IsBoundDirectCall);
        var budgetBoundary = generator.DefineLabel();
        var safepointBoundary = generator.DefineLabel();
        var guardBoundary = generator.DefineLabel();
        var invalidatedExit = generator.DefineLabel();
        var maximumBackedgePollInterval = plan.TableSites.IsEmpty
            ? MaximumBackedgePollInterval
            : Math.Min(
                MaximumBackedgePollInterval,
                LuaCodegenAbiV3.CompiledBackedgeSafepointQuantum);
        var backedgePollInterval = Math.Max(
            1,
            Math.Min(
                maximumBackedgePollInterval,
                int.MaxValue / Math.Max(1, plan.Region.ProgramCounters.Length)));

        EmitEntryGuard(generator, function, plan, mode, invalidatedExit);
        generator.Emit(OpCodes.Ldc_I4, int.MaxValue);
        generator.Emit(OpCodes.Stloc, minimumTop);
        EmitInt32(generator, backedgePollInterval);
        generator.Emit(OpCodes.Stloc, backedgeCountdown);
        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Callvirt, GetRemainingInstructionCount);
        generator.Emit(OpCodes.Stloc, remaining);
        foreach (var (programCounter, tableSiteLocal) in tableSiteLocals)
        {
            generator.Emit(OpCodes.Ldarg_3);
            EmitInt32(generator, programCounter);
            generator.Emit(OpCodes.Callvirt, GetTableSite);
            generator.Emit(OpCodes.Stloc, tableSiteLocal);
        }

        foreach (var (constant, constantLocal) in taggedConstantLocals)
        {
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldarg_2);
            EmitInt32(generator, constant);
            generator.Emit(OpCodes.Call, MaterializeConstant);
            generator.Emit(OpCodes.Stloc, constantLocal);
        }

        generator.Emit(OpCodes.Ldarg_2);
        generator.Emit(OpCodes.Callvirt, GetProgramCounter);
        EmitSwitch(generator, function.Instructions.Length, entryLabels, invalidatedExit);

        foreach (var pc in plan.Region.ProgramCounters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            generator.MarkLabel(entryLabels[pc]);
            generator.Emit(OpCodes.Ldloc, remaining);
            generator.Emit(OpCodes.Brfalse, budgetLabels[pc]);
            foreach (var register in plan.Region.Liveness.LiveBefore[pc])
            {
                var kind = plan.GetKindBefore(pc, register);
                if (!valueLocals.TryGetValue((register, kind), out var local))
                {
                    continue;
                }

                EmitLoadAndGuardRegister(
                    generator,
                    new LuaNumericRegionRegister(register, kind),
                    local,
                    taggedValue,
                    guardLabels[pc]);
            }

            generator.Emit(OpCodes.Br, resumeLabels[pc]);
        }

        foreach (var pc in plan.Region.ProgramCounters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            generator.MarkLabel(resumeLabels[pc]);
            EmitQuantumDecision(
                generator,
                plan,
                pc,
                remaining,
                backedgeCountdown,
                hotChargeLabels[pc],
                coldSlowTailLabels[pc]);

            generator.MarkLabel(hotChargeLabels[pc]);
            EmitAddPendingInstructions(
                generator,
                pending,
                plan.GetBudgetSite(pc).RemainingBasicBlockInstructionCost);
            generator.Emit(OpCodes.Br, hotBodyLabels[pc]);

            generator.MarkLabel(hotBodyLabels[pc]);
            EmitInstruction(
                generator,
                function,
                plan,
                boundDirectCalls,
                mode,
                NumericExecutionPath.HotQuantum,
                pc,
                function.Instructions[pc],
                hotBodyLabels,
                hotChargeLabels,
                resumeLabels,
                valueLocals,
                dirtyLocals,
                tableSiteLocals,
                tableRegionSiteLocals,
                tableDefinitionLocals,
                taggedConstantLocals,
                taggedValue,
                remaining,
                pending,
                backedgeCountdown,
                observedBackedges,
                backedgePollInterval,
                minimumTop,
                desiredTop,
                topDirty,
                headerReason,
                integerTemporary,
                integerRemainder,
                floatingTemporary,
                hotSemanticDeoptLabels.GetValueOrDefault(pc),
                hotDirectBudgetLabels.GetValueOrDefault(pc),
                hotDirectSafepointLabels.GetValueOrDefault(pc),
                cancellationToken);

            generator.MarkLabel(coldSlowTailLabels[pc]);
            EmitLocalInstructionReservation(generator, remaining, pending, budgetLabels[pc]);
            EmitInstruction(
                generator,
                function,
                plan,
                boundDirectCalls,
                mode,
                NumericExecutionPath.ColdSlowTail,
                pc,
                function.Instructions[pc],
                coldSlowTailLabels,
                coldSlowTailLabels,
                resumeLabels,
                valueLocals,
                dirtyLocals,
                tableSiteLocals,
                tableRegionSiteLocals,
                tableDefinitionLocals,
                taggedConstantLocals,
                taggedValue,
                remaining,
                pending,
                backedgeCountdown,
                observedBackedges,
                backedgePollInterval,
                minimumTop,
                desiredTop,
                topDirty,
                headerReason,
                integerTemporary,
                integerRemainder,
                floatingTemporary,
                coldSemanticDeoptLabels.GetValueOrDefault(pc),
                coldDirectBudgetLabels.GetValueOrDefault(pc),
                coldDirectSafepointLabels.GetValueOrDefault(pc),
                cancellationToken);
        }

        foreach (var pc in plan.Region.ProgramCounters)
        {
            generator.MarkLabel(budgetLabels[pc]);
            EmitInt32(generator, pc);
            generator.Emit(OpCodes.Stloc, boundaryProgramCounter);
            generator.Emit(OpCodes.Br, budgetBoundary);

            generator.MarkLabel(guardLabels[pc]);
            EmitInt32(generator, pc);
            generator.Emit(OpCodes.Stloc, boundaryProgramCounter);
            generator.Emit(OpCodes.Br, guardBoundary);

            if (hotSemanticDeoptLabels.TryGetValue(pc, out var hotSemanticDeopt))
            {
                generator.MarkLabel(hotSemanticDeopt);
                EmitSubtractPendingInstructions(
                    generator,
                    pending,
                    plan.GetBudgetSite(pc).FailureInstructionRollbackCount);
                EmitInt32(generator, plan.GetBudgetSite(pc).DeoptimizationProgramCounter);
                generator.Emit(OpCodes.Stloc, boundaryProgramCounter);
                generator.Emit(OpCodes.Br, guardBoundary);
            }

            if (coldSemanticDeoptLabels.TryGetValue(pc, out var coldSemanticDeopt))
            {
                generator.MarkLabel(coldSemanticDeopt);
                EmitCancelLocalInstructionReservation(generator, remaining, pending);
                EmitInt32(generator, plan.GetBudgetSite(pc).DeoptimizationProgramCounter);
                generator.Emit(OpCodes.Stloc, boundaryProgramCounter);
                generator.Emit(OpCodes.Br, guardBoundary);
            }

            if (hotDirectBudgetLabels.TryGetValue(pc, out var hotDirectBudget))
            {
                generator.MarkLabel(hotDirectBudget);
                EmitSubtractPendingInstructions(
                    generator,
                    pending,
                    plan.GetBudgetSite(pc).FailureInstructionRollbackCount);
                EmitInt32(generator, pc);
                generator.Emit(OpCodes.Stloc, boundaryProgramCounter);
                generator.Emit(OpCodes.Br, budgetBoundary);
            }

            if (coldDirectBudgetLabels.TryGetValue(pc, out var coldDirectBudget))
            {
                generator.MarkLabel(coldDirectBudget);
                EmitCancelLocalInstructionReservation(generator, remaining, pending);
                EmitInt32(generator, pc);
                generator.Emit(OpCodes.Stloc, boundaryProgramCounter);
                generator.Emit(OpCodes.Br, budgetBoundary);
            }

            if (hotDirectSafepointLabels.TryGetValue(pc, out var hotDirectSafepoint))
            {
                generator.MarkLabel(hotDirectSafepoint);
                EmitSubtractPendingInstructions(
                    generator,
                    pending,
                    plan.GetBudgetSite(pc).FailureInstructionRollbackCount);
                EmitInt32(generator, pc);
                generator.Emit(OpCodes.Stloc, boundaryProgramCounter);
                generator.Emit(OpCodes.Br, safepointBoundary);
            }

            if (coldDirectSafepointLabels.TryGetValue(pc, out var coldDirectSafepoint))
            {
                generator.MarkLabel(coldDirectSafepoint);
                EmitCancelLocalInstructionReservation(generator, remaining, pending);
                EmitInt32(generator, pc);
                generator.Emit(OpCodes.Stloc, boundaryProgramCounter);
                generator.Emit(OpCodes.Br, safepointBoundary);
            }
        }

        generator.MarkLabel(budgetBoundary);
        EmitBoundaryState(
            generator,
            plan,
            programCounter: 0,
            valueLocals,
            dirtyLocals,
            pending,
            remaining,
            observedBackedges,
            minimumTop,
            desiredTop,
            topDirty,
            boundaryProgramCounter);
        EmitDynamicExit(
            generator,
            PollExit,
            boundaryProgramCounter,
            LuaCompiledExitReason.InstructionBudget);

        generator.MarkLabel(safepointBoundary);
        EmitBoundaryState(
            generator,
            plan,
            programCounter: 0,
            valueLocals,
            dirtyLocals,
            pending,
            remaining,
            observedBackedges,
            minimumTop,
            desiredTop,
            topDirty,
            boundaryProgramCounter);
        EmitDynamicExit(
            generator,
            PollExit,
            boundaryProgramCounter,
            LuaCompiledExitReason.GarbageCollection);

        generator.MarkLabel(guardBoundary);
        EmitBoundaryState(
            generator,
            plan,
            programCounter: 0,
            valueLocals,
            dirtyLocals,
            pending,
            remaining,
            observedBackedges,
            minimumTop,
            desiredTop,
            topDirty,
            boundaryProgramCounter);
        EmitDynamicExit(
            generator,
            DeoptExit,
            boundaryProgramCounter,
            LuaCompiledExitReason.GuardFailure);

        generator.MarkLabel(invalidatedExit);
        generator.Emit(OpCodes.Ldarg_2);
        generator.Emit(OpCodes.Callvirt, GetProgramCounter);
        EmitInstructionsConsumed(generator);
        EmitInt32(generator, (int)LuaCompiledExitReason.BackendInvalidated);
        generator.Emit(OpCodes.Call, DeoptExit);
        generator.Emit(OpCodes.Ret);

    }

    private static void EmitEntryGuard(
        LuaNumericRegionIlGenerator generator,
        LuaIrFunction function,
        LuaNumericRegionPlan plan,
        LuaNumericRegionEmissionMode mode,
        LuaNumericIlLabel invalidatedExit)
    {
        generator.Emit(OpCodes.Ldarg_0);
        if (mode.RequireLoopOsrEntry)
        {
            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Ldarg_2);
            EmitInt32(generator, function.Id);
            EmitInt32(generator, function.RegisterCount);
            EmitInt32(generator, plan.Region.HeaderProgramCounter);
            generator.Emit(OpCodes.Call, CanEnterLoopOsr);
        }
        else
        {
            generator.Emit(OpCodes.Ldarg_2);
            EmitInt32(generator, function.Id);
            EmitInt32(generator, function.RegisterCount);
            generator.Emit(OpCodes.Call, CanExecuteCompiledFrame);
        }

        generator.Emit(OpCodes.Brfalse, invalidatedExit);
    }

    private static void EmitLoadAndGuardRegister(
        LuaNumericRegionIlGenerator generator,
        LuaNumericRegionRegister register,
        LuaNumericIlLocal local,
        LuaNumericIlLocal taggedValue,
        LuaNumericIlLabel guardExit)
    {
        generator.Emit(OpCodes.Ldarg_1);
        generator.Emit(OpCodes.Ldarg_2);
        EmitInt32(generator, register.Register);
        generator.Emit(OpCodes.Call, ReadRegister);
        generator.Emit(OpCodes.Stloc, taggedValue);
        if (register.Kind == LuaNumericRegionValueKind.Tagged)
        {
            generator.Emit(OpCodes.Ldloc, taggedValue);
            generator.Emit(OpCodes.Stloc, local);
            return;
        }

        generator.Emit(OpCodes.Ldloca, taggedValue);
        generator.Emit(OpCodes.Call, GetKind);
        EmitInt32(generator, (int)ValueKind(register.Kind));
        generator.Emit(OpCodes.Bne_Un, guardExit);
        generator.Emit(OpCodes.Ldloca, taggedValue);
        generator.Emit(
            OpCodes.Call,
            register.Kind switch
            {
                LuaNumericRegionValueKind.Integer => AsInteger,
                LuaNumericRegionValueKind.Float => AsFloat,
                LuaNumericRegionValueKind.Boolean => AsBoolean,
                _ => throw new InvalidOperationException(
                    $"Register {register.Register} has no promoted CLR type."),
            });
        generator.Emit(OpCodes.Stloc, local);
    }

    private static void EmitLocalInstructionReservation(
        LuaNumericRegionIlGenerator generator,
        LuaNumericIlLocal remaining,
        LuaNumericIlLocal pending,
        LuaNumericIlLabel budgetExit)
    {
        generator.Emit(OpCodes.Ldloc, remaining);
        generator.Emit(OpCodes.Brfalse, budgetExit);
        generator.Emit(OpCodes.Ldloc, remaining);
        generator.Emit(OpCodes.Ldc_I4_1);
        generator.Emit(OpCodes.Conv_I8);
        generator.Emit(OpCodes.Sub);
        generator.Emit(OpCodes.Stloc, remaining);
        generator.Emit(OpCodes.Ldloc, pending);
        generator.Emit(OpCodes.Ldc_I4_1);
        generator.Emit(OpCodes.Add);
        generator.Emit(OpCodes.Stloc, pending);
    }

    private static void EmitQuantumDecision(
        LuaNumericRegionIlGenerator generator,
        LuaNumericRegionPlan plan,
        int programCounter,
        LuaNumericIlLocal remaining,
        LuaNumericIlLocal backedgeCountdown,
        LuaNumericIlLabel hotQuantum,
        LuaNumericIlLabel coldSlowTail)
    {
        var site = plan.GetBudgetSite(programCounter);
        generator.Emit(OpCodes.Ldloc, remaining);
        EmitInt32(generator, site.MaximumInstructionCostToSafepointOrExit);
        generator.Emit(OpCodes.Conv_I8);
        generator.Emit(OpCodes.Ldloc, backedgeCountdown);
        generator.Emit(OpCodes.Ldc_I4_1);
        generator.Emit(OpCodes.Sub);
        generator.Emit(OpCodes.Conv_I8);
        EmitInt32(generator, plan.MaximumBackedgeSegmentInstructionCost);
        generator.Emit(OpCodes.Conv_I8);
        generator.Emit(OpCodes.Mul);
        generator.Emit(OpCodes.Add);
        generator.Emit(OpCodes.Bge, hotQuantum);
        generator.Emit(OpCodes.Br, coldSlowTail);
    }

    private static void EmitAddPendingInstructions(
        LuaNumericRegionIlGenerator generator,
        LuaNumericIlLocal pending,
        int instructionCount)
    {
        generator.Emit(OpCodes.Ldloc, pending);
        EmitInt32(generator, instructionCount);
        generator.Emit(OpCodes.Add);
        generator.Emit(OpCodes.Stloc, pending);
    }

    private static void EmitSubtractPendingInstructions(
        LuaNumericRegionIlGenerator generator,
        LuaNumericIlLocal pending,
        int instructionCount)
    {
        generator.Emit(OpCodes.Ldloc, pending);
        EmitInt32(generator, instructionCount);
        generator.Emit(OpCodes.Sub);
        generator.Emit(OpCodes.Stloc, pending);
    }

    private static void EmitCancelLocalInstructionReservation(
        LuaNumericRegionIlGenerator generator,
        LuaNumericIlLocal remaining,
        LuaNumericIlLocal pending)
    {
        generator.Emit(OpCodes.Ldloc, remaining);
        generator.Emit(OpCodes.Ldc_I4_1);
        generator.Emit(OpCodes.Conv_I8);
        generator.Emit(OpCodes.Add);
        generator.Emit(OpCodes.Stloc, remaining);
        generator.Emit(OpCodes.Ldloc, pending);
        generator.Emit(OpCodes.Ldc_I4_1);
        generator.Emit(OpCodes.Sub);
        generator.Emit(OpCodes.Stloc, pending);
    }

    private static void EmitSetTop(
        LuaNumericRegionIlGenerator generator,
        int registerCount,
        LuaNumericIlLocal minimumTop,
        LuaNumericIlLocal desiredTop,
        LuaNumericIlLocal topDirty,
        Dictionary<int, NumericDirtyState> dirtyLocals)
    {
        var updateMinimum = generator.DefineLabel();
        var minimumReady = generator.DefineLabel();
        generator.Emit(OpCodes.Ldloc, topDirty);
        generator.Emit(OpCodes.Brfalse, updateMinimum);
        generator.Emit(OpCodes.Ldloc, minimumTop);
        EmitInt32(generator, registerCount);
        generator.Emit(OpCodes.Ble, minimumReady);
        generator.MarkLabel(updateMinimum);
        EmitInt32(generator, registerCount);
        generator.Emit(OpCodes.Stloc, minimumTop);
        generator.MarkLabel(minimumReady);
        EmitInt32(generator, registerCount);
        generator.Emit(OpCodes.Stloc, desiredTop);
        EmitSetDirty(generator, topDirty);

        foreach (var (register, state) in dirtyLocals)
        {
            if (register < registerCount)
            {
                continue;
            }

            generator.Emit(OpCodes.Ldc_I4_0);
            generator.Emit(OpCodes.Stloc, state.Dirty);
        }
    }

    private static void EmitInstruction(
        LuaNumericRegionIlGenerator generator,
        LuaIrFunction function,
        LuaNumericRegionPlan plan,
        IReadOnlyDictionary<int, LuaBoundDirectCall>? boundDirectCalls,
        LuaNumericRegionEmissionMode mode,
        NumericExecutionPath executionPath,
        int pc,
        LuaIrInstruction instruction,
        NumericRegionLabelMap bodyLabels,
        NumericRegionLabelMap blockEntryLabels,
        NumericRegionLabelMap resumeLabels,
        Dictionary<(int Register, LuaNumericRegionValueKind Kind), LuaNumericIlLocal> valueLocals,
        Dictionary<int, NumericDirtyState> dirtyLocals,
        Dictionary<int, LuaNumericIlLocal> tableSiteLocals,
        Dictionary<int, LuaNumericIlLocal> tableRegionSiteLocals,
        Dictionary<int, LuaNumericIlLocal> tableDefinitionLocals,
        Dictionary<int, LuaNumericIlLocal> taggedConstantLocals,
        LuaNumericIlLocal taggedValue,
        LuaNumericIlLocal remaining,
        LuaNumericIlLocal pending,
        LuaNumericIlLocal backedgeCountdown,
        Dictionary<int, LuaNumericIlLocal> observedBackedges,
        int backedgePollInterval,
        LuaNumericIlLocal minimumTop,
        LuaNumericIlLocal desiredTop,
        LuaNumericIlLocal topDirty,
        LuaNumericIlLocal headerReason,
        LuaNumericIlLocal integerTemporary,
        LuaNumericIlLocal integerRemainder,
        LuaNumericIlLocal floatingTemporary,
        LuaNumericIlLabel semanticDeopt,
        LuaNumericIlLabel directBudgetFallback,
        LuaNumericIlLabel directSafepointFallback,
        CancellationToken cancellationToken)
    {
        switch (instruction.Opcode)
        {
            case LuaIrOpcode.LoadConstant:
                var constantKind = plan.GetKindAfter(pc, instruction.A);
                var constantDestination = NumericLocal(
                    valueLocals,
                    instruction.A,
                    constantKind);
                if (taggedConstantLocals.TryGetValue(
                        instruction.B,
                        out var taggedConstantLocal))
                {
                    generator.Emit(OpCodes.Ldloc, taggedConstantLocal);
                    generator.Emit(OpCodes.Stloc, constantDestination);
                }
                else
                {
                    EmitConstant(
                        generator,
                        function.Constants[instruction.B],
                        constantDestination);
                }
                EmitMarkDirty(generator, dirtyLocals[instruction.A], constantKind);
                EmitTransfer(
                    generator,
                    plan,
                    mode,
                    executionPath,
                    pc,
                    pc + 1,
                    bodyLabels,
                    blockEntryLabels,
                    resumeLabels,
                    valueLocals,
                    dirtyLocals,
                    remaining,
                    pending,
                    backedgeCountdown,
                    observedBackedges,
                    backedgePollInterval,
                    minimumTop,
                    desiredTop,
                    topDirty,
                    headerReason);
                break;
            case LuaIrOpcode.Move:
                var moveKind = plan.GetKindBefore(pc, instruction.B);
                generator.Emit(
                    OpCodes.Ldloc,
                    NumericLocal(valueLocals, instruction.B, moveKind));
                generator.Emit(
                    OpCodes.Stloc,
                    NumericLocal(valueLocals, instruction.A, plan.GetKindAfter(pc, instruction.A)));
                EmitMarkDirty(
                    generator,
                    dirtyLocals[instruction.A],
                    plan.GetKindAfter(pc, instruction.A));
                EmitTransfer(
                    generator,
                    plan,
                    mode,
                    executionPath,
                    pc,
                    pc + 1,
                    bodyLabels,
                    blockEntryLabels,
                    resumeLabels,
                    valueLocals,
                    dirtyLocals,
                    remaining,
                    pending,
                    backedgeCountdown,
                    observedBackedges,
                    backedgePollInterval,
                    minimumTop,
                    desiredTop,
                    topDirty,
                    headerReason);
                break;
            case LuaIrOpcode.SetTop:
                EmitSetTop(
                    generator,
                    instruction.A,
                    minimumTop,
                    desiredTop,
                    topDirty,
                    dirtyLocals);
                EmitTransfer(
                    generator,
                    plan,
                    mode,
                    executionPath,
                    pc,
                    pc + 1,
                    bodyLabels,
                    blockEntryLabels,
                    resumeLabels,
                    valueLocals,
                    dirtyLocals,
                    remaining,
                    pending,
                    backedgeCountdown,
                    observedBackedges,
                    backedgePollInterval,
                    minimumTop,
                    desiredTop,
                    topDirty,
                    headerReason);
                break;
            case LuaIrOpcode.Unary:
                EmitUnary(generator, plan, pc, instruction, valueLocals);
                EmitMarkDirty(
                    generator,
                    dirtyLocals[instruction.A],
                    plan.GetKindAfter(pc, instruction.A));
                EmitTransfer(
                    generator,
                    plan,
                    mode,
                    executionPath,
                    pc,
                    pc + 1,
                    bodyLabels,
                    blockEntryLabels,
                    resumeLabels,
                    valueLocals,
                    dirtyLocals,
                    remaining,
                    pending,
                    backedgeCountdown,
                    observedBackedges,
                    backedgePollInterval,
                    minimumTop,
                    desiredTop,
                    topDirty,
                    headerReason);
                break;
            case LuaIrOpcode.Binary:
                EmitBinary(
                    generator,
                    plan,
                    pc,
                    instruction,
                    valueLocals,
                    integerTemporary,
                    integerRemainder,
                    semanticDeopt);
                EmitMarkDirty(
                    generator,
                    dirtyLocals[instruction.A],
                    plan.GetKindAfter(pc, instruction.A));
                EmitTransfer(
                    generator,
                    plan,
                    mode,
                    executionPath,
                    pc,
                    pc + 1,
                    bodyLabels,
                    blockEntryLabels,
                    resumeLabels,
                    valueLocals,
                    dirtyLocals,
                    remaining,
                    pending,
                    backedgeCountdown,
                    observedBackedges,
                    backedgePollInterval,
                    minimumTop,
                    desiredTop,
                    topDirty,
                    headerReason);
                break;
            case LuaIrOpcode.Jump:
                EmitTransfer(
                    generator,
                    plan,
                    mode,
                    executionPath,
                    pc,
                    instruction.B,
                    bodyLabels,
                    blockEntryLabels,
                    resumeLabels,
                    valueLocals,
                    dirtyLocals,
                    remaining,
                    pending,
                    backedgeCountdown,
                    observedBackedges,
                    backedgePollInterval,
                    minimumTop,
                    desiredTop,
                    topDirty,
                    headerReason);
                break;
            case LuaIrOpcode.JumpIfFalse:
            case LuaIrOpcode.JumpIfTrue:
                EmitConditionalTransfer(
                    generator,
                    plan,
                    mode,
                    executionPath,
                    pc,
                    instruction,
                    bodyLabels,
                    blockEntryLabels,
                    resumeLabels,
                    valueLocals,
                    dirtyLocals,
                    remaining,
                    pending,
                    backedgeCountdown,
                    observedBackedges,
                    backedgePollInterval,
                    minimumTop,
                    desiredTop,
                    topDirty,
                    headerReason);
                break;
            case LuaIrOpcode.NumericForLoop:
                EmitNumericForLoop(
                    generator,
                    plan,
                    mode,
                    executionPath,
                    pc,
                    instruction,
                    bodyLabels,
                    blockEntryLabels,
                    resumeLabels,
                    valueLocals,
                    dirtyLocals,
                    remaining,
                    pending,
                    backedgeCountdown,
                    observedBackedges,
                    backedgePollInterval,
                    minimumTop,
                    desiredTop,
                    topDirty,
                    headerReason,
                    floatingTemporary);
                break;
            case LuaIrOpcode.GetTable:
            case LuaIrOpcode.SetTable:
                if (!plan.TryGetTableSite(pc, out var tableSite))
                {
                    throw new InvalidOperationException(
                        $"Numeric-region table PC {pc} has no compiler-proven table site.");
                }

                EmitIntegerTableOperation(
                    generator,
                    plan,
                    pc,
                    instruction,
                    tableSite,
                    valueLocals,
                    tableSiteLocals[pc],
                    tableRegionSiteLocals[pc],
                    tableDefinitionLocals[tableSite.TableDefinitionProgramCounter],
                    taggedValue,
                    semanticDeopt);
                if (instruction.Opcode == LuaIrOpcode.GetTable)
                {
                    EmitMarkDirty(
                        generator,
                        dirtyLocals[instruction.A],
                        plan.GetKindAfter(pc, instruction.A));
                }

                EmitTransfer(
                    generator,
                    plan,
                    mode,
                    executionPath,
                    pc,
                    pc + 1,
                    bodyLabels,
                    blockEntryLabels,
                    resumeLabels,
                    valueLocals,
                    dirtyLocals,
                    remaining,
                    pending,
                    backedgeCountdown,
                    observedBackedges,
                    backedgePollInterval,
                    minimumTop,
                    desiredTop,
                    topDirty,
                    headerReason);
                break;
            case LuaIrOpcode.Call:
                if (boundDirectCalls is null ||
                    !boundDirectCalls.TryGetValue(pc, out var directCall))
                {
                    throw new InvalidOperationException(
                        $"Numeric-region call PC {pc} has no bound direct target.");
                }

                EmitNumericDirectCall(
                    generator,
                    plan,
                    mode,
                    executionPath,
                    pc,
                    instruction,
                    directCall,
                    bodyLabels,
                    blockEntryLabels,
                    resumeLabels,
                    valueLocals,
                    dirtyLocals,
                    remaining,
                    pending,
                    backedgeCountdown,
                    observedBackedges,
                    backedgePollInterval,
                    minimumTop,
                    desiredTop,
                    topDirty,
                    headerReason,
                    semanticDeopt,
                    directBudgetFallback,
                    directSafepointFallback,
                    cancellationToken);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported numeric-region instruction {instruction.Opcode} at PC {pc}.");
        }
    }

    private static void EmitIntegerTableOperation(
        LuaNumericRegionIlGenerator generator,
        LuaNumericRegionPlan plan,
        int programCounter,
        LuaIrInstruction instruction,
        LuaNumericRegionTableSite tableSite,
        Dictionary<(int Register, LuaNumericRegionValueKind Kind), LuaNumericIlLocal> locals,
        LuaNumericIlLocal siteLocal,
        LuaNumericIlLocal regionSiteLocal,
        LuaNumericIlLocal tableLocal,
        LuaNumericIlLocal taggedValue,
        LuaNumericIlLabel semanticDeopt)
    {
        var isGet = instruction.Opcode == LuaIrOpcode.GetTable;
        if (isGet != (tableSite.Operation == LuaNumericRegionTableOperation.Get))
        {
            throw new InvalidOperationException(
                $"Numeric-region table operation mismatch at PC {programCounter}.");
        }

        var targetRegister = isGet ? instruction.B : instruction.A;
        var keyRegister = isGet ? instruction.C : instruction.B;
        if (isGet && tableSite.ForwardedValueRegister >= 0)
        {
            var forwardedKind = plan.GetKindBefore(
                programCounter,
                tableSite.ForwardedValueRegister);
            EmitLoadTaggedLocal(
                generator,
                NumericLocal(
                    locals,
                    tableSite.ForwardedValueRegister,
                    forwardedKind),
                forwardedKind);
            generator.Emit(OpCodes.Stloc, taggedValue);
            EmitStoreGuardedTaggedValue(
                generator,
                taggedValue,
                NumericLocal(
                    locals,
                    instruction.A,
                    plan.GetKindAfter(programCounter, instruction.A)),
                plan.GetKindAfter(programCounter, instruction.A),
                semanticDeopt);
            return;
        }

        generator.Emit(OpCodes.Ldloca, tableLocal);
        generator.Emit(
            OpCodes.Ldloc,
            NumericLocal(
                locals,
                targetRegister,
                LuaNumericRegionValueKind.Tagged));
        generator.Emit(OpCodes.Ldloc, siteLocal);
        var keyKind = plan.GetKindBefore(programCounter, keyRegister);
        if (keyKind == LuaNumericRegionValueKind.Tagged)
        {
            generator.Emit(OpCodes.Ldloca, regionSiteLocal);
        }

        generator.Emit(
            OpCodes.Ldloc,
            NumericLocal(
                locals,
                keyRegister,
                keyKind));
        if (isGet)
        {
            generator.Emit(OpCodes.Ldloca, taggedValue);
            generator.Emit(
                OpCodes.Call,
                keyKind == LuaNumericRegionValueKind.Integer
                    ? TryGetCompilerProvenIntegerTableValue
                    : TryGetCompilerProvenStringTableValue);
            generator.Emit(OpCodes.Brfalse, semanticDeopt);
            EmitStoreGuardedTaggedValue(
                generator,
                taggedValue,
                NumericLocal(
                    locals,
                    instruction.A,
                    plan.GetKindAfter(programCounter, instruction.A)),
                plan.GetKindAfter(programCounter, instruction.A),
                semanticDeopt);
            return;
        }

        var valueKind = plan.GetKindBefore(programCounter, instruction.C);
        var valueLocal = NumericLocal(locals, instruction.C, valueKind);
        if (valueKind == LuaNumericRegionValueKind.Tagged)
        {
            EmitLoadTaggedLocal(generator, valueLocal, valueKind);
        }
        else
        {
            generator.Emit(OpCodes.Ldloc, valueLocal);
        }

        generator.Emit(
            OpCodes.Call,
            keyKind == LuaNumericRegionValueKind.Integer
                ? valueKind switch
                {
                    LuaNumericRegionValueKind.Integer =>
                        TrySetCompilerProvenIntegerTableIntegerValue,
                    LuaNumericRegionValueKind.Float =>
                        TrySetCompilerProvenIntegerTableFloatValue,
                    LuaNumericRegionValueKind.Boolean =>
                        TrySetCompilerProvenIntegerTableBooleanValue,
                    LuaNumericRegionValueKind.Tagged =>
                        TrySetCompilerProvenIntegerTableValue,
                    _ => throw new InvalidOperationException(
                        $"Numeric-region table value kind {valueKind} cannot be stored."),
                }
                : valueKind switch
                {
                    LuaNumericRegionValueKind.Integer =>
                        TrySetCompilerProvenStringTableIntegerValue,
                    LuaNumericRegionValueKind.Float =>
                        TrySetCompilerProvenStringTableFloatValue,
                    LuaNumericRegionValueKind.Boolean =>
                        TrySetCompilerProvenStringTableBooleanValue,
                    LuaNumericRegionValueKind.Tagged =>
                        TrySetCompilerProvenStringTableValue,
                    _ => throw new InvalidOperationException(
                        $"Numeric-region table value kind {valueKind} cannot be stored."),
                });
        generator.Emit(OpCodes.Brfalse, semanticDeopt);
    }

    private static void EmitLoadTaggedLocal(
        LuaNumericRegionIlGenerator generator,
        LuaNumericIlLocal local,
        LuaNumericRegionValueKind kind)
    {
        generator.Emit(OpCodes.Ldloc, local);
        switch (kind)
        {
            case LuaNumericRegionValueKind.Integer:
                generator.Emit(OpCodes.Call, FromInteger);
                break;
            case LuaNumericRegionValueKind.Float:
                generator.Emit(OpCodes.Call, FromFloat);
                break;
            case LuaNumericRegionValueKind.Boolean:
                generator.Emit(OpCodes.Call, FromBoolean);
                break;
            case LuaNumericRegionValueKind.Tagged:
                break;
            default:
                throw new InvalidOperationException(
                    $"Numeric-region table value kind {kind} cannot be materialized.");
        }
    }

    private static void EmitStoreGuardedTaggedValue(
        LuaNumericRegionIlGenerator generator,
        LuaNumericIlLocal taggedValue,
        LuaNumericIlLocal destination,
        LuaNumericRegionValueKind kind,
        LuaNumericIlLabel semanticDeopt)
    {
        if (kind == LuaNumericRegionValueKind.Tagged)
        {
            generator.Emit(OpCodes.Ldloc, taggedValue);
            generator.Emit(OpCodes.Stloc, destination);
            return;
        }

        generator.Emit(OpCodes.Ldloca, taggedValue);
        generator.Emit(OpCodes.Call, GetKind);
        EmitInt32(generator, (int)ValueKind(kind));
        generator.Emit(OpCodes.Bne_Un, semanticDeopt);
        generator.Emit(OpCodes.Ldloca, taggedValue);
        generator.Emit(
            OpCodes.Call,
            kind switch
            {
                LuaNumericRegionValueKind.Integer => AsInteger,
                LuaNumericRegionValueKind.Float => AsFloat,
                LuaNumericRegionValueKind.Boolean => AsBoolean,
                _ => throw new InvalidOperationException(
                    $"Numeric-region table result kind {kind} cannot be promoted."),
            });
        generator.Emit(OpCodes.Stloc, destination);
    }

    private static void EmitUnary(
        LuaNumericRegionIlGenerator generator,
        LuaNumericRegionPlan plan,
        int pc,
        LuaIrInstruction instruction,
        Dictionary<(int Register, LuaNumericRegionValueKind Kind), LuaNumericIlLocal> locals)
    {
        var operation = (LuaIrUnaryOperator)instruction.C;
        var sourceKind = plan.GetKindBefore(pc, instruction.B);
        var source = NumericLocal(locals, instruction.B, sourceKind);
        var destination = NumericLocal(
            locals,
            instruction.A,
            plan.GetKindAfter(pc, instruction.A));
        switch (operation)
        {
            case LuaIrUnaryOperator.Negate:
                generator.Emit(OpCodes.Ldloc, source);
                generator.Emit(OpCodes.Neg);
                break;
            case LuaIrUnaryOperator.BitwiseNot:
                generator.Emit(OpCodes.Ldloc, source);
                generator.Emit(OpCodes.Not);
                break;
            case LuaIrUnaryOperator.LogicalNot:
                if (sourceKind == LuaNumericRegionValueKind.Boolean)
                {
                    generator.Emit(OpCodes.Ldloc, source);
                    generator.Emit(OpCodes.Ldc_I4_0);
                    generator.Emit(OpCodes.Ceq);
                }
                else
                {
                    generator.Emit(OpCodes.Ldc_I4_0);
                }

                break;
            default:
                throw new InvalidOperationException($"Unsupported numeric unary {operation}.");
        }

        generator.Emit(OpCodes.Stloc, destination);
    }

    private static void EmitNumericDirectCall(
        LuaNumericRegionIlGenerator generator,
        LuaNumericRegionPlan plan,
        LuaNumericRegionEmissionMode mode,
        NumericExecutionPath executionPath,
        int pc,
        LuaIrInstruction instruction,
        LuaBoundDirectCall directCall,
        NumericRegionLabelMap bodyLabels,
        NumericRegionLabelMap blockEntryLabels,
        NumericRegionLabelMap resumeLabels,
        Dictionary<(int Register, LuaNumericRegionValueKind Kind), LuaNumericIlLocal> valueLocals,
        Dictionary<int, NumericDirtyState> dirtyLocals,
        LuaNumericIlLocal remaining,
        LuaNumericIlLocal pending,
        LuaNumericIlLocal backedgeCountdown,
        Dictionary<int, LuaNumericIlLocal> observedBackedges,
        int backedgePollInterval,
        LuaNumericIlLocal minimumTop,
        LuaNumericIlLocal desiredTop,
        LuaNumericIlLocal topDirty,
        LuaNumericIlLocal headerReason,
        LuaNumericIlLabel semanticDeopt,
        LuaNumericIlLabel directBudgetFallback,
        LuaNumericIlLabel directSafepointFallback,
        CancellationToken cancellationToken)
    {
        var fallback = generator.DefineLabel();
        var budgetFallback = generator.DefineLabel();
        var safepointFallback = generator.DefineLabel();
        var completed = generator.DefineLabel();
        generator.Emit(
            OpCodes.Ldloc,
            NumericLocal(
                valueLocals,
                instruction.A,
                LuaNumericRegionValueKind.Tagged));
        generator.Emit(OpCodes.Ldarg_3);
        EmitInt32(generator, pc);
        generator.Emit(OpCodes.Ldstr, directCall.ModuleContentId);
        generator.Emit(OpCodes.Callvirt, GetCallSite);
        EmitInt32(generator, directCall.Function.Id);
        generator.Emit(OpCodes.Call, CanExecuteKnownClosureValue);
        generator.Emit(OpCodes.Brfalse, fallback);

        var arguments = Enumerable.Range(0, instruction.B)
            .Select(index => NumericLocal(
                valueLocals,
                instruction.A + index + 1,
                plan.GetKindBefore(pc, instruction.A + index + 1)))
            .ToArray();
        var results = Enumerable.Range(0, instruction.C)
            .Select(index => NumericLocal(
                valueLocals,
                instruction.A + index,
                plan.GetKindAfter(pc, instruction.A + index)))
            .ToArray();
        ReflectionEmitLuaDirectCallCompiler.EmitNumericRegionInline(
            directCall.Function,
            generator,
            arguments,
            results,
            remaining,
            pending,
            completed,
            fallback,
            budgetFallback,
            safepointFallback,
            cancellationToken);

        generator.MarkLabel(completed);
        generator.Emit(OpCodes.Ldarg_3);
        generator.Emit(OpCodes.Callvirt, RecordInlineDirectCallCompletion);
        for (var index = 0; index < instruction.C; index++)
        {
            EmitMarkDirty(
                generator,
                dirtyLocals[instruction.A + index],
                plan.GetKindAfter(pc, instruction.A + index));
        }

        EmitSetTop(
            generator,
            instruction.A + instruction.C,
            minimumTop,
            desiredTop,
            topDirty,
            dirtyLocals);
        EmitTransfer(
            generator,
            plan,
            mode,
            executionPath,
            pc,
            pc + 1,
            bodyLabels,
            blockEntryLabels,
            resumeLabels,
            valueLocals,
            dirtyLocals,
            remaining,
            pending,
            backedgeCountdown,
            observedBackedges,
            backedgePollInterval,
            minimumTop,
            desiredTop,
            topDirty,
            headerReason);

        generator.MarkLabel(fallback);
        generator.Emit(OpCodes.Ldarg_3);
        generator.Emit(OpCodes.Callvirt, RecordInlineDirectCallFallback);
        generator.Emit(OpCodes.Br, semanticDeopt);

        generator.MarkLabel(budgetFallback);
        generator.Emit(OpCodes.Ldarg_3);
        generator.Emit(OpCodes.Callvirt, RecordInlineDirectCallFallback);
        generator.Emit(OpCodes.Br, directBudgetFallback);

        generator.MarkLabel(safepointFallback);
        generator.Emit(OpCodes.Ldarg_3);
        generator.Emit(OpCodes.Callvirt, RecordInlineDirectCallFallback);
        generator.Emit(OpCodes.Br, directSafepointFallback);
    }

    private static void EmitBinary(
        LuaNumericRegionIlGenerator generator,
        LuaNumericRegionPlan plan,
        int pc,
        LuaIrInstruction instruction,
        Dictionary<(int Register, LuaNumericRegionValueKind Kind), LuaNumericIlLocal> locals,
        LuaNumericIlLocal integerTemporary,
        LuaNumericIlLocal integerRemainder,
        LuaNumericIlLabel semanticDeopt)
    {
        var operation = (LuaIrBinaryOperator)instruction.D;
        var leftKind = plan.GetKindBefore(pc, instruction.B);
        var rightKind = plan.GetKindBefore(pc, instruction.C);
        var left = NumericLocal(locals, instruction.B, leftKind);
        var right = NumericLocal(locals, instruction.C, rightKind);
        var result = NumericLocal(
            locals,
            instruction.A,
            plan.GetKindAfter(pc, instruction.A));
        if (IsComparison(operation))
        {
            EmitComparison(
                generator,
                operation,
                leftKind,
                rightKind,
                left,
                right);
            generator.Emit(OpCodes.Stloc, result);
            return;
        }

        if (leftKind == LuaNumericRegionValueKind.Integer &&
            rightKind == LuaNumericRegionValueKind.Integer)
        {
            switch (operation)
            {
                case LuaIrBinaryOperator.Add:
                case LuaIrBinaryOperator.Subtract:
                case LuaIrBinaryOperator.Multiply:
                case LuaIrBinaryOperator.BitwiseAnd:
                case LuaIrBinaryOperator.BitwiseOr:
                case LuaIrBinaryOperator.BitwiseXor:
                    generator.Emit(OpCodes.Ldloc, left);
                    generator.Emit(OpCodes.Ldloc, right);
                    generator.Emit(
                        operation switch
                        {
                            LuaIrBinaryOperator.Add => OpCodes.Add,
                            LuaIrBinaryOperator.Subtract => OpCodes.Sub,
                            LuaIrBinaryOperator.Multiply => OpCodes.Mul,
                            LuaIrBinaryOperator.BitwiseAnd => OpCodes.And,
                            LuaIrBinaryOperator.BitwiseOr => OpCodes.Or,
                            _ => OpCodes.Xor,
                        });
                    generator.Emit(OpCodes.Stloc, result);
                    return;
                case LuaIrBinaryOperator.Divide:
                    EmitLoadAsDouble(generator, leftKind, left);
                    EmitLoadAsDouble(generator, rightKind, right);
                    generator.Emit(OpCodes.Div);
                    generator.Emit(OpCodes.Stloc, result);
                    return;
                case LuaIrBinaryOperator.FloorDivide:
                case LuaIrBinaryOperator.Modulo:
                    EmitIntegerFloorOperation(
                        generator,
                        operation,
                        left,
                        right,
                        result,
                        integerTemporary,
                        integerRemainder,
                        semanticDeopt);
                    return;
                case LuaIrBinaryOperator.Power:
                    EmitLoadAsDouble(generator, leftKind, left);
                    EmitLoadAsDouble(generator, rightKind, right);
                    generator.Emit(OpCodes.Call, MathPow);
                    generator.Emit(OpCodes.Stloc, result);
                    return;
                case LuaIrBinaryOperator.ShiftLeft:
                case LuaIrBinaryOperator.ShiftRight:
                    generator.Emit(OpCodes.Ldloc, left);
                    generator.Emit(OpCodes.Ldloc, right);
                    generator.Emit(
                        operation == LuaIrBinaryOperator.ShiftLeft
                            ? OpCodes.Ldc_I4_1
                            : OpCodes.Ldc_I4_0);
                    generator.Emit(OpCodes.Call, Shift);
                    generator.Emit(OpCodes.Stloc, result);
                    return;
            }
        }

        EmitLoadAsDouble(generator, leftKind, left);
        EmitLoadAsDouble(generator, rightKind, right);
        switch (operation)
        {
            case LuaIrBinaryOperator.Add:
                generator.Emit(OpCodes.Add);
                break;
            case LuaIrBinaryOperator.Subtract:
                generator.Emit(OpCodes.Sub);
                break;
            case LuaIrBinaryOperator.Multiply:
                generator.Emit(OpCodes.Mul);
                break;
            case LuaIrBinaryOperator.Divide:
                generator.Emit(OpCodes.Div);
                break;
            case LuaIrBinaryOperator.FloorDivide:
                generator.Emit(OpCodes.Div);
                generator.Emit(OpCodes.Call, MathFloor);
                break;
            case LuaIrBinaryOperator.Modulo:
                generator.Emit(OpCodes.Call, FloatingModulo);
                break;
            case LuaIrBinaryOperator.Power:
                generator.Emit(OpCodes.Call, MathPow);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported floating numeric operation {operation}.");
        }

        generator.Emit(OpCodes.Stloc, result);
    }

    private static void EmitIntegerFloorOperation(
        LuaNumericRegionIlGenerator generator,
        LuaIrBinaryOperator operation,
        LuaNumericIlLocal dividend,
        LuaNumericIlLocal divisor,
        LuaNumericIlLocal result,
        LuaNumericIlLocal quotient,
        LuaNumericIlLocal remainder,
        LuaNumericIlLabel semanticDeopt)
    {
        var nonZero = generator.DefineLabel();
        var notNegativeOne = generator.DefineLabel();
        var adjust = generator.DefineLabel();
        var write = generator.DefineLabel();
        generator.Emit(OpCodes.Ldloc, divisor);
        generator.Emit(OpCodes.Brtrue, nonZero);
        generator.Emit(OpCodes.Br, semanticDeopt);
        generator.MarkLabel(nonZero);
        generator.Emit(OpCodes.Ldloc, divisor);
        generator.Emit(OpCodes.Ldc_I4_M1);
        generator.Emit(OpCodes.Conv_I8);
        generator.Emit(OpCodes.Bne_Un, notNegativeOne);
        if (operation == LuaIrBinaryOperator.FloorDivide)
        {
            generator.Emit(OpCodes.Ldloc, dividend);
            generator.Emit(OpCodes.Neg);
        }
        else
        {
            generator.Emit(OpCodes.Ldc_I4_0);
            generator.Emit(OpCodes.Conv_I8);
        }

        generator.Emit(OpCodes.Stloc, result);
        generator.Emit(OpCodes.Br, write);
        generator.MarkLabel(notNegativeOne);
        generator.Emit(OpCodes.Ldloc, dividend);
        generator.Emit(OpCodes.Ldloc, divisor);
        generator.Emit(OpCodes.Div);
        generator.Emit(OpCodes.Stloc, quotient);
        generator.Emit(OpCodes.Ldloc, dividend);
        generator.Emit(OpCodes.Ldloc, divisor);
        generator.Emit(OpCodes.Rem);
        generator.Emit(OpCodes.Stloc, remainder);
        generator.Emit(OpCodes.Ldloc, remainder);
        generator.Emit(OpCodes.Brfalse, adjust);
        generator.Emit(OpCodes.Ldloc, remainder);
        generator.Emit(OpCodes.Ldloc, divisor);
        generator.Emit(OpCodes.Xor);
        generator.Emit(OpCodes.Ldc_I4_0);
        generator.Emit(OpCodes.Conv_I8);
        generator.Emit(OpCodes.Bge, adjust);
        if (operation == LuaIrBinaryOperator.FloorDivide)
        {
            generator.Emit(OpCodes.Ldloc, quotient);
            generator.Emit(OpCodes.Ldc_I4_1);
            generator.Emit(OpCodes.Conv_I8);
            generator.Emit(OpCodes.Sub);
            generator.Emit(OpCodes.Stloc, quotient);
        }
        else
        {
            generator.Emit(OpCodes.Ldloc, remainder);
            generator.Emit(OpCodes.Ldloc, divisor);
            generator.Emit(OpCodes.Add);
            generator.Emit(OpCodes.Stloc, remainder);
        }

        generator.MarkLabel(adjust);
        generator.Emit(
            OpCodes.Ldloc,
            operation == LuaIrBinaryOperator.FloorDivide ? quotient : remainder);
        generator.Emit(OpCodes.Stloc, result);
        generator.MarkLabel(write);
    }

    private static void EmitComparison(
        LuaNumericRegionIlGenerator generator,
        LuaIrBinaryOperator operation,
        LuaNumericRegionValueKind leftKind,
        LuaNumericRegionValueKind rightKind,
        LuaNumericIlLocal left,
        LuaNumericIlLocal right)
    {
        if (leftKind != rightKind)
        {
            var integerOnLeft = leftKind == LuaNumericRegionValueKind.Integer;
            generator.Emit(OpCodes.Ldloc, integerOnLeft ? left : right);
            generator.Emit(OpCodes.Ldloc, integerOnLeft ? right : left);
            generator.Emit(integerOnLeft ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            EmitInt32(generator, (int)operation);
            generator.Emit(OpCodes.Call, CompareMixed);
            return;
        }

        generator.Emit(OpCodes.Ldloc, left);
        generator.Emit(OpCodes.Ldloc, right);
        var floating = leftKind == LuaNumericRegionValueKind.Float;
        switch (operation)
        {
            case LuaIrBinaryOperator.Equal:
                generator.Emit(OpCodes.Ceq);
                break;
            case LuaIrBinaryOperator.NotEqual:
                generator.Emit(OpCodes.Ceq);
                generator.Emit(OpCodes.Ldc_I4_0);
                generator.Emit(OpCodes.Ceq);
                break;
            case LuaIrBinaryOperator.LessThan:
                generator.Emit(OpCodes.Clt);
                break;
            case LuaIrBinaryOperator.LessThanOrEqual:
                generator.Emit(floating ? OpCodes.Cgt_Un : OpCodes.Cgt);
                generator.Emit(OpCodes.Ldc_I4_0);
                generator.Emit(OpCodes.Ceq);
                break;
            case LuaIrBinaryOperator.GreaterThan:
                generator.Emit(OpCodes.Cgt);
                break;
            case LuaIrBinaryOperator.GreaterThanOrEqual:
                generator.Emit(floating ? OpCodes.Clt_Un : OpCodes.Clt);
                generator.Emit(OpCodes.Ldc_I4_0);
                generator.Emit(OpCodes.Ceq);
                break;
            default:
                throw new InvalidOperationException($"Unsupported comparison {operation}.");
        }
    }

    private static void EmitConditionalTransfer(
        LuaNumericRegionIlGenerator generator,
        LuaNumericRegionPlan plan,
        LuaNumericRegionEmissionMode mode,
        NumericExecutionPath executionPath,
        int pc,
        LuaIrInstruction instruction,
        NumericRegionLabelMap bodyLabels,
        NumericRegionLabelMap blockEntryLabels,
        NumericRegionLabelMap resumeLabels,
        Dictionary<(int Register, LuaNumericRegionValueKind Kind), LuaNumericIlLocal> valueLocals,
        Dictionary<int, NumericDirtyState> dirtyLocals,
        LuaNumericIlLocal remaining,
        LuaNumericIlLocal pending,
        LuaNumericIlLocal backedgeCountdown,
        Dictionary<int, LuaNumericIlLocal> observedBackedges,
        int backedgePollInterval,
        LuaNumericIlLocal minimumTop,
        LuaNumericIlLocal desiredTop,
        LuaNumericIlLocal topDirty,
        LuaNumericIlLocal headerReason)
    {
        var kind = plan.GetKindBefore(pc, instruction.A);
        if (kind == LuaNumericRegionValueKind.Boolean)
        {
            generator.Emit(
                OpCodes.Ldloc,
                NumericLocal(valueLocals, instruction.A, kind));
        }
        else if (kind is LuaNumericRegionValueKind.Integer or LuaNumericRegionValueKind.Float)
        {
            generator.Emit(OpCodes.Ldc_I4_1);
        }
        else
        {
            throw new InvalidOperationException(
                $"Branch operand r{instruction.A} at PC {pc} has no type proof.");
        }

        var fallthrough = generator.DefineLabel();
        generator.Emit(
            instruction.Opcode == LuaIrOpcode.JumpIfTrue
                ? OpCodes.Brfalse
                : OpCodes.Brtrue,
            fallthrough);
        EmitTransfer(
            generator,
            plan,
            mode,
            executionPath,
            pc,
            instruction.B,
            bodyLabels,
            blockEntryLabels,
            resumeLabels,
            valueLocals,
            dirtyLocals,
            remaining,
            pending,
            backedgeCountdown,
            observedBackedges,
            backedgePollInterval,
            minimumTop,
            desiredTop,
            topDirty,
            headerReason);
        generator.MarkLabel(fallthrough);
        EmitTransfer(
            generator,
            plan,
            mode,
            executionPath,
            pc,
            pc + 1,
            bodyLabels,
            blockEntryLabels,
            resumeLabels,
            valueLocals,
            dirtyLocals,
            remaining,
            pending,
            backedgeCountdown,
            observedBackedges,
            backedgePollInterval,
            minimumTop,
            desiredTop,
            topDirty,
            headerReason);
    }

    private static void EmitNumericForLoop(
        LuaNumericRegionIlGenerator generator,
        LuaNumericRegionPlan plan,
        LuaNumericRegionEmissionMode mode,
        NumericExecutionPath executionPath,
        int pc,
        LuaIrInstruction instruction,
        NumericRegionLabelMap bodyLabels,
        NumericRegionLabelMap blockEntryLabels,
        NumericRegionLabelMap resumeLabels,
        Dictionary<(int Register, LuaNumericRegionValueKind Kind), LuaNumericIlLocal> valueLocals,
        Dictionary<int, NumericDirtyState> dirtyLocals,
        LuaNumericIlLocal remaining,
        LuaNumericIlLocal pending,
        LuaNumericIlLocal backedgeCountdown,
        Dictionary<int, LuaNumericIlLocal> observedBackedges,
        int backedgePollInterval,
        LuaNumericIlLocal minimumTop,
        LuaNumericIlLocal desiredTop,
        LuaNumericIlLocal topDirty,
        LuaNumericIlLocal headerReason,
        LuaNumericIlLocal floatingTemporary)
    {
        var continues = generator.DefineLabel();
        var exits = generator.DefineLabel();
        var kind = plan.GetKindBefore(pc, instruction.A);
        if (kind == LuaNumericRegionValueKind.Integer)
        {
            generator.Emit(
                OpCodes.Ldloc,
                NumericLocal(valueLocals, instruction.A + 1, kind));
            generator.Emit(OpCodes.Brtrue, continues);
            generator.Emit(OpCodes.Br, exits);
            generator.MarkLabel(continues);
            generator.Emit(OpCodes.Ldloc, NumericLocal(valueLocals, instruction.A, kind));
            generator.Emit(
                OpCodes.Ldloc,
                NumericLocal(valueLocals, instruction.A + 2, kind));
            generator.Emit(OpCodes.Add);
            generator.Emit(OpCodes.Stloc, NumericLocal(valueLocals, instruction.A, kind));
            generator.Emit(
                OpCodes.Ldloc,
                NumericLocal(valueLocals, instruction.A + 1, kind));
            generator.Emit(OpCodes.Ldc_I4_1);
            generator.Emit(OpCodes.Conv_I8);
            generator.Emit(OpCodes.Sub);
            generator.Emit(
                OpCodes.Stloc,
                NumericLocal(valueLocals, instruction.A + 1, kind));
            generator.Emit(OpCodes.Ldloc, NumericLocal(valueLocals, instruction.A, kind));
            generator.Emit(
                OpCodes.Stloc,
                NumericLocal(valueLocals, instruction.A + 3, kind));
        }
        else
        {
            generator.Emit(OpCodes.Ldloc, NumericLocal(valueLocals, instruction.A, kind));
            generator.Emit(
                OpCodes.Ldloc,
                NumericLocal(valueLocals, instruction.A + 2, kind));
            generator.Emit(OpCodes.Add);
            generator.Emit(OpCodes.Stloc, floatingTemporary);
            var negativeStep = generator.DefineLabel();
            generator.Emit(
                OpCodes.Ldloc,
                NumericLocal(valueLocals, instruction.A + 2, kind));
            generator.Emit(OpCodes.Ldc_R8, 0d);
            generator.Emit(OpCodes.Ble_Un, negativeStep);
            generator.Emit(OpCodes.Ldloc, floatingTemporary);
            generator.Emit(
                OpCodes.Ldloc,
                NumericLocal(valueLocals, instruction.A + 1, kind));
            generator.Emit(OpCodes.Cgt_Un);
            generator.Emit(OpCodes.Brtrue, exits);
            generator.Emit(OpCodes.Br, continues);
            generator.MarkLabel(negativeStep);
            generator.Emit(
                OpCodes.Ldloc,
                NumericLocal(valueLocals, instruction.A + 1, kind));
            generator.Emit(OpCodes.Ldloc, floatingTemporary);
            generator.Emit(OpCodes.Cgt_Un);
            generator.Emit(OpCodes.Brtrue, exits);
            generator.MarkLabel(continues);
            generator.Emit(OpCodes.Ldloc, floatingTemporary);
            generator.Emit(OpCodes.Stloc, NumericLocal(valueLocals, instruction.A, kind));
            generator.Emit(OpCodes.Ldloc, floatingTemporary);
            generator.Emit(
                OpCodes.Stloc,
                NumericLocal(valueLocals, instruction.A + 3, kind));
        }

        EmitMarkDirty(generator, dirtyLocals[instruction.A], kind);
        if (kind == LuaNumericRegionValueKind.Integer)
        {
            EmitMarkDirty(generator, dirtyLocals[instruction.A + 1], kind);
        }

        EmitMarkDirty(generator, dirtyLocals[instruction.A + 3], kind);
        EmitTransfer(
            generator,
            plan,
            mode,
            executionPath,
            pc,
            instruction.B,
            bodyLabels,
            blockEntryLabels,
            resumeLabels,
            valueLocals,
            dirtyLocals,
            remaining,
            pending,
            backedgeCountdown,
            observedBackedges,
            backedgePollInterval,
            minimumTop,
            desiredTop,
            topDirty,
            headerReason);
        generator.MarkLabel(exits);
        EmitTransfer(
            generator,
            plan,
            mode,
            executionPath,
            pc,
            pc + 1,
            bodyLabels,
            blockEntryLabels,
            resumeLabels,
            valueLocals,
            dirtyLocals,
            remaining,
            pending,
            backedgeCountdown,
            observedBackedges,
            backedgePollInterval,
            minimumTop,
            desiredTop,
            topDirty,
            headerReason);
    }

    private static void EmitTransfer(
        LuaNumericRegionIlGenerator generator,
        LuaNumericRegionPlan plan,
        LuaNumericRegionEmissionMode mode,
        NumericExecutionPath executionPath,
        int sourceProgramCounter,
        int targetProgramCounter,
        NumericRegionLabelMap bodyLabels,
        NumericRegionLabelMap blockEntryLabels,
        NumericRegionLabelMap resumeLabels,
        Dictionary<(int Register, LuaNumericRegionValueKind Kind), LuaNumericIlLocal> valueLocals,
        Dictionary<int, NumericDirtyState> dirtyLocals,
        LuaNumericIlLocal remaining,
        LuaNumericIlLocal pending,
        LuaNumericIlLocal backedgeCountdown,
        Dictionary<int, LuaNumericIlLocal> observedBackedges,
        int backedgePollInterval,
        LuaNumericIlLocal minimumTop,
        LuaNumericIlLocal desiredTop,
        LuaNumericIlLocal topDirty,
        LuaNumericIlLocal headerReason)
    {
        if (!plan.Contains(targetProgramCounter))
        {
            EmitBoundaryState(
                generator,
                plan,
                targetProgramCounter,
                valueLocals,
                dirtyLocals,
                pending,
                remaining,
                observedBackedges,
                minimumTop,
                desiredTop,
                topDirty);
            EmitInt32(generator, targetProgramCounter);
            EmitInstructionsConsumed(generator);
            generator.Emit(OpCodes.Call, ContinueExit);
            generator.Emit(OpCodes.Ret);
            return;
        }

        var backedge = targetProgramCounter <= sourceProgramCounter &&
            LuaNumericRegionAnalyzer.IsBackedgeInstruction(
                new LuaIrInstruction(
                    LuaIrOpcode.Jump,
                    b: targetProgramCounter,
                    c: -1),
                sourceProgramCounter);
        if (!backedge)
        {
            var sourceSite = plan.GetBudgetSite(sourceProgramCounter);
            var targetSite = plan.GetBudgetSite(targetProgramCounter);
            var staysInHotBlock = executionPath == NumericExecutionPath.HotQuantum &&
                sourceSite.BasicBlockEntryProgramCounter ==
                    targetSite.BasicBlockEntryProgramCounter;
            generator.Emit(
                OpCodes.Br,
                staysInHotBlock
                    ? bodyLabels[targetProgramCounter]
                    : blockEntryLabels[targetProgramCounter]);
            return;
        }

        if (mode.ObserveLoopOsrBackedge)
        {
            if (!observedBackedges.TryGetValue(sourceProgramCounter, out var observed))
            {
                throw new InvalidOperationException(
                    $"Backedge PC {sourceProgramCounter} has no observation accumulator.");
            }

            generator.Emit(OpCodes.Ldloc, observed);
            generator.Emit(OpCodes.Ldc_I4_1);
            generator.Emit(OpCodes.Add);
            generator.Emit(OpCodes.Stloc, observed);
        }

        generator.Emit(OpCodes.Ldloc, backedgeCountdown);
        generator.Emit(OpCodes.Ldc_I4_1);
        generator.Emit(OpCodes.Sub);
        generator.Emit(OpCodes.Stloc, backedgeCountdown);
        generator.Emit(OpCodes.Ldloc, backedgeCountdown);
        generator.Emit(OpCodes.Brtrue, blockEntryLabels[targetProgramCounter]);
        EmitInt32(generator, backedgePollInterval);
        generator.Emit(OpCodes.Stloc, backedgeCountdown);
        EmitBoundaryState(
            generator,
            plan,
            targetProgramCounter,
            valueLocals,
            dirtyLocals,
            pending,
            remaining,
            observedBackedges,
            minimumTop,
            desiredTop,
            topDirty);

        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Ldarg_1);
        generator.Emit(OpCodes.Ldarg_2);
        generator.Emit(OpCodes.Call, CheckLoopHeader);
        generator.Emit(OpCodes.Stloc, headerReason);
        var execute = generator.DefineLabel();
        var guardFailure = generator.DefineLabel();
        generator.Emit(OpCodes.Ldloc, headerReason);
        EmitInt32(generator, (int)LuaCompiledExitReason.None);
        generator.Emit(OpCodes.Beq, execute);
        generator.Emit(OpCodes.Ldloc, headerReason);
        EmitInt32(generator, (int)LuaCompiledExitReason.GuardFailure);
        generator.Emit(OpCodes.Beq, guardFailure);
        EmitInt32(generator, targetProgramCounter);
        EmitInstructionsConsumed(generator);
        generator.Emit(OpCodes.Ldloc, headerReason);
        generator.Emit(OpCodes.Call, PollExit);
        generator.Emit(OpCodes.Ret);
        generator.MarkLabel(guardFailure);
        EmitExit(generator, DeoptExit, targetProgramCounter, LuaCompiledExitReason.GuardFailure);
        generator.MarkLabel(execute);
        generator.Emit(OpCodes.Br, resumeLabels[targetProgramCounter]);
    }

    private static void EmitBoundaryState(
        LuaNumericRegionIlGenerator generator,
        LuaNumericRegionPlan plan,
        int programCounter,
        Dictionary<(int Register, LuaNumericRegionValueKind Kind), LuaNumericIlLocal> valueLocals,
        Dictionary<int, NumericDirtyState> dirtyLocals,
        LuaNumericIlLocal pending,
        LuaNumericIlLocal remaining,
        Dictionary<int, LuaNumericIlLocal> observedBackedges,
        LuaNumericIlLocal minimumTop,
        LuaNumericIlLocal desiredTop,
        LuaNumericIlLocal topDirty,
        LuaNumericIlLocal? dynamicProgramCounter = null)
    {
        EmitMinimumFrameTop(generator, minimumTop, topDirty);
        foreach (var (register, state) in dirtyLocals)
        {
            var clean = generator.DefineLabel();
            var written = generator.DefineLabel();
            generator.Emit(OpCodes.Ldloc, state.Dirty);
            generator.Emit(OpCodes.Brfalse, clean);
            foreach (var promoted in plan.Registers.Where(candidate =>
                         candidate.Register == register))
            {
                var nextKind = generator.DefineLabel();
                generator.Emit(OpCodes.Ldloc, state.ActiveKind);
                EmitInt32(generator, (int)promoted.Kind);
                generator.Emit(OpCodes.Bne_Un, nextKind);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, register);
                generator.Emit(
                    OpCodes.Ldloc,
                    NumericLocal(valueLocals, register, promoted.Kind));
                if (promoted.Kind != LuaNumericRegionValueKind.Tagged)
                {
                    generator.Emit(
                        OpCodes.Call,
                        promoted.Kind switch
                        {
                            LuaNumericRegionValueKind.Integer => FromInteger,
                            LuaNumericRegionValueKind.Float => FromFloat,
                            LuaNumericRegionValueKind.Boolean => FromBoolean,
                            _ => throw new InvalidOperationException(),
                        });
                }
                generator.Emit(OpCodes.Call, WriteRegister);
                generator.Emit(OpCodes.Br, written);
                generator.MarkLabel(nextKind);
            }

            generator.Emit(
                OpCodes.Ldstr,
                $"Dirty numeric register r{register} has no active promoted kind.");
            generator.Emit(OpCodes.Newobj, InvalidOperationExceptionConstructor);
            generator.Emit(OpCodes.Throw);
            generator.MarkLabel(written);
            generator.Emit(OpCodes.Ldc_I4_0);
            generator.Emit(OpCodes.Stloc, state.Dirty);
            generator.MarkLabel(clean);
        }

        EmitFinalFrameTop(generator, minimumTop, desiredTop, topDirty);
        EmitObservedBackedges(generator, observedBackedges);

        generator.Emit(OpCodes.Ldarg_2);
        if (dynamicProgramCounter is not { } programCounterLocal)
        {
            EmitInt32(generator, programCounter);
        }
        else
        {
            generator.Emit(OpCodes.Ldloc, programCounterLocal);
        }

        generator.Emit(OpCodes.Call, SetProgramCounter);
        EmitCommitPending(generator, pending, remaining);
    }

    private static void EmitObservedBackedges(
        LuaNumericRegionIlGenerator generator,
        Dictionary<int, LuaNumericIlLocal> observedBackedges)
    {
        foreach (var (programCounter, count) in observedBackedges)
        {
            var complete = generator.DefineLabel();
            generator.Emit(OpCodes.Ldloc, count);
            generator.Emit(OpCodes.Brfalse, complete);
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldarg_2);
            EmitInt32(generator, programCounter);
            generator.Emit(OpCodes.Ldloc, count);
            generator.Emit(OpCodes.Call, ObserveLoopOsrBackedges);
            generator.Emit(OpCodes.Ldc_I4_0);
            generator.Emit(OpCodes.Stloc, count);
            generator.MarkLabel(complete);
        }
    }

    private static void EmitMinimumFrameTop(
        LuaNumericRegionIlGenerator generator,
        LuaNumericIlLocal minimumTop,
        LuaNumericIlLocal topDirty)
    {
        var unchanged = generator.DefineLabel();
        generator.Emit(OpCodes.Ldloc, topDirty);
        generator.Emit(OpCodes.Brfalse, unchanged);
        generator.Emit(OpCodes.Ldarg_1);
        generator.Emit(OpCodes.Ldarg_2);
        generator.Emit(OpCodes.Ldloc, minimumTop);
        generator.Emit(OpCodes.Call, SetFrameTop);
        generator.MarkLabel(unchanged);
    }

    private static void EmitFinalFrameTop(
        LuaNumericRegionIlGenerator generator,
        LuaNumericIlLocal minimumTop,
        LuaNumericIlLocal desiredTop,
        LuaNumericIlLocal topDirty)
    {
        var reset = generator.DefineLabel();
        var complete = generator.DefineLabel();
        generator.Emit(OpCodes.Ldloc, topDirty);
        generator.Emit(OpCodes.Brfalse, complete);
        generator.Emit(OpCodes.Ldloc, minimumTop);
        generator.Emit(OpCodes.Ldloc, desiredTop);
        generator.Emit(OpCodes.Beq, reset);
        generator.Emit(OpCodes.Ldarg_1);
        generator.Emit(OpCodes.Ldarg_2);
        generator.Emit(OpCodes.Ldloc, desiredTop);
        generator.Emit(OpCodes.Call, SetFrameTop);
        generator.MarkLabel(reset);
        generator.Emit(OpCodes.Ldc_I4_0);
        generator.Emit(OpCodes.Stloc, topDirty);
        generator.Emit(OpCodes.Ldc_I4, int.MaxValue);
        generator.Emit(OpCodes.Stloc, minimumTop);
        generator.MarkLabel(complete);
    }

    private static void EmitCommitPending(
        LuaNumericRegionIlGenerator generator,
        LuaNumericIlLocal pending,
        LuaNumericIlLocal remaining)
    {
        var committed = generator.DefineLabel();
        generator.Emit(OpCodes.Ldloc, pending);
        generator.Emit(OpCodes.Brfalse, committed);
        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Ldloc, pending);
        generator.Emit(OpCodes.Callvirt, TryReserveInstructions);
        generator.Emit(OpCodes.Pop);
        generator.Emit(OpCodes.Ldc_I4_0);
        generator.Emit(OpCodes.Stloc, pending);
        generator.MarkLabel(committed);
        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Callvirt, GetRemainingInstructionCount);
        generator.Emit(OpCodes.Stloc, remaining);
    }

    private static void EmitLoadAsDouble(
        LuaNumericRegionIlGenerator generator,
        LuaNumericRegionValueKind kind,
        LuaNumericIlLocal local)
    {
        generator.Emit(OpCodes.Ldloc, local);
        if (kind == LuaNumericRegionValueKind.Integer)
        {
            generator.Emit(OpCodes.Conv_R8);
        }
    }

    private static void EmitConstant(
        LuaNumericRegionIlGenerator generator,
        LuaIrConstant constant,
        LuaNumericIlLocal destination)
    {
        switch (constant.Kind)
        {
            case LuaIrConstantKind.Boolean:
                generator.Emit(constant.Boolean ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                break;
            case LuaIrConstantKind.Integer:
                generator.Emit(OpCodes.Ldc_I8, constant.Integer);
                break;
            case LuaIrConstantKind.Float:
                generator.Emit(OpCodes.Ldc_R8, constant.Float);
                break;
            default:
                throw new InvalidOperationException(
                    $"Constant {constant.Kind} cannot enter a numeric region.");
        }

        generator.Emit(OpCodes.Stloc, destination);
    }

    private static void EmitSetDirty(LuaNumericRegionIlGenerator generator, LuaNumericIlLocal dirty)
    {
        generator.Emit(OpCodes.Ldc_I4_1);
        generator.Emit(OpCodes.Stloc, dirty);
    }

    private static void EmitMarkDirty(
        LuaNumericRegionIlGenerator generator,
        NumericDirtyState state,
        LuaNumericRegionValueKind kind)
    {
        EmitInt32(generator, (int)kind);
        generator.Emit(OpCodes.Stloc, state.ActiveKind);
        EmitSetDirty(generator, state.Dirty);
    }

    private static void EmitExit(
        LuaNumericRegionIlGenerator generator,
        MethodInfo factory,
        int programCounter,
        LuaCompiledExitReason reason)
    {
        EmitInt32(generator, programCounter);
        EmitInstructionsConsumed(generator);
        EmitInt32(generator, (int)reason);
        generator.Emit(OpCodes.Call, factory);
        generator.Emit(OpCodes.Ret);
    }

    private static void EmitDynamicExit(
        LuaNumericRegionIlGenerator generator,
        MethodInfo factory,
        LuaNumericIlLocal programCounter,
        LuaCompiledExitReason reason)
    {
        generator.Emit(OpCodes.Ldloc, programCounter);
        EmitInstructionsConsumed(generator);
        EmitInt32(generator, (int)reason);
        generator.Emit(OpCodes.Call, factory);
        generator.Emit(OpCodes.Ret);
    }

    private static void EmitInstructionsConsumed(LuaNumericRegionIlGenerator generator)
    {
        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Call, GetInstructionsConsumed);
    }

    private static void EmitSwitch(
        LuaNumericRegionIlGenerator generator,
        int instructionCount,
        NumericRegionLabelMap labels,
        LuaNumericIlLabel invalidatedExit)
    {
        var dispatch = new LuaNumericIlLabel[instructionCount];
        for (var pc = 0; pc < dispatch.Length; pc++)
        {
            dispatch[pc] = labels.TryGetValue(pc, out var label) ? label : invalidatedExit;
        }

        generator.Emit(OpCodes.Switch, dispatch);
        generator.Emit(OpCodes.Br, invalidatedExit);
    }

    private static NumericRegionLabelMap DefineLabels(
        LuaNumericRegionIlGenerator generator,
        ImmutableArray<int> programCounters,
        Func<int, bool>? include = null)
    {
        var labels = new LuaNumericIlLabel[programCounters.Length];
        Array.Fill(labels, new LuaNumericIlLabel(-1));
        for (var index = 0; index < programCounters.Length; index++)
        {
            if (include is null || include(programCounters[index]))
            {
                labels[index] = generator.DefineLabel();
            }
        }

        return new NumericRegionLabelMap(programCounters, labels);
    }

    private static Type LocalType(LuaNumericRegionValueKind kind) => kind switch
    {
        LuaNumericRegionValueKind.Integer => typeof(long),
        LuaNumericRegionValueKind.Float => typeof(double),
        LuaNumericRegionValueKind.Boolean => typeof(bool),
        LuaNumericRegionValueKind.Tagged => typeof(LuaValue),
        _ => throw new InvalidOperationException($"{kind} is not promotable."),
    };

    private static LuaNumericIlLocal NumericLocal(
        Dictionary<(int Register, LuaNumericRegionValueKind Kind), LuaNumericIlLocal> locals,
        int register,
        LuaNumericRegionValueKind kind) =>
        locals.TryGetValue((register, kind), out var local)
            ? local
            : throw new InvalidOperationException(
                $"Register r{register} has no promoted {kind} local.");

    private static LuaValueKind ValueKind(LuaNumericRegionValueKind kind) => kind switch
    {
        LuaNumericRegionValueKind.Integer => LuaValueKind.Integer,
        LuaNumericRegionValueKind.Float => LuaValueKind.Float,
        LuaNumericRegionValueKind.Boolean => LuaValueKind.Boolean,
        _ => throw new InvalidOperationException($"{kind} is not a Lua value kind."),
    };

    private static bool IsComparison(LuaIrBinaryOperator operation) => operation is
        LuaIrBinaryOperator.Equal or LuaIrBinaryOperator.NotEqual or
        LuaIrBinaryOperator.LessThan or LuaIrBinaryOperator.LessThanOrEqual or
        LuaIrBinaryOperator.GreaterThan or LuaIrBinaryOperator.GreaterThanOrEqual;

    private static MethodInfo Method(
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicMethods |
            DynamicallyAccessedMemberTypes.NonPublicMethods)]
        Type type,
        string name,
        Type[] parameters) =>
        type.GetMethod(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                BindingFlags.Instance,
            binder: null,
            parameters,
            modifiers: null) ?? throw new MissingMethodException(type.FullName, name);

    private static MethodInfo PropertyGetter(
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.NonPublicProperties)]
        Type type,
        string name) =>
        type.GetProperty(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                BindingFlags.Static)?.GetGetMethod(nonPublic: true) ??
        throw new MissingMemberException(
            type.FullName,
            name);

    private static void EmitInt32(LuaNumericRegionIlGenerator generator, int value)
    {
        switch (value)
        {
            case -1:
                generator.Emit(OpCodes.Ldc_I4_M1);
                break;
            case 0:
                generator.Emit(OpCodes.Ldc_I4_0);
                break;
            case 1:
                generator.Emit(OpCodes.Ldc_I4_1);
                break;
            case 2:
                generator.Emit(OpCodes.Ldc_I4_2);
                break;
            case 3:
                generator.Emit(OpCodes.Ldc_I4_3);
                break;
            case 4:
                generator.Emit(OpCodes.Ldc_I4_4);
                break;
            case 5:
                generator.Emit(OpCodes.Ldc_I4_5);
                break;
            case 6:
                generator.Emit(OpCodes.Ldc_I4_6);
                break;
            case 7:
                generator.Emit(OpCodes.Ldc_I4_7);
                break;
            case 8:
                generator.Emit(OpCodes.Ldc_I4_8);
                break;
            case >= sbyte.MinValue and <= sbyte.MaxValue:
                generator.Emit(OpCodes.Ldc_I4_S, (sbyte)value);
                break;
            default:
                generator.Emit(OpCodes.Ldc_I4, value);
                break;
        }
    }
}
