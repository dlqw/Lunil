using Lunil.IR.Canonical;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Operations;

/// <summary>
/// Semantic operations for one Lua state: indexing, assignment, arithmetic, comparison,
/// and call resolution including metamethod dispatch and per-version coercion rules. The
/// service is owned by its <see cref="LuaState"/> and captures the state's immutable
/// version profile at construction.
/// </summary>
public sealed class LuaRuntimeOperations
{
    private const int MaximumMetamethodChainLength = 2_000;

    private readonly LuaState _state;
    private readonly bool _coercesNumericStringsForBitwiseOperations;
    private readonly bool _orderingRequiresSameType;
    private readonly bool _allowsLessThanOrEqualFallback;
    private readonly bool _arithmeticStringCoercionProducesFloat;

    internal LuaRuntimeOperations(LuaState state)
    {
        _state = state;
        _coercesNumericStringsForBitwiseOperations = state.CoercesNumericStringsForBitwiseOperations;
        _orderingRequiresSameType = state.OrderingRequiresSameType;
        _allowsLessThanOrEqualFallback = state.AllowsLessThanOrEqualFallback;
        _arithmeticStringCoercionProducesFloat = state.ArithmeticStringCoercionProducesFloat;
    }

    public LuaOperationResolution GetIndex(LuaValue target, LuaValue key)
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

