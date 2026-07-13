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

internal readonly record struct LuaTier2EmissionMetrics(
    TimeSpan CilEmissionDuration,
    TimeSpan DelegateCreationDuration);

internal enum LuaTier2NumericSpecializationStatus : byte
{
    Eligible,
    NoNumericHotspot,
    PolymorphicNumericProfile,
    ManagedOptimizationRequired,
}

internal static class ReflectionEmitLuaTier2Compiler
{
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
    private static readonly MethodInfo ReadRegister = Method(
        typeof(LuaCodegenAbiV2),
        nameof(LuaCodegenAbiV2.ReadRegisterUnchecked),
        [typeof(LuaThread), typeof(LuaFrame), typeof(int)]);
    private static readonly MethodInfo WriteRegister = Method(
        typeof(LuaCodegenAbiV2),
        nameof(LuaCodegenAbiV2.WriteRegisterUnchecked),
        [typeof(LuaThread), typeof(LuaFrame), typeof(int), typeof(LuaValue)]);
    private static readonly MethodInfo ClearRegisters = Method(
        typeof(LuaCodegenAbiV2),
        nameof(LuaCodegenAbiV2.ClearRegistersUnchecked),
        [typeof(LuaThread), typeof(LuaFrame), typeof(int), typeof(int)]);
    private static readonly MethodInfo SetFrameTop = Method(
        typeof(LuaCodegenAbiV2),
        nameof(LuaCodegenAbiV2.SetFrameTopUnchecked),
        [typeof(LuaThread), typeof(LuaFrame), typeof(int)]);
    private static readonly MethodInfo CanSkipClose = Method(
        typeof(LuaCodegenAbiV2),
        nameof(LuaCodegenAbiV2.CanSkipClose),
        [typeof(LuaFrame), typeof(int)]);
    private static readonly MethodInfo MaterializeConstant = Method(
        typeof(LuaCodegenAbiV1),
        nameof(LuaCodegenAbiV1.MaterializeConstant),
        [typeof(LuaExecutionContext), typeof(LuaFrame), typeof(int)]);
    private static readonly MethodInfo CommitProgramCounter = Method(
        typeof(LuaCodegenAbiV1),
        nameof(LuaCodegenAbiV1.CommitProgramCounter),
        [typeof(LuaFrame), typeof(int)]);
    private static readonly MethodInfo ExecuteCanonicalInstruction = Method(
        typeof(LuaCodegenAbiV1),
        nameof(LuaCodegenAbiV1.ExecuteCanonicalInstruction),
        [typeof(LuaExecutionContext), typeof(LuaThread), typeof(LuaFrame), typeof(int)]);
    private static readonly MethodInfo ReserveInstructions = Method(
        typeof(LuaExecutionContext),
        nameof(LuaExecutionContext.TryReserveInstructions),
        [typeof(int)]);
    private static readonly MethodInfo GetInstructionsConsumed = PropertyGetter(
        typeof(LuaExecutionContext),
        "InstructionsConsumed");
    private static readonly MethodInfo GetProgramCounter = PropertyGetter(
        typeof(LuaFrame),
        nameof(LuaFrame.ProgramCounter));
    private static readonly MethodInfo SetProgramCounter = PropertySetter(
        typeof(LuaFrame),
        nameof(LuaFrame.ProgramCounter));
    private static readonly MethodInfo IsInteger = PropertyGetter(
        typeof(LuaValue),
        "IsInteger");
    private static readonly MethodInfo IsFloat = PropertyGetter(
        typeof(LuaValue),
        "IsFloat");
    private static readonly MethodInfo IsTruthy = PropertyGetter(
        typeof(LuaValue),
        nameof(LuaValue.IsTruthy));
    private static readonly MethodInfo UnaryInteger = Method(
        typeof(LuaValueOperations),
        "UnaryIntegerSpecialized",
        [typeof(LuaIrUnaryOperator), typeof(LuaValue)]);
    private static readonly MethodInfo UnaryFloat = Method(
        typeof(LuaValueOperations),
        "UnaryFloatSpecialized",
        [typeof(LuaIrUnaryOperator), typeof(LuaValue)]);
    private static readonly MethodInfo GenericUnary = Method(
        typeof(LuaValueOperations),
        nameof(LuaValueOperations.Unary),
        [typeof(LuaIrUnaryOperator), typeof(LuaValue)]);
    private static readonly MethodInfo BinaryInteger = Method(
        typeof(LuaValueOperations),
        "BinaryIntegerSpecialized",
        [typeof(LuaIrBinaryOperator), typeof(LuaValue), typeof(LuaValue)]);
    private static readonly MethodInfo BinaryFloat = Method(
        typeof(LuaValueOperations),
        "BinaryFloatSpecialized",
        [typeof(LuaIrBinaryOperator), typeof(LuaValue), typeof(LuaValue)]);
    private static readonly MethodInfo BinaryMixedNumeric = Method(
        typeof(LuaValueOperations),
        "BinaryMixedNumericSpecialized",
        [typeof(LuaIrBinaryOperator), typeof(LuaValue), typeof(LuaValue)]);
    private static readonly MethodInfo PollExit = Method(
        typeof(LuaCompiledExit),
        nameof(LuaCompiledExit.Poll),
        [typeof(int), typeof(int), typeof(LuaCompiledExitReason)]);
    private static readonly MethodInfo ReturnExit = Method(
        typeof(LuaCompiledExit),
        nameof(LuaCompiledExit.Return),
        [typeof(int), typeof(int)]);
    private static readonly MethodInfo DeoptExit = Method(
        typeof(LuaCompiledExit),
        nameof(LuaCompiledExit.Deopt),
        [typeof(int), typeof(int), typeof(LuaCompiledExitReason)]);

