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

    public static CilCallTarget ExitPoll { get; } = Call(
        "LuaCompiledExit.Poll",
        [CilStackValueKind.Int32, CilStackValueKind.Int32, CilStackValueKind.Int32],
        CilStackValueKind.CompiledExit);

    public static CilCallTarget ExitReturn { get; } = Call(
        "LuaCompiledExit.Return",
        [CilStackValueKind.Int32, CilStackValueKind.Int32],
        CilStackValueKind.CompiledExit);

    public static CilCallTarget ExitDeopt { get; } = Call(
        "LuaCompiledExit.Deopt",
        [CilStackValueKind.Int32, CilStackValueKind.Int32, CilStackValueKind.Int32],
        CilStackValueKind.CompiledExit);

    private static readonly Lazy<IReadOnlyDictionary<string, CilCallTarget>> Calls = new(() =>
        new[]
        {
            ContextTryReserveInstructions,
            FrameGetProgramCounter,
            CommitProgramCounter,
            MaterializeConstant,
            ReadRegister,
            WriteRegister,
            ClearRegisters,
            SetFrameTop,
            LuaValueIsTruthy,
            ExitPoll,
            ExitReturn,
            ExitDeopt,
        }.ToDictionary(static target => target.Id, StringComparer.Ordinal));

    public static bool TryGet(string id, out CilCallTarget target) =>
        Calls.Value.TryGetValue(id, out target!);

    private static CilCallTarget Call(
        string id,
        ImmutableArray<CilStackValueKind> parameters,
        CilStackValueKind result,
        bool isGcSafePoint = false) =>
        new(id, parameters, result, isGcSafePoint);
}