            var metamethod = GetMetamethod(target, LuaMetamethod.Index);
            if (metamethod.IsNil)
            {
                if (target.Kind == LuaValueKind.Table)
                {
                    return LuaOperationResolution.Immediate(LuaValue.Nil);
                }

                throw new LuaRuntimeException(
                    $"attempt to index a {LuaValueOperations.TypeName(target)} value")
                { Kind = LuaRuntimeErrorKind.AttemptTo };
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

    public LuaOperationResolution SetIndex(LuaValue target, LuaValue key, LuaValue value)
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

            var metamethod = GetMetamethod(target, LuaMetamethod.NewIndex);
            if (metamethod.IsNil)
            {
                if (target.Kind == LuaValueKind.Table)
                {
                    target.AsTable().Set(key, value);
                    return LuaOperationResolution.Immediate(LuaValue.Nil);
                }

                throw new LuaRuntimeException(
                    $"attempt to index a {LuaValueOperations.TypeName(target)} value")
                { Kind = LuaRuntimeErrorKind.AttemptTo };
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

    public LuaOperationResolution Unary(LuaIrUnaryOperator operation, LuaValue operand)
    {
        if (operation == LuaIrUnaryOperator.Negate &&
            LuaValueOperations.TryToNumber(operand, out var numericOperand))
        {
            return LuaOperationResolution.Immediate(
                LuaValueOperations.Unary(
                    operation,
                    NormalizeArithmeticOperand(operand, numericOperand)));
        }

        if (operation == LuaIrUnaryOperator.BitwiseNot &&
            _coercesNumericStringsForBitwiseOperations &&
            LuaValueOperations.TryToNumber(operand, out var numericBitwiseOperand))
        {
            if (!numericBitwiseOperand.TryGetInteger(out var integerOperand))
            {
                throw new LuaRuntimeException("number has no integer representation")
                { Kind = LuaRuntimeErrorKind.IntegerConversion };
            }

            return LuaOperationResolution.Immediate(
                LuaValueOperations.Unary(operation, LuaValue.FromInteger(integerOperand)));
        }

        if (operation == LuaIrUnaryOperator.BitwiseNot && IsNumber(operand) &&
            !operand.TryGetInteger(out _))
        {
            throw new LuaRuntimeException("number has no integer representation")
            { Kind = LuaRuntimeErrorKind.IntegerConversion };
        }

        if (CanExecutePrimitive(operation, operand))
        {
            return LuaOperationResolution.Immediate(LuaValueOperations.Unary(operation, operand));
        }

        var metamethod = GetMetamethod(operand, operation switch
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

    public LuaOperationResolution Binary(LuaIrBinaryOperator operation, LuaValue left, LuaValue right)
    {
        if (operation is LuaIrBinaryOperator.Equal or LuaIrBinaryOperator.NotEqual)
        {
            return Equal(left, right, operation == LuaIrBinaryOperator.NotEqual);
        }

        if (operation == LuaIrBinaryOperator.GreaterThan)
        {
            return Binary(LuaIrBinaryOperator.LessThan, right, left);
        }

        if (operation == LuaIrBinaryOperator.GreaterThanOrEqual)
        {
            return Binary(LuaIrBinaryOperator.LessThanOrEqual, right, left);
        }

        if (IsArithmetic(operation) &&
            LuaValueOperations.TryToNumber(left, out var numericLeft) &&
            LuaValueOperations.TryToNumber(right, out var numericRight))
        {
            return LuaOperationResolution.Immediate(
                LuaValueOperations.Binary(
                    _state,
                    operation,
                    NormalizeArithmeticOperand(left, numericLeft),
                    NormalizeArithmeticOperand(right, numericRight)));
        }

        if (IsBitwise(operation) && _coercesNumericStringsForBitwiseOperations)
        {
            var leftNumber = LuaValueOperations.TryToNumber(left, out var numericLeftBitwise);
            var rightNumber = LuaValueOperations.TryToNumber(right, out var numericRightBitwise);
            if (leftNumber && rightNumber)
            {
                if (!numericLeftBitwise.TryGetInteger(out var leftValue) ||
                    !numericRightBitwise.TryGetInteger(out var rightValue))
                {
                    throw new LuaRuntimeException("number has no integer representation")
                    { Kind = LuaRuntimeErrorKind.IntegerConversion };
                }

                return LuaOperationResolution.Immediate(
                    LuaValueOperations.Binary(
                        _state,
                        operation,
                        LuaValue.FromInteger(leftValue),
                        LuaValue.FromInteger(rightValue)));
            }
        }

        if (IsBitwise(operation) && !_coercesNumericStringsForBitwiseOperations &&
            IsNumber(left) && IsNumber(right))
        {
            if (!left.TryGetInteger(out _) || !right.TryGetInteger(out _))
            {
                throw new LuaRuntimeException("number has no integer representation")
                { Kind = LuaRuntimeErrorKind.IntegerConversion };
            }

            return LuaOperationResolution.Immediate(
                LuaValueOperations.Binary(_state, operation, left, right));
        }

        if (CanExecutePrimitive(operation, left, right))
        {
            return LuaOperationResolution.Immediate(
                LuaValueOperations.Binary(_state, operation, left, right));
        }

        // PUC Lua 5.1 rejects ordering operands of different types outright; 5.2
        // and later instead consult the ordering metamethod of either operand.
        if (operation is LuaIrBinaryOperator.LessThan or LuaIrBinaryOperator.LessThanOrEqual &&
            _orderingRequiresSameType &&
            left.Kind != right.Kind)
        {
            throw new LuaRuntimeException(BinaryTypeError(operation, left, right));
        }

        if (operation == LuaIrBinaryOperator.LessThanOrEqual)
        {
            var lessOrEqual = GetBinaryMetamethod(left, right, LuaMetamethod.LessThanOrEqual);
            if (!lessOrEqual.IsNil)
            {
                return LuaOperationResolution.Call(lessOrEqual, left, right);
            }

            if (_allowsLessThanOrEqualFallback)
            {
                var lessThan = GetBinaryMetamethod(right, left, LuaMetamethod.LessThan);
                if (!lessThan.IsNil)
                {
                    return LuaOperationResolution.Call(
                        lessThan,
                        right,
                        left,
                        LuaResultTransform.LogicalNot);
                }
            }
        }

        var metamethodName = GetBinaryMetamethod(operation);
        var metamethod = GetBinaryMetamethod(left, right, metamethodName);
        if (metamethod.IsNil)
        {
            throw new LuaRuntimeException(BinaryTypeError(operation, left, right));
        }

        return LuaOperationResolution.Call(metamethod, left, right);
    }

    public LuaOperationResolution ResolveCall(LuaValue callable, ReadOnlySpan<LuaValue> arguments)
    {
        var resolvedArguments = arguments;
        for (var iteration = 0; iteration < MaximumMetamethodChainLength; iteration++)
        {
            if (callable.Kind == LuaValueKind.Function)
            {
                return LuaOperationResolution.Call(callable, resolvedArguments);
            }

            var metamethod = GetMetamethod(callable, LuaMetamethod.Call);
            if (metamethod.IsNil)
            {
                throw new LuaRuntimeException(
                    $"attempt to call a {LuaValueOperations.TypeName(callable)} value")
                { Kind = LuaRuntimeErrorKind.AttemptTo };
            }

            var expanded = new LuaValue[resolvedArguments.Length + 1];
            expanded[0] = callable;
            resolvedArguments.CopyTo(expanded.AsSpan(1));
            resolvedArguments = expanded;
            callable = metamethod;
        }

        throw new LuaRuntimeException("'__call' chain is too long; possible loop.");
    }

    /// <summary>
    /// Resolves operations that cannot reach a metamethod or coercion rule, sharing the
    /// specialized helpers with the compiled tiers. The interpreter stays inside its
    /// compact loop for these results instead of re-entering scheduler validation.
    /// </summary>
    internal static bool TryResolvePrimitiveBinary(
        LuaIrBinaryOperator operation,
        LuaValue left,
        LuaValue right,
        out LuaValue result)
    {
        if (left.IsInteger)
        {
            if (right.IsInteger)
            {
                if (operation != LuaIrBinaryOperator.Concatenate)
                {
                    result = LuaValueOperations.BinaryIntegerSpecialized(operation, left, right);
                    return true;
                }
            }
            else if (right.IsFloat && IsNumberSpecializedOperation(operation))
            {
                result = LuaValueOperations.BinaryMixedNumericSpecialized(operation, left, right);
                return true;
            }
        }
        else if (left.IsFloat)
        {
            if (right.IsFloat)
            {
                if (IsNumberSpecializedOperation(operation))
                {
                    result = LuaValueOperations.BinaryFloatSpecialized(operation, left, right);
                    return true;
                }
            }
            else if (right.IsInteger && IsNumberSpecializedOperation(operation))
            {
                result = LuaValueOperations.BinaryMixedNumericSpecialized(operation, left, right);
                return true;
            }
        }

        result = LuaValue.Nil;
        return false;
    }

    /// <summary>See <see cref="TryResolvePrimitiveBinary"/>; covers numeric negation,
    /// integer bitwise-not, logical-not, string length, and metatable-free table length.</summary>
    internal static bool TryResolvePrimitiveUnary(
        LuaIrUnaryOperator operation,
        LuaValue operand,
        out LuaValue result)
    {
        if (operand.IsInteger)
        {
            if (operation is LuaIrUnaryOperator.Negate or LuaIrUnaryOperator.BitwiseNot)
            {
                result = LuaValueOperations.UnaryIntegerSpecialized(operation, operand);
                return true;
            }
        }
        else if (operand.IsFloat && operation == LuaIrUnaryOperator.Negate)
        {
            result = LuaValueOperations.UnaryFloatSpecialized(operation, operand);
            return true;
        }

        if (operation == LuaIrUnaryOperator.LogicalNot)
        {
            result = LuaValue.FromBoolean(!operand.IsTruthy);
            return true;
        }

        if (operation == LuaIrUnaryOperator.Length)
        {
            if (operand.TryGetString() is { } text)
            {
                result = LuaValue.FromInteger(text.Length);
                return true;
            }

            if (operand.TryGetTable() is { } table &&
                (table.Metatable is null ||
                    table.Metatable.GetMetamethodField(LuaMetamethod.Length).IsNil))
            {
                result = LuaValue.FromInteger(table.ArrayLength);
                return true;
            }
        }

        result = LuaValue.Nil;
        return false;
    }

    internal LuaValue GetMetamethod(LuaValue value, LuaMetamethod metamethod)
    {
        var metatable = value.Kind switch
        {
            LuaValueKind.Table => value.AsTable().Metatable,
            LuaValueKind.Userdata => value.AsUserdata().Metatable,
            _ => _state.GetTypeMetatable(value.Kind),
        };
        return metatable?.GetMetamethodField(metamethod) ?? LuaValue.Nil;
    }

    private LuaOperationResolution Equal(LuaValue left, LuaValue right, bool negate)
    {
        if (left == right || left.Kind != right.Kind ||
            left.Kind is not (LuaValueKind.Table or LuaValueKind.Userdata))
        {
            var equal = left == right;
            return LuaOperationResolution.Immediate(LuaValue.FromBoolean(negate ? !equal : equal));
        }

        var metamethod = GetBinaryMetamethod(left, right, LuaMetamethod.Equal);
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

    private LuaValue GetBinaryMetamethod(LuaValue left, LuaValue right, LuaMetamethod metamethod)
    {
        var value = GetMetamethod(left, metamethod);
        return value.IsNil ? GetMetamethod(right, metamethod) : value;
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

    internal LuaValue NormalizeArithmeticOperand(LuaValue original, LuaValue numeric) =>
        _arithmeticStringCoercionProducesFloat &&
        original.Kind == LuaValueKind.String &&
        numeric.Kind == LuaValueKind.Integer
            ? LuaValue.FromFloat(numeric.AsInteger())
            : numeric;

    private static bool IsNumberSpecializedOperation(LuaIrBinaryOperator operation) => operation is
        LuaIrBinaryOperator.Add or LuaIrBinaryOperator.Subtract or LuaIrBinaryOperator.Multiply or
        LuaIrBinaryOperator.Divide or LuaIrBinaryOperator.FloorDivide or LuaIrBinaryOperator.Modulo or
        LuaIrBinaryOperator.Power or LuaIrBinaryOperator.Equal or LuaIrBinaryOperator.NotEqual or
        LuaIrBinaryOperator.LessThan or LuaIrBinaryOperator.LessThanOrEqual or
        LuaIrBinaryOperator.GreaterThan or LuaIrBinaryOperator.GreaterThanOrEqual;

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
