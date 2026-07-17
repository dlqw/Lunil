using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Lunil.CodeGen.Cil.Planning;
using Lunil.CodeGen.Cil.Verification;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.CodeGen.Cil.Emission;

public delegate LuaCompiledExit LuaCompiledMethod(
    LuaExecutionContext context,
    LuaThread thread,
    LuaFrame frame);

public sealed record ReflectionEmitResult(
    LuaCompiledMethod? Method,
    ImmutableArray<CilPlanDiagnostic> Diagnostics,
    int MaximumEvaluationStack)
{
    public bool Succeeded => Method is not null && Diagnostics.IsEmpty;

    public ReflectionEmitMetrics Metrics { get; init; }
}

public readonly record struct ReflectionEmitMetrics(
    TimeSpan PlanVerificationDuration,
    TimeSpan EmissionDuration,
    TimeSpan DelegateCreationDuration);

public sealed class ReflectionEmitCilPlanSink : ICilInstructionSink
{
    private readonly Dictionary<int, Label> _labels = [];
    private readonly CancellationToken _cancellationToken;
    private static readonly ConcurrentDictionary<string, MethodInfo> ResolvedCalls =
        new(StringComparer.Ordinal);
    private DynamicMethod? _method;
    private ILGenerator? _generator;
    private long _emissionStarted;

    public TimeSpan EmissionDuration { get; private set; }

    public TimeSpan DelegateCreationDuration { get; private set; }

    public CilEmitterFlavor Flavor => CilEmitterFlavor.ReflectionEmit;

    public LuaCompiledMethod? CompiledMethod { get; private set; }

    public ReflectionEmitCilPlanSink()
    {
    }

    private ReflectionEmitCilPlanSink(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
    }

    internal static void PrepareRuntimeAbi()
    {
        foreach (var target in CilWellKnownCalls.All)
        {
            _ = ResolveCall(target);
        }
    }

    [RequiresDynamicCode("Reflection.Emit requires dynamic code support.")]
    public static ReflectionEmitResult Compile(
        CilMethodPlan plan,
        CilPlanLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return new ReflectionEmitResult(
                null,
                [new CilPlanDiagnostic("CIL2001", "Dynamic code is not supported by this runtime.")],
                0);
        }

