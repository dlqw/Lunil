using System.ComponentModel;
using Lunil.IR.Canonical;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Operations;
using Lunil.Runtime.Values;
using System.Runtime.CompilerServices;

namespace Lunil.Runtime.CodeGen;

/// <summary>
/// Runtime ABI v2 adds guarded primitive operations and numeric-for helpers used by compiled
/// basic blocks. ABI v1 remains available as an infrastructure facade, while current artifact
/// loading requires the exact ABI v2 version.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LuaCodegenAbiV2
{
    public const int RuntimeAbiVersion = 2;

    public static bool CanExecuteCompiledFrame(
        LuaExecutionContext context,
        LuaFrame frame,
        int functionId,
        int registerCount)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(frame);
        return context.IsBackendGenerationCurrent() &&
            LuaCodegenAbiV1.CanExecuteCompiled(context) &&
            frame.Function.Id == functionId &&
            frame.Function.RegisterCount == registerCount &&
            frame.Base >= 0 &&
            frame.Base <= context.Thread.Stack.Capacity - registerCount;
    }

    public static bool CanEnterLoopOsr(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int functionId,
        int registerCount,
        int headerProgramCounter)
    {
        ArgumentNullException.ThrowIfNull(thread);
        return frame.ProgramCounter == headerProgramCounter &&
            CanExecuteCompiledFrame(context, frame, functionId, registerCount) &&
            ReferenceEquals(context.Thread, thread) &&
            ReferenceEquals(thread.CurrentFrame, frame) &&
            thread.UnwindState is null &&
            !thread.IsClosing &&
            frame.Continuation.Kind == LuaContinuationKind.None &&
            frame.Top >= frame.Base &&
            frame.Top <= frame.Base + registerCount &&
            frame.ToBeClosedSlots.All(slot => slot >= frame.Base && slot < frame.Top);
    }

    public static LuaCompiledExitReason CheckLoopOsrHeader(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(frame);
        if (!context.IsBackendGenerationCurrent())
        {
            return LuaCompiledExitReason.BackendInvalidated;
        }

        if (!LuaCodegenAbiV1.CanExecuteCompiled(context) ||
            !ReferenceEquals(context.Thread, thread) ||
            !ReferenceEquals(thread.CurrentFrame, frame) ||
            thread.UnwindState is not null ||
            thread.IsClosing)
        {
            return LuaCompiledExitReason.GuardFailure;
        }

        if (context.RemainingInstructionCount == 0)
        {
            return LuaCompiledExitReason.InstructionBudget;
        }

        context.State.Heap.SafePoint();
        return context.State.Heap.PendingFinalizerCount == 0
            ? LuaCompiledExitReason.None
            : LuaCompiledExitReason.GarbageCollection;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static LuaValue ReadRegisterUnchecked(
        LuaThread thread,
        LuaFrame frame,
        int register) => thread.Stack.ReadUnchecked(frame.Base + register);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRegisterUnchecked(
        LuaThread thread,
        LuaFrame frame,
        int register,
        LuaValue value) => thread.Stack.WriteUnchecked(frame.Base + register, value);

    public static void ClearRegistersUnchecked(
        LuaThread thread,
        LuaFrame frame,
        int firstRegister,
        int count) => thread.Stack.Clear(frame.Base + firstRegister, count);

    public static void SetFrameTopUnchecked(
        LuaThread thread,
        LuaFrame frame,
        int registerCount) => LuaExecutionEngine.SetFrameTop(
            thread,
            frame,
            frame.Base + registerCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ReadTruthyAndSetFrameTopUnchecked(
        LuaThread thread,
        LuaFrame frame,
        int register,
        int registerCount)
    {
        var truthy = ReadRegisterUnchecked(thread, frame, register).IsTruthy;
        SetFrameTopUnchecked(thread, frame, registerCount);
        return truthy;
    }

    public static bool CanSkipClose(LuaThread thread, LuaFrame frame, int register)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(frame);
        var absolute = frame.Base + register;
        if (thread.HasOpenUpvalueAtOrAbove(absolute))
        {
            return false;
        }

        for (var index = frame.ToBeClosedSlots.Count - 1; index >= 0; index--)
        {
            if (frame.ToBeClosedSlots[index] >= absolute)
            {
                return false;
            }
        }

        return true;
    }

    public static bool CanExecuteUnaryPrimitive(
        LuaThread thread,
        LuaFrame frame,
        int operation,
        int operandRegister)
    {
        var operand = ReadRegisterUnchecked(thread, frame, operandRegister);
        return (LuaIrUnaryOperator)operation switch
        {
            LuaIrUnaryOperator.LogicalNot => true,
            LuaIrUnaryOperator.Negate => LuaValueOperations.TryToNumber(operand, out _),
            LuaIrUnaryOperator.BitwiseNot => operand.TryGetInteger(out _),
            LuaIrUnaryOperator.Length => operand.Kind == LuaValueKind.String,
            _ => false,
        };
    }

    public static void ExecuteUnaryPrimitive(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int destinationRegister,
        int operation,
        int operandRegister)
    {
        ArgumentNullException.ThrowIfNull(context);
        var resolution = LuaRuntimeOperations.Unary(
            context.State,
            (LuaIrUnaryOperator)operation,
            ReadRegisterUnchecked(thread, frame, operandRegister));
        if (resolution.RequiresCall)
        {
            throw new InvalidOperationException(
                "The verified unary primitive guard admitted a metamethod path.");
        }

        WriteRegisterUnchecked(thread, frame, destinationRegister, resolution.Value);
    }

    public static bool CanExecuteBinaryPrimitive(
        LuaThread thread,
        LuaFrame frame,
        int operation,
        int leftRegister,
        int rightRegister)
    {
        var left = ReadRegisterUnchecked(thread, frame, leftRegister);
        var right = ReadRegisterUnchecked(thread, frame, rightRegister);
        var binary = (LuaIrBinaryOperator)operation;
        if (binary == LuaIrBinaryOperator.Concatenate)
        {
            return (left.TryGetString() is not null || left.IsInteger || left.IsFloat) &&
                (right.TryGetString() is not null || right.IsInteger || right.IsFloat);
        }

        if (binary is LuaIrBinaryOperator.Equal or LuaIrBinaryOperator.NotEqual)
        {
            return left == right || left.Kind != right.Kind ||
                left.Kind is not (LuaValueKind.Table or LuaValueKind.Userdata);
        }

        if (binary is LuaIrBinaryOperator.Add or LuaIrBinaryOperator.Subtract or
            LuaIrBinaryOperator.Multiply or LuaIrBinaryOperator.Divide or
            LuaIrBinaryOperator.FloorDivide or LuaIrBinaryOperator.Modulo or
            LuaIrBinaryOperator.Power)
        {
            return (IsNumber(left) || LuaValueOperations.TryToNumber(left, out _)) &&
                (IsNumber(right) || LuaValueOperations.TryToNumber(right, out _));
        }

        if (binary is LuaIrBinaryOperator.BitwiseAnd or LuaIrBinaryOperator.BitwiseOr or
            LuaIrBinaryOperator.BitwiseXor or LuaIrBinaryOperator.ShiftLeft or
            LuaIrBinaryOperator.ShiftRight)
        {
            return left.TryGetInteger(out _) && right.TryGetInteger(out _);
        }

        if (binary is LuaIrBinaryOperator.LessThan or LuaIrBinaryOperator.LessThanOrEqual or
            LuaIrBinaryOperator.GreaterThan or LuaIrBinaryOperator.GreaterThanOrEqual)
        {
            return IsNumber(left) && IsNumber(right) ||
                left.Kind == LuaValueKind.String && right.Kind == LuaValueKind.String;
        }

        return false;
    }

    public static void ExecuteBinaryPrimitive(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int destinationRegister,
        int operation,
        int leftRegister,
        int rightRegister)
    {
        ArgumentNullException.ThrowIfNull(context);
        var binary = (LuaIrBinaryOperator)operation;
        var left = ReadRegisterUnchecked(thread, frame, leftRegister);
        var right = ReadRegisterUnchecked(thread, frame, rightRegister);
        LuaValue result;
        if (binary == LuaIrBinaryOperator.Concatenate)
        {
            result = LuaValueOperations.Concatenate(context.State, left, right);
        }
        else if (binary is LuaIrBinaryOperator.Add or LuaIrBinaryOperator.Subtract or
            LuaIrBinaryOperator.Multiply or LuaIrBinaryOperator.Divide or
            LuaIrBinaryOperator.FloorDivide or LuaIrBinaryOperator.Modulo or
            LuaIrBinaryOperator.Power)
        {
            var numericLeft = left;
            var numericRight = right;
            if ((!IsNumber(numericLeft) &&
                    !LuaValueOperations.TryToNumber(numericLeft, out numericLeft)) ||
                (!IsNumber(numericRight) &&
                    !LuaValueOperations.TryToNumber(numericRight, out numericRight)))
            {
                throw new InvalidOperationException(
                    "The verified arithmetic primitive guard admitted a non-number.");
            }

            result = numericLeft.Kind == LuaValueKind.Integer &&
                numericRight.Kind == LuaValueKind.Integer
                    ? LuaValueOperations.BinaryIntegerSpecialized(
                        binary,
                        numericLeft,
                        numericRight)
                    : numericLeft.Kind == LuaValueKind.Float &&
                        numericRight.Kind == LuaValueKind.Float
                        ? LuaValueOperations.BinaryFloatSpecialized(
                            binary,
                            numericLeft,
                            numericRight)
                        : LuaValueOperations.BinaryMixedNumericSpecialized(
                            binary,
                            numericLeft,
                            numericRight);
        }
        else
        {
            result = LuaValueOperations.Binary(context.State, binary, left, right);
        }

        WriteRegisterUnchecked(thread, frame, destinationRegister, result);
    }

    public static void ExecuteNumericForPrepare(
        LuaThread thread,
        LuaFrame frame,
        int baseRegister,
        int exitProgramCounter)
    {
        LuaExecutionEngine.ExecuteNumericForPrepare(
            thread,
            frame,
            new LuaIrInstruction(
                LuaIrOpcode.NumericForPrepare,
                baseRegister,
                exitProgramCounter));
    }

    public static void ExecuteVarArg(
        LuaThread thread,
        LuaFrame frame,
        int destinationRegister,
        int resultCount)
    {
        LuaExecutionEngine.ExecuteVarArg(
            thread,
            frame,
            new LuaIrInstruction(
                LuaIrOpcode.VarArg,
                destinationRegister,
                resultCount));
    }

    public static void ExecuteNumericForLoop(
        LuaThread thread,
        LuaFrame frame,
        int baseRegister,
        int bodyProgramCounter)
    {
        var index = ReadRegisterUnchecked(thread, frame, baseRegister);
        var counterOrLimit = ReadRegisterUnchecked(thread, frame, baseRegister + 1);
        var step = ReadRegisterUnchecked(thread, frame, baseRegister + 2);
        bool continues;
        if (step.Kind == LuaValueKind.Integer)
        {
            var count = unchecked((ulong)counterOrLimit.AsInteger());
            continues = count > 0;
            if (continues)
            {
                index = LuaValue.FromInteger(unchecked(index.AsInteger() + step.AsInteger()));
                WriteRegisterUnchecked(
                    thread,
                    frame,
                    baseRegister + 1,
                    LuaValue.FromInteger(unchecked((long)(count - 1))));
            }
        }
        else
        {
            index = LuaValue.FromFloat(index.AsFloat() + step.AsFloat());
            continues = 0 < step.AsFloat()
                ? index.AsFloat() <= counterOrLimit.AsFloat()
                : counterOrLimit.AsFloat() <= index.AsFloat();
        }

        if (continues)
        {
            WriteRegisterUnchecked(thread, frame, baseRegister, index);
            WriteRegisterUnchecked(thread, frame, baseRegister + 3, index);
            frame.ProgramCounter = bodyProgramCounter;
        }
        else
        {
            frame.ProgramCounter++;
        }
    }

    private static bool IsNumber(LuaValue value) => value.IsInteger || value.IsFloat;
}
