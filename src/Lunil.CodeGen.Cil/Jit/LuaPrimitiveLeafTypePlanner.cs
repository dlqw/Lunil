using System.Collections.Immutable;
using Lunil.IR.Canonical;

namespace Lunil.CodeGen.Cil.Jit;

internal sealed record LuaPrimitiveLeafTypePlan(
    ImmutableArray<LuaNumericRegionValueKind> ParameterKinds,
    ImmutableArray<LuaNumericRegionValueKind> ResultKinds,
    ImmutableArray<ImmutableArray<LuaNumericRegionValueKind>> KindsBefore,
    ImmutableArray<ImmutableArray<LuaNumericRegionValueKind>> KindsAfter,
    ImmutableArray<LuaNumericRegionRegister> Locals)
{
    public LuaNumericRegionValueKind GetKindBefore(int programCounter, int register) =>
        KindAt(KindsBefore, programCounter, register);

    public LuaNumericRegionValueKind GetKindAfter(int programCounter, int register) =>
        KindAt(KindsAfter, programCounter, register);

    private static LuaNumericRegionValueKind KindAt(
        ImmutableArray<ImmutableArray<LuaNumericRegionValueKind>> states,
        int programCounter,
        int register)
    {
        if ((uint)programCounter >= (uint)states.Length)
        {
            return LuaNumericRegionValueKind.Unknown;
        }

        var state = states[programCounter];
        return (uint)register < (uint)state.Length
            ? state[register]
            : LuaNumericRegionValueKind.Unknown;
    }
}

internal static class LuaPrimitiveLeafTypePlanner
{
    public static bool TryCreate(
        LuaIrFunction function,
        LuaJitFunctionProfile profile,
        out LuaPrimitiveLeafTypePlan? plan)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(profile);
        plan = null;
        if (function.Instructions.IsEmpty || function.RegisterCount <= 0 ||
            profile.ArgumentKinds.Length < function.ParameterCount)
        {
            return false;
        }

        var parameterKinds = ImmutableArray.CreateBuilder<LuaNumericRegionValueKind>(
            function.ParameterCount);
        var entry = new LuaNumericRegionValueKind[function.RegisterCount];
        for (var parameter = 0; parameter < function.ParameterCount; parameter++)
        {
            var kind = ExactPrimitiveKind(profile.ArgumentKinds[parameter]);
            if (!IsPrimitive(kind))
            {
                return false;
            }

            parameterKinds.Add(kind);
            entry[parameter] = kind;
        }

        var before = new LuaNumericRegionValueKind[function.Instructions.Length][];
        var after = new LuaNumericRegionValueKind[function.Instructions.Length][];
        var queued = new bool[function.Instructions.Length];
        var queue = new Queue<int>();
        before[0] = entry;
        queue.Enqueue(0);
        queued[0] = true;
        LuaNumericRegionValueKind[]? resultKinds = null;

        while (queue.TryDequeue(out var programCounter))
        {
            queued[programCounter] = false;
            var input = before[programCounter];
            if (input is null || !TryTransfer(
                    function,
                    programCounter,
                    input,
                    out var output,
                    out var successors,
                    out var returnedKinds))
            {
                return false;
            }

            after[programCounter] = output;
            if (returnedKinds is not null)
            {
                if (resultKinds is null)
                {
                    resultKinds = returnedKinds;
                }
                else if (!resultKinds.AsSpan().SequenceEqual(returnedKinds))
                {
                    return false;
                }
            }

            foreach (var successor in successors)
            {
                if ((uint)successor >= (uint)before.Length)
                {
                    return false;
                }

                if (before[successor] is null)
                {
                    before[successor] = (LuaNumericRegionValueKind[])output.Clone();
                }
                else if (!TryMerge(before[successor], output, out var changed))
                {
                    return false;
                }
                else if (!changed)
                {
                    continue;
                }

                if (!queued[successor])
                {
                    queue.Enqueue(successor);
                    queued[successor] = true;
                }
            }
        }

        if (resultKinds is null)
        {
            return false;
        }

        var locals = new HashSet<LuaNumericRegionRegister>();
        for (var programCounter = 0; programCounter < before.Length; programCounter++)
        {
            AddLocals(before[programCounter], locals);
            AddLocals(after[programCounter], locals);
        }

