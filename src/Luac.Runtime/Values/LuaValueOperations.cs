using System.Globalization;
using Luac.Core.Numerics;
using Luac.IR.Canonical;

namespace Luac.Runtime.Values;

internal static class LuaValueOperations
{
    public static bool NumberEquals(LuaValue left, LuaValue right)
    {
        if (left.Kind == LuaValueKind.Integer && right.Kind == LuaValueKind.Integer)
        {
            return left.AsInteger() == right.AsInteger();
        }

        if (left.Kind == LuaValueKind.Float && right.Kind == LuaValueKind.Float)
        {
            return left.AsFloat() == right.AsFloat();
        }

        var integer = left.Kind == LuaValueKind.Integer ? left.AsInteger() : right.AsInteger();
        var floatingPoint = left.Kind == LuaValueKind.Float ? left.AsFloat() : right.AsFloat();
        return IntegerFloatCompare(integer, floatingPoint) == 0;
    }

    public static LuaValue Unary(LuaIrUnaryOperator operation, LuaValue operand) => operation switch
    {
        LuaIrUnaryOperator.Negate when operand.Kind == LuaValueKind.Integer =>
            LuaValue.FromInteger(unchecked(-operand.AsInteger())),
        LuaIrUnaryOperator.Negate when IsNumber(operand) =>
            LuaValue.FromFloat(-operand.AsFloat()),
        LuaIrUnaryOperator.BitwiseNot => LuaValue.FromInteger(~ToInteger(operand)),
        LuaIrUnaryOperator.LogicalNot => LuaValue.FromBoolean(!operand.IsTruthy),
        LuaIrUnaryOperator.Length when operand.Kind == LuaValueKind.String =>
            LuaValue.FromInteger(operand.AsString().Length),
        LuaIrUnaryOperator.Length when operand.Kind == LuaValueKind.Table =>
            LuaValue.FromInteger(operand.AsTable().ArrayLength),
        _ => throw new LuaRuntimeException($"Cannot apply {operation} to {operand.Kind}."),
    };

    public static LuaValue Binary(
        LuaState state,
        LuaIrBinaryOperator operation,
        LuaValue left,
        LuaValue right) => operation switch
        {
            LuaIrBinaryOperator.Add => Arithmetic(left, right, static (a, b) => unchecked(a + b), static (a, b) => a + b),
            LuaIrBinaryOperator.Subtract => Arithmetic(left, right, static (a, b) => unchecked(a - b), static (a, b) => a - b),
            LuaIrBinaryOperator.Multiply => Arithmetic(left, right, static (a, b) => unchecked(a * b), static (a, b) => a * b),
            LuaIrBinaryOperator.Divide => LuaValue.FromFloat(ToNumber(left) / ToNumber(right)),
            LuaIrBinaryOperator.FloorDivide => FloorDivide(left, right),
            LuaIrBinaryOperator.Modulo => Modulo(left, right),
            LuaIrBinaryOperator.Power => LuaValue.FromFloat(Math.Pow(ToNumber(left), ToNumber(right))),
            LuaIrBinaryOperator.Concatenate => Concatenate(state, left, right),
            LuaIrBinaryOperator.Equal => LuaValue.FromBoolean(left.Equals(right)),
            LuaIrBinaryOperator.NotEqual => LuaValue.FromBoolean(!left.Equals(right)),
            LuaIrBinaryOperator.LessThan => LuaValue.FromBoolean(LessThan(left, right)),
            LuaIrBinaryOperator.LessThanOrEqual => LuaValue.FromBoolean(LessThanOrEqual(left, right)),
            LuaIrBinaryOperator.GreaterThan => LuaValue.FromBoolean(LessThan(right, left)),
            LuaIrBinaryOperator.GreaterThanOrEqual => LuaValue.FromBoolean(LessThanOrEqual(right, left)),
            LuaIrBinaryOperator.BitwiseAnd => LuaValue.FromInteger(ToInteger(left) & ToInteger(right)),
            LuaIrBinaryOperator.BitwiseOr => LuaValue.FromInteger(ToInteger(left) | ToInteger(right)),
            LuaIrBinaryOperator.BitwiseXor => LuaValue.FromInteger(ToInteger(left) ^ ToInteger(right)),
            LuaIrBinaryOperator.ShiftLeft => LuaValue.FromInteger(Shift(ToInteger(left), ToInteger(right), left: true)),
            LuaIrBinaryOperator.ShiftRight => LuaValue.FromInteger(Shift(ToInteger(left), ToInteger(right), left: false)),
            _ => throw new InvalidOperationException($"Unknown binary operation {operation}."),
        };

