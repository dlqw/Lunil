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
        // LuaExecutionContext.TryReserveInstructions is an instance method whose class cannot be
        // constructed at type-initialization time (the constructor needs a live LuaState and
        // LuaThread), so that one token keeps its reflection lookup. LuaFrame.get_ProgramCounter
        // is a property getter, which has no method-group form. Every other token is resolved
        // once through a delegate of the exact signature and its Method property.
        "LuaExecutionContext.TryReserveInstructions" => Method(
            typeof(LuaExecutionContext),
            nameof(LuaExecutionContext.TryReserveInstructions),
            [typeof(int)]),
        "LuaFrame.get_ProgramCounter" => typeof(LuaFrame)
            .GetProperty(nameof(LuaFrame.ProgramCounter))!.GetMethod!,
        "LuaCodegenAbiV1.CommitProgramCounter" => Tokens.AbiV1CommitProgramCounter,
        "LuaCodegenAbiV1.MaterializeConstant" => Tokens.AbiV1MaterializeConstant,
        "LuaCodegenAbiV1.ReadRegister" => Tokens.AbiV1ReadRegister,
        "LuaCodegenAbiV1.WriteRegister" => Tokens.AbiV1WriteRegister,
        "LuaCodegenAbiV1.ReadUpvalue" => Tokens.AbiV1ReadUpvalue,
        "LuaCodegenAbiV1.WriteUpvalue" => Tokens.AbiV1WriteUpvalue,
        "LuaCodegenAbiV1.ClearRegisters" => Tokens.AbiV1ClearRegisters,
        "LuaCodegenAbiV1.SetFrameTop" => Tokens.AbiV1SetFrameTop,
        "LuaCodegenAbiV1.IsTruthy" => Tokens.AbiV1IsTruthy,
        "LuaCodegenAbiV1.CanExecuteCompiled" => Tokens.AbiV1CanExecuteCompiled,
        "LuaCodegenAbiV2.CanExecuteCompiledFrame" => Tokens.AbiV2CanExecuteCompiledFrame,
        "LuaCodegenAbiV2.ReadRegisterUnchecked" => Tokens.AbiV2ReadRegisterUnchecked,
        "LuaCodegenAbiV2.WriteRegisterUnchecked" => Tokens.AbiV2WriteRegisterUnchecked,
        "LuaCodegenAbiV2.ClearRegistersUnchecked" => Tokens.AbiV2ClearRegistersUnchecked,
        "LuaCodegenAbiV2.SetFrameTopUnchecked" => Tokens.AbiV2SetFrameTopUnchecked,
        "LuaCodegenAbiV2.ReadTruthyAndSetFrameTopUnchecked" => Tokens.AbiV2ReadTruthyAndSetFrameTopUnchecked,
        "LuaCodegenAbiV2.CanSkipClose" => Tokens.AbiV2CanSkipClose,
        "LuaCodegenAbiV1.ObserveCanonicalInstruction" => Tokens.AbiV1ObserveCanonicalInstruction,
        "LuaCodegenAbiV1.ExecuteCanonicalInstruction" => Tokens.AbiV1ExecuteCanonicalInstruction,
        "LuaCodegenAbiV2.CanExecuteUnaryPrimitive" => Tokens.AbiV2CanExecuteUnaryPrimitive,
        "LuaCodegenAbiV2.ExecuteUnaryPrimitive" => Tokens.AbiV2ExecuteUnaryPrimitive,
        "LuaCodegenAbiV2.CanExecuteBinaryPrimitive" => Tokens.AbiV2CanExecuteBinaryPrimitive,
        "LuaCodegenAbiV2.ExecuteBinaryPrimitive" => Tokens.AbiV2ExecuteBinaryPrimitive,
        "LuaCodegenAbiV2.ExecuteNumericForPrepare" => Tokens.AbiV2ExecuteNumericForPrepare,
        "LuaCodegenAbiV2.ExecuteNumericForLoop" => Tokens.AbiV2ExecuteNumericForLoop,
        "LuaCodegenAbiV3.ExecuteNewTable" => Tokens.AbiV3ExecuteNewTable,
        "LuaCodegenAbiV3.ExecuteGetTable" => Tokens.AbiV3ExecuteGetTable,
        "LuaCodegenAbiV3.ExecuteSetTable" => Tokens.AbiV3ExecuteSetTable,
        "LuaCodegenAbiV3.ExecuteSetList" => Tokens.AbiV3ExecuteSetList,
        "LuaCodegenAbiV3.ExecuteClosure" => Tokens.AbiV3ExecuteClosure,
        "LuaCodegenAbiV3.ExecuteVarArg" => Tokens.AbiV3ExecuteVarArg,
        "LuaCodegenAbiV3.TryExecuteFramelessCall" => Tokens.AbiV3TryExecuteFramelessCall,
        "LuaCodegenAbiV3.CanContinueAfterFramelessCall" => Tokens.AbiV3CanContinueAfterFramelessCall,
        "LuaCodegenAbiV3.PollGcSafepoint" => Tokens.AbiV3PollGcSafepoint,
        "LuaCompiledExit.Poll" => Tokens.ExitPoll,
        "LuaCompiledExit.Continue" => Tokens.ExitContinue,
        "LuaCompiledExit.Return" => Tokens.ExitReturn,
        "LuaCompiledExit.Call" => Tokens.ExitCall,
        "LuaCompiledExit.TailCall" => Tokens.ExitTailCall,
        "LuaCompiledExit.Deopt" => Tokens.ExitDeopt,
        _ => throw new InvalidOperationException($"Unknown Runtime ABI call target {target.Id}."),
    };

    private static class Tokens
    {
        public static readonly MethodInfo AbiV1CommitProgramCounter =
            ((Action<LuaFrame, int>)LuaCodegenAbiV1.CommitProgramCounter).Method;
        public static readonly MethodInfo AbiV1MaterializeConstant =
            ((Func<LuaExecutionContext, LuaFrame, int, LuaValue>)LuaCodegenAbiV1.MaterializeConstant).Method;
        public static readonly MethodInfo AbiV1ReadRegister =
            ((Func<LuaThread, LuaFrame, int, LuaValue>)LuaCodegenAbiV1.ReadRegister).Method;
        public static readonly MethodInfo AbiV1WriteRegister =
            ((Action<LuaThread, LuaFrame, int, LuaValue>)LuaCodegenAbiV1.WriteRegister).Method;
        public static readonly MethodInfo AbiV1ReadUpvalue =
            ((Func<LuaFrame, int, LuaValue>)LuaCodegenAbiV1.ReadUpvalue).Method;
        public static readonly MethodInfo AbiV1WriteUpvalue =
            ((Action<LuaFrame, int, LuaValue>)LuaCodegenAbiV1.WriteUpvalue).Method;
        public static readonly MethodInfo AbiV1ClearRegisters =
            ((Action<LuaThread, LuaFrame, int, int>)LuaCodegenAbiV1.ClearRegisters).Method;
        public static readonly MethodInfo AbiV1SetFrameTop =
            ((Action<LuaThread, LuaFrame, int>)LuaCodegenAbiV1.SetFrameTop).Method;
        public static readonly MethodInfo AbiV1IsTruthy =
            ((Func<LuaValue, bool>)LuaCodegenAbiV1.IsTruthy).Method;
        public static readonly MethodInfo AbiV1CanExecuteCompiled =
            ((Func<LuaExecutionContext, bool>)LuaCodegenAbiV1.CanExecuteCompiled).Method;
        public static readonly MethodInfo AbiV2CanExecuteCompiledFrame =
            ((Func<LuaExecutionContext, LuaFrame, int, int, bool>)LuaCodegenAbiV2.CanExecuteCompiledFrame).Method;
        public static readonly MethodInfo AbiV2ReadRegisterUnchecked =
            ((Func<LuaThread, LuaFrame, int, LuaValue>)LuaCodegenAbiV2.ReadRegisterUnchecked).Method;
        public static readonly MethodInfo AbiV2WriteRegisterUnchecked =
            ((Action<LuaThread, LuaFrame, int, LuaValue>)LuaCodegenAbiV2.WriteRegisterUnchecked).Method;
        public static readonly MethodInfo AbiV2ClearRegistersUnchecked =
            ((Action<LuaThread, LuaFrame, int, int>)LuaCodegenAbiV2.ClearRegistersUnchecked).Method;
        public static readonly MethodInfo AbiV2SetFrameTopUnchecked =
            ((Action<LuaThread, LuaFrame, int>)LuaCodegenAbiV2.SetFrameTopUnchecked).Method;
        public static readonly MethodInfo AbiV2ReadTruthyAndSetFrameTopUnchecked =
            ((Func<LuaThread, LuaFrame, int, int, bool>)LuaCodegenAbiV2.ReadTruthyAndSetFrameTopUnchecked).Method;
        public static readonly MethodInfo AbiV2CanSkipClose =
            ((Func<LuaThread, LuaFrame, int, bool>)LuaCodegenAbiV2.CanSkipClose).Method;
        public static readonly MethodInfo AbiV1ObserveCanonicalInstruction =
            ((Action<LuaExecutionContext, LuaThread, LuaFrame, int>)LuaCodegenAbiV1.ObserveCanonicalInstruction).Method;
        public static readonly MethodInfo AbiV1ExecuteCanonicalInstruction =
            ((Func<LuaExecutionContext, LuaThread, LuaFrame, int, LuaCompiledExit>)LuaCodegenAbiV1.ExecuteCanonicalInstruction).Method;
        public static readonly MethodInfo AbiV2CanExecuteUnaryPrimitive =
            ((Func<LuaThread, LuaFrame, int, int, bool>)LuaCodegenAbiV2.CanExecuteUnaryPrimitive).Method;
        public static readonly MethodInfo AbiV2ExecuteUnaryPrimitive =
            ((Action<LuaExecutionContext, LuaThread, LuaFrame, int, int, int>)LuaCodegenAbiV2.ExecuteUnaryPrimitive).Method;
        public static readonly MethodInfo AbiV2CanExecuteBinaryPrimitive =
            ((Func<LuaThread, LuaFrame, int, int, int, bool>)LuaCodegenAbiV2.CanExecuteBinaryPrimitive).Method;
        public static readonly MethodInfo AbiV2ExecuteBinaryPrimitive =
            ((Action<LuaExecutionContext, LuaThread, LuaFrame, int, int, int, int>)LuaCodegenAbiV2.ExecuteBinaryPrimitive).Method;
        public static readonly MethodInfo AbiV2ExecuteNumericForPrepare =
            ((Action<LuaThread, LuaFrame, int, int>)LuaCodegenAbiV2.ExecuteNumericForPrepare).Method;
        public static readonly MethodInfo AbiV2ExecuteNumericForLoop =
            ((Action<LuaThread, LuaFrame, int, int>)LuaCodegenAbiV2.ExecuteNumericForLoop).Method;
        public static readonly MethodInfo AbiV3ExecuteNewTable =
            ((Action<LuaExecutionContext, LuaThread, LuaFrame, int, int, int>)LuaCodegenAbiV3.ExecuteNewTable).Method;
        public static readonly MethodInfo AbiV3ExecuteGetTable =
            ((Func<LuaExecutionContext, LuaThread, LuaFrame, int, int, int, bool>)LuaCodegenAbiV3.ExecuteGetTable).Method;
        public static readonly MethodInfo AbiV3ExecuteSetTable =
            ((Func<LuaExecutionContext, LuaThread, LuaFrame, int, int, int, bool>)LuaCodegenAbiV3.ExecuteSetTable).Method;
        public static readonly MethodInfo AbiV3ExecuteSetList =
            ((Action<LuaThread, LuaFrame, int, int, int, int>)LuaCodegenAbiV3.ExecuteSetList).Method;
        public static readonly MethodInfo AbiV3ExecuteClosure =
            ((Action<LuaExecutionContext, LuaThread, LuaFrame, int, int>)LuaCodegenAbiV3.ExecuteClosure).Method;
        public static readonly MethodInfo AbiV3ExecuteVarArg =
            ((Action<LuaThread, LuaFrame, int, int>)LuaCodegenAbiV3.ExecuteVarArg).Method;
        public static readonly MethodInfo AbiV3TryExecuteFramelessCall =
            ((Func<LuaExecutionContext, LuaThread, LuaFrame, int, int, int, int>)LuaCodegenAbiV3.TryExecuteFramelessCall).Method;
        public static readonly MethodInfo AbiV3CanContinueAfterFramelessCall =
            ((Func<LuaExecutionContext, LuaThread, LuaFrame, bool>)LuaCodegenAbiV3.CanContinueAfterFramelessCall).Method;
        public static readonly MethodInfo AbiV3PollGcSafepoint =
            ((Func<LuaExecutionContext, LuaThread, LuaFrame, bool>)LuaCodegenAbiV3.PollGcSafepoint).Method;
        public static readonly MethodInfo ExitPoll =
            ((Func<int, long, LuaCompiledExitReason, LuaCompiledExit>)LuaCompiledExit.Poll).Method;
        public static readonly MethodInfo ExitContinue =
            ((Func<int, long, LuaCompiledExit>)LuaCompiledExit.Continue).Method;
        public static readonly MethodInfo ExitReturn =
            ((Func<int, long, LuaCompiledExit>)LuaCompiledExit.Return).Method;
        public static readonly MethodInfo ExitCall =
            ((Func<int, long, LuaCompiledExit>)LuaCompiledExit.Call).Method;
        public static readonly MethodInfo ExitTailCall =
            ((Func<int, long, LuaCompiledExit>)LuaCompiledExit.TailCall).Method;
        public static readonly MethodInfo ExitDeopt =
            ((Func<int, long, LuaCompiledExitReason, LuaCompiledExit>)LuaCompiledExit.Deopt).Method;
    }

    private static MethodInfo Method(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type,
        string name,
        Type[] parameters) =>
        type.GetMethod(
            name,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static,
            parameters) ?? throw new InvalidOperationException($"Cannot resolve {type.FullName}.{name}.");
}
