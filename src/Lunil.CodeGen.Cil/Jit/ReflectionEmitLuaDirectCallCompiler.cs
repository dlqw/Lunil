using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.CodeGen.Cil.Jit;

internal delegate bool LuaDirectCompiledMethod(
    LuaExecutionContext context,
    LuaThread thread,
    LuaFrame caller,
    int functionRegister,
    int argumentCount,
    int expectedResults);

internal sealed record LuaCompiledDirectCall(
    LuaDirectCompiledMethod Method,
    long EstimatedCodeBytes,
    int MaximumBackedges,
    LuaPrimitiveLeafTypePlan TypePlan);

internal sealed record LuaBoundDirectCall(
    LuaIrFunction Function,
    LuaDirectCompiledMethod Method,
    long EstimatedCodeBytes,
    string ModuleContentId,
    LuaPrimitiveLeafTypePlan TypePlan);

/// <summary>
/// Emits a bounded, side-effect-free primitive leaf directly against the caller result window.
/// The generated method keeps every callee register in CLR locals and commits budget/results only
/// at a fixed return. Any unstable path discards the locals and re-enters the ordinary scheduler.
/// </summary>
internal static class ReflectionEmitLuaDirectCallCompiler
{
    private enum DirectEmissionMode : byte
    {
        Standalone,
        FrameInline,
        NumericRegionInline,
    }

    private sealed record NumericRegionEmission(
        IReadOnlyList<LuaNumericIlLocal> Arguments,
        IReadOnlyList<LuaNumericIlLocal> Results,
        LuaNumericIlLocal Remaining,
        LuaNumericIlLocal Pending,
        LuaNumericIlLabel BudgetFallback,
        LuaNumericIlLabel SafepointFallback);

    private const int MaximumInstructions = 128;
    private const int MaximumRegisters = 32;
    private const int MaximumParameters = 8;
    private const int MaximumResults = 4;
    private const int MaximumBackedges = 1024;

