using System.ComponentModel;
using System.Runtime.CompilerServices;
using Lunil.IR.Canonical;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

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

    /// <summary>
    /// Attempts a generation-bound, side-effect-free compiled leaf call without creating a
    /// callee frame. A false result guarantees that the ordinary call path can restart the
    /// callee from PC 0 without undoing callee state.
    /// </summary>
    public static bool TryExecuteDirectCompiledCall(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame caller,
        LuaCodegenCallSiteCache cache,
        int functionRegister,
        int expectedFunctionId,
        int argumentCount,
        int expectedResults)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(cache);
        return context.ExecutionEngine?.TryExecuteDirectCall(
            context,
            thread,
            caller,
            cache,
            functionRegister,
            expectedFunctionId,
            argumentCount,
            expectedResults) == true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanExecuteBoundDirectCall(LuaExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.State.Heap.PendingFinalizerCount == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CanExecuteKnownClosureValue(
        LuaValue function,
        LuaCodegenCallSiteCache cache,
        int expectedFunctionId)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var closure = function.TryGetClosure();
        return closure is not null && closure.Function.Id == expectedFunctionId &&
            cache.TryMatchOrAdd(closure);
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