    [RequiresDynamicCode("Tier 2 CIL specialization requires Reflection.Emit support.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "The caller checks dynamic-code support before requesting Tier 2 compilation.")]
    public static bool TryCompile(
        LuaIrFunction function,
        ImmutableDictionary<int, ProfileGuidedLuaTier2Compiler.OptimizedInstruction> optimized,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out LuaCompiledMethod? method,
        out long estimatedCodeBytes,
        out LuaTier2EmissionMetrics metrics)
    {
        method = null;
        estimatedCodeBytes = 0;
        metrics = default;
        if (!RuntimeFeature.IsDynamicCodeSupported ||
            EvaluateNumericSpecialization(function, optimized) !=
                LuaTier2NumericSpecializationStatus.Eligible)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var emissionStarted = Stopwatch.GetTimestamp();
        var dynamicMethod = new DynamicMethod(
            $"lunil_jit2_numeric_f{function.Id}",
            typeof(LuaCompiledExit),
            CompiledMethodParameters,
            typeof(ReflectionEmitLuaTier2Compiler).Module,
            skipVisibility: true);
        var generator = dynamicMethod.GetILGenerator();
        var firstValue = generator.DeclareLocal(typeof(LuaValue));
        var secondValue = generator.DeclareLocal(typeof(LuaValue));
        var labels = new Label[function.Instructions.Length];
        var budgetExits = new Label[function.Instructions.Length];
        var guardExits = new Label[function.Instructions.Length];
        var slowPathExits = new Label[function.Instructions.Length];
        for (var pc = 0; pc < labels.Length; pc++)
        {
            labels[pc] = generator.DefineLabel();
            budgetExits[pc] = generator.DefineLabel();
            guardExits[pc] = generator.DefineLabel();
            slowPathExits[pc] = generator.DefineLabel();
        }

        var debugExit = generator.DefineLabel();
        var invalidatedExit = generator.DefineLabel();
        EmitEntryGuard(generator, function, debugExit);
        generator.Emit(OpCodes.Ldarg_2);
        generator.Emit(OpCodes.Callvirt, GetProgramCounter);
        generator.Emit(OpCodes.Switch, labels);
        generator.Emit(OpCodes.Br, invalidatedExit);

        for (var pc = 0; pc < function.Instructions.Length; pc++)
        {
            if ((pc & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            generator.MarkLabel(labels[pc]);
            var instruction = function.Instructions[pc];
            if (optimized.TryGetValue(pc, out var optimization))
            {
                EmitOptimizedInstruction(
                    generator,
                    function,
                    pc,
                    instruction,
                    optimization,
                    labels,
                    budgetExits[pc],
                    guardExits[pc],
                    slowPathExits[pc],
                    invalidatedExit,
                    firstValue,
                    secondValue);
            }
            else
            {
                EmitDirectInstruction(
                    generator,
                    function,
                    pc,
                    instruction,
                    labels,
                    budgetExits[pc],
                    slowPathExits[pc],
                    invalidatedExit,
                    firstValue);
            }
        }

        for (var pc = 0; pc < function.Instructions.Length; pc++)
        {
            generator.MarkLabel(budgetExits[pc]);
            EmitExit(generator, PollExit, pc, LuaCompiledExitReason.InstructionBudget);
            generator.MarkLabel(guardExits[pc]);
            EmitExit(generator, DeoptExit, pc, LuaCompiledExitReason.GuardFailure);
            generator.MarkLabel(slowPathExits[pc]);
            EmitSlowPath(generator, pc);
        }

        generator.MarkLabel(debugExit);
        EmitFrameProgramCounterExit(
            generator,
            DeoptExit,
            LuaCompiledExitReason.DebugModeChanged);
        generator.MarkLabel(invalidatedExit);
        EmitFrameProgramCounterExit(
            generator,
            DeoptExit,
            LuaCompiledExitReason.BackendInvalidated);

        cancellationToken.ThrowIfCancellationRequested();
        var cilEmissionDuration = Stopwatch.GetElapsedTime(emissionStarted);
        var delegateStarted = Stopwatch.GetTimestamp();
        method = (LuaCompiledMethod)dynamicMethod.CreateDelegate(typeof(LuaCompiledMethod));
        var delegateCreationDuration = Stopwatch.GetElapsedTime(delegateStarted);
        estimatedCodeBytes = checked(
            function.Instructions.Length * 32L + optimized.Count * 48L);
        metrics = new LuaTier2EmissionMetrics(
            cilEmissionDuration,
            delegateCreationDuration);
        return true;
    }

    internal static LuaTier2NumericSpecializationStatus EvaluateNumericSpecialization(
        LuaIrFunction function,
        ImmutableDictionary<int, ProfileGuidedLuaTier2Compiler.OptimizedInstruction> optimized)
    {
        var hasNumericHotspot = false;
        foreach (var pair in optimized)
        {
            var optimization = pair.Value;
            switch (optimization.Kind)
            {
                case LuaJitOptimizationKind.DeadMove:
                case LuaJitOptimizationKind.BooleanBranch:
                    break;
                case LuaJitOptimizationKind.NumericUnary:
                    if (!IsExactNumericKind(optimization.FirstKinds) ||
                        (LuaIrUnaryOperator)function.Instructions[pair.Key].C is not
                            (LuaIrUnaryOperator.Negate or
                                LuaIrUnaryOperator.BitwiseNot or
                                LuaIrUnaryOperator.LogicalNot))
                    {
                        return LuaTier2NumericSpecializationStatus.PolymorphicNumericProfile;
                    }

                    hasNumericHotspot = true;
                    break;
                case LuaJitOptimizationKind.NumericBinary:
                    if (!IsExactNumericKind(optimization.FirstKinds) ||
                        !IsExactNumericKind(optimization.SecondKinds))
                    {
                        return LuaTier2NumericSpecializationStatus.PolymorphicNumericProfile;
                    }

                    hasNumericHotspot = true;
                    break;
                default:
                    return LuaTier2NumericSpecializationStatus.ManagedOptimizationRequired;
            }
        }

        return hasNumericHotspot
            ? LuaTier2NumericSpecializationStatus.Eligible
            : LuaTier2NumericSpecializationStatus.NoNumericHotspot;
    }

    private static bool IsExactNumericKind(LuaJitValueKinds kinds) =>
        kinds is LuaJitValueKinds.Integer or LuaJitValueKinds.Float;

    private static void EmitEntryGuard(
        ILGenerator generator,
        LuaIrFunction function,
        Label debugExit)
    {
        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Ldarg_2);
        EmitInt32(generator, function.Id);
        EmitInt32(generator, function.RegisterCount);
        generator.Emit(OpCodes.Call, CanExecuteCompiledFrame);
        generator.Emit(OpCodes.Brfalse, debugExit);
    }

    private static void EmitOptimizedInstruction(
        ILGenerator generator,
        LuaIrFunction function,
        int pc,
        LuaIrInstruction instruction,
        ProfileGuidedLuaTier2Compiler.OptimizedInstruction optimization,
        Label[] labels,
        Label budgetExit,
        Label guardExit,
        Label slowPathExit,
        Label invalidatedExit,
        LocalBuilder firstValue,
        LocalBuilder secondValue)
    {
        switch (optimization.Kind)
        {
            case LuaJitOptimizationKind.DeadMove:
                EmitReserve(generator, optimization.InstructionCount, budgetExit);
                EmitNext(generator, pc + optimization.InstructionCount, labels, invalidatedExit);
                break;
            case LuaJitOptimizationKind.NumericUnary:
                EmitReadRegister(generator, instruction.B);
                generator.Emit(OpCodes.Stloc, firstValue);
                EmitKindGuard(generator, firstValue, optimization.FirstKinds, guardExit);
                EmitReserve(generator, optimization.InstructionCount, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitInt32(generator, instruction.C);
                generator.Emit(OpCodes.Ldloc, firstValue);
                generator.Emit(
                    OpCodes.Call,
                    GetUnaryMethod(
                        optimization.FirstKinds,
                        (LuaIrUnaryOperator)instruction.C));
                generator.Emit(OpCodes.Call, WriteRegister);
                EmitNext(generator, pc + optimization.InstructionCount, labels, invalidatedExit);
                break;
            case LuaJitOptimizationKind.NumericBinary:
                EmitReadRegister(generator, instruction.B);
                generator.Emit(OpCodes.Stloc, firstValue);
                EmitReadRegister(generator, instruction.C);
                generator.Emit(OpCodes.Stloc, secondValue);
                EmitKindGuard(generator, firstValue, optimization.FirstKinds, guardExit);
                EmitKindGuard(generator, secondValue, optimization.SecondKinds, guardExit);
                EmitReserve(generator, optimization.InstructionCount, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitInt32(generator, instruction.D);
                generator.Emit(OpCodes.Ldloc, firstValue);
                generator.Emit(OpCodes.Ldloc, secondValue);
                generator.Emit(
                    OpCodes.Call,
                    GetBinaryMethod(optimization.FirstKinds, optimization.SecondKinds));
                generator.Emit(OpCodes.Call, WriteRegister);
                EmitNext(generator, pc + optimization.InstructionCount, labels, invalidatedExit);
                break;
            case LuaJitOptimizationKind.BooleanBranch:
                EmitReadRegister(generator, instruction.A);
                generator.Emit(OpCodes.Stloc, firstValue);
                generator.Emit(OpCodes.Ldloca, firstValue);
                generator.Emit(OpCodes.Call, IsTruthy);
                if (instruction.Opcode == LuaIrOpcode.JumpIfFalse)
                {
                    generator.Emit(OpCodes.Ldc_I4_0);
                    generator.Emit(OpCodes.Ceq);
                }

                generator.Emit(
                    optimization.ExpectedBranchTaken ? OpCodes.Brfalse : OpCodes.Brtrue,
                    guardExit);
                EmitReserve(generator, optimization.InstructionCount, budgetExit);
                EmitNext(
                    generator,
                    optimization.ExpectedBranchTaken ? instruction.B : pc + 1,
                    labels,
                    invalidatedExit);
                break;
            default:
                generator.Emit(OpCodes.Br, slowPathExit);
                break;
        }
    }

    private static void EmitDirectInstruction(
        ILGenerator generator,
        LuaIrFunction function,
        int pc,
        LuaIrInstruction instruction,
        Label[] labels,
        Label budgetExit,
        Label slowPathExit,
        Label invalidatedExit,
        LocalBuilder firstValue)
    {
        switch (instruction.Opcode)
        {
            case LuaIrOpcode.LoadConstant:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.B);
                generator.Emit(OpCodes.Call, MaterializeConstant);
                generator.Emit(OpCodes.Call, WriteRegister);
                EmitNext(generator, pc + 1, labels, invalidatedExit);
                break;
            case LuaIrOpcode.LoadNil:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitInt32(generator, instruction.B);
                generator.Emit(OpCodes.Call, ClearRegisters);
                EmitNext(generator, pc + 1, labels, invalidatedExit);
                break;
            case LuaIrOpcode.Move:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitReadRegister(generator, instruction.B);
                generator.Emit(OpCodes.Call, WriteRegister);
                EmitNext(generator, pc + 1, labels, invalidatedExit);
                break;
            case LuaIrOpcode.SetTop:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                generator.Emit(OpCodes.Call, SetFrameTop);
                EmitNext(generator, pc + 1, labels, invalidatedExit);
                break;
            case LuaIrOpcode.Close:
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                generator.Emit(OpCodes.Call, CanSkipClose);
                generator.Emit(OpCodes.Brfalse, slowPathExit);
                EmitReserve(generator, 1, budgetExit);
                EmitNext(generator, pc + 1, labels, invalidatedExit);
                break;
            case LuaIrOpcode.Jump when instruction.C < 0:
                EmitReserve(generator, 1, budgetExit);
                EmitNext(generator, instruction.B, labels, invalidatedExit);
                break;
            case LuaIrOpcode.JumpIfFalse:
            case LuaIrOpcode.JumpIfTrue:
                EmitReserve(generator, 1, budgetExit);
                EmitReadRegister(generator, instruction.A);
                generator.Emit(OpCodes.Stloc, firstValue);
                generator.Emit(OpCodes.Ldloca, firstValue);
                generator.Emit(OpCodes.Call, IsTruthy);
                var taken = generator.DefineLabel();
                generator.Emit(
                    instruction.Opcode == LuaIrOpcode.JumpIfTrue
                        ? OpCodes.Brtrue
                        : OpCodes.Brfalse,
                    taken);
                EmitNext(generator, pc + 1, labels, invalidatedExit);
                generator.MarkLabel(taken);
                EmitNext(generator, instruction.B, labels, invalidatedExit);
                break;
            case LuaIrOpcode.Return:
                EmitReserve(generator, 1, budgetExit);
                EmitInt32(generator, pc);
                EmitInstructionsConsumed(generator);
                generator.Emit(OpCodes.Call, ReturnExit);
                generator.Emit(OpCodes.Ret);
                break;
            default:
                generator.Emit(OpCodes.Br, slowPathExit);
                break;
        }
    }

    private static void EmitKindGuard(
        ILGenerator generator,
        LocalBuilder value,
        LuaJitValueKinds kinds,
        Label guardExit)
    {
        generator.Emit(OpCodes.Ldloca, value);
        generator.Emit(
            OpCodes.Call,
            kinds == LuaJitValueKinds.Integer ? IsInteger : IsFloat);
        generator.Emit(OpCodes.Brfalse, guardExit);
    }

    private static MethodInfo GetUnaryMethod(
        LuaJitValueKinds kinds,
        LuaIrUnaryOperator operation)
    {
        if (operation == LuaIrUnaryOperator.LogicalNot)
        {
            return GenericUnary;
        }

        return kinds == LuaJitValueKinds.Integer ? UnaryInteger : UnaryFloat;
    }

    private static MethodInfo GetBinaryMethod(
        LuaJitValueKinds first,
        LuaJitValueKinds second)
    {
        if (first == LuaJitValueKinds.Integer && second == LuaJitValueKinds.Integer)
        {
            return BinaryInteger;
        }

        return first == LuaJitValueKinds.Float && second == LuaJitValueKinds.Float
            ? BinaryFloat
            : BinaryMixedNumeric;
    }

    private static void EmitReserve(
        ILGenerator generator,
        int instructionCount,
        Label budgetExit)
    {
        generator.Emit(OpCodes.Ldarg_0);
        EmitInt32(generator, instructionCount);
        generator.Emit(OpCodes.Callvirt, ReserveInstructions);
        generator.Emit(OpCodes.Brfalse, budgetExit);
    }

    private static void EmitReadRegister(ILGenerator generator, int register)
    {
        generator.Emit(OpCodes.Ldarg_1);
        generator.Emit(OpCodes.Ldarg_2);
        EmitInt32(generator, register);
        generator.Emit(OpCodes.Call, ReadRegister);
    }

    private static void EmitNext(
        ILGenerator generator,
        int nextProgramCounter,
        Label[] labels,
        Label invalidatedExit)
    {
        generator.Emit(OpCodes.Ldarg_2);
        EmitInt32(generator, nextProgramCounter);
        generator.Emit(OpCodes.Callvirt, SetProgramCounter);
        generator.Emit(
            OpCodes.Br,
            (uint)nextProgramCounter < (uint)labels.Length
                ? labels[nextProgramCounter]
                : invalidatedExit);
    }

    private static void EmitSlowPath(ILGenerator generator, int programCounter)
    {
        generator.Emit(OpCodes.Ldarg_2);
        EmitInt32(generator, programCounter);
        generator.Emit(OpCodes.Call, CommitProgramCounter);
        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Ldarg_1);
        generator.Emit(OpCodes.Ldarg_2);
        EmitInt32(generator, programCounter);
        generator.Emit(OpCodes.Call, ExecuteCanonicalInstruction);
        generator.Emit(OpCodes.Ret);
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

    private static void EmitFrameProgramCounterExit(
        ILGenerator generator,
        MethodInfo factory,
        LuaCompiledExitReason reason)
    {
        generator.Emit(OpCodes.Ldarg_2);
        generator.Emit(OpCodes.Callvirt, GetProgramCounter);
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

    private static MethodInfo Method(
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicMethods |
            DynamicallyAccessedMemberTypes.NonPublicMethods)]
        Type type,
        string name,
        Type[] parameterTypes) =>
        type.GetMethod(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                BindingFlags.Instance,
            parameterTypes) ??
        throw new MissingMethodException(type.FullName, name);

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
        throw new MissingMethodException(type.FullName, $"get_{name}");

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
        throw new MissingMethodException(type.FullName, $"set_{name}");
}
