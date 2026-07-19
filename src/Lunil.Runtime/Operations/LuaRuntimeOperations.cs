using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Operations;

public static class LuaRuntimeOperations
{
    private const int MaximumMetamethodChainLength = 2_000;

    public static LuaOperationResolution GetIndex(
        LuaState state,
        LuaValue target,
        LuaValue key)
    {
        for (var iteration = 0; iteration < MaximumMetamethodChainLength; iteration++)
        {
            if (target.Kind == LuaValueKind.Table)
            {
                var table = target.AsTable();
                var value = table.Get(key);
                if (!value.IsNil)
                {
                    return LuaOperationResolution.Immediate(value);
                }

                if (table.Metatable is null)
                {
                    return LuaOperationResolution.Immediate(LuaValue.Nil);
                }
            }

            var metamethod = GetMetamethod(state, target, LuaMetamethod.Index);
            if (metamethod.IsNil)
            {
                if (target.Kind == LuaValueKind.Table)
                {
                    return LuaOperationResolution.Immediate(LuaValue.Nil);
                }

                throw new LuaRuntimeException(
                    $"attempt to index a {LuaValueOperations.TypeName(target)} value");
            }

            if (metamethod.Kind != LuaValueKind.Function)
            {
                target = metamethod;
                continue;
            }

            return LuaOperationResolution.Call(metamethod, target, key);
        }

        throw new LuaRuntimeException("'__index' chain is too long; possible loop.");
    }

    public static LuaOperationResolution SetIndex(
        LuaState state,
        LuaValue target,
        LuaValue key,
        LuaValue value)
    {
        for (var iteration = 0; iteration < MaximumMetamethodChainLength; iteration++)
        {
            if (target.Kind == LuaValueKind.Table)
            {
                var table = target.AsTable();
                if (table.TryGetExistingEntry(key, out _, out var entry))
                {
                    table.SetExistingEntry(entry, key, value);
                    return LuaOperationResolution.Immediate(LuaValue.Nil);
                }

                if (table.Metatable is null)
                {
                    table.Set(key, value);
                    return LuaOperationResolution.Immediate(LuaValue.Nil);
                }
            }

            var metamethod = GetMetamethod(state, target, LuaMetamethod.NewIndex);
            if (metamethod.IsNil)
            {
                if (target.Kind == LuaValueKind.Table)
                {
                    target.AsTable().Set(key, value);
                    return LuaOperationResolution.Immediate(LuaValue.Nil);
                }

                throw new LuaRuntimeException(
                    $"attempt to index a {LuaValueOperations.TypeName(target)} value");
            }

            if (metamethod.Kind != LuaValueKind.Function)
            {
                target = metamethod;
                continue;
            }

            return LuaOperationResolution.Call(metamethod, target, key, value);
        }

        throw new LuaRuntimeException("'__newindex' chain is too long; possible loop.");
    }

    public static LuaOperationResolution Unary(
        LuaState state,
        LuaIrUnaryOperator operation,
        LuaValue operand)
    {
        if (operation == LuaIrUnaryOperator.Negate &&
            LuaValueOperations.TryToNumber(operand, out var numericOperand))
        {
            return LuaOperationResolution.Immediate(
                LuaValueOperations.Unary(
                    operation,
                    NormalizeArithmeticOperand(state, operand, numericOperand)));
        }

        if (operation == LuaIrUnaryOperator.BitwiseNot &&
            state.LanguageVersion == LuaLanguageVersion.Lua53 &&
            LuaValueOperations.TryToNumber(operand, out var numericBitwiseOperand))
        {
            if (!numericBitwiseOperand.TryGetInteger(out var integerOperand))
            {
                throw new LuaRuntimeException("number has no integer representation");
            }

            return LuaOperationResolution.Immediate(
                LuaValueOperations.Unary(operation, LuaValue.FromInteger(integerOperand)));
        }

        if (operation == LuaIrUnaryOperator.BitwiseNot && IsNumber(operand) &&
            !operand.TryGetInteger(out _))
        {
            throw new LuaRuntimeException("number has no integer representation");
        }

        if (CanExecutePrimitive(operation, operand))
        {
            return LuaOperationResolution.Immediate(LuaValueOperations.Unary(operation, operand));
        }

        var metamethod = GetMetamethod(state, operand, operation switch
        {
            LuaIrUnaryOperator.Negate => LuaMetamethod.UnaryMinus,
            LuaIrUnaryOperator.BitwiseNot => LuaMetamethod.BitwiseNot,
            LuaIrUnaryOperator.Length => LuaMetamethod.Length,
            _ => throw new InvalidOperationException($"No metamethod exists for {operation}."),
        });
        if (metamethod.IsNil)
        {
            var type = LuaValueOperations.TypeName(operand);
            var message = operation switch
            {
                LuaIrUnaryOperator.Negate => $"attempt to perform arithmetic on a {type} value",
                LuaIrUnaryOperator.BitwiseNot =>
                    $"attempt to perform bitwise operation on a {type} value",
                LuaIrUnaryOperator.Length => $"attempt to get length of a {type} value",
                _ => $"cannot apply {operation} to {type}",
            };
            throw new LuaRuntimeException(message);
        }

        // Lua 5.4 passes the operand twice to unary metamethods.  The second
        // argument is intentionally redundant, but is observable by vararg
        // metamethods and therefore part of the language contract.
        return LuaOperationResolution.Call(metamethod, operand, operand);
    }