    private static readonly Type[] DirectMethodParameters =
    [
        typeof(LuaExecutionContext),
        typeof(LuaThread),
        typeof(LuaFrame),
        typeof(int),
        typeof(int),
        typeof(int),
    ];

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
    private static readonly MethodInfo GetValueKind = PropertyGetter(
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
    private static readonly MethodInfo TryReserveInstructions = Method(
        typeof(LuaExecutionContext),
        nameof(LuaExecutionContext.TryReserveInstructions),
        [typeof(int)]);
    private static readonly MethodInfo CanExecuteBoundDirectCall = Method(
        typeof(LuaCodegenAbiV4),
        nameof(LuaCodegenAbiV4.CanExecuteBoundDirectCall),
        [typeof(LuaExecutionContext)]);
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
    private static readonly MethodInfo PrepareIntegerFor =
        typeof(ReflectionEmitLuaDirectCallCompiler).GetMethod(
            nameof(PrepareIntegerForLoop),
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            [typeof(long), typeof(long), typeof(long), typeof(long).MakeByRefType()],
            modifiers: null) ?? throw new MissingMethodException(
                typeof(ReflectionEmitLuaDirectCallCompiler).FullName,
                nameof(PrepareIntegerForLoop));

    [RequiresDynamicCode("Direct compiled calls require Reflection.Emit support.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "The JIT executor checks dynamic-code support before Tier 2 compilation.")]
    public static bool TryCompile(
        LuaIrFunction function,
        LuaJitFunctionProfile profile,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out LuaCompiledDirectCall? result)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(profile);
        result = null;
        if (!RuntimeFeature.IsDynamicCodeSupported ||
            !TryCreatePlan(function, profile, out var typePlan))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var dynamicMethod = new DynamicMethod(
            $"lunil_direct_primitive_f{function.Id}",
            typeof(bool),
            DirectMethodParameters,
            typeof(ReflectionEmitLuaDirectCallCompiler).Module,
            skipVisibility: true);
        Emit(
            function,
            typePlan,
            new ReflectionEmitLuaNumericRegionIlGenerator(dynamicMethod.GetILGenerator()),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var method = (LuaDirectCompiledMethod)dynamicMethod.CreateDelegate(
            typeof(LuaDirectCompiledMethod));
        result = new LuaCompiledDirectCall(
            method,
            checked(function.Instructions.Length * 40L + function.RegisterCount * 24L + 128L),
            MaximumBackedges,
            typePlan);
        return true;
    }

    internal static bool CanCompile(LuaIrFunction function, LuaJitFunctionProfile profile) =>
        TryCreatePlan(function, profile, out _);

    private static bool TryCreatePlan(
        LuaIrFunction function,
        LuaJitFunctionProfile profile,
        [NotNullWhen(true)] out LuaPrimitiveLeafTypePlan? typePlan)
    {
        typePlan = null;
        if (function.IsVarArg || function.Upvalues.Length != 0 ||
            function.Instructions.Length is 0 or > MaximumInstructions ||
            function.RegisterCount is 0 or > MaximumRegisters ||
            function.ParameterCount > MaximumParameters ||
            profile.ArgumentKinds.Length < function.ParameterCount)
        {
            return false;
        }

        return LuaPrimitiveLeafTypePlanner.TryCreate(function, profile, out typePlan) &&
            typePlan is { ResultKinds.Length: <= MaximumResults };
    }

    internal static bool CanInlineWithResultCount(LuaIrFunction function, int resultCount)
    {
        if (resultCount is < 0 or > MaximumResults || function.Instructions.IsEmpty)
        {
            return false;
        }

        var pending = new Stack<int>();
        var visited = new HashSet<int>();
        var sawReturn = false;
        pending.Push(0);
        while (pending.TryPop(out var pc))
        {
            if ((uint)pc >= (uint)function.Instructions.Length || !visited.Add(pc))
            {
                continue;
            }

            var instruction = function.Instructions[pc];
            if (instruction.Opcode == LuaIrOpcode.Return)
            {
                if (instruction.B != resultCount)
                {
                    return false;
                }

                sawReturn = true;
                continue;
            }

            switch (instruction.Opcode)
            {
                case LuaIrOpcode.Jump:
                    pending.Push(instruction.B);
                    break;
                case LuaIrOpcode.JumpIfFalse:
                case LuaIrOpcode.JumpIfTrue:
                case LuaIrOpcode.NumericForPrepare:
                case LuaIrOpcode.NumericForLoop:
                    pending.Push(instruction.B);
                    pending.Push(pc + 1);
                    break;
                default:
                    pending.Push(pc + 1);
                    break;
            }
        }

        return sawReturn;
    }

    private static void Emit(
        LuaIrFunction function,
        LuaPrimitiveLeafTypePlan typePlan,
        LuaNumericRegionIlGenerator generator,
        CancellationToken cancellationToken) => EmitCore(
            function,
            typePlan,
            generator,
            DirectEmissionMode.Standalone,
            functionRegister: 0,
            expectedResults: 0,
            success: default,
            externalFallback: default,
            numericRegion: null,
            cancellationToken);

    internal static void EmitInline(
        LuaIrFunction function,
        LuaPrimitiveLeafTypePlan typePlan,
        LuaNumericRegionIlGenerator generator,
        int functionRegister,
        int expectedResults,
        LuaNumericIlLabel success,
        LuaNumericIlLabel fallback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentOutOfRangeException.ThrowIfNegative(functionRegister);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedResults);
        EmitCore(
            function,
            typePlan,
            generator,
            DirectEmissionMode.FrameInline,
            functionRegister,
            expectedResults,
            success,
            fallback,
            numericRegion: null,
            cancellationToken);
    }

    internal static void EmitNumericRegionInline(
        LuaIrFunction function,
        LuaPrimitiveLeafTypePlan typePlan,
        LuaNumericRegionIlGenerator generator,
        IReadOnlyList<LuaNumericIlLocal> arguments,
        IReadOnlyList<LuaNumericIlLocal> results,
        LuaNumericIlLocal remaining,
        LuaNumericIlLocal pending,
        LuaNumericIlLabel success,
        LuaNumericIlLabel fallback,
        LuaNumericIlLabel budgetFallback,
        LuaNumericIlLabel safepointFallback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(results);
        if (arguments.Count != function.ParameterCount)
        {
            throw new ArgumentException(
                "The numeric direct-call argument window does not match the callee.",
                nameof(arguments));
        }

        EmitCore(
            function,
            typePlan,
            generator,
            DirectEmissionMode.NumericRegionInline,
            functionRegister: 0,
            expectedResults: results.Count,
            success,
            fallback,
            new NumericRegionEmission(
                arguments,
                results,
                remaining,
                pending,
                budgetFallback,
                safepointFallback),
            cancellationToken);
    }

    private static void EmitCore(
        LuaIrFunction function,
        LuaPrimitiveLeafTypePlan typePlan,
        LuaNumericRegionIlGenerator generator,
        DirectEmissionMode mode,
        int functionRegister,
        int expectedResults,
        LuaNumericIlLabel success,
        LuaNumericIlLabel externalFallback,
        NumericRegionEmission? numericRegion,
        CancellationToken cancellationToken)
    {
        var registers = typePlan.Locals.ToDictionary(
            static local => (local.Register, local.Kind),
            local => generator.DeclareLocal(LocalType(local.Kind)));
        var taggedValue = generator.DeclareLocal(typeof(LuaValue));
        var consumed = generator.DeclareLocal(typeof(int));
        var backedges = generator.DeclareLocal(typeof(int));
        var prepareStatus = generator.DeclareLocal(typeof(int));
        var integerTemporary = generator.DeclareLocal(typeof(long));
        var integerRemainder = generator.DeclareLocal(typeof(long));
        var labels = function.Instructions.Select(_ => generator.DefineLabel()).ToArray();
        var standalone = mode == DirectEmissionMode.Standalone;
        var frameInline = mode == DirectEmissionMode.FrameInline;
        var numericInline = mode == DirectEmissionMode.NumericRegionInline;
        var dynamicAccounting = function.Instructions.Any(static instruction =>
            instruction.Opcode is LuaIrOpcode.Jump or LuaIrOpcode.JumpIfFalse or
                LuaIrOpcode.JumpIfTrue or LuaIrOpcode.NumericForPrepare or
                LuaIrOpcode.NumericForLoop);
        var fallback = standalone ? generator.DefineLabel() : externalFallback;

        if (!standalone && !numericInline)
        {
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Call, CanExecuteBoundDirectCall);
            generator.Emit(
                OpCodes.Brfalse,
                numericInline ? numericRegion!.SafepointFallback : fallback);
        }

        // Inline IL is reached repeatedly when the caller contains a loop. Dynamic-method locals
        // are initialized only once per caller entry, so per-call accounting must be reset here
        // rather than relying on InitLocals as the standalone trampoline can.
        if (dynamicAccounting)
        {
            generator.Emit(OpCodes.Ldc_I4_0);
            generator.Emit(OpCodes.Dup);
            generator.Emit(OpCodes.Stloc, consumed);
            generator.Emit(OpCodes.Stloc, backedges);
        }

        if (standalone)
        {
            generator.Emit(OpCodes.Ldarg_S, 4);
            EmitInt32(generator, function.ParameterCount);
            generator.Emit(OpCodes.Bne_Un, fallback);
        }
        for (var parameter = 0; parameter < function.ParameterCount; parameter++)
        {
            var parameterKind = typePlan.ParameterKinds[parameter];
            var destination = NumericLocal(registers, parameter, parameterKind);
            if (numericInline)
            {
                generator.Emit(OpCodes.Ldloc, numericRegion!.Arguments[parameter]);
                generator.Emit(OpCodes.Stloc, destination);
                continue;
            }

            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Ldarg_2);
            if (frameInline)
            {
                EmitInt32(generator, functionRegister + parameter + 1);
            }
            else
            {
                generator.Emit(OpCodes.Ldarg_3);
                EmitInt32(generator, parameter + 1);
                generator.Emit(OpCodes.Add);
            }
            generator.Emit(OpCodes.Call, ReadRegister);
            generator.Emit(OpCodes.Stloc, taggedValue);
            generator.Emit(OpCodes.Ldloca, taggedValue);
            generator.Emit(OpCodes.Call, GetValueKind);
            EmitInt32(generator, (int)ValueKind(parameterKind));
            generator.Emit(OpCodes.Bne_Un, fallback);
            generator.Emit(OpCodes.Ldloca, taggedValue);
            generator.Emit(OpCodes.Call, AsMethod(parameterKind));
            generator.Emit(OpCodes.Stloc, destination);
        }

        generator.Emit(OpCodes.Br, labels[0]);
        for (var programCounter = 0;
             programCounter < function.Instructions.Length;
             programCounter++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            generator.MarkLabel(labels[programCounter]);
            if (dynamicAccounting)
            {
                generator.Emit(OpCodes.Ldloc, consumed);
                generator.Emit(OpCodes.Ldc_I4_1);
                generator.Emit(OpCodes.Add);
                generator.Emit(OpCodes.Stloc, consumed);
            }
            var instruction = function.Instructions[programCounter];
            switch (instruction.Opcode)
            {
                case LuaIrOpcode.LoadConstant:
                    EmitConstant(
                        generator,
                        function.Constants[instruction.B],
                        NumericLocal(
                            registers,
                            instruction.A,
                            typePlan.GetKindAfter(programCounter, instruction.A)));
                    EmitNext(generator, labels, programCounter, fallback);
                    break;
                case LuaIrOpcode.Move:
                    generator.Emit(
                        OpCodes.Ldloc,
                        NumericLocal(
                            registers,
                            instruction.B,
                            typePlan.GetKindBefore(programCounter, instruction.B)));
                    generator.Emit(
                        OpCodes.Stloc,
                        NumericLocal(
                            registers,
                            instruction.A,
                            typePlan.GetKindAfter(programCounter, instruction.A)));
                    EmitNext(generator, labels, programCounter, fallback);
                    break;
                case LuaIrOpcode.SetTop:
                    EmitNext(generator, labels, programCounter, fallback);
                    break;
                case LuaIrOpcode.Unary:
                    EmitUnary(
                        generator,
                        typePlan,
                        programCounter,
                        instruction,
                        registers);
                    EmitNext(generator, labels, programCounter, fallback);
                    break;
                case LuaIrOpcode.Binary:
                    EmitBinary(
                        generator,
                        typePlan,
                        programCounter,
                        instruction,
                        registers,
                        integerTemporary,
                        integerRemainder,
                        fallback);
                    EmitNext(generator, labels, programCounter, fallback);
                    break;
                case LuaIrOpcode.Jump:
                    EmitBoundedTransfer(
                        generator,
                        labels,
                        backedges,
                        programCounter,
                        instruction.B,
                        fallback);
                    break;
                case LuaIrOpcode.JumpIfFalse:
                case LuaIrOpcode.JumpIfTrue:
                    {
                        var operandKind = typePlan.GetKindBefore(
                            programCounter,
                            instruction.A);
                        if (operandKind == LuaNumericRegionValueKind.Boolean)
                        {
                            generator.Emit(
                                OpCodes.Ldloc,
                                NumericLocal(registers, instruction.A, operandKind));
                        }
                        else
                        {
                            generator.Emit(OpCodes.Ldc_I4_1);
                        }

                        var fallthrough = generator.DefineLabel();
                        generator.Emit(
                            instruction.Opcode == LuaIrOpcode.JumpIfTrue
                                ? OpCodes.Brfalse
                                : OpCodes.Brtrue,
                            fallthrough);
                        EmitBoundedTransfer(
                            generator,
                            labels,
                            backedges,
                            programCounter,
                            instruction.B,
                            fallback);
                        generator.MarkLabel(fallthrough);
                        EmitNext(generator, labels, programCounter, fallback);
                        break;
                    }
                case LuaIrOpcode.NumericForPrepare:
                    generator.Emit(OpCodes.Ldloc, IntegerLocal(registers, instruction.A));
                    generator.Emit(OpCodes.Stloc, IntegerLocal(registers, instruction.A + 3));
                    generator.Emit(OpCodes.Ldloc, IntegerLocal(registers, instruction.A));
                    generator.Emit(OpCodes.Ldloc, IntegerLocal(registers, instruction.A + 1));
                    generator.Emit(OpCodes.Ldloc, IntegerLocal(registers, instruction.A + 2));
                    generator.Emit(OpCodes.Ldloca, IntegerLocal(registers, instruction.A + 1));
                    generator.Emit(OpCodes.Call, PrepareIntegerFor);
                    generator.Emit(OpCodes.Stloc, prepareStatus);
                    generator.Emit(OpCodes.Ldloc, prepareStatus);
                    generator.Emit(OpCodes.Brfalse, fallback);
                    generator.Emit(OpCodes.Ldloc, prepareStatus);
                    generator.Emit(OpCodes.Ldc_I4_1);
                    generator.Emit(OpCodes.Beq, Target(labels, instruction.B, fallback));
                    EmitNext(generator, labels, programCounter, fallback);
                    break;
                case LuaIrOpcode.NumericForLoop:
                    {
                        var finished = generator.DefineLabel();
                        generator.Emit(OpCodes.Ldloc, IntegerLocal(registers, instruction.A + 1));
                        generator.Emit(OpCodes.Brfalse, finished);
                        generator.Emit(OpCodes.Ldloc, IntegerLocal(registers, instruction.A));
                        generator.Emit(OpCodes.Ldloc, IntegerLocal(registers, instruction.A + 2));
                        generator.Emit(OpCodes.Add);
                        generator.Emit(OpCodes.Stloc, IntegerLocal(registers, instruction.A));
                        generator.Emit(OpCodes.Ldloc, IntegerLocal(registers, instruction.A + 1));
                        generator.Emit(OpCodes.Ldc_I4_1);
                        generator.Emit(OpCodes.Conv_I8);
                        generator.Emit(OpCodes.Sub);
                        generator.Emit(OpCodes.Stloc, IntegerLocal(registers, instruction.A + 1));
                        generator.Emit(OpCodes.Ldloc, IntegerLocal(registers, instruction.A));
                        generator.Emit(OpCodes.Stloc, IntegerLocal(registers, instruction.A + 3));
                        EmitBoundedTransfer(
                            generator,
                            labels,
                            backedges,
                            programCounter,
                            instruction.B,
                            fallback);
                        generator.MarkLabel(finished);
                        EmitNext(generator, labels, programCounter, fallback);
                        break;
                    }
                case LuaIrOpcode.Return:
                    EmitReturn(
                        generator,
                        registers,
                        typePlan,
                        programCounter,
                        consumed,
                        dynamicAccounting ? -1 : programCounter + 1,
                        instruction,
                        fallback,
                        mode,
                        functionRegister,
                        expectedResults,
                        success,
                        numericRegion);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported direct-call instruction {instruction.Opcode}.");
            }
        }

        if (standalone)
        {
            generator.MarkLabel(fallback);
            generator.Emit(OpCodes.Ldc_I4_0);
            generator.Emit(OpCodes.Ret);
        }
    }

