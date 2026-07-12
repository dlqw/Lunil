using System.ComponentModel;
using Lunil.IR.Canonical;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Operations;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.CodeGen;

/// <summary>
/// Versioned Runtime operations callable from dynamically emitted and persisted CIL.
/// This is infrastructure ABI rather than the ordinary hosting surface.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LuaCodegenAbiV1
{
    public const int RuntimeAbiVersion = 1;

    public static LuaValue ReadRegister(LuaThread thread, LuaFrame frame, int register)
    {
        ValidateRegister(frame, register);
        return LuaExecutionEngine.Read(thread, frame, register);
    }

    public static void WriteRegister(
        LuaThread thread,
        LuaFrame frame,
        int register,
        LuaValue value)
    {
        ValidateRegister(frame, register);
        LuaExecutionEngine.Write(thread, frame, register, value);
    }

    public static void ClearRegisters(
        LuaThread thread,
        LuaFrame frame,
        int firstRegister,
        int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ValidateRegisterRange(frame, firstRegister, count);
        thread.Stack.Clear(frame.Base + firstRegister, count);
    }

    public static void SetFrameTop(LuaThread thread, LuaFrame frame, int registerCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(registerCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            registerCount,
            frame.Closure.Function.RegisterCount);
        LuaExecutionEngine.SetFrameTop(thread, frame, checked(frame.Base + registerCount));
    }

    public static LuaValue ReadUpvalue(LuaFrame frame, int upvalue) =>
        frame.Closure.GetUpvalue(upvalue).Value;

    public static void WriteUpvalue(LuaFrame frame, int upvalue, LuaValue value) =>
        frame.Closure.GetUpvalue(upvalue).Value = value;

    public static LuaValue MaterializeConstant(
        LuaExecutionContext context,
        LuaFrame frame,
        int constant)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(constant);
        var constants = frame.Closure.Function.Constants;
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(constant, constants.Length);
        return LuaExecutionEngine.MaterializeConstant(context.State, constants[constant]);
    }

    public static LuaValue CreateClosure(
        LuaExecutionContext context,
        LuaFrame parent,
        int functionId)
    {
        return LuaValue.FromFunction(
            LuaExecutionEngine.CreateClosure(context.Thread, parent, functionId));
    }

    public static LuaOperationResolution GetIndex(
        LuaExecutionContext context,
        LuaValue target,
        LuaValue key) =>
        LuaRuntimeOperations.GetIndex(context.State, target, key);

    public static LuaOperationResolution SetIndex(
        LuaExecutionContext context,
        LuaValue target,
        LuaValue key,
        LuaValue value) =>
        LuaRuntimeOperations.SetIndex(context.State, target, key, value);

    public static LuaOperationResolution Unary(
        LuaExecutionContext context,
        LuaIrUnaryOperator operation,
        LuaValue operand) =>
        LuaRuntimeOperations.Unary(context.State, operation, operand);

    public static LuaOperationResolution Binary(
        LuaExecutionContext context,
        LuaIrBinaryOperator operation,
        LuaValue left,
        LuaValue right) =>
        LuaRuntimeOperations.Binary(context.State, operation, left, right);

    public static bool IsTruthy(LuaValue value) => value.IsTruthy;

    public static bool CanExecuteCompiled(LuaExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return !context.HasExactDebugHooks && context.IsDebugModeCurrent();
    }

    public static void ObserveCanonicalInstruction(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int programCounter)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ExecutionEngine?.ObserveCodegenInstruction(
            context,
            thread,
            frame,
            programCounter);
    }

    public static LuaCompiledExit ExecuteCanonicalInstruction(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int programCounter)
    {
        ArgumentNullException.ThrowIfNull(context);
        var executionEngine = context.ExecutionEngine ??
            throw new InvalidOperationException(
                "The execution context is not attached to an execution engine.");
        return executionEngine.ExecuteCodegenSlowPath(context, thread, frame, programCounter);
    }

    public static void CommitProgramCounter(LuaFrame frame, int programCounter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(programCounter);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            programCounter,
            frame.Closure.Function.Instructions.Length);
        frame.ProgramCounter = programCounter;
    }

    private static void ValidateRegister(LuaFrame frame, int register)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(register);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            register,
            frame.Closure.Function.RegisterCount);
    }

    private static void ValidateRegisterRange(LuaFrame frame, int firstRegister, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstRegister);
        var registerCount = frame.Closure.Function.RegisterCount;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstRegister, registerCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, registerCount - firstRegister);
    }
}