        plan = new LuaPrimitiveLeafTypePlan(
            parameterKinds.MoveToImmutable(),
            [.. resultKinds],
            ToImmutable(before),
            ToImmutable(after),
            [.. locals.OrderBy(static local => local.Register)
                .ThenBy(static local => local.Kind)]);
        return true;
    }

    private static bool TryTransfer(
        LuaIrFunction function,
        int programCounter,
        LuaNumericRegionValueKind[] input,
        out LuaNumericRegionValueKind[] output,
        out int[] successors,
        out LuaNumericRegionValueKind[]? returnedKinds)
    {
        var mutableOutput = (LuaNumericRegionValueKind[])input.Clone();
        output = mutableOutput;
        successors = [];
        returnedKinds = null;
        var instruction = function.Instructions[programCounter];
        bool IsRegister(int register) => (uint)register < (uint)function.RegisterCount;
        bool IsWindow(int first, int count) => first >= 0 && count >= 0 &&
            first <= function.RegisterCount - count;
        LuaNumericRegionValueKind Read(int register) => IsRegister(register)
            ? input[register]
            : LuaNumericRegionValueKind.Conflict;
        bool Write(int register, LuaNumericRegionValueKind kind)
        {
            if (!IsRegister(register) || !IsPrimitive(kind))
            {
                return false;
            }

            mutableOutput[register] = kind;
            return true;
        }

        switch (instruction.Opcode)
        {
            case LuaIrOpcode.LoadConstant:
                if ((uint)instruction.B >= (uint)function.Constants.Length ||
                    !Write(instruction.A, ConstantKind(function.Constants[instruction.B])))
                {
                    return false;
                }
                break;
            case LuaIrOpcode.Move:
                if (!IsPrimitive(Read(instruction.B)) ||
                    !Write(instruction.A, Read(instruction.B)))
                {
                    return false;
                }
                break;
            case LuaIrOpcode.SetTop:
                if (instruction.A < 0 || instruction.A > function.RegisterCount)
                {
                    return false;
                }
                for (var register = instruction.A;
                     register < function.RegisterCount;
                     register++)
                {
                    mutableOutput[register] = LuaNumericRegionValueKind.Unknown;
                }
                break;
            case LuaIrOpcode.Unary:
                {
                    var operand = Read(instruction.B);
                    var result = (LuaIrUnaryOperator)instruction.C switch
                    {
                        LuaIrUnaryOperator.Negate when IsNumeric(operand) => operand,
                        LuaIrUnaryOperator.BitwiseNot
                            when operand == LuaNumericRegionValueKind.Integer => operand,
                        LuaIrUnaryOperator.LogicalNot when IsPrimitive(operand) =>
                            LuaNumericRegionValueKind.Boolean,
                        _ => LuaNumericRegionValueKind.Conflict,
                    };
                    if (!Write(instruction.A, result))
                    {
                        return false;
                    }
                    break;
                }
            case LuaIrOpcode.Binary:
                if (!Write(
                        instruction.A,
                        BinaryResultKind(
                            (LuaIrBinaryOperator)instruction.D,
                            Read(instruction.B),
                            Read(instruction.C))))
                {
                    return false;
                }
                break;
            case LuaIrOpcode.Jump:
                if (instruction.C >= 0)
                {
                    return false;
                }
                successors = [instruction.B];
                return true;
            case LuaIrOpcode.JumpIfFalse:
            case LuaIrOpcode.JumpIfTrue:
                if (instruction.D != 0 || !IsPrimitive(Read(instruction.A)))
                {
                    return false;
                }
                successors = [instruction.B, programCounter + 1];
                return true;
            case LuaIrOpcode.NumericForPrepare:
                if (!IsWindow(instruction.A, 4) ||
                    Enumerable.Range(instruction.A, 3).Any(register =>
                        Read(register) != LuaNumericRegionValueKind.Integer))
                {
                    return false;
                }
                mutableOutput[instruction.A + 1] = LuaNumericRegionValueKind.Integer;
                mutableOutput[instruction.A + 3] = LuaNumericRegionValueKind.Integer;
                successors = [instruction.B, programCounter + 1];
                return true;
            case LuaIrOpcode.NumericForLoop:
                if (!IsWindow(instruction.A, 4) ||
                    Enumerable.Range(instruction.A, 4).Any(register =>
                        Read(register) != LuaNumericRegionValueKind.Integer))
                {
                    return false;
                }
                successors = [instruction.B, programCounter + 1];
                return true;
            case LuaIrOpcode.Return:
                if (!IsWindow(instruction.A, instruction.B) || instruction.B > 4)
                {
                    return false;
                }
                returnedKinds = new LuaNumericRegionValueKind[instruction.B];
                for (var result = 0; result < returnedKinds.Length; result++)
                {
                    returnedKinds[result] = Read(instruction.A + result);
                    if (!IsPrimitive(returnedKinds[result]))
                    {
                        return false;
                    }
                }
                return true;
            default:
                return false;
        }

        successors = [programCounter + 1];
        return true;
    }

    private static bool TryMerge(
        LuaNumericRegionValueKind[] destination,
        LuaNumericRegionValueKind[] source,
        out bool changed)
    {
        changed = false;
        for (var register = 0; register < destination.Length; register++)
        {
            if (destination[register] == source[register])
            {
                continue;
            }

            if (destination[register] == LuaNumericRegionValueKind.Conflict)
            {
                continue;
            }

            destination[register] = LuaNumericRegionValueKind.Conflict;
            changed = true;
        }

        return true;
    }

    private static LuaNumericRegionValueKind BinaryResultKind(
        LuaIrBinaryOperator operation,
        LuaNumericRegionValueKind left,
        LuaNumericRegionValueKind right)
    {
        if (operation is LuaIrBinaryOperator.Equal or LuaIrBinaryOperator.NotEqual &&
            left == LuaNumericRegionValueKind.Boolean &&
            right == LuaNumericRegionValueKind.Boolean)
        {
            return LuaNumericRegionValueKind.Boolean;
        }

        if (!IsNumeric(left) || !IsNumeric(right))
        {
            return LuaNumericRegionValueKind.Conflict;
        }

        return operation switch
        {
            LuaIrBinaryOperator.Equal or LuaIrBinaryOperator.NotEqual or
                LuaIrBinaryOperator.LessThan or LuaIrBinaryOperator.LessThanOrEqual or
                LuaIrBinaryOperator.GreaterThan or LuaIrBinaryOperator.GreaterThanOrEqual =>
                LuaNumericRegionValueKind.Boolean,
            LuaIrBinaryOperator.BitwiseAnd or LuaIrBinaryOperator.BitwiseOr or
                LuaIrBinaryOperator.BitwiseXor or LuaIrBinaryOperator.ShiftLeft or
                LuaIrBinaryOperator.ShiftRight =>
                left == LuaNumericRegionValueKind.Integer &&
                right == LuaNumericRegionValueKind.Integer
                    ? LuaNumericRegionValueKind.Integer
                    : LuaNumericRegionValueKind.Conflict,
            LuaIrBinaryOperator.Divide or LuaIrBinaryOperator.Power =>
                LuaNumericRegionValueKind.Float,
            LuaIrBinaryOperator.Add or LuaIrBinaryOperator.Subtract or
                LuaIrBinaryOperator.Multiply or LuaIrBinaryOperator.FloorDivide or
                LuaIrBinaryOperator.Modulo =>
                left == LuaNumericRegionValueKind.Integer &&
                right == LuaNumericRegionValueKind.Integer
                    ? LuaNumericRegionValueKind.Integer
                    : LuaNumericRegionValueKind.Float,
            _ => LuaNumericRegionValueKind.Conflict,
        };
    }

    private static LuaNumericRegionValueKind ExactPrimitiveKind(LuaJitValueKinds kinds) =>
        kinds switch
        {
            LuaJitValueKinds.Integer => LuaNumericRegionValueKind.Integer,
            LuaJitValueKinds.Float => LuaNumericRegionValueKind.Float,
            LuaJitValueKinds.Boolean => LuaNumericRegionValueKind.Boolean,
            _ => LuaNumericRegionValueKind.Conflict,
        };

    private static LuaNumericRegionValueKind ConstantKind(LuaIrConstant constant) =>
        constant.Kind switch
        {
            LuaIrConstantKind.Boolean => LuaNumericRegionValueKind.Boolean,
            LuaIrConstantKind.Integer => LuaNumericRegionValueKind.Integer,
            LuaIrConstantKind.Float => LuaNumericRegionValueKind.Float,
            _ => LuaNumericRegionValueKind.Conflict,
        };

    private static bool IsPrimitive(LuaNumericRegionValueKind kind) =>
        kind is LuaNumericRegionValueKind.Integer or LuaNumericRegionValueKind.Float or
            LuaNumericRegionValueKind.Boolean;

    private static bool IsNumeric(LuaNumericRegionValueKind kind) =>
        kind is LuaNumericRegionValueKind.Integer or LuaNumericRegionValueKind.Float;

    private static void AddLocals(
        LuaNumericRegionValueKind[]? state,
        HashSet<LuaNumericRegionRegister> locals)
    {
        if (state is null)
        {
            return;
        }

        for (var register = 0; register < state.Length; register++)
        {
            if (IsPrimitive(state[register]))
            {
                locals.Add(new LuaNumericRegionRegister(register, state[register]));
            }
        }
    }

    private static ImmutableArray<ImmutableArray<LuaNumericRegionValueKind>> ToImmutable(
        LuaNumericRegionValueKind[][] states) =>
        [.. states.Select(static state => state is null
            ? ImmutableArray<LuaNumericRegionValueKind>.Empty
            : [.. state])];
}
