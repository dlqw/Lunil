using System.Security.Cryptography;
using System.Numerics;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Operations;
using Lunil.Runtime.Values;

namespace Lunil.StandardLibrary;

internal static class LuaMathLibrary
{
    private const double Pi = 3.141592653589793238462643383279502884;

    public static LuaTable Install(LuaState state)
    {
        var module = state.CreateTable(hashCapacity: 40);
        SetUnary(state, module, "acos", Math.Acos);
        SetUnary(state, module, "asin", Math.Asin);
        SetUnary(state, module, "cos", Math.Cos);
        SetUnary(state, module, "exp", Math.Exp);
        SetUnary(state, module, "sin", Math.Sin);
        SetUnary(state, module, "sqrt", Math.Sqrt);
        SetUnary(state, module, "tan", Math.Tan);
        LuaLibraryHelpers.SetFunction(state, module, "abs", Abs);
        LuaLibraryHelpers.SetFunction(state, module, "atan", Atan);
        LuaLibraryHelpers.SetFunction(state, module, "ceil", Ceil);
        LuaLibraryHelpers.SetFunction(state, module, "deg", Deg);
        LuaLibraryHelpers.SetFunction(state, module, "floor", Floor);
        LuaLibraryHelpers.SetFunction(state, module, "fmod", Fmod);
        LuaLibraryHelpers.SetFunction(state, module, "log", Log);
        LuaLibraryHelpers.SetFunction(state, module, "log10", Log10);
        LuaLibraryHelpers.SetFunction(state, module, "max", Max);
        LuaLibraryHelpers.SetFunction(state, module, "min", Min);
        LuaLibraryHelpers.SetFunction(state, module, "modf", Modf);
        LuaLibraryHelpers.SetFunction(state, module, "rad", Rad);
        LuaLibraryHelpers.SetFunction(state, module, "tointeger", ToInteger);
        LuaLibraryHelpers.SetFunction(state, module, "type", NumberType);
        LuaLibraryHelpers.SetFunction(state, module, "ult", UnsignedLessThan);
        LuaLibraryHelpers.SetFunction(state, module, "atan2", Atan2);
        LuaLibraryHelpers.SetFunction(state, module, "pow", Pow);
        LuaLibraryHelpers.SetFunction(state, module, "sinh", Sinh);
        LuaLibraryHelpers.SetFunction(state, module, "cosh", Cosh);
        LuaLibraryHelpers.SetFunction(state, module, "tanh", Tanh);
        LuaLibraryHelpers.SetFunction(state, module, "frexp", Frexp);
        LuaLibraryHelpers.SetFunction(state, module, "ldexp", Ldexp);

        var random = new LuaRandomState();
        LuaLibraryHelpers.SetFunction(state, module, "random", random.Random);
        LuaLibraryHelpers.SetFunction(state, module, "randomseed", random.RandomSeed);
        LuaLibraryHelpers.Set(state, module, "pi", LuaValue.FromFloat(Pi));
        LuaLibraryHelpers.Set(state, module, "huge", LuaValue.FromFloat(double.PositiveInfinity));
        LuaLibraryHelpers.Set(state, module, "maxinteger", LuaValue.FromInteger(long.MaxValue));
        LuaLibraryHelpers.Set(state, module, "mininteger", LuaValue.FromInteger(long.MinValue));
        state.SetGlobal("math", LuaValue.FromTable(module));
        return module;
    }

    private static void SetUnary(
        LuaState state,
        LuaTable module,
        string name,
        Func<double, double> operation) =>
        LuaLibraryHelpers.SetFunction(
            state,
            module,
            name,
            (_, arguments) =>
                [LuaValue.FromFloat(operation(LuaLibraryHelpers.CheckNumber(arguments, 0, name)))]);

    private static LuaValue[] Abs(LuaState _, ReadOnlySpan<LuaValue> arguments)
    {
        var value = LuaLibraryHelpers.Required(arguments, 0, "abs");
        if (value.Kind == LuaValueKind.Integer)
        {
            var integer = value.AsInteger();
            return [LuaValue.FromInteger(integer < 0 ? unchecked(0L - integer) : integer)];
        }

        return [LuaValue.FromFloat(Math.Abs(LuaLibraryHelpers.CheckNumber(arguments, 0, "abs")))];
    }

    private static LuaValue[] Atan(LuaState _, ReadOnlySpan<LuaValue> arguments) =>
        [LuaValue.FromFloat(Math.Atan2(
            LuaLibraryHelpers.CheckNumber(arguments, 0, "atan"),
            LuaLibraryHelpers.OptionalNumber(arguments, 1, 1, "atan")))];