    public static LuaOperationResolution Binary(
        LuaState state,
        LuaIrBinaryOperator operation,
        LuaValue left,
        LuaValue right)
    {
        if (operation is LuaIrBinaryOperator.Equal or LuaIrBinaryOperator.NotEqual)
        {
            return Equal(state, left, right, operation == LuaIrBinaryOperator.NotEqual);
        }

        if (operation == LuaIrBinaryOperator.GreaterThan)
        {
            return Binary(state, LuaIrBinaryOperator.LessThan, right, left);
        }

        if (operation == LuaIrBinaryOperator.GreaterThanOrEqual)
        {
            return Binary(state, LuaIrBinaryOperator.LessThanOrEqual, right, left);
        }

        if (IsArithmetic(operation) &&
            LuaValueOperations.TryToNumber(left, out var numericLeft) &&
            LuaValueOperations.TryToNumber(right, out var numericRight))
        {
            return LuaOperationResolution.Immediate(
                LuaValueOperations.Binary(
                    state,
                    operation,
                    NormalizeArithmeticOperand(state, left, numericLeft),
                    NormalizeArithmeticOperand(state, right, numericRight)));
        }

        if (IsBitwise(operation) && state.LanguageVersion == LuaLanguageVersion.Lua53)
        {
            long leftValue = 0;
            long rightValue = 0;
            var leftInteger = LuaValueOperations.TryToNumber(left, out var numericLeftBitwise) &&
                numericLeftBitwise.TryGetInteger(out leftValue);
            var rightInteger = LuaValueOperations.TryToNumber(right, out var numericRightBitwise) &&
                numericRightBitwise.TryGetInteger(out rightValue);
            if (leftInteger && rightInteger)
            {
                return LuaOperationResolution.Immediate(
                    LuaValueOperations.Binary(
                        state,
                        operation,
                        LuaValue.FromInteger(leftValue),
                        LuaValue.FromInteger(rightValue)));
            }

            if (LuaValueOperations.TryToNumber(left, out _) ||
                LuaValueOperations.TryToNumber(right, out _))
            {
                throw new LuaRuntimeException("number has no integer representation");
            }
        }

        if (IsBitwise(operation) && state.LanguageVersion != LuaLanguageVersion.Lua53 &&
            IsNumber(left) && IsNumber(right))
        {
            if (!left.TryGetInteger(out _) || !right.TryGetInteger(out _))
            {
                throw new LuaRuntimeException("number has no integer representation");
            }

            return LuaOperationResolution.Immediate(
                LuaValueOperations.Binary(state, operation, left, right));
        }

        if (CanExecutePrimitive(operation, left, right))
        {
            return LuaOperationResolution.Immediate(
                LuaValueOperations.Binary(state, operation, left, right));
        }

        if (operation == LuaIrBinaryOperator.LessThanOrEqual)
        {
            var lessOrEqual = GetBinaryMetamethod(state, left, right, LuaMetamethod.LessThanOrEqual);
            if (!lessOrEqual.IsNil)
            {
                return LuaOperationResolution.Call(lessOrEqual, left, right);
            }

            var lessThan = GetBinaryMetamethod(state, right, left, LuaMetamethod.LessThan);
            if (!lessThan.IsNil)
            {
                return LuaOperationResolution.Call(
                    lessThan,
                    right,
                    left,
                    LuaResultTransform.LogicalNot);
            }
        }

        var metamethodName = GetBinaryMetamethod(operation);
        var metamethod = GetBinaryMetamethod(state, left, right, metamethodName);
        if (metamethod.IsNil)
        {
            throw new LuaRuntimeException(BinaryTypeError(operation, left, right));
        }

        return LuaOperationResolution.Call(metamethod, left, right);
    }

