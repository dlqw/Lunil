using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Lunil.CodeGen.Cil.Planning;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.CodeGen.Cil.Emission;

/// <summary>
/// Owner-scoped memo that resolves well-known Runtime ABI call targets to MethodInfo. Long-lived
/// emitters (the Reflection.Emit Tier 1 compiler) hold one instance and pre-populate it before
/// timed compilations; transient sinks resolve on demand, deterministically per call-target id.
/// </summary>
internal sealed class ReflectionEmitRuntimeAbiCache
{
    private readonly ConcurrentDictionary<string, MethodInfo> _resolvedCalls =
        new(StringComparer.Ordinal);

    public void PrepareRuntimeAbi()
    {
        foreach (var target in CilWellKnownCalls.All)
        {
            _ = ResolveCall(target);
        }
    }

    public MethodInfo ResolveCall(CilCallTarget target) => _resolvedCalls.GetOrAdd(
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
}