    public static bool TryToNumber(LuaValue value, out LuaValue number)
    {
        if (IsNumber(value))
        {
            number = value;
            return true;
        }

        if (value.Kind == LuaValueKind.String &&
            LuaNumberParser.TryParseString(value.AsString().AsSpan(), out var parsed))
        {
            number = parsed.Kind switch
            {
                LuaNumberKind.Integer => LuaValue.FromInteger(parsed.Integer),
                LuaNumberKind.Float => LuaValue.FromFloat(parsed.Float),
                _ => throw new InvalidOperationException(),
            };
            return true;
        }

        number = LuaValue.Nil;
        return false;
    }

    private static LuaValue Arithmetic(
        LuaValue left,
        LuaValue right,
        Func<long, long, long> integerOperation,
        Func<double, double, double> floatOperation)
    {
        if (left.Kind == LuaValueKind.Integer && right.Kind == LuaValueKind.Integer)
        {
            return LuaValue.FromInteger(integerOperation(left.AsInteger(), right.AsInteger()));
        }

        return LuaValue.FromFloat(floatOperation(ToNumber(left), ToNumber(right)));
    }

    private static LuaValue FloorDivide(LuaValue left, LuaValue right)
    {
        if (left.Kind == LuaValueKind.Integer && right.Kind == LuaValueKind.Integer)
        {
            var dividend = left.AsInteger();
            var divisor = right.AsInteger();
            if (divisor == 0)
            {
                throw new LuaRuntimeException("Attempt to divide by zero.");
            }

            if (divisor == -1)
            {
                return LuaValue.FromInteger(unchecked(-dividend));
            }

            var quotient = dividend / divisor;
            var remainder = dividend % divisor;
            if (remainder != 0 && (remainder ^ divisor) < 0)
            {
                quotient--;
            }

            return LuaValue.FromInteger(quotient);
        }

        return LuaValue.FromFloat(Math.Floor(ToNumber(left) / ToNumber(right)));
    }

    private static LuaValue Modulo(LuaValue left, LuaValue right)
    {
        if (left.Kind == LuaValueKind.Integer && right.Kind == LuaValueKind.Integer)
        {
            var dividend = left.AsInteger();
            var divisor = right.AsInteger();
            if (divisor == 0)
            {
                throw new LuaRuntimeException("Attempt to perform modulo by zero.");
            }

            if (divisor == -1)
            {
                return LuaValue.FromInteger(0);
            }

            var remainder = dividend % divisor;
            if (remainder != 0 && (remainder ^ divisor) < 0)
            {
                remainder += divisor;
            }

            return LuaValue.FromInteger(remainder);
        }

        var a = ToNumber(left);
        var b = ToNumber(right);
        return LuaValue.FromFloat(a - Math.Floor(a / b) * b);
    }

    private static LuaValue Concatenate(LuaState state, LuaValue left, LuaValue right)
    {
        var leftBytes = ToStringBytes(left);
        var rightBytes = ToStringBytes(right);
        var bytes = new byte[checked(leftBytes.Length + rightBytes.Length)];
        leftBytes.CopyTo(bytes);
        rightBytes.CopyTo(bytes.AsSpan(leftBytes.Length));
        return LuaValue.FromString(state.Strings.GetOrCreate(bytes));
    }

