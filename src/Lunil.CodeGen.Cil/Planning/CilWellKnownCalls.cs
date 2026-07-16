using System.Collections.Immutable;

namespace Lunil.CodeGen.Cil.Planning;

public static class CilWellKnownCalls
{
    public static CilCallTarget ContextTryReserveInstructions { get; } = Call(
        "LuaExecutionContext.TryReserveInstructions",
        [CilStackValueKind.ExecutionContext, CilStackValueKind.Int32],
        CilStackValueKind.Int32);

    public static CilCallTarget FrameGetProgramCounter { get; } = Call(
        "LuaFrame.get_ProgramCounter",
        [CilStackValueKind.Frame],
        CilStackValueKind.Int32);

    public static CilCallTarget CommitProgramCounter { get; } = Call(
        "LuaCodegenAbiV1.CommitProgramCounter",
        [CilStackValueKind.Frame, CilStackValueKind.Int32],
        CilStackValueKind.Void);

    public static CilCallTarget MaterializeConstant { get; } = Call(
        "LuaCodegenAbiV1.MaterializeConstant",
        [
            CilStackValueKind.ExecutionContext,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.LuaValue,
        isGcSafePoint: true);

    public static CilCallTarget ReadRegister { get; } = Call(
        "LuaCodegenAbiV1.ReadRegister",
        [CilStackValueKind.Thread, CilStackValueKind.Frame, CilStackValueKind.Int32],
        CilStackValueKind.LuaValue);

    public static CilCallTarget WriteRegister { get; } = Call(
        "LuaCodegenAbiV1.WriteRegister",
        [
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
            CilStackValueKind.LuaValue,
        ],
        CilStackValueKind.Void);

    public static CilCallTarget ReadUpvalue { get; } = Call(
        "LuaCodegenAbiV1.ReadUpvalue",
        [CilStackValueKind.Frame, CilStackValueKind.Int32],
        CilStackValueKind.LuaValue);

    public static CilCallTarget WriteUpvalue { get; } = Call(
        "LuaCodegenAbiV1.WriteUpvalue",
        [CilStackValueKind.Frame, CilStackValueKind.Int32, CilStackValueKind.LuaValue],
        CilStackValueKind.Void);

    public static CilCallTarget ClearRegisters { get; } = Call(
        "LuaCodegenAbiV1.ClearRegisters",
        [
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.Void);

    public static CilCallTarget SetFrameTop { get; } = Call(
        "LuaCodegenAbiV1.SetFrameTop",
        [CilStackValueKind.Thread, CilStackValueKind.Frame, CilStackValueKind.Int32],
        CilStackValueKind.Void);

    public static CilCallTarget LuaValueIsTruthy { get; } = Call(
        "LuaCodegenAbiV1.IsTruthy",
        [CilStackValueKind.LuaValue],
        CilStackValueKind.Int32);

    public static CilCallTarget CanExecuteCompiled { get; } = Call(
        "LuaCodegenAbiV1.CanExecuteCompiled",
        [CilStackValueKind.ExecutionContext],
        CilStackValueKind.Int32);

    public static CilCallTarget CanExecuteCompiledFrame { get; } = Call(
        "LuaCodegenAbiV2.CanExecuteCompiledFrame",
        [
            CilStackValueKind.ExecutionContext,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.Int32);

    public static CilCallTarget ReadRegisterUnchecked { get; } = Call(
        "LuaCodegenAbiV2.ReadRegisterUnchecked",
        [CilStackValueKind.Thread, CilStackValueKind.Frame, CilStackValueKind.Int32],
        CilStackValueKind.LuaValue);

    public static CilCallTarget WriteRegisterUnchecked { get; } = Call(
        "LuaCodegenAbiV2.WriteRegisterUnchecked",
        [
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
            CilStackValueKind.LuaValue,
        ],
        CilStackValueKind.Void);

    public static CilCallTarget ClearRegistersUnchecked { get; } = Call(
        "LuaCodegenAbiV2.ClearRegistersUnchecked",
        [
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.Void);

    public static CilCallTarget SetFrameTopUnchecked { get; } = Call(
        "LuaCodegenAbiV2.SetFrameTopUnchecked",
        [CilStackValueKind.Thread, CilStackValueKind.Frame, CilStackValueKind.Int32],
        CilStackValueKind.Void);

    public static CilCallTarget ReadTruthyAndSetFrameTopUnchecked { get; } = Call(
        "LuaCodegenAbiV2.ReadTruthyAndSetFrameTopUnchecked",
        [
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.Int32);

    public static CilCallTarget CanSkipClose { get; } = Call(
        "LuaCodegenAbiV2.CanSkipClose",
        [CilStackValueKind.Thread, CilStackValueKind.Frame, CilStackValueKind.Int32],
        CilStackValueKind.Int32);

    public static CilCallTarget ObserveCanonicalInstruction { get; } = Call(
        "LuaCodegenAbiV1.ObserveCanonicalInstruction",
        [
            CilStackValueKind.ExecutionContext,
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.Void);

    public static CilCallTarget ExecuteCanonicalInstruction { get; } = Call(
        "LuaCodegenAbiV1.ExecuteCanonicalInstruction",
        [
            CilStackValueKind.ExecutionContext,
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.CompiledExit);

    public static CilCallTarget CanExecuteUnaryPrimitive { get; } = Call(
        "LuaCodegenAbiV2.CanExecuteUnaryPrimitive",
        [
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.Int32);

    public static CilCallTarget ExecuteUnaryPrimitive { get; } = Call(
        "LuaCodegenAbiV2.ExecuteUnaryPrimitive",
        [
            CilStackValueKind.ExecutionContext,
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.Void);

    public static CilCallTarget CanExecuteBinaryPrimitive { get; } = Call(
        "LuaCodegenAbiV2.CanExecuteBinaryPrimitive",
        [
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.Int32);

    public static CilCallTarget ExecuteBinaryPrimitive { get; } = Call(
        "LuaCodegenAbiV2.ExecuteBinaryPrimitive",
        [
            CilStackValueKind.ExecutionContext,
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.Void);

    public static CilCallTarget ExecuteNumericForPrepare { get; } = Call(
        "LuaCodegenAbiV2.ExecuteNumericForPrepare",
        [
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.Void);

    public static CilCallTarget ExecuteNumericForLoop { get; } = Call(
        "LuaCodegenAbiV2.ExecuteNumericForLoop",
        [
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.Void);

    public static CilCallTarget ExecuteNewTable { get; } = Call(
        "LuaCodegenAbiV3.ExecuteNewTable",
        [
            CilStackValueKind.ExecutionContext,
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.Void,
        isGcSafePoint: true);

    public static CilCallTarget ExecuteGetTable { get; } = Call(
        "LuaCodegenAbiV3.ExecuteGetTable",
        [
            CilStackValueKind.ExecutionContext,
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.Int32,
        isGcSafePoint: true);

    public static CilCallTarget ExecuteSetTable { get; } = Call(
        "LuaCodegenAbiV3.ExecuteSetTable",
        [
            CilStackValueKind.ExecutionContext,
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.Int32,
        isGcSafePoint: true);

    public static CilCallTarget ExecuteSetList { get; } = Call(
        "LuaCodegenAbiV3.ExecuteSetList",
        [
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.Void,
        isGcSafePoint: true);

    public static CilCallTarget ExecuteClosure { get; } = Call(
        "LuaCodegenAbiV3.ExecuteClosure",
        [
            CilStackValueKind.ExecutionContext,
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.Void,
        isGcSafePoint: true);

    public static CilCallTarget ExecuteVarArg { get; } = Call(
        "LuaCodegenAbiV3.ExecuteVarArg",
        [
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.Void);

    public static CilCallTarget TryExecuteFramelessCall { get; } = Call(
        "LuaCodegenAbiV3.TryExecuteFramelessCall",
        [
            CilStackValueKind.ExecutionContext,
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
            CilStackValueKind.Int32,
        ],
        CilStackValueKind.Int32,
        isGcSafePoint: true);

    public static CilCallTarget CanContinueAfterFramelessCall { get; } = Call(
        "LuaCodegenAbiV3.CanContinueAfterFramelessCall",
        [
            CilStackValueKind.ExecutionContext,
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
        ],
        CilStackValueKind.Int32);

    public static CilCallTarget PollGcSafepoint { get; } = Call(
        "LuaCodegenAbiV3.PollGcSafepoint",
        [
            CilStackValueKind.ExecutionContext,
            CilStackValueKind.Thread,
            CilStackValueKind.Frame,
        ],
        CilStackValueKind.Int32,
        isGcSafePoint: true);

    public static CilCallTarget ExitPoll { get; } = Call(
        "LuaCompiledExit.Poll",
        [CilStackValueKind.Int32, CilStackValueKind.Int32, CilStackValueKind.Int32],
        CilStackValueKind.CompiledExit);

    public static CilCallTarget ExitContinue { get; } = Call(
        "LuaCompiledExit.Continue",
        [CilStackValueKind.Int32, CilStackValueKind.Int32],
        CilStackValueKind.CompiledExit);

    public static CilCallTarget ExitReturn { get; } = Call(
        "LuaCompiledExit.Return",
        [CilStackValueKind.Int32, CilStackValueKind.Int32],
        CilStackValueKind.CompiledExit);

    public static CilCallTarget ExitCall { get; } = Call(
        "LuaCompiledExit.Call",
        [CilStackValueKind.Int32, CilStackValueKind.Int32],
        CilStackValueKind.CompiledExit);

    public static CilCallTarget ExitTailCall { get; } = Call(
        "LuaCompiledExit.TailCall",
        [CilStackValueKind.Int32, CilStackValueKind.Int32],
        CilStackValueKind.CompiledExit);

    public static CilCallTarget ExitDeopt { get; } = Call(
        "LuaCompiledExit.Deopt",
        [CilStackValueKind.Int32, CilStackValueKind.Int32, CilStackValueKind.Int32],
        CilStackValueKind.CompiledExit);

    public static ImmutableArray<CilCallTarget> All { get; } =
        [
            ContextTryReserveInstructions,
            FrameGetProgramCounter,
            CommitProgramCounter,
            MaterializeConstant,
            ReadRegister,
            WriteRegister,
            ReadUpvalue,
            WriteUpvalue,
            ClearRegisters,
            SetFrameTop,
            LuaValueIsTruthy,
            CanExecuteCompiled,
            CanExecuteCompiledFrame,
            ReadRegisterUnchecked,
            WriteRegisterUnchecked,
            ClearRegistersUnchecked,
            SetFrameTopUnchecked,
            ReadTruthyAndSetFrameTopUnchecked,
            CanSkipClose,
            ObserveCanonicalInstruction,
            ExecuteCanonicalInstruction,
            CanExecuteUnaryPrimitive,
            ExecuteUnaryPrimitive,
            CanExecuteBinaryPrimitive,
            ExecuteBinaryPrimitive,
            ExecuteNumericForPrepare,
            ExecuteNumericForLoop,
            ExecuteNewTable,
            ExecuteGetTable,
            ExecuteSetTable,
            ExecuteSetList,
            ExecuteClosure,
            ExecuteVarArg,
            TryExecuteFramelessCall,
            CanContinueAfterFramelessCall,
            PollGcSafepoint,
            ExitContinue,
            ExitPoll,
            ExitCall,
            ExitTailCall,
            ExitReturn,
            ExitDeopt,
        ];

    private static readonly Lazy<IReadOnlyDictionary<string, CilCallTarget>> Calls = new(() =>
        All
            .ToDictionary(static target => target.Id, StringComparer.Ordinal));

    public static bool TryGet(string id, out CilCallTarget target) =>
        Calls.Value.TryGetValue(id, out target!);

    private static CilCallTarget Call(
        string id,
        ImmutableArray<CilStackValueKind> parameters,
        CilStackValueKind result,
        bool isGcSafePoint = false) =>
        new(id, parameters, result, isGcSafePoint);
}
