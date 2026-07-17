using System.Globalization;
using System.Buffers;
using System.Buffers.Text;
using System.Text;
using Lunil.Core.Numerics;
using Lunil.IR.Canonical;
using System.Runtime.CompilerServices;

namespace Lunil.Runtime.Values;

public static class LuaValueOperations
{
    internal const int MaximumFormattedNumberByteCount = 32;

    public static string TypeName(LuaValue value)
    {
        var metatable = value.Kind switch
        {
            LuaValueKind.Table => value.AsTable().Metatable,
            LuaValueKind.Userdata => value.AsUserdata().Metatable,
            _ => null,
        };
        if (metatable?.GetStringField("__name"u8) is { Kind: LuaValueKind.String } customName)
        {
            return customName.AsString().ToString();
        }

        return BasicTypeName(value);
    }

    public static string BasicTypeName(LuaValue value) =>
        value.Kind switch
        {
            LuaValueKind.Integer or LuaValueKind.Float => "number",
            LuaValueKind.LightUserdata => "userdata",
            _ => value.Kind.ToString().ToLowerInvariant(),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static LuaValue UnaryIntegerSpecialized(
        LuaIrUnaryOperator operation,
        LuaValue operand)
    {
        var value = operand.AsIntegerUnchecked();
        return operation switch
        {
            LuaIrUnaryOperator.Negate => LuaValue.FromInteger(unchecked(-value)),
            LuaIrUnaryOperator.BitwiseNot => LuaValue.FromInteger(~value),
            _ => throw new InvalidOperationException(
                $"Integer specialization does not support {operation}."),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static LuaValue UnaryFloatSpecialized(
        LuaIrUnaryOperator operation,
        LuaValue operand) => operation switch
        {
            LuaIrUnaryOperator.Negate =>
                LuaValue.FromFloat(-operand.AsFloatUnchecked()),
            _ => Unary(operation, operand),
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static LuaValue BinaryIntegerSpecialized(
        LuaIrBinaryOperator operation,
        LuaValue left,
        LuaValue right)
    {
        var leftValue = left.AsIntegerUnchecked();
        var rightValue = right.AsIntegerUnchecked();
        return operation switch
        {
            LuaIrBinaryOperator.Add =>
                LuaValue.FromInteger(unchecked(leftValue + rightValue)),
            LuaIrBinaryOperator.Subtract =>
                LuaValue.FromInteger(unchecked(leftValue - rightValue)),
            LuaIrBinaryOperator.Multiply =>
                LuaValue.FromInteger(unchecked(leftValue * rightValue)),
            LuaIrBinaryOperator.Divide =>
                LuaValue.FromFloat((double)leftValue / rightValue),
            LuaIrBinaryOperator.FloorDivide =>
                FloorDivideIntegers(leftValue, rightValue),
            LuaIrBinaryOperator.Modulo => ModuloIntegers(leftValue, rightValue),
            LuaIrBinaryOperator.Power =>
                LuaValue.FromFloat(Math.Pow(leftValue, rightValue)),
            LuaIrBinaryOperator.Equal => LuaValue.FromBoolean(leftValue == rightValue),
            LuaIrBinaryOperator.NotEqual => LuaValue.FromBoolean(leftValue != rightValue),
            LuaIrBinaryOperator.LessThan => LuaValue.FromBoolean(leftValue < rightValue),
            LuaIrBinaryOperator.LessThanOrEqual => LuaValue.FromBoolean(leftValue <= rightValue),
            LuaIrBinaryOperator.GreaterThan => LuaValue.FromBoolean(leftValue > rightValue),
            LuaIrBinaryOperator.GreaterThanOrEqual => LuaValue.FromBoolean(leftValue >= rightValue),
            LuaIrBinaryOperator.BitwiseAnd => LuaValue.FromInteger(leftValue & rightValue),
            LuaIrBinaryOperator.BitwiseOr => LuaValue.FromInteger(leftValue | rightValue),
            LuaIrBinaryOperator.BitwiseXor => LuaValue.FromInteger(leftValue ^ rightValue),
            LuaIrBinaryOperator.ShiftLeft =>
                LuaValue.FromInteger(Shift(leftValue, rightValue, left: true)),
            LuaIrBinaryOperator.ShiftRight =>
                LuaValue.FromInteger(Shift(leftValue, rightValue, left: false)),
            _ => throw new InvalidOperationException(
                $"Integer specialization does not support {operation}."),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static LuaValue BinaryFloatSpecialized(
        LuaIrBinaryOperator operation,
        LuaValue left,
        LuaValue right)
    {
        var leftValue = left.AsFloatUnchecked();
        var rightValue = right.AsFloatUnchecked();
        return BinaryFloatingPointSpecialized(operation, leftValue, rightValue, left, right);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static LuaValue BinaryMixedNumericSpecialized(
        LuaIrBinaryOperator operation,
        LuaValue left,
        LuaValue right)
    {
        var leftValue = left.AsFloat();
        var rightValue = right.AsFloat();
        return operation switch
        {
            LuaIrBinaryOperator.Equal => LuaValue.FromBoolean(NumberEquals(left, right)),
            LuaIrBinaryOperator.NotEqual => LuaValue.FromBoolean(!NumberEquals(left, right)),
            LuaIrBinaryOperator.LessThan => LuaValue.FromBoolean(
                !double.IsNaN(leftValue) &&
                !double.IsNaN(rightValue) &&
                CompareNumbers(left, right) < 0),
            LuaIrBinaryOperator.LessThanOrEqual => LuaValue.FromBoolean(
                !double.IsNaN(leftValue) &&
                !double.IsNaN(rightValue) &&
                CompareNumbers(left, right) <= 0),
            LuaIrBinaryOperator.GreaterThan => LuaValue.FromBoolean(
                !double.IsNaN(leftValue) &&
                !double.IsNaN(rightValue) &&
                CompareNumbers(left, right) > 0),
            LuaIrBinaryOperator.GreaterThanOrEqual => LuaValue.FromBoolean(
                !double.IsNaN(leftValue) &&
                !double.IsNaN(rightValue) &&
                CompareNumbers(left, right) >= 0),
            _ => BinaryFloatingPointSpecialized(
                operation,
                leftValue,
                rightValue,
                left,
                right),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static LuaValue BinaryPrimitiveSpecialized(
        LuaIrBinaryOperator operation,
        LuaValue left,
        LuaValue right) => operation switch
        {
            LuaIrBinaryOperator.Equal => LuaValue.FromBoolean(left.Equals(right)),
            LuaIrBinaryOperator.NotEqual => LuaValue.FromBoolean(!left.Equals(right)),
            LuaIrBinaryOperator.LessThan => LuaValue.FromBoolean(LessThan(left, right)),
            LuaIrBinaryOperator.LessThanOrEqual =>
                LuaValue.FromBoolean(LessThanOrEqual(left, right)),
            LuaIrBinaryOperator.GreaterThan => LuaValue.FromBoolean(LessThan(right, left)),
            LuaIrBinaryOperator.GreaterThanOrEqual =>
                LuaValue.FromBoolean(LessThanOrEqual(right, left)),
            _ => throw new InvalidOperationException(
                $"Primitive specialization does not support {operation}."),
        };

    public static string FormatFloat(double value)
    {
        Span<byte> bytes = stackalloc byte[MaximumFormattedNumberByteCount];
        var length = FormatFloatUtf8(value, bytes);
        return Encoding.ASCII.GetString(bytes[..length]);
    }

    internal static int FormatNumberUtf8(LuaValue value, Span<byte> destination) =>
        value.Kind switch
        {
            LuaValueKind.Integer => FormatIntegerUtf8(value.AsInteger(), destination),
            LuaValueKind.Float => FormatFloatUtf8(value.AsFloat(), destination),
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                "Only Lua numbers have a canonical numeric string representation."),
        };

    private static int FormatIntegerUtf8(long value, Span<byte> destination)
    {
        if (!Utf8Formatter.TryFormat(value, destination, out var written))
        {
            throw new ArgumentException("The numeric formatting destination is too small.", nameof(destination));
        }

        return written;
    }

    private static int FormatFloatUtf8(double value, Span<byte> destination)
    {
        ReadOnlySpan<byte> special = double.IsPositiveInfinity(value)
            ? "inf"u8
            : double.IsNegativeInfinity(value)
                ? "-inf"u8
                : double.IsNaN(value)
                    ? "nan"u8
                    : default;
        if (!special.IsEmpty)
        {
            if (!special.TryCopyTo(destination))
            {
                throw new ArgumentException(
                    "The numeric formatting destination is too small.",
                    nameof(destination));
            }

            return special.Length;
        }

        if (!Utf8Formatter.TryFormat(
                value,
                destination,
                out var written,
                new StandardFormat('G', 14)))
        {
            throw new ArgumentException("The numeric formatting destination is too small.", nameof(destination));
        }

        var hasDecimalOrExponent = false;
        for (var index = 0; index < written; index++)
        {
            if (destination[index] == (byte)'E')
            {
                destination[index] = (byte)'e';
                hasDecimalOrExponent = true;
            }
            else if (destination[index] is (byte)'.' or (byte)'e')
            {
                hasDecimalOrExponent = true;
            }
        }

        if (!hasDecimalOrExponent)
        {
            if (destination.Length - written < 2)
            {
                throw new ArgumentException(
                    "The numeric formatting destination is too small.",
                    nameof(destination));
            }

            destination[written++] = (byte)'.';
            destination[written++] = (byte)'0';
        }

        return written;
    }

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
        _ => throw new LuaRuntimeException(
            $"Cannot apply {operation} to {TypeName(operand)}."),
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
            return FloorDivideIntegers(left.AsInteger(), right.AsInteger());
        }

        return LuaValue.FromFloat(Math.Floor(ToNumber(left) / ToNumber(right)));
    }

    private static LuaValue Modulo(LuaValue left, LuaValue right)
    {
        if (left.Kind == LuaValueKind.Integer && right.Kind == LuaValueKind.Integer)
        {
            return ModuloIntegers(left.AsInteger(), right.AsInteger());
        }

        var dividendNumber = ToNumber(left);
        var divisorNumber = ToNumber(right);
        var floatingRemainder = dividendNumber % divisorNumber;
        if (floatingRemainder > 0 ? divisorNumber < 0 :
            floatingRemainder < 0 && divisorNumber > 0)
        {
            floatingRemainder += divisorNumber;
        }

        return LuaValue.FromFloat(floatingRemainder);
    }

    internal static LuaValue Concatenate(LuaState state, LuaValue left, LuaValue right)
    {
        var leftString = left.TryGetString();
        if (leftString is not null &&
            right.IsInteger &&
            state.Strings.TryGetOrCreateIntegerConcat(
                leftString,
                right.AsIntegerUnchecked(),
                textFirst: true,
                out var cachedRight))
        {
            return LuaValue.FromString(cachedRight);
        }

        var rightString = right.TryGetString();
        if (left.IsInteger &&
            rightString is not null &&
            state.Strings.TryGetOrCreateIntegerConcat(
                rightString,
                left.AsIntegerUnchecked(),
                textFirst: false,
                out var cachedLeft))
        {
            return LuaValue.FromString(cachedLeft);
        }

        Span<byte> leftNumber = stackalloc byte[MaximumFormattedNumberByteCount];
        Span<byte> rightNumber = stackalloc byte[MaximumFormattedNumberByteCount];
        int leftLength;
        if (left.Kind == LuaValueKind.String)
        {
            leftLength = left.AsString().Length;
        }
        else if (left.Kind is LuaValueKind.Integer or LuaValueKind.Float)
        {
            leftLength = FormatNumberUtf8(left, leftNumber);
        }
        else
        {
            throw new LuaRuntimeException($"Cannot concatenate a {TypeName(left)} value.");
        }

        int rightLength;
        if (right.Kind == LuaValueKind.String)
        {
            rightLength = right.AsString().Length;
        }
        else if (right.Kind is LuaValueKind.Integer or LuaValueKind.Float)
        {
            rightLength = FormatNumberUtf8(right, rightNumber);
        }
        else
        {
            throw new LuaRuntimeException($"Cannot concatenate a {TypeName(right)} value.");
        }

        const int maximumStackBytes = 256;
        var length = checked(leftLength + rightLength);
        byte[]? rented = null;
        Span<byte> bytes = length <= maximumStackBytes
            ? stackalloc byte[length]
            : (rented = ArrayPool<byte>.Shared.Rent(length));
        try
        {
            var result = bytes[..length];
            if (left.Kind == LuaValueKind.String)
            {
                left.AsString().AsSpan().CopyTo(result);
            }
            else
            {
                leftNumber[..leftLength].CopyTo(result);
            }

            if (right.Kind == LuaValueKind.String)
            {
                right.AsString().AsSpan().CopyTo(result[leftLength..]);
            }
            else
            {
                rightNumber[..rightLength].CopyTo(result[leftLength..]);
            }

            return LuaValue.FromString(state.Strings.GetOrCreate(result));
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static bool LessThan(LuaValue left, LuaValue right)
    {
        if (IsNumber(left) && IsNumber(right))
        {
            if (left.Kind == LuaValueKind.Float && double.IsNaN(left.AsFloat()) ||
                right.Kind == LuaValueKind.Float && double.IsNaN(right.AsFloat()))
            {
                return false;
            }

            return CompareNumbers(left, right) < 0;
        }

        if (left.Kind == LuaValueKind.String && right.Kind == LuaValueKind.String)
        {
            return left.AsString().AsSpan().SequenceCompareTo(right.AsString().AsSpan()) < 0;
        }

        throw new LuaRuntimeException(
            $"Cannot compare {TypeName(left)} with {TypeName(right)}.");
    }

    private static bool LessThanOrEqual(LuaValue left, LuaValue right)
    {
        if (IsNumber(left) && IsNumber(right))
        {
            if (left.Kind == LuaValueKind.Float && double.IsNaN(left.AsFloat()) ||
                right.Kind == LuaValueKind.Float && double.IsNaN(right.AsFloat()))
            {
                return false;
            }

            return CompareNumbers(left, right) <= 0;
        }

        if (left.Kind == LuaValueKind.String && right.Kind == LuaValueKind.String)
        {
            return left.AsString().AsSpan().SequenceCompareTo(right.AsString().AsSpan()) <= 0;
        }

        throw new LuaRuntimeException(
            $"Cannot compare {TypeName(left)} with {TypeName(right)}.");
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

    private static LuaValue FloorDivideIntegers(long dividend, long divisor)
    {
        if (divisor == 0)
        {
            throw new LuaRuntimeException("attempt to divide by zero");
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

    private static LuaValue ModuloIntegers(long dividend, long divisor)
    {
        if (divisor == 0)
        {
            throw new LuaRuntimeException("attempt to perform 'n%0'");
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

    private static LuaValue BinaryFloatingPointSpecialized(
        LuaIrBinaryOperator operation,
        double leftValue,
        double rightValue,
        LuaValue left,
        LuaValue right) => operation switch
        {
            LuaIrBinaryOperator.Add => LuaValue.FromFloat(leftValue + rightValue),
            LuaIrBinaryOperator.Subtract => LuaValue.FromFloat(leftValue - rightValue),
            LuaIrBinaryOperator.Multiply => LuaValue.FromFloat(leftValue * rightValue),
            LuaIrBinaryOperator.Divide => LuaValue.FromFloat(leftValue / rightValue),
            LuaIrBinaryOperator.FloorDivide =>
                LuaValue.FromFloat(Math.Floor(leftValue / rightValue)),
            LuaIrBinaryOperator.Modulo =>
                LuaValue.FromFloat(FloatingModulo(leftValue, rightValue)),
            LuaIrBinaryOperator.Power => LuaValue.FromFloat(Math.Pow(leftValue, rightValue)),
            LuaIrBinaryOperator.Equal => LuaValue.FromBoolean(leftValue == rightValue),
            LuaIrBinaryOperator.NotEqual => LuaValue.FromBoolean(leftValue != rightValue),
            LuaIrBinaryOperator.LessThan => LuaValue.FromBoolean(leftValue < rightValue),
            LuaIrBinaryOperator.LessThanOrEqual => LuaValue.FromBoolean(leftValue <= rightValue),
            LuaIrBinaryOperator.GreaterThan => LuaValue.FromBoolean(leftValue > rightValue),
            LuaIrBinaryOperator.GreaterThanOrEqual => LuaValue.FromBoolean(leftValue >= rightValue),
            _ => Binary(null!, operation, left, right),
        };

    private static double FloatingModulo(double dividend, double divisor)
    {
        var remainder = dividend % divisor;
        if (remainder > 0 ? divisor < 0 : remainder < 0 && divisor > 0)
        {
            remainder += divisor;
        }

        return remainder;
    }

    private static bool IsNumber(LuaValue value) =>
        value.Kind is LuaValueKind.Integer or LuaValueKind.Float;

    private static double ToNumber(LuaValue value) => TryToNumber(value, out var number)
        ? number.AsFloat()
        : throw new LuaRuntimeException($"Expected number, got {TypeName(value)}.");

    private static long ToInteger(LuaValue value) => value.TryGetInteger(out var integer)
        ? integer
        : throw new LuaRuntimeException($"Number has no integer representation: {value}.");
}
