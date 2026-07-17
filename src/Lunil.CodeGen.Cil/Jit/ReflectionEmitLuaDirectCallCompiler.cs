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
    int MaximumBackedges);

internal sealed record LuaBoundDirectCall(
    LuaIrFunction Function,
    LuaDirectCompiledMethod Method,
    long EstimatedCodeBytes,
    string ModuleContentId);

/// <summary>
/// Emits a bounded, side-effect-free integer leaf directly against the caller result window.
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
    private static readonly MethodInfo FromInteger = Method(
        typeof(LuaValue),
        nameof(LuaValue.FromInteger),
        [typeof(long)]);
    private static readonly MethodInfo TryReserveInstructions = Method(
        typeof(LuaExecutionContext),
        nameof(LuaExecutionContext.TryReserveInstructions),
        [typeof(int)]);
    private static readonly MethodInfo CanExecuteBoundDirectCall = Method(
        typeof(LuaCodegenAbiV4),
        nameof(LuaCodegenAbiV4.CanExecuteBoundDirectCall),
        [typeof(LuaExecutionContext)]);
    private static readonly MethodInfo PrepareIntegerFor = Method(
        typeof(ReflectionEmitLuaDirectCallCompiler),
        nameof(PrepareIntegerForLoop),
        [typeof(long), typeof(long), typeof(long), typeof(long).MakeByRefType()]);

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
            !CanCompile(function, profile))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var dynamicMethod = new DynamicMethod(
            $"lunil_direct_integer_f{function.Id}",
            typeof(bool),
            DirectMethodParameters,
            typeof(ReflectionEmitLuaDirectCallCompiler).Module,
            skipVisibility: true);
        Emit(
            function,
            new ReflectionEmitLuaNumericRegionIlGenerator(dynamicMethod.GetILGenerator()),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var method = (LuaDirectCompiledMethod)dynamicMethod.CreateDelegate(
            typeof(LuaDirectCompiledMethod));
        result = new LuaCompiledDirectCall(
            method,
            checked(function.Instructions.Length * 40L + function.RegisterCount * 24L + 128L),
            MaximumBackedges);
        return true;
    }

    internal static bool CanCompile(LuaIrFunction function, LuaJitFunctionProfile profile)
    {
        if (function.IsVarArg || function.Upvalues.Length != 0 ||
            function.Instructions.Length is 0 or > MaximumInstructions ||
            function.RegisterCount is 0 or > MaximumRegisters ||
            function.ParameterCount > MaximumParameters ||
            profile.ArgumentKinds.Length < function.ParameterCount)
        {
            return false;
        }

        for (var index = 0; index < function.ParameterCount; index++)
        {
            if (profile.ArgumentKinds[index] != LuaJitValueKinds.Integer)
            {
                return false;
            }
        }

        var hasFixedReturn = false;
        foreach (var instruction in function.Instructions)
        {
            switch (instruction.Opcode)
            {
                case LuaIrOpcode.LoadConstant when
                    (uint)instruction.B < (uint)function.Constants.Length &&
                    function.Constants[instruction.B].Kind == LuaIrConstantKind.Integer:
                case LuaIrOpcode.Move:
                case LuaIrOpcode.SetTop:
                case LuaIrOpcode.Jump:
                case LuaIrOpcode.NumericForPrepare:
                case LuaIrOpcode.NumericForLoop:
                    break;
                case LuaIrOpcode.Unary when
                    (LuaIrUnaryOperator)instruction.C is LuaIrUnaryOperator.Negate or
                        LuaIrUnaryOperator.BitwiseNot:
                    break;
                case LuaIrOpcode.Binary when
                    (LuaIrBinaryOperator)instruction.D is LuaIrBinaryOperator.Add or
                        LuaIrBinaryOperator.Subtract or LuaIrBinaryOperator.Multiply or
                        LuaIrBinaryOperator.BitwiseAnd or LuaIrBinaryOperator.BitwiseOr or
                        LuaIrBinaryOperator.BitwiseXor:
                    break;
                case LuaIrOpcode.Return when instruction.B is >= 0 and <= MaximumResults:
                    hasFixedReturn = true;
                    break;
                default:
                    return false;
            }
        }

        return hasFixedReturn;
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
        LuaNumericRegionIlGenerator generator,
        CancellationToken cancellationToken) => EmitCore(
            function,
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
        LuaNumericRegionIlGenerator generator,
        DirectEmissionMode mode,
        int functionRegister,
        int expectedResults,
        LuaNumericIlLabel success,
        LuaNumericIlLabel externalFallback,
        NumericRegionEmission? numericRegion,
        CancellationToken cancellationToken)
    {
        var registers = Enumerable.Range(0, function.RegisterCount)
            .Select(_ => generator.DeclareLocal(typeof(long)))
            .ToArray();
        var taggedValue = generator.DeclareLocal(typeof(LuaValue));
        var consumed = generator.DeclareLocal(typeof(int));
        var backedges = generator.DeclareLocal(typeof(int));
        var prepareStatus = generator.DeclareLocal(typeof(int));
        var labels = function.Instructions.Select(_ => generator.DefineLabel()).ToArray();
        var standalone = mode == DirectEmissionMode.Standalone;
        var frameInline = mode == DirectEmissionMode.FrameInline;
        var numericInline = mode == DirectEmissionMode.NumericRegionInline;
        var fallback = standalone ? generator.DefineLabel() : externalFallback;

        if (!standalone)
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
        generator.Emit(OpCodes.Ldc_I4_0);
        generator.Emit(OpCodes.Dup);
        generator.Emit(OpCodes.Stloc, consumed);
        generator.Emit(OpCodes.Stloc, backedges);

        if (standalone)
        {
            generator.Emit(OpCodes.Ldarg_S, 4);
            EmitInt32(generator, function.ParameterCount);
            generator.Emit(OpCodes.Bne_Un, fallback);
        }
        for (var parameter = 0; parameter < function.ParameterCount; parameter++)
        {
            if (numericInline)
            {
                generator.Emit(OpCodes.Ldloc, numericRegion!.Arguments[parameter]);
                generator.Emit(OpCodes.Stloc, registers[parameter]);
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
            EmitInt32(generator, (int)LuaValueKind.Integer);
            generator.Emit(OpCodes.Bne_Un, fallback);
            generator.Emit(OpCodes.Ldloca, taggedValue);
            generator.Emit(OpCodes.Call, AsInteger);
            generator.Emit(OpCodes.Stloc, registers[parameter]);
        }

        generator.Emit(OpCodes.Br, labels[0]);
        for (var programCounter = 0;
             programCounter < function.Instructions.Length;
             programCounter++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            generator.MarkLabel(labels[programCounter]);
            generator.Emit(OpCodes.Ldloc, consumed);
            generator.Emit(OpCodes.Ldc_I4_1);
            generator.Emit(OpCodes.Add);
            generator.Emit(OpCodes.Stloc, consumed);
            var instruction = function.Instructions[programCounter];
            switch (instruction.Opcode)
            {
                case LuaIrOpcode.LoadConstant:
                    generator.Emit(OpCodes.Ldc_I8, function.Constants[instruction.B].Integer);
                    generator.Emit(OpCodes.Stloc, registers[instruction.A]);
                    EmitNext(generator, labels, programCounter, fallback);
                    break;
                case LuaIrOpcode.Move:
                    generator.Emit(OpCodes.Ldloc, registers[instruction.B]);
                    generator.Emit(OpCodes.Stloc, registers[instruction.A]);
                    EmitNext(generator, labels, programCounter, fallback);
                    break;
                case LuaIrOpcode.SetTop:
                    EmitNext(generator, labels, programCounter, fallback);
                    break;
                case LuaIrOpcode.Unary:
                    generator.Emit(OpCodes.Ldloc, registers[instruction.B]);
                    generator.Emit(
                        (LuaIrUnaryOperator)instruction.C == LuaIrUnaryOperator.Negate
                            ? OpCodes.Neg
                            : OpCodes.Not);
                    generator.Emit(OpCodes.Stloc, registers[instruction.A]);
                    EmitNext(generator, labels, programCounter, fallback);
                    break;
                case LuaIrOpcode.Binary:
                    generator.Emit(OpCodes.Ldloc, registers[instruction.B]);
                    generator.Emit(OpCodes.Ldloc, registers[instruction.C]);
                    generator.Emit((LuaIrBinaryOperator)instruction.D switch
                    {
                        LuaIrBinaryOperator.Add => OpCodes.Add,
                        LuaIrBinaryOperator.Subtract => OpCodes.Sub,
                        LuaIrBinaryOperator.Multiply => OpCodes.Mul,
                        LuaIrBinaryOperator.BitwiseAnd => OpCodes.And,
                        LuaIrBinaryOperator.BitwiseOr => OpCodes.Or,
                        LuaIrBinaryOperator.BitwiseXor => OpCodes.Xor,
                        _ => throw new InvalidOperationException(
                            "An unsupported integer binary operation reached emission."),
                    });
                    generator.Emit(OpCodes.Stloc, registers[instruction.A]);
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
                case LuaIrOpcode.NumericForPrepare:
                    generator.Emit(OpCodes.Ldloc, registers[instruction.A]);
                    generator.Emit(OpCodes.Stloc, registers[instruction.A + 3]);
                    generator.Emit(OpCodes.Ldloc, registers[instruction.A]);
                    generator.Emit(OpCodes.Ldloc, registers[instruction.A + 1]);
                    generator.Emit(OpCodes.Ldloc, registers[instruction.A + 2]);
                    generator.Emit(OpCodes.Ldloca, registers[instruction.A + 1]);
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
                        generator.Emit(OpCodes.Ldloc, registers[instruction.A + 1]);
                        generator.Emit(OpCodes.Brfalse, finished);
                        generator.Emit(OpCodes.Ldloc, registers[instruction.A]);
                        generator.Emit(OpCodes.Ldloc, registers[instruction.A + 2]);
                        generator.Emit(OpCodes.Add);
                        generator.Emit(OpCodes.Stloc, registers[instruction.A]);
                        generator.Emit(OpCodes.Ldloc, registers[instruction.A + 1]);
                        generator.Emit(OpCodes.Ldc_I4_1);
                        generator.Emit(OpCodes.Conv_I8);
                        generator.Emit(OpCodes.Sub);
                        generator.Emit(OpCodes.Stloc, registers[instruction.A + 1]);
                        generator.Emit(OpCodes.Ldloc, registers[instruction.A]);
                        generator.Emit(OpCodes.Stloc, registers[instruction.A + 3]);
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
                        consumed,
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
        LuaNumericIlLocal[] registers,
        LuaNumericIlLocal consumed,
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
            generator.Emit(OpCodes.Ldloc, consumed);
            generator.Emit(OpCodes.Conv_I8);
            generator.Emit(OpCodes.Blt, numericRegion.BudgetFallback);
            generator.Emit(OpCodes.Ldloc, numericRegion.Remaining);
            generator.Emit(OpCodes.Ldloc, consumed);
            generator.Emit(OpCodes.Conv_I8);
            generator.Emit(OpCodes.Sub);
            generator.Emit(OpCodes.Stloc, numericRegion.Remaining);
            generator.Emit(OpCodes.Ldloc, numericRegion.Pending);
            generator.Emit(OpCodes.Ldloc, consumed);
            generator.Emit(OpCodes.Add);
            generator.Emit(OpCodes.Stloc, numericRegion.Pending);
        }
        else
        {
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldloc, consumed);
            generator.Emit(OpCodes.Callvirt, TryReserveInstructions);
            generator.Emit(OpCodes.Brfalse, fallback);
        }

        for (var index = 0; index < instruction.B; index++)
        {
            if (numericInline)
            {
                generator.Emit(OpCodes.Ldloc, registers[instruction.A + index]);
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
            generator.Emit(OpCodes.Ldloc, registers[instruction.A + index]);
            generator.Emit(OpCodes.Call, FromInteger);
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