    private static LuaValue[] Atan2(LuaState _, ReadOnlySpan<LuaValue> arguments) =>
        [LuaValue.FromFloat(Math.Atan2(
            LuaLibraryHelpers.CheckNumber(arguments, 0, "atan2"),
            LuaLibraryHelpers.CheckNumber(arguments, 1, "atan2")))];

    private static LuaValue[] Pow(LuaState _, ReadOnlySpan<LuaValue> arguments) =>
        [LuaValue.FromFloat(Math.Pow(
            LuaLibraryHelpers.CheckNumber(arguments, 0, "pow"),
            LuaLibraryHelpers.CheckNumber(arguments, 1, "pow")))];

    private static LuaValue[] Log10(LuaState _, ReadOnlySpan<LuaValue> arguments) =>
        [LuaValue.FromFloat(Math.Log10(LuaLibraryHelpers.CheckNumber(arguments, 0, "log10")))];

    private static LuaValue[] Sinh(LuaState _, ReadOnlySpan<LuaValue> arguments) =>
        [LuaValue.FromFloat(Math.Sinh(LuaLibraryHelpers.CheckNumber(arguments, 0, "sinh")))];

    private static LuaValue[] Cosh(LuaState _, ReadOnlySpan<LuaValue> arguments) =>
        [LuaValue.FromFloat(Math.Cosh(LuaLibraryHelpers.CheckNumber(arguments, 0, "cosh")))];

    private static LuaValue[] Tanh(LuaState _, ReadOnlySpan<LuaValue> arguments) =>
        [LuaValue.FromFloat(Math.Tanh(LuaLibraryHelpers.CheckNumber(arguments, 0, "tanh")))];

    private static LuaValue[] Frexp(LuaState _, ReadOnlySpan<LuaValue> arguments)
    {
        var value = LuaLibraryHelpers.CheckNumber(arguments, 0, "frexp");
        if (value == 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            return [LuaValue.FromFloat(value), LuaValue.FromInteger(0)];
        }

        var exponent = Math.ILogB(Math.Abs(value)) + 1;
        return
        [
            LuaValue.FromFloat(value / Math.Pow(2, exponent)),
            LuaValue.FromInteger(exponent),
        ];
    }

    private static LuaValue[] Ldexp(LuaState _, ReadOnlySpan<LuaValue> arguments) =>
        [LuaValue.FromFloat(Math.ScaleB(
            LuaLibraryHelpers.CheckNumber(arguments, 0, "ldexp"),
            checked((int)LuaLibraryHelpers.CheckInteger(arguments, 1, "ldexp"))))];

    private static LuaValue[] Floor(LuaState _, ReadOnlySpan<LuaValue> arguments) =>
        [RoundToIntegerWhenPossible(arguments, "floor", Math.Floor)];

    private static LuaValue[] Ceil(LuaState _, ReadOnlySpan<LuaValue> arguments) =>
        [RoundToIntegerWhenPossible(arguments, "ceil", Math.Ceiling)];

    private static LuaValue RoundToIntegerWhenPossible(
        ReadOnlySpan<LuaValue> arguments,
        string name,
        Func<double, double> operation)
    {
        var value = LuaLibraryHelpers.Required(arguments, 0, name);
        if (value.Kind == LuaValueKind.Integer)
        {
            return value;
        }

        return NumberWithIntegerTagWhenPossible(operation(
            LuaLibraryHelpers.CheckNumber(arguments, 0, name)));
    }

    private static LuaValue[] Fmod(LuaState _, ReadOnlySpan<LuaValue> arguments)
    {
        var left = LuaLibraryHelpers.Required(arguments, 0, "fmod");
        var right = LuaLibraryHelpers.Required(arguments, 1, "fmod");
        if (left.Kind == LuaValueKind.Integer && right.Kind == LuaValueKind.Integer)
        {
            var divisor = right.AsInteger();
            if (divisor == 0)
            {
                throw LuaLibraryHelpers.BadArgument("fmod", 1, "zero");
            }

            return [LuaValue.FromInteger(divisor == -1 ? 0 : left.AsInteger() % divisor)];
        }

        return [LuaValue.FromFloat(
            LuaLibraryHelpers.CheckNumber(arguments, 0, "fmod") %
            LuaLibraryHelpers.CheckNumber(arguments, 1, "fmod"))];
    }

