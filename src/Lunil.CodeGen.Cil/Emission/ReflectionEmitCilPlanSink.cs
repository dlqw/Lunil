using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Lunil.CodeGen.Cil.Planning;
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
}

public sealed class ReflectionEmitCilPlanSink : ICilInstructionSink
{
    private readonly Dictionary<int, Label> _labels = [];
    private DynamicMethod? _method;
    private ILGenerator? _generator;

    public CilEmitterFlavor Flavor => CilEmitterFlavor.ReflectionEmit;

    public LuaCompiledMethod? CompiledMethod { get; private set; }

    [RequiresDynamicCode("Reflection.Emit requires dynamic code support.")]
    public static ReflectionEmitResult Compile(
        CilMethodPlan plan,
        CilPlanLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return new ReflectionEmitResult(
                null,
                [new CilPlanDiagnostic("CIL2001", "Dynamic code is not supported by this runtime.")],
                0);
        }

        var sink = new ReflectionEmitCilPlanSink();
        var verification = CilPlanEmitter.Emit(plan, sink, limits);
        return new ReflectionEmitResult(
            verification.Succeeded ? sink.CompiledMethod : null,
            verification.Diagnostics,
            verification.MaximumEvaluationStack);
    }

    public void BeginMethod(CilMethodPlan plan, int maximumEvaluationStack)
    {
        _method = new DynamicMethod(
            plan.Name,
            typeof(LuaCompiledExit),
            [typeof(LuaExecutionContext), typeof(LuaThread), typeof(LuaFrame)],
            typeof(ReflectionEmitCilPlanSink).Module,
            skipVisibility: true);
        _generator = _method.GetILGenerator(maximumEvaluationStack);
        foreach (var instruction in plan.Instructions)
        {
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
            case CilPlanOpCode.Add:
                generator.Emit(OpCodes.Add);
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
        CompiledMethod = (LuaCompiledMethod)(_method ??
            throw new InvalidOperationException("CIL method was not initialized."))
            .CreateDelegate(typeof(LuaCompiledMethod));
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

    private static MethodInfo ResolveCall(CilCallTarget target) => target.Id switch
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
        "LuaCompiledExit.Poll" => Method(
            typeof(LuaCompiledExit),
            nameof(LuaCompiledExit.Poll),
            [typeof(int), typeof(int), typeof(LuaCompiledExitReason)]),
        "LuaCompiledExit.Return" => Method(
            typeof(LuaCompiledExit),
            nameof(LuaCompiledExit.Return),
            [typeof(int), typeof(int)]),
        "LuaCompiledExit.Deopt" => Method(
            typeof(LuaCompiledExit),
            nameof(LuaCompiledExit.Deopt),
            [typeof(int), typeof(int), typeof(LuaCompiledExitReason)]),
        _ => throw new InvalidOperationException($"Unknown Runtime ABI call target {target.Id}."),
    };

    private static MethodInfo Method(Type type, string name, Type[] parameters) =>
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
