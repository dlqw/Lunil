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

internal readonly record struct LuaLoopOsrEmissionMetrics(
    TimeSpan CilEmissionDuration,
    TimeSpan DelegateCreationDuration);

internal static class ReflectionEmitLuaLoopOsrCompiler
{
    private static readonly Type[] CompiledMethodParameters =
    [
        typeof(LuaExecutionContext),
        typeof(LuaThread),
        typeof(LuaFrame),
    ];

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
    private static readonly MethodInfo CheckLoopOsrHeader = Method(
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
    private static readonly MethodInfo ClearRegisters = Method(
        typeof(LuaCodegenAbiV2),
        nameof(LuaCodegenAbiV2.ClearRegistersUnchecked),
        [typeof(LuaThread), typeof(LuaFrame), typeof(int), typeof(int)]);
    private static readonly MethodInfo SetFrameTop = Method(
        typeof(LuaCodegenAbiV2),
        nameof(LuaCodegenAbiV2.SetFrameTopUnchecked),
        [typeof(LuaThread), typeof(LuaFrame), typeof(int)]);
    private static readonly MethodInfo ReadTruthyAndSetFrameTop = Method(
        typeof(LuaCodegenAbiV2),
        nameof(LuaCodegenAbiV2.ReadTruthyAndSetFrameTopUnchecked),
        [typeof(LuaThread), typeof(LuaFrame), typeof(int), typeof(int)]);
    private static readonly MethodInfo CanSkipClose = Method(
        typeof(LuaCodegenAbiV2),
        nameof(LuaCodegenAbiV2.CanSkipClose),
        [typeof(LuaFrame), typeof(int)]);
    private static readonly MethodInfo MaterializeConstant = Method(
        typeof(LuaCodegenAbiV1),
        nameof(LuaCodegenAbiV1.MaterializeConstant),
        [typeof(LuaExecutionContext), typeof(LuaFrame), typeof(int)]);
    private static readonly MethodInfo ObserveLoopOsrBackedge = Method(
        typeof(LuaCodegenAbiV1),
        nameof(LuaCodegenAbiV1.ObserveLoopOsrBackedge),
        [typeof(LuaExecutionContext), typeof(LuaFrame), typeof(int)]);
    private static readonly MethodInfo ExecuteNumericForPrepare = Method(
        typeof(LuaCodegenAbiV2),
        nameof(LuaCodegenAbiV2.ExecuteNumericForPrepare),
        [typeof(LuaThread), typeof(LuaFrame), typeof(int), typeof(int)]);
    private static readonly MethodInfo ExecuteNumericForLoop = Method(
        typeof(LuaCodegenAbiV2),
        nameof(LuaCodegenAbiV2.ExecuteNumericForLoop),
        [typeof(LuaThread), typeof(LuaFrame), typeof(int), typeof(int)]);
    private static readonly MethodInfo ReserveInstructions = Method(
        typeof(LuaExecutionContext),
        nameof(LuaExecutionContext.TryReserveInstructions),
        [typeof(int)]);
    private static readonly MethodInfo GetInstructionsConsumed = PropertyGetter(
        typeof(LuaExecutionContext),
        nameof(LuaExecutionContext.InstructionsConsumed));
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

    public static void PrepareRuntimeAbi()
    {
        _ = CanEnterLoopOsr;
        _ = CheckLoopOsrHeader;
    }

    public static bool IsEligible(
        LuaIrFunction function,
        LuaJitLoopOsrPlan plan,
        out int specializedInstructionCount,
        out int guardCount)
    {
        var eligibility = LuaLoopOsrEligibilityEvaluator.Evaluate(function, plan);
        specializedInstructionCount = eligibility.SpecializedInstructionCount;
        guardCount = eligibility.GuardCount;
        return eligibility.IsAutoEligible;
    }

    [RequiresDynamicCode("Loop OSR CIL specialization requires Reflection.Emit support.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "The caller checks dynamic-code support before requesting loop OSR compilation.")]
    public static bool TryCompile(
        LuaIrFunction function,
        LuaJitLoopOsrPlan plan,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out LuaCompiledMethod? method,
        out long estimatedCodeBytes,
        out int specializedInstructionCount,
        out int guardCount,
        out LuaLoopOsrEmissionMetrics metrics)
    {
        method = null;
        estimatedCodeBytes = 0;
        specializedInstructionCount = 0;
        guardCount = 0;
        metrics = default;
        if (!RuntimeFeature.IsDynamicCodeSupported ||
            !IsEligible(function, plan, out specializedInstructionCount, out guardCount))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var emissionStarted = Stopwatch.GetTimestamp();
        var dynamicMethod = new DynamicMethod(
            $"lunil_osr_numeric_f{function.Id}_h{plan.HeaderProgramCounter}",
            typeof(LuaCompiledExit),
            CompiledMethodParameters,
            typeof(ReflectionEmitLuaLoopOsrCompiler).Module,
            skipVisibility: true);
        var generator = dynamicMethod.GetILGenerator();
        var firstValue = generator.DeclareLocal(typeof(LuaValue));
        var secondValue = generator.DeclareLocal(typeof(LuaValue));
        var resultValue = generator.DeclareLocal(typeof(LuaValue));
        var headerReason = generator.DeclareLocal(typeof(LuaCompiledExitReason));
        var insideLoop = plan.ProgramCounters.ToHashSet();
        var labels = plan.ProgramCounters.ToDictionary(
            static pc => pc,
            _ => generator.DefineLabel());
        var budgetExits = plan.ProgramCounters.ToDictionary(
            static pc => pc,
            _ => generator.DefineLabel());
        var guardExits = plan.ProgramCounters.ToDictionary(
            static pc => pc,
            _ => generator.DefineLabel());
        var outsideExit = generator.DefineLabel();
        var invalidatedExit = generator.DefineLabel();

        EmitEntryGuard(generator, function, plan, guardExits[plan.HeaderProgramCounter]);
        EmitDispatchCurrentProgramCounter(
            generator,
            function.Instructions.Length,
            labels,
            invalidatedExit);

        foreach (var pc in plan.ProgramCounters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            generator.MarkLabel(labels[pc]);
            if (pc == plan.HeaderProgramCounter)
            {
                EmitHeaderGuard(generator, pc, headerReason, guardExits[pc]);
            }

            if (pc == plan.BackedgeProgramCounter)
            {
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, pc);
                generator.Emit(OpCodes.Call, ObserveLoopOsrBackedge);
            }

            EmitInstruction(
                generator,
                function,
                pc,
                function.Instructions[pc],
                labels,
                insideLoop,
                budgetExits[pc],
                guardExits[pc],
                outsideExit,
                firstValue,
                secondValue,
                resultValue);
        }

        foreach (var pc in plan.ProgramCounters)
        {
            generator.MarkLabel(budgetExits[pc]);
            EmitExit(generator, PollExit, pc, LuaCompiledExitReason.InstructionBudget);
            generator.MarkLabel(guardExits[pc]);
            EmitExit(generator, DeoptExit, pc, LuaCompiledExitReason.GuardFailure);
        }

        generator.MarkLabel(outsideExit);
        EmitFrameProgramCounterExit(generator, ContinueExit);
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
            plan.ProgramCounters.Length * 40L +
            specializedInstructionCount * 64L +
            guardCount * 16L);
        metrics = new LuaLoopOsrEmissionMetrics(
            cilEmissionDuration,
            delegateCreationDuration);
        return true;
    }

    private static void EmitEntryGuard(
        ILGenerator generator,
        LuaIrFunction function,
        LuaJitLoopOsrPlan plan,
        Label guardExit)
    {
        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Ldarg_1);
        generator.Emit(OpCodes.Ldarg_2);
        EmitInt32(generator, function.Id);
        EmitInt32(generator, function.RegisterCount);
        EmitInt32(generator, plan.HeaderProgramCounter);
        generator.Emit(OpCodes.Call, CanEnterLoopOsr);
        generator.Emit(OpCodes.Brfalse, guardExit);
    }