    private static LuaValue[] Modf(LuaState _, ReadOnlySpan<LuaValue> arguments)
    {
        var value = LuaLibraryHelpers.Required(arguments, 0, "modf");
        if (value.Kind == LuaValueKind.Integer)
        {
            return [value, LuaValue.FromFloat(0)];
        }

        var number = LuaLibraryHelpers.CheckNumber(arguments, 0, "modf");
        var integerPart = number < 0 ? Math.Ceiling(number) : Math.Floor(number);
        return
        [
            NumberWithIntegerTagWhenPossible(integerPart),
            LuaValue.FromFloat(number == integerPart ? 0 : number - integerPart),
        ];
    }

    private static LuaValue[] Log(LuaState _, ReadOnlySpan<LuaValue> arguments)
    {
        var number = LuaLibraryHelpers.CheckNumber(arguments, 0, "log");
        if (arguments.Length < 2 || arguments[1].IsNil)
        {
            return [LuaValue.FromFloat(Math.Log(number))];
        }

        var @base = LuaLibraryHelpers.CheckNumber(arguments, 1, "log");
        var result = @base == 2 ? Math.Log2(number) :
            @base == 10 ? Math.Log10(number) : Math.Log(number) / Math.Log(@base);
        return [LuaValue.FromFloat(result)];
    }

    private static LuaValue[] Deg(LuaState _, ReadOnlySpan<LuaValue> arguments) =>
        [LuaValue.FromFloat(LuaLibraryHelpers.CheckNumber(arguments, 0, "deg") * (180 / Pi))];

    private static LuaValue[] Rad(LuaState _, ReadOnlySpan<LuaValue> arguments) =>
        [LuaValue.FromFloat(LuaLibraryHelpers.CheckNumber(arguments, 0, "rad") * (Pi / 180))];

    private static LuaValue[] ToInteger(LuaState _, ReadOnlySpan<LuaValue> arguments)
    {
        var value = LuaLibraryHelpers.Required(arguments, 0, "tointeger");
        return LuaValueOperations.TryToNumber(value, out var number) &&
            number.TryGetInteger(out var integer)
                ? [LuaValue.FromInteger(integer)]
                : [LuaValue.Nil];
    }

    private static LuaValue[] NumberType(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var value = LuaLibraryHelpers.Required(arguments, 0, "type");
        return value.Kind switch
        {
            LuaValueKind.Integer => [LuaLibraryHelpers.String(state, "integer")],
            LuaValueKind.Float => [LuaLibraryHelpers.String(state, "float")],
            _ => [LuaValue.Nil],
        };
    }

    private static LuaValue[] UnsignedLessThan(LuaState _, ReadOnlySpan<LuaValue> arguments) =>
        [LuaValue.FromBoolean(
            (ulong)LuaLibraryHelpers.CheckInteger(arguments, 0, "ult") <
            (ulong)LuaLibraryHelpers.CheckInteger(arguments, 1, "ult"))];

    private static LuaNativeStep Min(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values) =>
        FindExtreme(context, continuationId, values, findMaximum: false);

    private static LuaNativeStep Max(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values) =>
        FindExtreme(context, continuationId, values, findMaximum: true);

    private static LuaNativeStep FindExtreme(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values,
        bool findMaximum)
    {
        LuaValue[] arguments;
        var index = 1;
        var best = 0;
        if (continuationId == 0)
        {
            if (values.Length == 0)
            {
                throw LuaLibraryHelpers.BadArgument(
                    findMaximum ? "max" : "min",
                    0,
                    "value expected");
            }

            arguments = values.ToArray();
        }
        else
        {
            var state = context.InvocationState;
            index = checked((int)state[^2].AsInteger());
            best = checked((int)state[^1].AsInteger());
            arguments = state.Take(state.Count - 2).ToArray();
            if (values.Length == 0)
            {
                throw new LuaRuntimeException("comparison returned no value");
            }

            if (values[0].IsTruthy)
            {
                best = index;
            }

            index++;
        }

        while (index < arguments.Length)
        {
            var left = findMaximum ? arguments[best] : arguments[index];
            var right = findMaximum ? arguments[index] : arguments[best];
            var comparison = LuaRuntimeOperations.Binary(
                context.State,
                LuaIrBinaryOperator.LessThan,
                left,
                right);
            if (comparison.RequiresCall)
            {
                return LuaNativeStep.CallLua(
                    comparison.Callable,
                    comparison.Arguments,
                    continuationId: 1,
                    stateValues:
                    [
                        .. arguments,
                        LuaValue.FromInteger(index),
                        LuaValue.FromInteger(best),
                    ],
                    callIsYieldable: false);
            }

            if (comparison.Value.IsTruthy)
            {
                best = index;
            }

            index++;
        }

        return LuaNativeStep.Completed(arguments[best]);
    }

