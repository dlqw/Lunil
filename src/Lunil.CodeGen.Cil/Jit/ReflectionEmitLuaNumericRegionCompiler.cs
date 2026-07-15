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
    LuaNumericRegionEmissionMetrics Metrics);

internal static class LuaNumericRegionRuntime
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Shift(long value, long count, bool left)
    {
        if (count < 0)
        {
            if (count == long.MinValue)
            {
                return 0;
            }

            return Shift(value, -count, !left);
        }

        if (count >= 64)
        {
            return 0;
        }

        return left
            ? unchecked((long)((ulong)value << (int)count))
            : unchecked((long)((ulong)value >> (int)count));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double FloatingModulo(double dividend, double divisor)
    {
        var remainder = dividend % divisor;
        if (remainder > 0 ? divisor < 0 : remainder < 0 && divisor > 0)
        {
            remainder += divisor;
        }

        return remainder;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CompareMixed(
        long integer,
        double floatingPoint,
        bool integerOnLeft,
        LuaIrBinaryOperator operation)
    {
        if (double.IsNaN(floatingPoint))
        {
            return operation == LuaIrBinaryOperator.NotEqual;
        }

        var comparison = IntegerFloatCompare(integer, floatingPoint);
        if (!integerOnLeft)
        {
            comparison = -comparison;
        }

        return operation switch
        {
            LuaIrBinaryOperator.Equal => comparison == 0,
            LuaIrBinaryOperator.NotEqual => comparison != 0,
            LuaIrBinaryOperator.LessThan => comparison < 0,
            LuaIrBinaryOperator.LessThanOrEqual => comparison <= 0,
            LuaIrBinaryOperator.GreaterThan => comparison > 0,
            LuaIrBinaryOperator.GreaterThanOrEqual => comparison >= 0,
            _ => throw new InvalidOperationException(
                $"{operation} is not a numeric comparison."),
        };
    }

    private static int IntegerFloatCompare(long integer, double floatingPoint)
    {
        if (floatingPoint >= 9_223_372_036_854_775_808d)
        {
            return -1;
        }

        if (floatingPoint < long.MinValue)
        {
            return 1;
        }

        var integral = (long)Math.Truncate(floatingPoint);
        var comparison = integer.CompareTo(integral);
        if (comparison != 0)
        {
            return comparison;
        }

        return floatingPoint.CompareTo((double)integral) switch
        {
            > 0 => -1,
            < 0 => 1,
            _ => 0,
        };
    }
}

/// <summary>
/// Emits a verified natural loop as raw CLR numeric locals. The frame is touched only at entry,
/// side exits, deoptimization, and backedge safepoints; canonical instruction accounting is kept
/// in locals and committed atomically at those same boundaries.
/// </summary>
internal static class ReflectionEmitLuaNumericRegionCompiler
{
    private const int MaximumBackedgePollInterval = 1024;

    private enum NumericExecutionPath : byte
    {
        HotQuantum,
        ColdSlowTail,
    }

    private readonly record struct NumericDirtyState(
        LocalBuilder Dirty,
        LocalBuilder ActiveKind);

    private static readonly Type[] CompiledMethodParameters =
    [
        typeof(LuaExecutionContext),
        typeof(LuaThread),
        typeof(LuaFrame),
    ];

    private static readonly MethodInfo CanExecuteCompiledFrame = Method(
        typeof(LuaCodegenAbiV2),
        nameof(LuaCodegenAbiV2.CanExecuteCompiledFrame),
        [typeof(LuaExecutionContext), typeof(LuaFrame), typeof(int), typeof(int)]);
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
    private static readonly MethodInfo GetInstructionsConsumed = PropertyGetter(
        typeof(LuaExecutionContext),
        "InstructionsConsumed");
    private static readonly MethodInfo GetProgramCounter = PropertyGetter(
        typeof(LuaFrame),
        nameof(LuaFrame.ProgramCounter));
    private static readonly MethodInfo SetProgramCounter = PropertySetter(
        typeof(LuaFrame),
        nameof(LuaFrame.ProgramCounter));
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
        typeof(LuaNumericRegionRuntime),
        nameof(LuaNumericRegionRuntime.Shift),
        [typeof(long), typeof(long), typeof(bool)]);
    private static readonly MethodInfo FloatingModulo = Method(
        typeof(LuaNumericRegionRuntime),
        nameof(LuaNumericRegionRuntime.FloatingModulo),
        [typeof(double), typeof(double)]);
    private static readonly MethodInfo CompareMixed = Method(
        typeof(LuaNumericRegionRuntime),
        nameof(LuaNumericRegionRuntime.CompareMixed),
        [typeof(long), typeof(double), typeof(bool), typeof(LuaIrBinaryOperator)]);
    private static readonly MethodInfo ContinueExit = Method(
        typeof(LuaCompiledExit),
        nameof(LuaCompiledExit.Continue),
        [typeof(int), typeof(int)]);
    private static readonly MethodInfo PollExit = Method(
        typeof(LuaCompiledExit),
        nameof(LuaCompiledExit.Poll),
        [typeof(int), typeof(int), typeof(LuaCompiledExitReason)]);
    private static readonly MethodInfo DeoptExit = Method(
        typeof(LuaCompiledExit),
        nameof(LuaCompiledExit.Deopt),
        [typeof(int), typeof(int), typeof(LuaCompiledExitReason)]);
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
        var generator = dynamicMethod.GetILGenerator();
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
        var hotBodyLabels = plan.Region.ProgramCounters.ToDictionary(
            static pc => pc,
            _ => generator.DefineLabel());
        var hotChargeLabels = plan.Region.ProgramCounters.ToDictionary(
            static pc => pc,
            _ => generator.DefineLabel());
        var coldSlowTailLabels = plan.Region.ProgramCounters.ToDictionary(
            static pc => pc,
            _ => generator.DefineLabel());
        var resumeLabels = plan.Region.ProgramCounters.ToDictionary(
            static pc => pc,
            _ => generator.DefineLabel());
        var entryLabels = plan.Region.ProgramCounters.ToDictionary(
            static pc => pc,
            _ => generator.DefineLabel());
        var budgetLabels = plan.Region.ProgramCounters.ToDictionary(
            static pc => pc,
            _ => generator.DefineLabel());
        var guardLabels = plan.Region.ProgramCounters.ToDictionary(
            static pc => pc,
            _ => generator.DefineLabel());
        var hotSemanticDeoptLabels = plan.Region.ProgramCounters.ToDictionary(
            static pc => pc,
            _ => generator.DefineLabel());
        var coldSemanticDeoptLabels = plan.Region.ProgramCounters.ToDictionary(
            static pc => pc,
            _ => generator.DefineLabel());
        var budgetBoundary = generator.DefineLabel();
        var guardBoundary = generator.DefineLabel();
        var invalidatedExit = generator.DefineLabel();
        var backedgePollInterval = Math.Max(
            1,
            Math.Min(
                MaximumBackedgePollInterval,
                int.MaxValue / Math.Max(1, plan.Region.ProgramCounters.Length)));

        EmitEntryGuard(generator, function, plan, mode, invalidatedExit);
        generator.Emit(OpCodes.Ldc_I4, int.MaxValue);
        generator.Emit(OpCodes.Stloc, minimumTop);
        EmitInt32(generator, backedgePollInterval);
        generator.Emit(OpCodes.Stloc, backedgeCountdown);
        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Callvirt, GetRemainingInstructionCount);
        generator.Emit(OpCodes.Stloc, remaining);
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
                mode,
                NumericExecutionPath.HotQuantum,
                pc,
                function.Instructions[pc],
                hotBodyLabels,
                hotChargeLabels,
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
                integerTemporary,
                integerRemainder,
                floatingTemporary,
                hotSemanticDeoptLabels[pc]);

            generator.MarkLabel(coldSlowTailLabels[pc]);
            EmitLocalInstructionReservation(generator, remaining, pending, budgetLabels[pc]);
            EmitInstruction(
                generator,
                function,
                plan,
                mode,
                NumericExecutionPath.ColdSlowTail,
                pc,
                function.Instructions[pc],
                coldSlowTailLabels,
                coldSlowTailLabels,
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
                integerTemporary,
                integerRemainder,
                floatingTemporary,
                coldSemanticDeoptLabels[pc]);
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

            generator.MarkLabel(hotSemanticDeoptLabels[pc]);
            EmitSubtractPendingInstructions(
                generator,
                pending,
                plan.GetBudgetSite(pc).FailureInstructionRollbackCount);
            EmitInt32(generator, plan.GetBudgetSite(pc).DeoptimizationProgramCounter);
            generator.Emit(OpCodes.Stloc, boundaryProgramCounter);
            generator.Emit(OpCodes.Br, guardBoundary);

            generator.MarkLabel(coldSemanticDeoptLabels[pc]);
            EmitCancelLocalInstructionReservation(generator, remaining, pending);
            EmitInt32(generator, plan.GetBudgetSite(pc).DeoptimizationProgramCounter);
            generator.Emit(OpCodes.Stloc, boundaryProgramCounter);
            generator.Emit(OpCodes.Br, guardBoundary);
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

        cancellationToken.ThrowIfCancellationRequested();
        var cilEmissionDuration = Stopwatch.GetElapsedTime(emissionStarted);
        var delegateStarted = Stopwatch.GetTimestamp();
        var method = (LuaCompiledMethod)dynamicMethod.CreateDelegate(typeof(LuaCompiledMethod));
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
                delegateCreationDuration));
        return true;
    }

    private static void EmitEntryGuard(
        ILGenerator generator,
        LuaIrFunction function,
        LuaNumericRegionPlan plan,
        LuaNumericRegionEmissionMode mode,
        Label invalidatedExit)
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
        ILGenerator generator,
        LuaNumericRegionRegister register,
        LocalBuilder local,
        LocalBuilder taggedValue,
        Label guardExit)
    {
        generator.Emit(OpCodes.Ldarg_1);
        generator.Emit(OpCodes.Ldarg_2);
        EmitInt32(generator, register.Register);
        generator.Emit(OpCodes.Call, ReadRegister);
        generator.Emit(OpCodes.Stloc, taggedValue);
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
        ILGenerator generator,
        LocalBuilder remaining,
        LocalBuilder pending,
        Label budgetExit)
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
        ILGenerator generator,
        LuaNumericRegionPlan plan,
        int programCounter,
        LocalBuilder remaining,
        LocalBuilder backedgeCountdown,
        Label hotQuantum,
        Label coldSlowTail)
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
        ILGenerator generator,
        LocalBuilder pending,
        int instructionCount)
    {
        generator.Emit(OpCodes.Ldloc, pending);
        EmitInt32(generator, instructionCount);
        generator.Emit(OpCodes.Add);
        generator.Emit(OpCodes.Stloc, pending);
    }

    private static void EmitSubtractPendingInstructions(
        ILGenerator generator,
        LocalBuilder pending,
        int instructionCount)
    {
        generator.Emit(OpCodes.Ldloc, pending);
        EmitInt32(generator, instructionCount);
        generator.Emit(OpCodes.Sub);
        generator.Emit(OpCodes.Stloc, pending);
    }

    private static void EmitCancelLocalInstructionReservation(
        ILGenerator generator,
        LocalBuilder remaining,
        LocalBuilder pending)
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
        ILGenerator generator,
        int registerCount,
        LocalBuilder minimumTop,
        LocalBuilder desiredTop,
        LocalBuilder topDirty,
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
        ILGenerator generator,
        LuaIrFunction function,
        LuaNumericRegionPlan plan,
        LuaNumericRegionEmissionMode mode,
        NumericExecutionPath executionPath,
        int pc,
        LuaIrInstruction instruction,
        IReadOnlyDictionary<int, Label> bodyLabels,
        IReadOnlyDictionary<int, Label> blockEntryLabels,
        IReadOnlyDictionary<int, Label> resumeLabels,
        Dictionary<(int Register, LuaNumericRegionValueKind Kind), LocalBuilder> valueLocals,
        Dictionary<int, NumericDirtyState> dirtyLocals,
        LocalBuilder remaining,
        LocalBuilder pending,
        LocalBuilder backedgeCountdown,
        Dictionary<int, LocalBuilder> observedBackedges,
        int backedgePollInterval,
        LocalBuilder minimumTop,
        LocalBuilder desiredTop,
        LocalBuilder topDirty,
        LocalBuilder headerReason,
        LocalBuilder integerTemporary,
        LocalBuilder integerRemainder,
        LocalBuilder floatingTemporary,
        Label semanticDeopt)
    {
        switch (instruction.Opcode)
        {
            case LuaIrOpcode.LoadConstant:
                var constantKind = plan.GetKindAfter(pc, instruction.A);
                EmitConstant(
                    generator,
                    function.Constants[instruction.B],
                    NumericLocal(valueLocals, instruction.A, constantKind));
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
            default:
                throw new InvalidOperationException(
                    $"Unsupported numeric-region instruction {instruction.Opcode} at PC {pc}.");
        }
    }

    private static void EmitUnary(
        ILGenerator generator,
        LuaNumericRegionPlan plan,
        int pc,
        LuaIrInstruction instruction,
        Dictionary<(int Register, LuaNumericRegionValueKind Kind), LocalBuilder> locals)
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

    private static void EmitBinary(
        ILGenerator generator,
        LuaNumericRegionPlan plan,
        int pc,
        LuaIrInstruction instruction,
        Dictionary<(int Register, LuaNumericRegionValueKind Kind), LocalBuilder> locals,
        LocalBuilder integerTemporary,
        LocalBuilder integerRemainder,
        Label semanticDeopt)
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
        ILGenerator generator,
        LuaIrBinaryOperator operation,
        LocalBuilder dividend,
        LocalBuilder divisor,
        LocalBuilder result,
        LocalBuilder quotient,
        LocalBuilder remainder,
        Label semanticDeopt)
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
        ILGenerator generator,
        LuaIrBinaryOperator operation,
        LuaNumericRegionValueKind leftKind,
        LuaNumericRegionValueKind rightKind,
        LocalBuilder left,
        LocalBuilder right)
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
        ILGenerator generator,
        LuaNumericRegionPlan plan,
        LuaNumericRegionEmissionMode mode,
        NumericExecutionPath executionPath,
        int pc,
        LuaIrInstruction instruction,
        IReadOnlyDictionary<int, Label> bodyLabels,
        IReadOnlyDictionary<int, Label> blockEntryLabels,
        IReadOnlyDictionary<int, Label> resumeLabels,
        Dictionary<(int Register, LuaNumericRegionValueKind Kind), LocalBuilder> valueLocals,
        Dictionary<int, NumericDirtyState> dirtyLocals,
        LocalBuilder remaining,
        LocalBuilder pending,
        LocalBuilder backedgeCountdown,
        Dictionary<int, LocalBuilder> observedBackedges,
        int backedgePollInterval,
        LocalBuilder minimumTop,
        LocalBuilder desiredTop,
        LocalBuilder topDirty,
        LocalBuilder headerReason)
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
        ILGenerator generator,
        LuaNumericRegionPlan plan,
        LuaNumericRegionEmissionMode mode,
        NumericExecutionPath executionPath,
        int pc,
        LuaIrInstruction instruction,
        IReadOnlyDictionary<int, Label> bodyLabels,
        IReadOnlyDictionary<int, Label> blockEntryLabels,
        IReadOnlyDictionary<int, Label> resumeLabels,
        Dictionary<(int Register, LuaNumericRegionValueKind Kind), LocalBuilder> valueLocals,
        Dictionary<int, NumericDirtyState> dirtyLocals,
        LocalBuilder remaining,
        LocalBuilder pending,
        LocalBuilder backedgeCountdown,
        Dictionary<int, LocalBuilder> observedBackedges,
        int backedgePollInterval,
        LocalBuilder minimumTop,
        LocalBuilder desiredTop,
        LocalBuilder topDirty,
        LocalBuilder headerReason,
        LocalBuilder floatingTemporary)
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
        ILGenerator generator,
        LuaNumericRegionPlan plan,
        LuaNumericRegionEmissionMode mode,
        NumericExecutionPath executionPath,
        int sourceProgramCounter,
        int targetProgramCounter,
        IReadOnlyDictionary<int, Label> bodyLabels,
        IReadOnlyDictionary<int, Label> blockEntryLabels,
        IReadOnlyDictionary<int, Label> resumeLabels,
        Dictionary<(int Register, LuaNumericRegionValueKind Kind), LocalBuilder> valueLocals,
        Dictionary<int, NumericDirtyState> dirtyLocals,
        LocalBuilder remaining,
        LocalBuilder pending,
        LocalBuilder backedgeCountdown,
        Dictionary<int, LocalBuilder> observedBackedges,
        int backedgePollInterval,
        LocalBuilder minimumTop,
        LocalBuilder desiredTop,
        LocalBuilder topDirty,
        LocalBuilder headerReason)
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
        ILGenerator generator,
        LuaNumericRegionPlan plan,
        int programCounter,
        Dictionary<(int Register, LuaNumericRegionValueKind Kind), LocalBuilder> valueLocals,
        Dictionary<int, NumericDirtyState> dirtyLocals,
        LocalBuilder pending,
        LocalBuilder remaining,
        Dictionary<int, LocalBuilder> observedBackedges,
        LocalBuilder minimumTop,
        LocalBuilder desiredTop,
        LocalBuilder topDirty,
        LocalBuilder? dynamicProgramCounter = null)
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
                generator.Emit(
                    OpCodes.Call,
                    promoted.Kind switch
                    {
                        LuaNumericRegionValueKind.Integer => FromInteger,
                        LuaNumericRegionValueKind.Float => FromFloat,
                        LuaNumericRegionValueKind.Boolean => FromBoolean,
                        _ => throw new InvalidOperationException(),
                    });
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
        if (dynamicProgramCounter is null)
        {
            EmitInt32(generator, programCounter);
        }
        else
        {
            generator.Emit(OpCodes.Ldloc, dynamicProgramCounter);
        }

        generator.Emit(OpCodes.Callvirt, SetProgramCounter);
        EmitCommitPending(generator, pending, remaining);
    }

    private static void EmitObservedBackedges(
        ILGenerator generator,
        Dictionary<int, LocalBuilder> observedBackedges)
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
        ILGenerator generator,
        LocalBuilder minimumTop,
        LocalBuilder topDirty)
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
        ILGenerator generator,
        LocalBuilder minimumTop,
        LocalBuilder desiredTop,
        LocalBuilder topDirty)
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
        ILGenerator generator,
        LocalBuilder pending,
        LocalBuilder remaining)
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
        ILGenerator generator,
        LuaNumericRegionValueKind kind,
        LocalBuilder local)
    {
        generator.Emit(OpCodes.Ldloc, local);
        if (kind == LuaNumericRegionValueKind.Integer)
        {
            generator.Emit(OpCodes.Conv_R8);
        }
    }

    private static void EmitConstant(
        ILGenerator generator,
        LuaIrConstant constant,
        LocalBuilder destination)
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

    private static void EmitSetDirty(ILGenerator generator, LocalBuilder dirty)
    {
        generator.Emit(OpCodes.Ldc_I4_1);
        generator.Emit(OpCodes.Stloc, dirty);
    }

    private static void EmitMarkDirty(
        ILGenerator generator,
        NumericDirtyState state,
        LuaNumericRegionValueKind kind)
    {
        EmitInt32(generator, (int)kind);
        generator.Emit(OpCodes.Stloc, state.ActiveKind);
        EmitSetDirty(generator, state.Dirty);
    }

    private static void EmitExit(
        ILGenerator generator,
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
        ILGenerator generator,
        MethodInfo factory,
        LocalBuilder programCounter,
        LuaCompiledExitReason reason)
    {
        generator.Emit(OpCodes.Ldloc, programCounter);
        EmitInstructionsConsumed(generator);
        EmitInt32(generator, (int)reason);
        generator.Emit(OpCodes.Call, factory);
        generator.Emit(OpCodes.Ret);
    }

    private static void EmitInstructionsConsumed(ILGenerator generator)
    {
        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Callvirt, GetInstructionsConsumed);
    }

    private static void EmitSwitch(
        ILGenerator generator,
        int instructionCount,
        IReadOnlyDictionary<int, Label> labels,
        Label invalidatedExit)
    {
        var dispatch = new Label[instructionCount];
        for (var pc = 0; pc < dispatch.Length; pc++)
        {
            dispatch[pc] = labels.GetValueOrDefault(pc, invalidatedExit);
        }

        generator.Emit(OpCodes.Switch, dispatch);
        generator.Emit(OpCodes.Br, invalidatedExit);
    }

    private static Type LocalType(LuaNumericRegionValueKind kind) => kind switch
    {
        LuaNumericRegionValueKind.Integer => typeof(long),
        LuaNumericRegionValueKind.Float => typeof(double),
        LuaNumericRegionValueKind.Boolean => typeof(bool),
        _ => throw new InvalidOperationException($"{kind} is not promotable."),
    };

    private static LocalBuilder NumericLocal(
        Dictionary<(int Register, LuaNumericRegionValueKind Kind), LocalBuilder> locals,
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

    private static MethodInfo PropertySetter(
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.NonPublicProperties)]
        Type type,
        string name) =>
        type.GetProperty(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                BindingFlags.Static)?.GetSetMethod(nonPublic: true) ??
        throw new MissingMemberException(
            type.FullName,
            name);

    private static void EmitInt32(ILGenerator generator, int value)
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