    private static void EmitHeaderGuard(
        ILGenerator generator,
        int programCounter,
        LocalBuilder reason,
        Label guardExit)
    {
        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Ldarg_1);
        generator.Emit(OpCodes.Ldarg_2);
        generator.Emit(OpCodes.Call, CheckLoopOsrHeader);
        generator.Emit(OpCodes.Stloc, reason);
        var execute = generator.DefineLabel();
        generator.Emit(OpCodes.Ldloc, reason);
        EmitInt32(generator, (int)LuaCompiledExitReason.None);
        generator.Emit(OpCodes.Beq, execute);
        generator.Emit(OpCodes.Ldloc, reason);
        EmitInt32(generator, (int)LuaCompiledExitReason.GuardFailure);
        generator.Emit(OpCodes.Beq, guardExit);
        EmitInt32(generator, programCounter);
        EmitInstructionsConsumed(generator);
        generator.Emit(OpCodes.Ldloc, reason);
        generator.Emit(OpCodes.Call, PollExit);
        generator.Emit(OpCodes.Ret);
        generator.MarkLabel(execute);
    }

    private static void EmitInstruction(
        ILGenerator generator,
        LuaIrFunction function,
        int pc,
        LuaIrInstruction instruction,
        IReadOnlyDictionary<int, Label> labels,
        IReadOnlySet<int> insideLoop,
        Label budgetExit,
        Label guardExit,
        Label outsideExit,
        LocalBuilder firstValue,
        LocalBuilder secondValue,
        LocalBuilder resultValue)
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
                EmitNext(generator, pc + 1, labels, insideLoop, outsideExit);
                break;
            case LuaIrOpcode.LoadNil:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitInt32(generator, instruction.B);
                generator.Emit(OpCodes.Call, ClearRegisters);
                EmitNext(generator, pc + 1, labels, insideLoop, outsideExit);
                break;
            case LuaIrOpcode.Move:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitReadRegister(generator, instruction.B);
                generator.Emit(OpCodes.Call, WriteRegister);
                EmitNext(generator, pc + 1, labels, insideLoop, outsideExit);
                break;
            case LuaIrOpcode.SetTop:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                generator.Emit(OpCodes.Call, SetFrameTop);
                EmitNext(generator, pc + 1, labels, insideLoop, outsideExit);
                break;
            case LuaIrOpcode.Jump:
                if (instruction.C >= 0)
                {
                    EmitCanSkipClose(generator, instruction.C, guardExit);
                }

                EmitReserve(generator, 1, budgetExit);
                EmitNext(generator, instruction.B, labels, insideLoop, outsideExit);
                break;
            case LuaIrOpcode.Close:
                EmitCanSkipClose(generator, instruction.A, guardExit);
                EmitReserve(generator, 1, budgetExit);
                EmitNext(generator, pc + 1, labels, insideLoop, outsideExit);
                break;
            case LuaIrOpcode.JumpIfFalse:
            case LuaIrOpcode.JumpIfTrue:
                EmitReserve(generator, 1, budgetExit);
                if (instruction.D != 0)
                {
                    generator.Emit(OpCodes.Ldarg_1);
                    generator.Emit(OpCodes.Ldarg_2);
                    EmitInt32(generator, instruction.A);
                    EmitInt32(generator, instruction.C);
                    generator.Emit(OpCodes.Call, ReadTruthyAndSetFrameTop);
                }
                else
                {
                    EmitReadRegister(generator, instruction.A);
                    generator.Emit(OpCodes.Stloc, firstValue);
                    generator.Emit(OpCodes.Ldloca, firstValue);
                    generator.Emit(OpCodes.Call, IsTruthy);
                }
                var taken = generator.DefineLabel();
                generator.Emit(
                    instruction.Opcode == LuaIrOpcode.JumpIfTrue
                        ? OpCodes.Brtrue
                        : OpCodes.Brfalse,
                    taken);
                EmitNext(generator, pc + 1, labels, insideLoop, outsideExit);
                generator.MarkLabel(taken);
                EmitNext(generator, instruction.B, labels, insideLoop, outsideExit);
                break;
            case LuaIrOpcode.Unary:
                EmitUnary(
                    generator,
                    pc,
                    instruction,
                    labels,
                    insideLoop,
                    budgetExit,
                    guardExit,
                    outsideExit,
                    firstValue,
                    resultValue);
                break;
            case LuaIrOpcode.Binary:
                EmitBinary(
                    generator,
                    pc,
                    instruction,
                    labels,
                    insideLoop,
                    budgetExit,
                    guardExit,
                    outsideExit,
                    firstValue,
                    secondValue,
                    resultValue);
                break;
            case LuaIrOpcode.NumericForPrepare:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitInt32(generator, instruction.B);
                generator.Emit(OpCodes.Call, ExecuteNumericForPrepare);
                EmitDispatchCurrentProgramCounter(
                    generator,
                    function.Instructions.Length,
                    labels,
                    outsideExit);
                break;
            case LuaIrOpcode.NumericForLoop:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitInt32(generator, instruction.B);
                generator.Emit(OpCodes.Call, ExecuteNumericForLoop);
                EmitDispatchCurrentProgramCounter(
                    generator,
                    function.Instructions.Length,
                    labels,
                    outsideExit);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported specialized loop OSR opcode {instruction.Opcode}.");
        }
    }

    private static void EmitUnary(
        ILGenerator generator,
        int pc,
        LuaIrInstruction instruction,
        IReadOnlyDictionary<int, Label> labels,
        IReadOnlySet<int> insideLoop,
        Label budgetExit,
        Label guardExit,
        Label outsideExit,
        LocalBuilder operand,
        LocalBuilder result)
    {
        var operation = (LuaIrUnaryOperator)instruction.C;
        EmitReadRegister(generator, instruction.B);
        generator.Emit(OpCodes.Stloc, operand);
        if (operation == LuaIrUnaryOperator.BitwiseNot)
        {
            EmitIntegerGuard(generator, operand, guardExit);
        }
        else if (operation == LuaIrUnaryOperator.Negate)
        {
            EmitNumericGuard(generator, operand, guardExit);
        }

        EmitReserve(generator, 1, budgetExit);
        if (operation == LuaIrUnaryOperator.LogicalNot)
        {
            EmitInt32(generator, instruction.C);
            generator.Emit(OpCodes.Ldloc, operand);
            generator.Emit(OpCodes.Call, GenericUnary);
            generator.Emit(OpCodes.Stloc, result);
        }
        else if (operation == LuaIrUnaryOperator.BitwiseNot)
        {
            EmitInt32(generator, instruction.C);
            generator.Emit(OpCodes.Ldloc, operand);
            generator.Emit(OpCodes.Call, UnaryInteger);
            generator.Emit(OpCodes.Stloc, result);
        }
        else
        {
            var emitFloat = generator.DefineLabel();
            var write = generator.DefineLabel();
            generator.Emit(OpCodes.Ldloca, operand);
            generator.Emit(OpCodes.Call, IsInteger);
            generator.Emit(OpCodes.Brfalse, emitFloat);
            EmitInt32(generator, instruction.C);
            generator.Emit(OpCodes.Ldloc, operand);
            generator.Emit(OpCodes.Call, UnaryInteger);
            generator.Emit(OpCodes.Stloc, result);
            generator.Emit(OpCodes.Br, write);
            generator.MarkLabel(emitFloat);
            EmitInt32(generator, instruction.C);
            generator.Emit(OpCodes.Ldloc, operand);
            generator.Emit(OpCodes.Call, UnaryFloat);
            generator.Emit(OpCodes.Stloc, result);
            generator.MarkLabel(write);
        }

        EmitWriteResult(generator, instruction.A, result);
        EmitNext(generator, pc + 1, labels, insideLoop, outsideExit);
    }

    private static void EmitBinary(
        ILGenerator generator,
        int pc,
        LuaIrInstruction instruction,
        IReadOnlyDictionary<int, Label> labels,
        IReadOnlySet<int> insideLoop,
        Label budgetExit,
        Label guardExit,
        Label outsideExit,
        LocalBuilder left,
        LocalBuilder right,
        LocalBuilder result)
    {
        var operation = (LuaIrBinaryOperator)instruction.D;
        EmitReadRegister(generator, instruction.B);
        generator.Emit(OpCodes.Stloc, left);
        EmitReadRegister(generator, instruction.C);
        generator.Emit(OpCodes.Stloc, right);
        var integerOnly = operation is
            LuaIrBinaryOperator.BitwiseAnd or
            LuaIrBinaryOperator.BitwiseOr or
            LuaIrBinaryOperator.BitwiseXor or
            LuaIrBinaryOperator.ShiftLeft or
            LuaIrBinaryOperator.ShiftRight;
        if (integerOnly)
        {
            EmitIntegerGuard(generator, left, guardExit);
            EmitIntegerGuard(generator, right, guardExit);
        }
        else
        {
            EmitNumericGuard(generator, left, guardExit);
            EmitNumericGuard(generator, right, guardExit);
        }

        EmitReserve(generator, 1, budgetExit);
        if (integerOnly)
        {
            EmitBinaryCall(generator, instruction.D, left, right, BinaryInteger, result);
        }
        else
        {
            var leftFloat = generator.DefineLabel();
            var mixed = generator.DefineLabel();
            var write = generator.DefineLabel();
            generator.Emit(OpCodes.Ldloca, left);
            generator.Emit(OpCodes.Call, IsInteger);
            generator.Emit(OpCodes.Brfalse, leftFloat);
            generator.Emit(OpCodes.Ldloca, right);
            generator.Emit(OpCodes.Call, IsInteger);
            generator.Emit(OpCodes.Brfalse, mixed);
            EmitBinaryCall(generator, instruction.D, left, right, BinaryInteger, result);
            generator.Emit(OpCodes.Br, write);
            generator.MarkLabel(leftFloat);
            generator.Emit(OpCodes.Ldloca, right);
            generator.Emit(OpCodes.Call, IsFloat);
            generator.Emit(OpCodes.Brfalse, mixed);
            EmitBinaryCall(generator, instruction.D, left, right, BinaryFloat, result);
            generator.Emit(OpCodes.Br, write);
            generator.MarkLabel(mixed);
            EmitBinaryCall(generator, instruction.D, left, right, BinaryMixedNumeric, result);
            generator.MarkLabel(write);
        }

        EmitWriteResult(generator, instruction.A, result);
        EmitNext(generator, pc + 1, labels, insideLoop, outsideExit);
    }

    private static void EmitBinaryCall(
        ILGenerator generator,
        int operation,
        LocalBuilder left,
        LocalBuilder right,
        MethodInfo method,
        LocalBuilder result)
    {
        EmitInt32(generator, operation);
        generator.Emit(OpCodes.Ldloc, left);
        generator.Emit(OpCodes.Ldloc, right);
        generator.Emit(OpCodes.Call, method);
        generator.Emit(OpCodes.Stloc, result);
    }

    private static void EmitNumericGuard(
        ILGenerator generator,
        LocalBuilder value,
        Label guardExit)
    {
        var valid = generator.DefineLabel();
        generator.Emit(OpCodes.Ldloca, value);
        generator.Emit(OpCodes.Call, IsInteger);
        generator.Emit(OpCodes.Brtrue, valid);
        generator.Emit(OpCodes.Ldloca, value);
        generator.Emit(OpCodes.Call, IsFloat);
        generator.Emit(OpCodes.Brfalse, guardExit);
        generator.MarkLabel(valid);
    }

    private static void EmitCanSkipClose(
        ILGenerator generator,
        int register,
        Label guardExit)
    {
        generator.Emit(OpCodes.Ldarg_2);
        EmitInt32(generator, register);
        generator.Emit(OpCodes.Call, CanSkipClose);
        generator.Emit(OpCodes.Brfalse, guardExit);
    }

    private static void EmitIntegerGuard(
        ILGenerator generator,
        LocalBuilder value,
        Label guardExit)
    {
        generator.Emit(OpCodes.Ldloca, value);
        generator.Emit(OpCodes.Call, IsInteger);
        generator.Emit(OpCodes.Brfalse, guardExit);
    }

    private static void EmitWriteResult(
        ILGenerator generator,
        int register,
        LocalBuilder result)
    {
        generator.Emit(OpCodes.Ldarg_1);
        generator.Emit(OpCodes.Ldarg_2);
        EmitInt32(generator, register);
        generator.Emit(OpCodes.Ldloc, result);
        generator.Emit(OpCodes.Call, WriteRegister);
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
        IReadOnlyDictionary<int, Label> labels,
        IReadOnlySet<int> insideLoop,
        Label outsideExit)
    {
        generator.Emit(OpCodes.Ldarg_2);
        EmitInt32(generator, nextProgramCounter);
        generator.Emit(OpCodes.Callvirt, SetProgramCounter);
        generator.Emit(
            OpCodes.Br,
            insideLoop.Contains(nextProgramCounter)
                ? labels[nextProgramCounter]
                : outsideExit);
    }

    private static void EmitDispatchCurrentProgramCounter(
        ILGenerator generator,
        int instructionCount,
        IReadOnlyDictionary<int, Label> labels,
        Label outsideExit)
    {
        var dispatch = new Label[instructionCount];
        for (var pc = 0; pc < dispatch.Length; pc++)
        {
            dispatch[pc] = labels.TryGetValue(pc, out var label) ? label : outsideExit;
        }

        generator.Emit(OpCodes.Ldarg_2);
        generator.Emit(OpCodes.Callvirt, GetProgramCounter);
        generator.Emit(OpCodes.Switch, dispatch);
        generator.Emit(OpCodes.Br, outsideExit);
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
        MethodInfo factory)
    {
        generator.Emit(OpCodes.Ldarg_2);
        generator.Emit(OpCodes.Callvirt, GetProgramCounter);
        EmitInstructionsConsumed(generator);
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