    public static LuaOperationResolution ResolveCall(
        LuaState state,
        LuaValue callable,
        ReadOnlySpan<LuaValue> arguments)
    {
        var resolvedArguments = arguments;
        for (var iteration = 0; iteration < MaximumMetamethodChainLength; iteration++)
        {
            if (callable.Kind == LuaValueKind.Function)
            {
                return LuaOperationResolution.Call(callable, resolvedArguments);
            }

            var metamethod = GetMetamethod(state, callable, LuaMetamethod.Call);
            if (metamethod.IsNil)
            {
                throw new LuaRuntimeException(
                    $"attempt to call a {LuaValueOperations.TypeName(callable)} value");
            }

            var expanded = new LuaValue[resolvedArguments.Length + 1];
            expanded[0] = callable;
            resolvedArguments.CopyTo(expanded.AsSpan(1));
            resolvedArguments = expanded;
            callable = metamethod;
        }

        throw new LuaRuntimeException("'__call' chain is too long; possible loop.");
    }

    internal static LuaValue GetMetamethod(
        LuaState state,
        LuaValue value,
        LuaMetamethod metamethod)
    {
        var metatable = value.Kind switch
        {
            LuaValueKind.Table => value.AsTable().Metatable,
            LuaValueKind.Userdata => value.AsUserdata().Metatable,
            _ => state.GetTypeMetatable(value.Kind),
        };
        return metatable?.GetMetamethodField(metamethod) ?? LuaValue.Nil;
    }

    private static LuaOperationResolution Equal(
        LuaState state,
        LuaValue left,
        LuaValue right,
        bool negate)
    {
        if (left == right || left.Kind != right.Kind ||
            left.Kind is not (LuaValueKind.Table or LuaValueKind.Userdata))
        {
            var equal = left == right;
            return LuaOperationResolution.Immediate(LuaValue.FromBoolean(negate ? !equal : equal));
        }

        var metamethod = GetBinaryMetamethod(state, left, right, LuaMetamethod.Equal);
        if (metamethod.IsNil)
        {
            return LuaOperationResolution.Immediate(LuaValue.FromBoolean(negate));
        }

        return LuaOperationResolution.Call(
            metamethod,
            left,
            right,
            negate ? LuaResultTransform.LogicalNot : LuaResultTransform.None);
    }

    private static LuaValue GetBinaryMetamethod(
        LuaState state,
        LuaValue left,
        LuaValue right,
        LuaMetamethod metamethod)
    {
        var value = GetMetamethod(state, left, metamethod);
        return value.IsNil ? GetMetamethod(state, right, metamethod) : value;
    }

    private static string BinaryTypeError(
        LuaIrBinaryOperator operation,
        LuaValue left,
        LuaValue right)
    {
        if (operation is LuaIrBinaryOperator.LessThan or LuaIrBinaryOperator.LessThanOrEqual)
        {
            var leftType = LuaValueOperations.TypeName(left);
            var rightType = LuaValueOperations.TypeName(right);
            return string.Equals(leftType, rightType, StringComparison.Ordinal)
                ? $"attempt to compare two {leftType} values"
                : $"attempt to compare {leftType} with {rightType}";
        }

        LuaValue offender;
        string action;
        if (IsArithmetic(operation))
        {
            offender = LuaValueOperations.TryToNumber(left, out _) ? right : left;
            action = "perform arithmetic on";
        }
        else if (IsBitwise(operation))
        {
            offender = IsNumber(left) ? right : left;
            action = "perform bitwise operation on";
        }
        else if (operation == LuaIrBinaryOperator.Concatenate)
        {
            offender = IsConcatenable(left) ? right : left;
            action = "concatenate";
        }
        else
        {
            return $"cannot apply {operation} to {LuaValueOperations.TypeName(left)} and " +
                LuaValueOperations.TypeName(right);
        }

        return $"attempt to {action} a {LuaValueOperations.TypeName(offender)} value";
    }

    private static bool IsConcatenable(LuaValue value) =>
        value.Kind is LuaValueKind.String or LuaValueKind.Integer or LuaValueKind.Float;

