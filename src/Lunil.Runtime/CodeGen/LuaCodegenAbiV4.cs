using System.ComponentModel;
using System.Runtime.CompilerServices;
using Lunil.IR.Canonical;
using Lunil.Runtime.Execution;

namespace Lunil.Runtime.CodeGen;

/// <summary>
/// Runtime ABI v4 adds deterministic numeric-region helpers shared by Tier 2 and Loop OSR.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LuaCodegenAbiV4
{
    public const int RuntimeAbiVersion = 4;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetProgramCounter(LuaFrame frame, int programCounter)
    {
        ArgumentNullException.ThrowIfNull(frame);
        frame.ProgramCounter = programCounter;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetInstructionsConsumed(LuaExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.InstructionsConsumed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Shift(long value, long count, bool left)
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double FloatingModulo(double dividend, double divisor)
    {
        var remainder = dividend % divisor;
        if (remainder > 0 ? divisor < 0 : remainder < 0 && divisor > 0)
        {
            remainder += divisor;
        }

        return remainder;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CompareMixed(
        long integerValue,
        double floatingPoint,
        bool integerOnLeft,
        int operationValue)
    {
        var operation = (LuaIrBinaryOperator)operationValue;
        if (double.IsNaN(floatingPoint))
        {
            return operation == LuaIrBinaryOperator.NotEqual;
        }

        var comparison = IntegerFloatCompare(integerValue, floatingPoint);
        if (!integerOnLeft)
        {
            comparison = -comparison;
        }

        return operation switch
        {
            LuaIrBinaryOperator.Equal => comparison == 0,
            LuaIrBinaryOperator.NotEqual => comparison != 0,
            LuaIrBinaryOperator.LessThan => comparison < 0,
            LuaIrBinaryOperator.LessThanOrEqual => comparison <= 0,
            LuaIrBinaryOperator.GreaterThan => comparison > 0,
            LuaIrBinaryOperator.GreaterThanOrEqual => comparison >= 0,
            _ => throw new InvalidOperationException(
                $"{operation} is not a numeric comparison."),
        };
    }

    private static int IntegerFloatCompare(long integerValue, double floatingPoint)
    {
        if (floatingPoint >= 9_223_372_036_854_775_808d)
        {
            return -1;
        }

        if (floatingPoint < long.MinValue)
        {
            return 1;
        }

        var integral = (long)Math.Truncate(floatingPoint);
        var comparison = integerValue.CompareTo(integral);
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
}