    private static LuaValue NumberWithIntegerTagWhenPossible(double value)
    {
        var result = LuaValue.FromFloat(value);
        return result.TryGetInteger(out var integer) ? LuaValue.FromInteger(integer) : result;
    }

    private sealed class LuaRandomState
    {
        private readonly ulong[] _state = new ulong[4];

        public LuaRandomState()
        {
            Span<byte> seed = stackalloc byte[16];
            RandomNumberGenerator.Fill(seed);
            SetSeed(
                BitConverter.ToUInt64(seed[..8]),
                BitConverter.ToUInt64(seed[8..]));
        }

        public LuaValue[] Random(LuaState state, ReadOnlySpan<LuaValue> arguments)
        {
            var random = Next();
            if (arguments.Length == 0)
            {
                return [LuaValue.FromFloat((random >> 11) * (0.5 / (1UL << 52)))];
            }

            if (arguments.Length > 2)
            {
                throw new LuaRuntimeException("wrong number of arguments");
            }

            var low = arguments.Length == 1
                ? 1
                : LuaLibraryHelpers.CheckInteger(arguments, 0, "random");
            var high = LuaLibraryHelpers.CheckInteger(
                arguments,
                arguments.Length == 1 ? 0 : 1,
                "random");
            if (arguments.Length == 1 && high == 0 &&
                state.LanguageVersion == LuaLanguageVersion.Lua54)
            {
                return [LuaValue.FromInteger(unchecked((long)random))];
            }

            if (low > high)
            {
                throw LuaLibraryHelpers.BadArgument("random", 0, "interval is empty");
            }

            var interval = unchecked((ulong)high - (ulong)low);
            if (state.LanguageVersion == LuaLanguageVersion.Lua53 &&
                interval > long.MaxValue)
            {
                throw LuaLibraryHelpers.BadArgument("random", 1, "interval is too large");
            }

            var projected = Project(random, interval);
            return [LuaValue.FromInteger(unchecked((long)(projected + (ulong)low)))];
        }

        public LuaValue[] RandomSeed(LuaState state, ReadOnlySpan<LuaValue> arguments)
        {
            ulong first;
            ulong second;
            if (arguments.Length == 0)
            {
                Span<byte> seed = stackalloc byte[16];
                RandomNumberGenerator.Fill(seed);
                first = BitConverter.ToUInt64(seed[..8]);
                second = BitConverter.ToUInt64(seed[8..]);
            }
            else
            {
                first = unchecked((ulong)LuaLibraryHelpers.CheckInteger(arguments, 0, "randomseed"));
                second = unchecked((ulong)LuaLibraryHelpers.OptionalInteger(
                    arguments,
                    1,
                    0,
                    "randomseed"));
            }

            SetSeed(first, second);
            return state.LanguageVersion == LuaLanguageVersion.Lua53
                ? []
                :
                [
                    LuaValue.FromInteger(unchecked((long)first)),
                    LuaValue.FromInteger(unchecked((long)second)),
                ];
        }

        private void SetSeed(ulong first, ulong second)
        {
            _state[0] = first;
            _state[1] = 0xff;
            _state[2] = second;
            _state[3] = 0;
            for (var index = 0; index < 16; index++)
            {
                Next();
            }
        }

        private ulong Next()
        {
            var state0 = _state[0];
            var state1 = _state[1];
            var state2 = _state[2] ^ state0;
            var state3 = _state[3] ^ state1;
            var result = BitOperations.RotateLeft(state1 * 5, 7) * 9;
            _state[0] = state0 ^ state3;
            _state[1] = state1 ^ state2;
            _state[2] = state2 ^ (state1 << 17);
            _state[3] = BitOperations.RotateLeft(state3, 45);
            return result;
        }

        private ulong Project(ulong random, ulong upperBound)
        {
            if ((upperBound & (upperBound + 1)) == 0)
            {
                return random & upperBound;
            }

            var limit = upperBound;
            limit |= limit >> 1;
            limit |= limit >> 2;
            limit |= limit >> 4;
            limit |= limit >> 8;
            limit |= limit >> 16;
            limit |= limit >> 32;
            while ((random &= limit) > upperBound)
            {
                random = Next();
            }

            return random;
        }
    }
}