    private static LuaMetamethod GetBinaryMetamethod(LuaIrBinaryOperator operation) => operation switch
    {
        LuaIrBinaryOperator.Add => LuaMetamethod.Add,
        LuaIrBinaryOperator.Subtract => LuaMetamethod.Subtract,
        LuaIrBinaryOperator.Multiply => LuaMetamethod.Multiply,
        LuaIrBinaryOperator.Divide => LuaMetamethod.Divide,
        LuaIrBinaryOperator.FloorDivide => LuaMetamethod.FloorDivide,
        LuaIrBinaryOperator.Modulo => LuaMetamethod.Modulo,
        LuaIrBinaryOperator.Power => LuaMetamethod.Power,
        LuaIrBinaryOperator.Concatenate => LuaMetamethod.Concatenate,
        LuaIrBinaryOperator.LessThan => LuaMetamethod.LessThan,
        LuaIrBinaryOperator.LessThanOrEqual => LuaMetamethod.LessThanOrEqual,
        LuaIrBinaryOperator.GreaterThan => LuaMetamethod.LessThan,
        LuaIrBinaryOperator.GreaterThanOrEqual => LuaMetamethod.LessThanOrEqual,
        LuaIrBinaryOperator.BitwiseAnd => LuaMetamethod.BitwiseAnd,
        LuaIrBinaryOperator.BitwiseOr => LuaMetamethod.BitwiseOr,
        LuaIrBinaryOperator.BitwiseXor => LuaMetamethod.BitwiseXor,
        LuaIrBinaryOperator.ShiftLeft => LuaMetamethod.ShiftLeft,
        LuaIrBinaryOperator.ShiftRight => LuaMetamethod.ShiftRight,
        _ => throw new InvalidOperationException($"No binary metamethod exists for {operation}."),
    };

    private static bool CanExecutePrimitive(LuaIrUnaryOperator operation, LuaValue operand) =>
        operation switch
        {
            LuaIrUnaryOperator.LogicalNot => true,
            LuaIrUnaryOperator.BitwiseNot => operand.TryGetInteger(out _),
            LuaIrUnaryOperator.Length => operand.Kind == LuaValueKind.String ||
                operand.Kind == LuaValueKind.Table &&
                (operand.AsTable().Metatable is not { } metatable ||
                    metatable.GetMetamethodField(LuaMetamethod.Length).IsNil),
            _ => false,
        };

    private static bool CanExecutePrimitive(
        LuaIrBinaryOperator operation,
        LuaValue left,
        LuaValue right) => operation switch
        {
            LuaIrBinaryOperator.BitwiseAnd or LuaIrBinaryOperator.BitwiseOr or
            LuaIrBinaryOperator.BitwiseXor or LuaIrBinaryOperator.ShiftLeft or LuaIrBinaryOperator.ShiftRight =>
                left.TryGetInteger(out _) && right.TryGetInteger(out _),
            LuaIrBinaryOperator.Concatenate => IsStringOrNumber(left) && IsStringOrNumber(right),
            LuaIrBinaryOperator.LessThan or LuaIrBinaryOperator.LessThanOrEqual or
            LuaIrBinaryOperator.GreaterThan or LuaIrBinaryOperator.GreaterThanOrEqual =>
                IsNumber(left) && IsNumber(right) ||
                left.Kind == LuaValueKind.String && right.Kind == LuaValueKind.String,
            _ => false,
        };

    private static LuaValue NormalizeArithmeticOperand(
        LuaState state,
        LuaValue original,
        LuaValue numeric) =>
        state.LanguageVersion == LuaLanguageVersion.Lua53 &&
        original.Kind == LuaValueKind.String &&
        numeric.Kind == LuaValueKind.Integer
            ? LuaValue.FromFloat(numeric.AsInteger())
            : numeric;

    private static bool IsNumber(LuaValue value) =>
        value.Kind is LuaValueKind.Integer or LuaValueKind.Float;

    private static bool IsStringOrNumber(LuaValue value) =>
        value.Kind == LuaValueKind.String || IsNumber(value);

    private static bool IsArithmetic(LuaIrBinaryOperator operation) => operation is
        LuaIrBinaryOperator.Add or LuaIrBinaryOperator.Subtract or LuaIrBinaryOperator.Multiply or
        LuaIrBinaryOperator.Divide or LuaIrBinaryOperator.FloorDivide or LuaIrBinaryOperator.Modulo or
        LuaIrBinaryOperator.Power;

    private static bool IsBitwise(LuaIrBinaryOperator operation) => operation is
        LuaIrBinaryOperator.BitwiseAnd or LuaIrBinaryOperator.BitwiseOr or
        LuaIrBinaryOperator.BitwiseXor or LuaIrBinaryOperator.ShiftLeft or
        LuaIrBinaryOperator.ShiftRight;
}