        var sink = new ReflectionEmitCilPlanSink(cancellationToken);
        var started = Stopwatch.GetTimestamp();
        var verification = CilPlanEmitter.Emit(plan, sink, limits, cancellationToken);
        var totalDuration = Stopwatch.GetElapsedTime(started);
        var planVerificationDuration = totalDuration -
            sink.EmissionDuration -
            sink.DelegateCreationDuration;
        return new ReflectionEmitResult(
            verification.Succeeded ? sink.CompiledMethod : null,
            verification.Diagnostics,
            verification.MaximumEvaluationStack)
        {
            Metrics = new ReflectionEmitMetrics(
                planVerificationDuration < TimeSpan.Zero
                    ? TimeSpan.Zero
                    : planVerificationDuration,
                sink.EmissionDuration,
                sink.DelegateCreationDuration),
        };
    }

    [RequiresDynamicCode("Reflection.Emit requires dynamic code support.")]
    public static ReflectionEmitResult Compile(
        CilMethodPlan plan,
        CilPlanVerificationResult verification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(verification);
        cancellationToken.ThrowIfCancellationRequested();
        if (!verification.Succeeded)
        {
            return new ReflectionEmitResult(
                null,
                verification.Diagnostics,
                verification.MaximumEvaluationStack);
        }

        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return new ReflectionEmitResult(
                null,
                [new CilPlanDiagnostic("CIL2001", "Dynamic code is not supported by this runtime.")],
                0);
        }

        var sink = new ReflectionEmitCilPlanSink(cancellationToken);
        CilPlanEmitter.EmitVerified(plan, sink, verification, cancellationToken);
        return new ReflectionEmitResult(
            sink.CompiledMethod,
            [],
            verification.MaximumEvaluationStack)
        {
            Metrics = new ReflectionEmitMetrics(
                TimeSpan.Zero,
                sink.EmissionDuration,
                sink.DelegateCreationDuration),
        };
    }

    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "ReflectionEmitCilPlanSink is only reached after the dynamic-code capability check.")]
    public void BeginMethod(CilMethodPlan plan, int maximumEvaluationStack)
    {
        _emissionStarted = Stopwatch.GetTimestamp();
        _method = new DynamicMethod(
            plan.Name,
            typeof(LuaCompiledExit),
            [typeof(LuaExecutionContext), typeof(LuaThread), typeof(LuaFrame)],
            typeof(ReflectionEmitCilPlanSink).Module,
            skipVisibility: true);
        _generator = _method.GetILGenerator(maximumEvaluationStack);
        var labelCount = 0;
        for (var index = 0; index < plan.Instructions.Length; index++)
        {
            if ((index & 63) == 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
            }

            if (plan.Instructions[index].OpCode == CilPlanOpCode.MarkLabel)
            {
                labelCount++;
            }
        }

        _labels.EnsureCapacity(labelCount);
        var instructionIndex = 0;
        foreach (var instruction in plan.Instructions)
        {
            if ((instructionIndex++ & 63) == 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
            }

            if (instruction.OpCode == CilPlanOpCode.MarkLabel)
            {
                _labels.Add(instruction.Label.Id, _generator.DefineLabel());
            }
        }
    }

    public void DeclareLocal(CilLocal local)
    {
        var generator = Generator();
        var declared = generator.DeclareLocal(TypeOf(local.Kind));
        if (declared.LocalIndex != local.Index)
        {
            throw new InvalidOperationException("CIL plan local indexes are not dense and ordered.");
        }
    }

    public void Emit(CilPlanInstruction instruction)
    {
        var generator = Generator();
        switch (instruction.OpCode)
        {
            case CilPlanOpCode.MarkLabel:
                generator.MarkLabel(_labels[instruction.Label.Id]);
                break;
            case CilPlanOpCode.Nop:
                generator.Emit(OpCodes.Nop);
                break;
            case CilPlanOpCode.LoadArgument:
                EmitLoadArgument(generator, instruction.Int32Operand);
                break;
            case CilPlanOpCode.LoadLocal:
                generator.Emit(OpCodes.Ldloc, instruction.Int32Operand);
                break;
            case CilPlanOpCode.StoreLocal:
                generator.Emit(OpCodes.Stloc, instruction.Int32Operand);
                break;
            case CilPlanOpCode.LoadInt32:
                EmitLoadInt32(generator, instruction.Int32Operand);
                break;
            case CilPlanOpCode.LoadInt64:
                generator.Emit(OpCodes.Ldc_I8, instruction.Int64Operand);
                break;
            case CilPlanOpCode.ConvertInt64:
                generator.Emit(OpCodes.Conv_I8);
                break;
            case CilPlanOpCode.Add:
                generator.Emit(OpCodes.Add);
                break;
            case CilPlanOpCode.Subtract:
                generator.Emit(OpCodes.Sub);
                break;
            case CilPlanOpCode.Call:
                generator.Emit(OpCodes.Call, ResolveCall(instruction.CallTarget!));
                break;
            case CilPlanOpCode.Branch:
                generator.Emit(OpCodes.Br, _labels[instruction.Label.Id]);
                break;
            case CilPlanOpCode.BranchTrue:
                generator.Emit(OpCodes.Brtrue, _labels[instruction.Label.Id]);
                break;
            case CilPlanOpCode.BranchFalse:
                generator.Emit(OpCodes.Brfalse, _labels[instruction.Label.Id]);
                break;
            case CilPlanOpCode.Switch:
                generator.Emit(OpCodes.Switch, instruction.Labels.Select(label =>
                    _labels[label.Id]).ToArray());
                break;
            case CilPlanOpCode.Return:
                generator.Emit(OpCodes.Ret);
                break;
            default:
                throw new InvalidOperationException($"Unsupported CIL plan opcode {instruction.OpCode}.");
        }
    }

    public void EndMethod()
    {
        var delegateStarted = Stopwatch.GetTimestamp();
        CompiledMethod = (LuaCompiledMethod)(_method ??
            throw new InvalidOperationException("CIL method was not initialized."))
            .CreateDelegate(typeof(LuaCompiledMethod));
        DelegateCreationDuration = Stopwatch.GetElapsedTime(delegateStarted);
        var totalEmissionDuration = Stopwatch.GetElapsedTime(_emissionStarted);
        EmissionDuration = totalEmissionDuration - DelegateCreationDuration;
        if (EmissionDuration < TimeSpan.Zero)
        {
            EmissionDuration = TimeSpan.Zero;
        }
    }

    private ILGenerator Generator() => _generator ??
        throw new InvalidOperationException("CIL method was not initialized.");

    private static Type TypeOf(CilStackValueKind kind) => kind switch
    {
        CilStackValueKind.Int32 => typeof(int),
        CilStackValueKind.Int64 => typeof(long),
        CilStackValueKind.Float => typeof(double),
        CilStackValueKind.Object => typeof(object),
        CilStackValueKind.LuaValue => typeof(LuaValue),
        CilStackValueKind.ExecutionContext => typeof(LuaExecutionContext),
        CilStackValueKind.Thread => typeof(LuaThread),
        CilStackValueKind.Frame => typeof(LuaFrame),
        CilStackValueKind.CompiledExit => typeof(LuaCompiledExit),
        _ => throw new InvalidOperationException($"No CLR type exists for {kind}."),
    };

    private static MethodInfo ResolveCall(CilCallTarget target) => ResolvedCalls.GetOrAdd(
        target.Id,
        static (_, callTarget) => ResolveCallCore(callTarget),
        target);

    private static MethodInfo ResolveCallCore(CilCallTarget target) => target.Id switch
    {
        "LuaExecutionContext.TryReserveInstructions" => Method(
            typeof(LuaExecutionContext),
            nameof(LuaExecutionContext.TryReserveInstructions),
            [typeof(int)]),
        "LuaFrame.get_ProgramCounter" => typeof(LuaFrame)
            .GetProperty(nameof(LuaFrame.ProgramCounter))!.GetMethod!,
        "LuaCodegenAbiV1.CommitProgramCounter" => Method(
            typeof(LuaCodegenAbiV1),
            nameof(LuaCodegenAbiV1.CommitProgramCounter),
            [typeof(LuaFrame), typeof(int)]),
        "LuaCodegenAbiV1.MaterializeConstant" => Method(
            typeof(LuaCodegenAbiV1),
            nameof(LuaCodegenAbiV1.MaterializeConstant),
            [typeof(LuaExecutionContext), typeof(LuaFrame), typeof(int)]),
        "LuaCodegenAbiV1.ReadRegister" => Method(
            typeof(LuaCodegenAbiV1),
            nameof(LuaCodegenAbiV1.ReadRegister),
            [typeof(LuaThread), typeof(LuaFrame), typeof(int)]),
        "LuaCodegenAbiV1.WriteRegister" => Method(
            typeof(LuaCodegenAbiV1),
            nameof(LuaCodegenAbiV1.WriteRegister),
            [typeof(LuaThread), typeof(LuaFrame), typeof(int), typeof(LuaValue)]),
        "LuaCodegenAbiV1.ReadUpvalue" => Method(
            typeof(LuaCodegenAbiV1),
            nameof(LuaCodegenAbiV1.ReadUpvalue),
            [typeof(LuaFrame), typeof(int)]),
        "LuaCodegenAbiV1.WriteUpvalue" => Method(
            typeof(LuaCodegenAbiV1),
            nameof(LuaCodegenAbiV1.WriteUpvalue),
            [typeof(LuaFrame), typeof(int), typeof(LuaValue)]),
        "LuaCodegenAbiV1.ClearRegisters" => Method(
            typeof(LuaCodegenAbiV1),
            nameof(LuaCodegenAbiV1.ClearRegisters),
            [typeof(LuaThread), typeof(LuaFrame), typeof(int), typeof(int)]),
        "LuaCodegenAbiV1.SetFrameTop" => Method(
            typeof(LuaCodegenAbiV1),
            nameof(LuaCodegenAbiV1.SetFrameTop),
            [typeof(LuaThread), typeof(LuaFrame), typeof(int)]),
        "LuaCodegenAbiV1.IsTruthy" => Method(
            typeof(LuaCodegenAbiV1),
            nameof(LuaCodegenAbiV1.IsTruthy),
            [typeof(LuaValue)]),
        "LuaCodegenAbiV1.CanExecuteCompiled" => Method(
            typeof(LuaCodegenAbiV1),
            nameof(LuaCodegenAbiV1.CanExecuteCompiled),
            [typeof(LuaExecutionContext)]),
        "LuaCodegenAbiV2.CanExecuteCompiledFrame" => Method(
            typeof(LuaCodegenAbiV2),
            nameof(LuaCodegenAbiV2.CanExecuteCompiledFrame),
            [typeof(LuaExecutionContext), typeof(LuaFrame), typeof(int), typeof(int)]),
        "LuaCodegenAbiV2.ReadRegisterUnchecked" => Method(
            typeof(LuaCodegenAbiV2),
            nameof(LuaCodegenAbiV2.ReadRegisterUnchecked),
            [typeof(LuaThread), typeof(LuaFrame), typeof(int)]),
        "LuaCodegenAbiV2.WriteRegisterUnchecked" => Method(
            typeof(LuaCodegenAbiV2),
            nameof(LuaCodegenAbiV2.WriteRegisterUnchecked),
            [typeof(LuaThread), typeof(LuaFrame), typeof(int), typeof(LuaValue)]),
        "LuaCodegenAbiV2.ClearRegistersUnchecked" => Method(
            typeof(LuaCodegenAbiV2),
            nameof(LuaCodegenAbiV2.ClearRegistersUnchecked),
            [typeof(LuaThread), typeof(LuaFrame), typeof(int), typeof(int)]),
        "LuaCodegenAbiV2.SetFrameTopUnchecked" => Method(
            typeof(LuaCodegenAbiV2),
            nameof(LuaCodegenAbiV2.SetFrameTopUnchecked),
            [typeof(LuaThread), typeof(LuaFrame), typeof(int)]),
        "LuaCodegenAbiV2.ReadTruthyAndSetFrameTopUnchecked" => Method(
            typeof(LuaCodegenAbiV2),
            nameof(LuaCodegenAbiV2.ReadTruthyAndSetFrameTopUnchecked),
            [typeof(LuaThread), typeof(LuaFrame), typeof(int), typeof(int)]),
        "LuaCodegenAbiV2.CanSkipClose" => Method(
            typeof(LuaCodegenAbiV2),
            nameof(LuaCodegenAbiV2.CanSkipClose),
            [typeof(LuaThread), typeof(LuaFrame), typeof(int)]),
        "LuaCodegenAbiV1.ObserveCanonicalInstruction" => Method(
            typeof(LuaCodegenAbiV1),
            nameof(LuaCodegenAbiV1.ObserveCanonicalInstruction),
            [
                typeof(LuaExecutionContext),
                typeof(LuaThread),
                typeof(LuaFrame),
                typeof(int),
            ]),
        "LuaCodegenAbiV1.ExecuteCanonicalInstruction" => Method(
            typeof(LuaCodegenAbiV1),
            nameof(LuaCodegenAbiV1.ExecuteCanonicalInstruction),
            [
                typeof(LuaExecutionContext),
                typeof(LuaThread),
                typeof(LuaFrame),
                typeof(int),
            ]),
        "LuaCodegenAbiV2.CanExecuteUnaryPrimitive" => Method(
            typeof(LuaCodegenAbiV2),
            nameof(LuaCodegenAbiV2.CanExecuteUnaryPrimitive),
            [typeof(LuaThread), typeof(LuaFrame), typeof(int), typeof(int)]),
        "LuaCodegenAbiV2.ExecuteUnaryPrimitive" => Method(
            typeof(LuaCodegenAbiV2),
            nameof(LuaCodegenAbiV2.ExecuteUnaryPrimitive),
            [
                typeof(LuaExecutionContext),
                typeof(LuaThread),
                typeof(LuaFrame),
                typeof(int),
                typeof(int),
                typeof(int),
            ]),
        "LuaCodegenAbiV2.CanExecuteBinaryPrimitive" => Method(
            typeof(LuaCodegenAbiV2),
            nameof(LuaCodegenAbiV2.CanExecuteBinaryPrimitive),
            [typeof(LuaThread), typeof(LuaFrame), typeof(int), typeof(int), typeof(int)]),
        "LuaCodegenAbiV2.ExecuteBinaryPrimitive" => Method(
            typeof(LuaCodegenAbiV2),
            nameof(LuaCodegenAbiV2.ExecuteBinaryPrimitive),
            [
                typeof(LuaExecutionContext),
                typeof(LuaThread),
                typeof(LuaFrame),
                typeof(int),
                typeof(int),
                typeof(int),
                typeof(int),
            ]),
        "LuaCodegenAbiV2.ExecuteNumericForPrepare" => Method(
            typeof(LuaCodegenAbiV2),
            nameof(LuaCodegenAbiV2.ExecuteNumericForPrepare),
            [typeof(LuaThread), typeof(LuaFrame), typeof(int), typeof(int)]),
        "LuaCodegenAbiV2.ExecuteNumericForLoop" => Method(
            typeof(LuaCodegenAbiV2),
            nameof(LuaCodegenAbiV2.ExecuteNumericForLoop),
            [typeof(LuaThread), typeof(LuaFrame), typeof(int), typeof(int)]),
        "LuaCodegenAbiV3.ExecuteNewTable" => Method(
            typeof(LuaCodegenAbiV3),
            nameof(LuaCodegenAbiV3.ExecuteNewTable),
            [
                typeof(LuaExecutionContext),
                typeof(LuaThread),
                typeof(LuaFrame),
                typeof(int),
                typeof(int),
                typeof(int),
            ]),
        "LuaCodegenAbiV3.ExecuteGetTable" => Method(
            typeof(LuaCodegenAbiV3),
            nameof(LuaCodegenAbiV3.ExecuteGetTable),
            [
                typeof(LuaExecutionContext),
                typeof(LuaThread),
                typeof(LuaFrame),
                typeof(int),
                typeof(int),
                typeof(int),
            ]),
        "LuaCodegenAbiV3.ExecuteSetTable" => Method(
            typeof(LuaCodegenAbiV3),
            nameof(LuaCodegenAbiV3.ExecuteSetTable),
            [
                typeof(LuaExecutionContext),
                typeof(LuaThread),
                typeof(LuaFrame),
                typeof(int),
                typeof(int),
                typeof(int),
            ]),
        "LuaCodegenAbiV3.ExecuteSetList" => Method(
            typeof(LuaCodegenAbiV3),
            nameof(LuaCodegenAbiV3.ExecuteSetList),
            [typeof(LuaThread), typeof(LuaFrame), typeof(int), typeof(int), typeof(int), typeof(int)]),
        "LuaCodegenAbiV3.ExecuteClosure" => Method(
            typeof(LuaCodegenAbiV3),
            nameof(LuaCodegenAbiV3.ExecuteClosure),
            [typeof(LuaExecutionContext), typeof(LuaThread), typeof(LuaFrame), typeof(int), typeof(int)]),
        "LuaCodegenAbiV3.ExecuteVarArg" => Method(
            typeof(LuaCodegenAbiV3),
            nameof(LuaCodegenAbiV3.ExecuteVarArg),
            [typeof(LuaThread), typeof(LuaFrame), typeof(int), typeof(int)]),
        "LuaCodegenAbiV3.TryExecuteFramelessCall" => Method(
            typeof(LuaCodegenAbiV3),
            nameof(LuaCodegenAbiV3.TryExecuteFramelessCall),
            [
                typeof(LuaExecutionContext),
                typeof(LuaThread),
                typeof(LuaFrame),
                typeof(int),
                typeof(int),
                typeof(int),
            ]),
        "LuaCodegenAbiV3.CanContinueAfterFramelessCall" => Method(
            typeof(LuaCodegenAbiV3),
            nameof(LuaCodegenAbiV3.CanContinueAfterFramelessCall),
            [typeof(LuaExecutionContext), typeof(LuaThread), typeof(LuaFrame)]),
        "LuaCodegenAbiV3.PollGcSafepoint" => Method(
            typeof(LuaCodegenAbiV3),
            nameof(LuaCodegenAbiV3.PollGcSafepoint),
            [typeof(LuaExecutionContext), typeof(LuaThread), typeof(LuaFrame)]),
        "LuaCompiledExit.Poll" => Method(
            typeof(LuaCompiledExit),
            nameof(LuaCompiledExit.Poll),
            [typeof(int), typeof(long), typeof(LuaCompiledExitReason)]),
        "LuaCompiledExit.Continue" => Method(
            typeof(LuaCompiledExit),
            nameof(LuaCompiledExit.Continue),
            [typeof(int), typeof(long)]),
        "LuaCompiledExit.Return" => Method(
            typeof(LuaCompiledExit),
            nameof(LuaCompiledExit.Return),
            [typeof(int), typeof(long)]),
        "LuaCompiledExit.Call" => Method(
            typeof(LuaCompiledExit),
            nameof(LuaCompiledExit.Call),
            [typeof(int), typeof(long)]),
        "LuaCompiledExit.TailCall" => Method(
            typeof(LuaCompiledExit),
            nameof(LuaCompiledExit.TailCall),
            [typeof(int), typeof(long)]),
        "LuaCompiledExit.Deopt" => Method(
            typeof(LuaCompiledExit),
            nameof(LuaCompiledExit.Deopt),
            [typeof(int), typeof(long), typeof(LuaCompiledExitReason)]),
        _ => throw new InvalidOperationException($"Unknown Runtime ABI call target {target.Id}."),
    };

    private static MethodInfo Method(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type,
        string name,
        Type[] parameters) =>
        type.GetMethod(
            name,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static,
            parameters) ?? throw new InvalidOperationException($"Cannot resolve {type.FullName}.{name}.");

    private static void EmitLoadArgument(ILGenerator generator, int argument)
    {
        switch (argument)
        {
            case 0:
                generator.Emit(OpCodes.Ldarg_0);
                break;
            case 1:
                generator.Emit(OpCodes.Ldarg_1);
                break;
            case 2:
                generator.Emit(OpCodes.Ldarg_2);
                break;
            case 3:
                generator.Emit(OpCodes.Ldarg_3);
                break;
            default:
                generator.Emit(OpCodes.Ldarg, argument);
                break;
        }
    }

    private static void EmitLoadInt32(ILGenerator generator, int value)
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
