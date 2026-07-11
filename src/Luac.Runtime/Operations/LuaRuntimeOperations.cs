using Luac.IR.Canonical;
using Luac.Runtime.Values;

namespace Luac.Runtime.Operations;

internal static class LuaRuntimeOperations
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
                var value = target.AsTable().Get(key);
                if (!value.IsNil)
                {
                    return LuaOperationResolution.Immediate(value);
                }
            }

            var metamethod = GetMetamethod(state, target, LuaMetamethod.Index);
            if (metamethod.IsNil)
            {
                if (target.Kind == LuaValueKind.Table)
                {
                    return LuaOperationResolution.Immediate(LuaValue.Nil);
                }

                throw new LuaRuntimeException($"Attempt to index a {target.Kind} value.");
            }

            if (metamethod.Kind == LuaValueKind.Table)
            {
                target = metamethod;
                continue;
            }

            return LuaOperationResolution.Call(metamethod, [target, key]);
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
                if (!table.Get(key).IsNil)
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

                throw new LuaRuntimeException($"Attempt to index a {target.Kind} value.");
            }

            if (metamethod.Kind == LuaValueKind.Table)
            {
                target = metamethod;
                continue;
            }

            return LuaOperationResolution.Call(metamethod, [target, key, value]);
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
                LuaValueOperations.Unary(operation, numericOperand));
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
            throw new LuaRuntimeException($"Cannot apply {operation} to {operand.Kind}.");
        }

        return LuaOperationResolution.Call(metamethod, [operand]);
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
                LuaValueOperations.Binary(state, operation, numericLeft, numericRight));
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
                return LuaOperationResolution.Call(lessOrEqual, [left, right]);
            }

            var lessThan = GetBinaryMetamethod(state, right, left, LuaMetamethod.LessThan);
            if (!lessThan.IsNil)
            {
                return LuaOperationResolution.Call(
                    lessThan,
                    [right, left],
                    LuaResultTransform.LogicalNot);
            }
        }

        var metamethodName = GetBinaryMetamethod(operation);
        var metamethod = GetBinaryMetamethod(state, left, right, metamethodName);
        if (metamethod.IsNil)
        {
            throw new LuaRuntimeException(
                $"Cannot apply {operation} to {left.Kind} and {right.Kind}.");
        }

        return LuaOperationResolution.Call(metamethod, [left, right]);
    }

    public static LuaOperationResolution ResolveCall(
        LuaState state,
        LuaValue callable,
        ReadOnlySpan<LuaValue> arguments)
    {
        var resolvedArguments = arguments.ToArray();
        for (var iteration = 0; iteration < MaximumMetamethodChainLength; iteration++)
        {
            if (callable.Kind == LuaValueKind.Function)
            {
                return LuaOperationResolution.Call(callable, resolvedArguments);
            }

            var metamethod = GetMetamethod(state, callable, LuaMetamethod.Call);
            if (metamethod.IsNil)
            {
                throw new LuaRuntimeException($"Attempt to call a {callable.Kind} value.");
            }

            var expanded = new LuaValue[resolvedArguments.Length + 1];
            expanded[0] = callable;
            resolvedArguments.CopyTo(expanded, 1);
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
        var metatable = value.Kind == LuaValueKind.Table
            ? value.AsTable().Metatable
            : state.GetTypeMetatable(value.Kind);
        return metatable?.GetStringField(LuaMetamethodFacts.GetName(metamethod)) ?? LuaValue.Nil;
    }

    private static LuaOperationResolution Equal(
        LuaState state,
        LuaValue left,
        LuaValue right,
        bool negate)
    {
        if (left == right || left.Kind != LuaValueKind.Table || right.Kind != LuaValueKind.Table)
        {
            var equal = left == right;
            return LuaOperationResolution.Immediate(LuaValue.FromBoolean(negate ? !equal : equal));
        }

        var leftMetamethod = GetMetamethod(state, left, LuaMetamethod.Equal);
        var rightMetamethod = GetMetamethod(state, right, LuaMetamethod.Equal);
        if (leftMetamethod.IsNil || leftMetamethod != rightMetamethod)
        {
            return LuaOperationResolution.Immediate(LuaValue.FromBoolean(negate));
        }

        return LuaOperationResolution.Call(
            leftMetamethod,
            [left, right],
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
                    metatable.GetStringField("__len"u8).IsNil),
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

    private static bool IsNumber(LuaValue value) =>
        value.Kind is LuaValueKind.Integer or LuaValueKind.Float;

    private static bool IsStringOrNumber(LuaValue value) =>
        value.Kind == LuaValueKind.String || IsNumber(value);

    private static bool IsArithmetic(LuaIrBinaryOperator operation) => operation is
        LuaIrBinaryOperator.Add or LuaIrBinaryOperator.Subtract or LuaIrBinaryOperator.Multiply or
        LuaIrBinaryOperator.Divide or LuaIrBinaryOperator.FloorDivide or LuaIrBinaryOperator.Modulo or
        LuaIrBinaryOperator.Power;
}
