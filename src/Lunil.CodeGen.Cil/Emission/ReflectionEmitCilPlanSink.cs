using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
    private readonly ReflectionEmitRuntimeAbiCache _runtimeAbi;
    private readonly CancellationToken _cancellationToken;
    private DynamicMethod? _method;
    private ILGenerator? _generator;
    private long _emissionStarted;

    public TimeSpan EmissionDuration { get; private set; }

    public TimeSpan DelegateCreationDuration { get; private set; }

    public CilEmitterFlavor Flavor => CilEmitterFlavor.ReflectionEmit;

    public LuaCompiledMethod? CompiledMethod { get; private set; }

    public ReflectionEmitCilPlanSink()
        : this(new ReflectionEmitRuntimeAbiCache(), CancellationToken.None)
    {
    }

    internal ReflectionEmitCilPlanSink(
        ReflectionEmitRuntimeAbiCache runtimeAbi,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimeAbi);
        _runtimeAbi = runtimeAbi;
        _cancellationToken = cancellationToken;
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

        var sink = new ReflectionEmitCilPlanSink(
            new ReflectionEmitRuntimeAbiCache(),
            cancellationToken);
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
        CancellationToken cancellationToken = default) =>
        Compile(
            plan,
            verification,
            new ReflectionEmitRuntimeAbiCache(),
            cancellationToken);

    [RequiresDynamicCode("Reflection.Emit requires dynamic code support.")]
    internal static ReflectionEmitResult Compile(
        CilMethodPlan plan,
        CilPlanVerificationResult verification,
        ReflectionEmitRuntimeAbiCache runtimeAbi,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(runtimeAbi);
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

        var sink = new ReflectionEmitCilPlanSink(runtimeAbi, cancellationToken);
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
                generator.Emit(OpCodes.Call, _runtimeAbi.ResolveCall(instruction.CallTarget!));
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