    private static void EmitReturn(
        LuaNumericRegionIlGenerator generator,
        IReadOnlyDictionary<
            (int Register, LuaNumericRegionValueKind Kind),
            LuaNumericIlLocal> registers,
        LuaPrimitiveLeafTypePlan typePlan,
        int programCounter,
        LuaNumericIlLocal consumed,
        int fixedConsumed,
        LuaIrInstruction instruction,
        LuaNumericIlLabel fallback,
        DirectEmissionMode mode,
        int functionRegister,
        int expectedResults,
        LuaNumericIlLabel success,
        NumericRegionEmission? numericRegion)
    {
        var standalone = mode == DirectEmissionMode.Standalone;
        var frameInline = mode == DirectEmissionMode.FrameInline;
        var numericInline = mode == DirectEmissionMode.NumericRegionInline;
        if (!standalone)
        {
            if (instruction.B != expectedResults)
            {
                generator.Emit(OpCodes.Br, fallback);
                return;
            }
        }
        else
        {
            generator.Emit(OpCodes.Ldarg_S, 5);
            EmitInt32(generator, instruction.B);
            generator.Emit(OpCodes.Bne_Un, fallback);
        }

        if (numericInline)
        {
            generator.Emit(OpCodes.Ldloc, numericRegion!.Remaining);
            EmitConsumed(generator, consumed, fixedConsumed, convertToInt64: true);
            generator.Emit(OpCodes.Blt, numericRegion.BudgetFallback);
            generator.Emit(OpCodes.Ldloc, numericRegion.Remaining);
            EmitConsumed(generator, consumed, fixedConsumed, convertToInt64: true);
            generator.Emit(OpCodes.Sub);
            generator.Emit(OpCodes.Stloc, numericRegion.Remaining);
            generator.Emit(OpCodes.Ldloc, numericRegion.Pending);
            EmitConsumed(generator, consumed, fixedConsumed, convertToInt64: false);
            generator.Emit(OpCodes.Add);
            generator.Emit(OpCodes.Stloc, numericRegion.Pending);
        }
        else
        {
            generator.Emit(OpCodes.Ldarg_0);
            EmitConsumed(generator, consumed, fixedConsumed, convertToInt64: false);
            generator.Emit(OpCodes.Callvirt, TryReserveInstructions);
            generator.Emit(OpCodes.Brfalse, fallback);
        }

        for (var index = 0; index < instruction.B; index++)
        {
            var kind = typePlan.GetKindBefore(
                programCounter,
                instruction.A + index);
            var source = NumericLocal(registers, instruction.A + index, kind);
            if (numericInline)
            {
                generator.Emit(OpCodes.Ldloc, source);
                generator.Emit(OpCodes.Stloc, numericRegion!.Results[index]);
                continue;
            }

            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Ldarg_2);
            if (frameInline)
            {
                EmitInt32(generator, functionRegister + index);
            }
            else
            {
                generator.Emit(OpCodes.Ldarg_3);
                EmitInt32(generator, index);
                generator.Emit(OpCodes.Add);
            }
            generator.Emit(OpCodes.Ldloc, source);
            generator.Emit(OpCodes.Call, FromMethod(kind));
            generator.Emit(OpCodes.Call, WriteRegister);
        }