    private static byte[] ToStringBytes(LuaValue value) => value.Kind switch
    {
        LuaValueKind.String => value.AsString().ToArray(),
        LuaValueKind.Integer => System.Text.Encoding.ASCII.GetBytes(
            value.AsInteger().ToString(CultureInfo.InvariantCulture)),
        LuaValueKind.Float => System.Text.Encoding.ASCII.GetBytes(
            value.AsFloat().ToString("G14", CultureInfo.InvariantCulture)),
        _ => throw new LuaRuntimeException($"Cannot concatenate a {value.Kind} value."),
    };

    private static bool LessThan(LuaValue left, LuaValue right)
    {
        if (IsNumber(left) && IsNumber(right))
        {
            return CompareNumbers(left, right) < 0;
        }

        if (left.Kind == LuaValueKind.String && right.Kind == LuaValueKind.String)
        {
            return left.AsString().AsSpan().SequenceCompareTo(right.AsString().AsSpan()) < 0;
        }

        throw new LuaRuntimeException($"Cannot compare {left.Kind} with {right.Kind}.");
    }

    private static bool LessThanOrEqual(LuaValue left, LuaValue right)
    {
        if (IsNumber(left) && IsNumber(right))
        {
            return CompareNumbers(left, right) <= 0;
        }

        if (left.Kind == LuaValueKind.String && right.Kind == LuaValueKind.String)
        {
            return left.AsString().AsSpan().SequenceCompareTo(right.AsString().AsSpan()) <= 0;
        }

        throw new LuaRuntimeException($"Cannot compare {left.Kind} with {right.Kind}.");
    }

    private static int CompareNumbers(LuaValue left, LuaValue right)
    {
        if (left.Kind == LuaValueKind.Integer && right.Kind == LuaValueKind.Integer)
        {
            return left.AsInteger().CompareTo(right.AsInteger());
        }

        if (left.Kind == LuaValueKind.Integer)
        {
            return IntegerFloatCompare(left.AsInteger(), right.AsFloat());
        }

        if (right.Kind == LuaValueKind.Integer)
        {
            return -IntegerFloatCompare(right.AsInteger(), left.AsFloat());
        }

        return left.AsFloat().CompareTo(right.AsFloat());
    }

    private static int IntegerFloatCompare(long integer, double floatingPoint)
    {
        if (double.IsNaN(floatingPoint))
        {
            return 1;
        }

        if (floatingPoint >= 9_223_372_036_854_775_808d)
        {
            return -1;
        }

        if (floatingPoint < long.MinValue)
        {
            return 1;
        }

        var integral = (long)Math.Truncate(floatingPoint);
        var comparison = integer.CompareTo(integral);
        if (comparison != 0)
        {
            return comparison;
        }

        return floatingPoint.CompareTo((double)integral) switch
        {
            > 0 => -1,
            < 0 => 1,
            _ => 0,
        };
    }

    private static long Shift(long value, long count, bool left)
    {
        if (count < 0)
        {
            if (count == long.MinValue)
            {
                return 0;
            }

            return Shift(value, -count, !left);
        }

        if (count >= 64)
        {
            return 0;
        }

        return left
            ? unchecked((long)((ulong)value << (int)count))
            : unchecked((long)((ulong)value >> (int)count));
    }

    private static bool IsNumber(LuaValue value) =>
        value.Kind is LuaValueKind.Integer or LuaValueKind.Float;

    private static double ToNumber(LuaValue value) => TryToNumber(value, out var number)
        ? number.AsFloat()
        : throw new LuaRuntimeException($"Expected number, got {value.Kind}.");

    private static long ToInteger(LuaValue value) => value.TryGetInteger(out var integer)
        ? integer
        : throw new LuaRuntimeException($"Number has no integer representation: {value}.");
}
