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

    public static CilCallTarget CanSkipClose { get; } = Call(
        "LuaCodegenAbiV2.CanSkipClose",
        [CilStackValueKind.Frame, CilStackValueKind.Int32],
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
            CanSkipClose,
            ObserveCanonicalInstruction,
            ExecuteCanonicalInstruction,
            CanExecuteUnaryPrimitive,
            ExecuteUnaryPrimitive,
            CanExecuteBinaryPrimitive,
            ExecuteBinaryPrimitive,
            ExecuteNumericForPrepare,
            ExecuteNumericForLoop,
            ExitContinue,
            ExitPoll,
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