        if (!numericInline)
        {
            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Ldarg_2);
            if (frameInline)
            {
                EmitInt32(generator, functionRegister + instruction.B);
            }
            else
            {
                generator.Emit(OpCodes.Ldarg_3);
                EmitInt32(generator, instruction.B);
                generator.Emit(OpCodes.Add);
            }
            generator.Emit(OpCodes.Call, SetFrameTop);
        }
        if (!standalone)
        {
            generator.Emit(OpCodes.Br, success);
        }
        else
        {
            generator.Emit(OpCodes.Ldc_I4_1);
            generator.Emit(OpCodes.Ret);
        }
    }

    private static void EmitConsumed(
        LuaNumericRegionIlGenerator generator,
        LuaNumericIlLocal consumed,
        int fixedConsumed,
        bool convertToInt64)
    {
        if (fixedConsumed >= 0)
        {
            EmitInt32(generator, fixedConsumed);
        }
        else
        {
            generator.Emit(OpCodes.Ldloc, consumed);
        }

        if (convertToInt64)
        {
            generator.Emit(OpCodes.Conv_I8);
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
                    $"Constant {constant.Kind} cannot enter a primitive direct call.");
        }

        generator.Emit(OpCodes.Stloc, destination);
    }

    private static void EmitUnary(
        LuaNumericRegionIlGenerator generator,
        LuaPrimitiveLeafTypePlan typePlan,
        int programCounter,
        LuaIrInstruction instruction,
        IReadOnlyDictionary<
            (int Register, LuaNumericRegionValueKind Kind),
            LuaNumericIlLocal> registers)
    {
        var sourceKind = typePlan.GetKindBefore(programCounter, instruction.B);
        var destinationKind = typePlan.GetKindAfter(programCounter, instruction.A);
        var source = NumericLocal(registers, instruction.B, sourceKind);
        var destination = NumericLocal(registers, instruction.A, destinationKind);
        var operation = (LuaIrUnaryOperator)instruction.C;
        switch (operation)
        {
            case LuaIrUnaryOperator.Negate:
            case LuaIrUnaryOperator.BitwiseNot:
                generator.Emit(OpCodes.Ldloc, source);
                generator.Emit(
                    operation == LuaIrUnaryOperator.Negate ? OpCodes.Neg : OpCodes.Not);
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
                throw new InvalidOperationException($"Unsupported primitive unary {operation}.");
        }

        generator.Emit(OpCodes.Stloc, destination);
    }

    private static void EmitBinary(
        LuaNumericRegionIlGenerator generator,
        LuaPrimitiveLeafTypePlan typePlan,
        int programCounter,
        LuaIrInstruction instruction,
        IReadOnlyDictionary<
            (int Register, LuaNumericRegionValueKind Kind),
            LuaNumericIlLocal> registers,
        LuaNumericIlLocal integerTemporary,
        LuaNumericIlLocal integerRemainder,
        LuaNumericIlLabel fallback)
    {
        var operation = (LuaIrBinaryOperator)instruction.D;
        var leftKind = typePlan.GetKindBefore(programCounter, instruction.B);
        var rightKind = typePlan.GetKindBefore(programCounter, instruction.C);
        var resultKind = typePlan.GetKindAfter(programCounter, instruction.A);
        var left = NumericLocal(registers, instruction.B, leftKind);
        var right = NumericLocal(registers, instruction.C, rightKind);
        var result = NumericLocal(registers, instruction.A, resultKind);
        if (IsComparison(operation))
        {
            EmitComparison(generator, operation, leftKind, rightKind, left, right);
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
                    generator.Emit(operation switch
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
                        fallback);
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
                    $"Unsupported floating primitive operation {operation}.");
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
        LuaNumericIlLabel fallback)
    {
        var nonZero = generator.DefineLabel();
        var notNegativeOne = generator.DefineLabel();
        var adjust = generator.DefineLabel();
        var write = generator.DefineLabel();
        generator.Emit(OpCodes.Ldloc, divisor);
        generator.Emit(OpCodes.Brtrue, nonZero);
        generator.Emit(OpCodes.Br, fallback);
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
        if (leftKind == LuaNumericRegionValueKind.Boolean)
        {
            generator.Emit(OpCodes.Ldloc, left);
            generator.Emit(OpCodes.Ldloc, right);
            generator.Emit(OpCodes.Ceq);
            if (operation == LuaIrBinaryOperator.NotEqual)
            {
                generator.Emit(OpCodes.Ldc_I4_0);
                generator.Emit(OpCodes.Ceq);
            }
            return;
        }

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

    private static Type LocalType(LuaNumericRegionValueKind kind) => kind switch
    {
        LuaNumericRegionValueKind.Integer => typeof(long),
        LuaNumericRegionValueKind.Float => typeof(double),
        LuaNumericRegionValueKind.Boolean => typeof(bool),
        _ => throw new InvalidOperationException($"{kind} is not primitive."),
    };

    private static LuaValueKind ValueKind(LuaNumericRegionValueKind kind) => kind switch
    {
        LuaNumericRegionValueKind.Integer => LuaValueKind.Integer,
        LuaNumericRegionValueKind.Float => LuaValueKind.Float,
        LuaNumericRegionValueKind.Boolean => LuaValueKind.Boolean,
        _ => throw new InvalidOperationException($"{kind} is not a Lua value kind."),
    };

    private static MethodInfo AsMethod(LuaNumericRegionValueKind kind) => kind switch
    {
        LuaNumericRegionValueKind.Integer => AsInteger,
        LuaNumericRegionValueKind.Float => AsFloat,
        LuaNumericRegionValueKind.Boolean => AsBoolean,
        _ => throw new InvalidOperationException($"{kind} has no Lua accessor."),
    };

    private static MethodInfo FromMethod(LuaNumericRegionValueKind kind) => kind switch
    {
        LuaNumericRegionValueKind.Integer => FromInteger,
        LuaNumericRegionValueKind.Float => FromFloat,
        LuaNumericRegionValueKind.Boolean => FromBoolean,
        _ => throw new InvalidOperationException($"{kind} has no Lua constructor."),
    };

    private static LuaNumericIlLocal NumericLocal(
        IReadOnlyDictionary<
            (int Register, LuaNumericRegionValueKind Kind),
            LuaNumericIlLocal> locals,
        int register,
        LuaNumericRegionValueKind kind) =>
        locals.TryGetValue((register, kind), out var local)
            ? local
            : throw new InvalidOperationException(
                $"Register r{register} has no primitive {kind} local.");

    private static LuaNumericIlLocal IntegerLocal(
        IReadOnlyDictionary<
            (int Register, LuaNumericRegionValueKind Kind),
            LuaNumericIlLocal> locals,
        int register) => NumericLocal(
            locals,
            register,
            LuaNumericRegionValueKind.Integer);

    private static bool IsComparison(LuaIrBinaryOperator operation) => operation is
        LuaIrBinaryOperator.Equal or LuaIrBinaryOperator.NotEqual or
        LuaIrBinaryOperator.LessThan or LuaIrBinaryOperator.LessThanOrEqual or
        LuaIrBinaryOperator.GreaterThan or LuaIrBinaryOperator.GreaterThanOrEqual;

    private static void EmitBoundedTransfer(
        LuaNumericRegionIlGenerator generator,
        LuaNumericIlLabel[] labels,
        LuaNumericIlLocal backedges,
        int sourceProgramCounter,
        int targetProgramCounter,
        LuaNumericIlLabel fallback)
    {
        if (targetProgramCounter <= sourceProgramCounter)
        {
            generator.Emit(OpCodes.Ldloc, backedges);
            generator.Emit(OpCodes.Ldc_I4_1);
            generator.Emit(OpCodes.Add);
            generator.Emit(OpCodes.Dup);
            generator.Emit(OpCodes.Stloc, backedges);
            EmitInt32(generator, MaximumBackedges);
            generator.Emit(OpCodes.Bgt, fallback);
        }

        generator.Emit(OpCodes.Br, Target(labels, targetProgramCounter, fallback));
    }

    private static void EmitNext(
        LuaNumericRegionIlGenerator generator,
        LuaNumericIlLabel[] labels,
        int programCounter,
        LuaNumericIlLabel fallback) =>
        generator.Emit(OpCodes.Br, Target(labels, programCounter + 1, fallback));

    private static LuaNumericIlLabel Target(
        LuaNumericIlLabel[] labels,
        int programCounter,
        LuaNumericIlLabel fallback) =>
        (uint)programCounter < (uint)labels.Length ? labels[programCounter] : fallback;

    /// <returns>0 when the zero step requires scheduler fallback, 1 when skipped, 2 when entered.</returns>
    private static int PrepareIntegerForLoop(
        long initial,
        long limit,
        long step,
        out long counter)
    {
        counter = limit;
        if (step == 0)
        {
            return 0;
        }

        var skip = step > 0 ? initial > limit : initial < limit;
        if (skip)
        {
            return 1;
        }

        ulong count;
        if (step > 0)
        {
            count = unchecked((ulong)limit - (ulong)initial);
            if (step != 1)
            {
                count /= (ulong)step;
            }
        }
        else
        {
            count = unchecked((ulong)initial - (ulong)limit);
            count /= unchecked((ulong)(-(step + 1)) + 1);
        }

        counter = unchecked((long)count);
        return 2;
    }

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
        throw new MissingMemberException(type.FullName, name);

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
