using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Lunil.Runtime.Values;

namespace Lunil.Hosting;

/// <summary>One bounded CLR iterator step.</summary>
public readonly record struct LuaClrIteratorResult(bool HasValue, LuaValue Value);

/// <summary>A cancellable, single-pass projection of a CLR enumerable.</summary>
public sealed class LuaClrIterator : IDisposable
{
    private readonly IEnumerator _enumerator;
    private readonly LuaClrBridge _bridge;
    private readonly int _maximumItems;
    private readonly long _maximumBytes;
    private CancellationToken _cancellationToken;
    private int _count;
    private long _bytes;
    private int _disposed;

    internal LuaClrIterator(
        IEnumerator enumerator,
        LuaClrBridge bridge,
        int maximumItems,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        _enumerator = enumerator;
        _bridge = bridge;
        _maximumItems = maximumItems;
        _maximumBytes = maximumBytes;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Gets whether this iterator has been exhausted, cancelled, or disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Links cancellation before the next step.</summary>
    public void LinkCancellation(LuaClrCancellation cancellation)
    {
        LunilGuard.NotNull(cancellation);
        if (IsDisposed)
        {
            throw new LuaClrException(LuaClrErrorCode.IteratorClosed, "The CLR iterator is closed.");
        }
        _cancellationToken = cancellation.Token;
    }

    /// <summary>Advances once without materializing the remaining sequence.</summary>
    public LuaClrIteratorResult MoveNext()
    {
        if (IsDisposed)
        {
            throw new LuaClrException(LuaClrErrorCode.IteratorClosed, "The CLR iterator is closed.");
        }
        if (_cancellationToken.IsCancellationRequested)
        {
            Dispose();
            throw new LuaClrException(LuaClrErrorCode.IteratorClosed, "The CLR iterator was cancelled.");
        }
        if (_count >= _maximumItems)
        {
            Dispose();
            throw new LuaClrException(LuaClrErrorCode.ConversionLimitExceeded,
                "The CLR iterator exceeded its configured item limit.");
        }

        try
        {
            if (!_enumerator.MoveNext())
            {
                Dispose();
                return new LuaClrIteratorResult(false, LuaValue.Nil);
            }
            _count++;
            var current = _enumerator.Current;
            _bytes = checked(_bytes + LuaClrBridge.EstimateIteratorValueBytes(current));
            if (_bytes > _maximumBytes)
            {
                Dispose();
                throw new LuaClrException(LuaClrErrorCode.ConversionLimitExceeded,
                    "The CLR iterator exceeded its configured byte limit.");
            }
            return new LuaClrIteratorResult(true, _bridge.ConvertIteratorValue(current));
        }
        catch (LuaClrException)
        {
            Dispose();
            throw;
        }
        catch (OverflowException exception)
        {
            Dispose();
            throw new LuaClrException(LuaClrErrorCode.ConversionLimitExceeded,
                "The CLR iterator exceeded its configured byte limit.", exception);
        }
        catch (Exception exception)
        {
            Dispose();
            throw new LuaClrException(LuaClrErrorCode.IteratorClosed,
                "The CLR iterator failed while advancing.", exception);
        }
    }

    /// <summary>Disposes the underlying enumerator at most once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _enumerator is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

public sealed partial class LuaClrBridge
{
    private sealed class ConversionContext
    {
        private readonly LuaClrConversionLimits _limits;
        private readonly HashSet<object> _active = new(ReferenceComparer.Instance);
        private int _items;
        private long _bytes;

        public ConversionContext(LuaClrConversionLimits limits) => _limits = limits;

        public void Enter(object value, int depth)
        {
            if (depth > _limits.MaximumDepth)
            {
                throw new LuaClrException(LuaClrErrorCode.ConversionLimitExceeded,
                    "CLR value projection exceeded its configured depth limit.");
            }
            if (!_active.Add(value))
            {
                throw new LuaClrException(LuaClrErrorCode.ConversionCycle,
                    "CLR value projection contains a reference cycle.");
            }
        }

        public void Exit(object value) => _active.Remove(value);

        public void Charge(int items, long bytes)
        {
            try
            {
                _items = checked(_items + items);
                _bytes = checked(_bytes + bytes);
            }
            catch (OverflowException)
            {
                throw Limit();
            }
            if (_items > _limits.MaximumItems || _bytes > _limits.MaximumBytes)
            {
                throw Limit();
            }
        }

        private static LuaClrException Limit() => new(
            LuaClrErrorCode.ConversionLimitExceeded,
            "CLR value projection exceeded its configured item or byte limit.");
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static ReferenceComparer Instance { get; } = new();
        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }

    internal LuaValue ConvertIteratorValue(object? value) => ToLuaValue(value);

    internal static long EstimateIteratorValueBytes(object? value) => value switch
    {
        null => 8,
        string text => checked(16L + Encoding.UTF8.GetByteCount(text)),
        char => 18,
        bool or byte or sbyte or short or ushort or int or uint or long or ulong or
            float or double or decimal or Enum => 24,
        Array array => checked(64L + array.LongLength * 24L),
        ICollection collection => checked(64L + collection.Count * 24L),
        _ => 64,
    };

    /// <summary>Advances a CLR iterator userdata once.</summary>
    [SuppressMessage("Performance", "CA1822", Justification = "The operation is intentionally scoped to a bridge-owned iterator value.")]
    public LuaClrIteratorResult MoveNext(LuaValue iterator)
    {
        if (iterator.Kind != LuaValueKind.Userdata ||
            iterator.AsUserdata().Payload is not LuaClrIterator clrIterator)
        {
            throw new LuaClrException(LuaClrErrorCode.IteratorClosed,
                "A CLR iterator userdata is required.");
        }
        return clrIterator.MoveNext();
    }

    /// <summary>Links a CLR iterator userdata to bridge cancellation.</summary>
    [SuppressMessage("Performance", "CA1822", Justification = "The operation is intentionally scoped to a bridge-owned iterator value.")]
    public void LinkIteratorCancellation(LuaValue iterator, LuaClrCancellation cancellation)
    {
        if (iterator.Kind != LuaValueKind.Userdata ||
            iterator.AsUserdata().Payload is not LuaClrIterator clrIterator)
        {
            throw new LuaClrException(LuaClrErrorCode.IteratorClosed,
                "A CLR iterator userdata is required.");
        }
        clrIterator.LinkCancellation(cancellation);
    }

    private LuaValue ToLuaValue(object? value) =>
        ToLuaValue(value, new ConversionContext(_options.ConversionLimits), depth: 0);

    private LuaValue ToLuaValue(object? value, ConversionContext context, int depth)
    {
        context.Charge(1, 16);
        if (value is null)
        {
            return LuaValue.Nil;
        }
        if (value is LuaValue luaValue)
        {
            _state.Heap.ValidateValue(luaValue);
            return luaValue;
        }

        switch (value)
        {
            case bool boolean: return LuaValue.FromBoolean(boolean);
            case string text:
                context.Charge(0, Encoding.UTF8.GetByteCount(text));
                return StringValue(text);
            case char character: return StringValue(character.ToString());
            case byte number: return LuaValue.FromInteger(number);
            case sbyte number: return LuaValue.FromInteger(number);
            case short number: return LuaValue.FromInteger(number);
            case ushort number: return LuaValue.FromInteger(number);
            case int number: return LuaValue.FromInteger(number);
            case uint number: return LuaValue.FromInteger(number);
            case long number: return LuaValue.FromInteger(number);
            case ulong number when number <= long.MaxValue: return LuaValue.FromInteger((long)number);
            case ulong number:
                throw new LuaClrException(LuaClrErrorCode.InvocationFailed,
                    $"CLR UInt64 value '{number}' exceeds the Lua integer range.");
            case float number: return LuaValue.FromFloat(number);
            case double number: return LuaValue.FromFloat(number);
            case decimal number: return DecimalToLua(number);
            case Enum enumeration: return EnumToLua(enumeration);
            case Task task:
                return LuaValue.FromUserdata(_state.CreateUserdata(new LuaClrTask(task, this), 1, 64));
            case Array array:
                return ToLuaArray(array, context, depth + 1);
            case ITuple tuple:
                return ToLuaTuple(tuple, value, context, depth + 1);
        }

        if (_options.CollectionProjection == LuaClrCollectionProjection.TablesAndIterators)
        {
            if (value is IDictionary dictionary)
            {
                return ToLuaDictionary(dictionary, context, depth + 1);
            }
            if (value is IList list)
            {
                return ToLuaList(list, context, depth + 1);
            }
            if (value is IEnumerable enumerable)
            {
                IEnumerator enumerator;
                try
                {
                    enumerator = enumerable.GetEnumerator();
                }
                catch (Exception exception)
                {
                    throw new LuaClrException(LuaClrErrorCode.IteratorClosed,
                        "The CLR enumerable failed to create an iterator.", exception);
                }
                return LuaValue.FromUserdata(_state.CreateUserdata(
                    new LuaClrIterator(enumerator, this, _options.ConversionLimits.MaximumItems,
                        _options.ConversionLimits.MaximumBytes, CancellationToken.None), 1, 64));
            }
        }

        var valueType = value.GetType();
        if (valueType == typeof(ValueTask) ||
            valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            EnsureReflectionFallback(valueType);
            var asTask = valueType.GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance);
            if (asTask?.Invoke(value, null) is Task task)
            {
                return LuaValue.FromUserdata(_state.CreateUserdata(new LuaClrTask(task, this), 1, 64));
            }
        }

        var userdata = _state.CreateUserdata(new LuaClrObject(value, ownsInstance: false), 1, 64);
        if ((_options.Capabilities & LuaClrCapabilities.MemberAccess) != LuaClrCapabilities.None)
        {
            AttachMetatable(userdata, valueType);
        }
        return LuaValue.FromUserdata(userdata);
    }

    private LuaValue DecimalToLua(decimal number) => _options.DecimalRepresentation switch
    {
        LuaClrDecimalRepresentation.ExactString => StringValue(number.ToString(CultureInfo.InvariantCulture)),
        LuaClrDecimalRepresentation.ExactInteger when decimal.Truncate(number) == number &&
            number >= long.MinValue && number <= long.MaxValue => LuaValue.FromInteger(decimal.ToInt64(number)),
        LuaClrDecimalRepresentation.ExactInteger => throw new LuaClrException(
            LuaClrErrorCode.ConversionFailed,
            "The CLR decimal value is not an exact Lua integer."),
        LuaClrDecimalRepresentation.LossyFloat => LuaValue.FromFloat((double)number),
        _ => throw new InvalidOperationException("Unknown decimal representation."),
    };

    private LuaValue EnumToLua(Enum enumeration)
    {
        var integer = EnumToInt64(enumeration);
        var name = enumeration.ToString();
        return _options.EnumRepresentation switch
        {
            LuaClrEnumRepresentation.Name => StringValue(name),
            LuaClrEnumRepresentation.UnderlyingValue => LuaValue.FromInteger(integer),
            LuaClrEnumRepresentation.NameAndInteger => EnumTable(name, integer),
            _ => throw new InvalidOperationException("Unknown enum representation."),
        };
    }

    private LuaValue EnumTable(string name, long integer)
    {
        var table = _state.CreateTable(0, 2);
        table.Set(StringValue("name"), StringValue(name));
        table.Set(StringValue("value"), LuaValue.FromInteger(integer));
        return LuaValue.FromTable(table);
    }

    private static long EnumToInt64(Enum value)
    {
        var underlying = Enum.GetUnderlyingType(value.GetType());
        try
        {
            if (underlying == typeof(ulong))
            {
                var unsigned = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
                if (unsigned > long.MaxValue)
                {
                    throw new LuaClrException(LuaClrErrorCode.ConversionFailed,
                        "The CLR enum value exceeds the Lua integer range.");
                }
                return (long)unsigned;
            }
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (OverflowException exception)
        {
            throw new LuaClrException(LuaClrErrorCode.ConversionFailed,
                "The CLR enum value exceeds the Lua integer range.", exception);
        }
    }

    private LuaValue ToLuaArray(Array array, ConversionContext context, int depth)
    {
        context.Enter(array, depth);
        try
        {
            var indices = new int[array.Rank];
            return ToLuaArrayDimension(array, indices, 0, context, depth);
        }
        catch (LuaClrException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw ProjectionFailed("array", exception);
        }
        finally
        {
            context.Exit(array);
        }
    }

    private LuaValue ToLuaArrayDimension(
        Array array,
        int[] indices,
        int dimension,
        ConversionContext context,
        int depth)
    {
        var length = array.GetLength(dimension);
        context.Charge(length, checked(length * 24L));
        var lowerBound = array.GetLowerBound(dimension);
        var table = _state.CreateTable(length, 0);
        for (var offset = 0; offset < length; offset++)
        {
            indices[dimension] = lowerBound + offset;
            var item = dimension + 1 == array.Rank
                ? ToLuaValue(array.GetValue(indices), context, depth + 1)
                : ToLuaArrayDimension(array, indices, dimension + 1, context, depth + 1);
            table.Set(LuaValue.FromInteger(offset + 1L), item);
        }
        return LuaValue.FromTable(table);
    }

    private LuaValue ToLuaList(IList list, ConversionContext context, int depth)
    {
        context.Enter(list, depth);
        try
        {
            var count = list.Count;
            context.Charge(count, checked(count * 24L));
            var table = _state.CreateTable(count, 0);
            for (var index = 0; index < count; index++)
            {
                table.Set(LuaValue.FromInteger(index + 1L),
                    ToLuaValue(list[index], context, depth + 1));
            }
            return LuaValue.FromTable(table);
        }
        catch (LuaClrException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw ProjectionFailed("list", exception);
        }
        finally
        {
            context.Exit(list);
        }
    }

    private LuaValue ToLuaDictionary(IDictionary dictionary, ConversionContext context, int depth)
    {
        context.Enter(dictionary, depth);
        try
        {
            var count = dictionary.Count;
            context.Charge(checked(count * 2), checked(count * 48L));
            var table = _state.CreateTable(0, count);
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = ToLuaValue(entry.Key, context, depth + 1);
                if (key.IsNil || key.Kind == LuaValueKind.Float && double.IsNaN(key.AsFloat()))
                {
                    throw new LuaClrException(LuaClrErrorCode.ConversionFailed,
                        "A CLR dictionary key cannot be represented as a Lua table key.");
                }
                table.Set(key, ToLuaValue(entry.Value, context, depth + 1));
            }
            return LuaValue.FromTable(table);
        }
        catch (LuaClrException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw ProjectionFailed("dictionary", exception);
        }
        finally
        {
            context.Exit(dictionary);
        }
    }

    private LuaValue ToLuaTuple(ITuple tuple, object identity, ConversionContext context, int depth)
    {
        context.Enter(identity, depth);
        try
        {
            var length = tuple.Length;
            context.Charge(length, checked(length * 24L));
            var table = _state.CreateTable(length, 0);
            for (var index = 0; index < length; index++)
            {
                table.Set(LuaValue.FromInteger(index + 1L),
                    ToLuaValue(tuple[index], context, depth + 1));
            }
            return LuaValue.FromTable(table);
        }
        catch (LuaClrException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw ProjectionFailed("tuple", exception);
        }
        finally
        {
            context.Exit(identity);
        }
    }

    private static bool IsIntegralTarget(Type type) => type == typeof(byte) ||
        type == typeof(sbyte) ||
        type == typeof(short) ||
        type == typeof(ushort) ||
        type == typeof(int) ||
        type == typeof(uint) ||
        type == typeof(long) ||
        type == typeof(ulong);

    private static LuaClrException ProjectionFailed(string kind, Exception exception) => new(
        LuaClrErrorCode.ConversionFailed,
        $"The CLR {kind} failed while it was being projected to Lua.",
        exception);

    private LuaValue StringValue(string value) => LuaValue.FromString(
        _state.Strings.GetOrCreate(Encoding.UTF8.GetBytes(value)));

    private bool TryConvert(
        LuaValue value,
        Type targetType,
        out object? converted,
        out int score)
    {
        return TryConvert(value, targetType, new ConversionContext(_options.ConversionLimits),
            depth: 0, out converted, out score);
    }

    private bool TryConvert(
        LuaValue value,
        Type targetType,
        ConversionContext context,
        int depth,
        out object? converted,
        out int score)
    {
        context.Charge(1, 16);
        score = 0;
        var nullable = Nullable.GetUnderlyingType(targetType);
        var nonNullable = nullable ?? targetType;
        if (nonNullable.IsByRefLike || nonNullable.IsPointer)
        {
            converted = null;
            return false;
        }
        if (nonNullable == typeof(CancellationToken))
        {
            if (value.IsNil)
            {
                converted = CancellationToken.None;
                score = 1;
                return true;
            }
            if (value.Kind == LuaValueKind.Userdata &&
                value.AsUserdata().Payload is LuaClrCancellation cancellation)
            {
                converted = cancellation.Token;
                return true;
            }
        }
        if (value.IsNil)
        {
            converted = null;
            score = 1;
            return !nonNullable.IsValueType || nullable is not null;
        }
        if (value.Kind == LuaValueKind.Userdata)
        {
            var payload = value.AsUserdata().Payload;
            var instance = payload is LuaClrObject clrObject ? clrObject.Instance : payload;
            if (instance is not null && nonNullable.IsInstanceOfType(instance))
            {
                converted = instance;
                score = nonNullable == instance.GetType() ? 0 : 2;
                return true;
            }
        }
        if (nonNullable == typeof(LuaValue))
        {
            converted = value;
            return true;
        }
        if (nonNullable == typeof(string) && value.Kind == LuaValueKind.String)
        {
            converted = value.AsString().ToString();
            return true;
        }
        if (nonNullable == typeof(bool) && value.Kind == LuaValueKind.Boolean)
        {
            converted = value.AsBoolean();
            return true;
        }
        if (nonNullable == typeof(char) && value.Kind == LuaValueKind.String)
        {
            var text = value.AsString().ToString();
            if (text.Length == 1)
            {
                converted = text[0];
                score = 1;
                return true;
            }
        }
        if (nonNullable == typeof(decimal))
        {
            return TryConvertDecimal(value, out converted, out score);
        }
        if (nonNullable.IsEnum)
        {
            return TryConvertEnum(value, nonNullable, out converted, out score);
        }
        if (nonNullable.IsArray && value.Kind == LuaValueKind.Table)
        {
            return TryConvertArray(value.AsTable(), nonNullable, context, depth + 1,
                out converted, out score);
        }
        if (value.Kind == LuaValueKind.Table &&
            TryConvertCollection(value.AsTable(), nonNullable, context, depth + 1,
                out converted, out score))
        {
            return true;
        }
        if (IsTupleType(nonNullable) && value.Kind == LuaValueKind.Table)
        {
            return TryConvertTuple(value.AsTable(), nonNullable, context, depth + 1,
                out converted, out score);
        }
        if (IsNumeric(nonNullable) && value.Kind is LuaValueKind.Integer or LuaValueKind.Float)
        {
            // Lua 5.3+ semantics: a float without an exact integer representation
            // never binds to an integral CLR parameter instead of silently rounding.
            if (value.Kind == LuaValueKind.Float && IsIntegralTarget(nonNullable) &&
                !value.TryGetInteger(out _))
            {
                converted = null;
                score = int.MaxValue;
                return false;
            }

            var number = value.Kind == LuaValueKind.Integer ? (object)value.AsInteger() : value.AsFloat();
            try
            {
                converted = Convert.ChangeType(number, nonNullable, CultureInfo.InvariantCulture);
                score = (nonNullable == typeof(long) && value.Kind == LuaValueKind.Integer) ||
                    (nonNullable == typeof(double) && value.Kind == LuaValueKind.Float) ? 0 : 1;
                return value.Kind == LuaValueKind.Integer || double.IsFinite((double)number) ||
                    nonNullable == typeof(double) || nonNullable == typeof(float);
            }
            catch (Exception exception) when (exception is InvalidCastException or OverflowException or FormatException)
            {
            }
        }
        if (nonNullable == typeof(object))
        {
            converted = value.Kind switch
            {
                LuaValueKind.String => value.AsString().ToString(),
                LuaValueKind.Boolean => value.AsBoolean(),
                LuaValueKind.Integer => value.AsInteger(),
                LuaValueKind.Float => value.AsFloat(),
                _ => null,
            };
            if (converted is not null)
            {
                score = 10;
                return true;
            }
        }
        converted = null;
        score = 0;
        return false;
    }

    private bool TryConvertDecimal(LuaValue value, out object? converted, out int score)
    {
        score = 1;
        if (_options.DecimalRepresentation == LuaClrDecimalRepresentation.ExactString &&
            value.Kind == LuaValueKind.String &&
            decimal.TryParse(value.AsString().ToString(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var exact))
        {
            converted = exact;
            return true;
        }
        if (_options.DecimalRepresentation == LuaClrDecimalRepresentation.ExactInteger &&
            value.TryGetInteger(out var integer))
        {
            converted = (decimal)integer;
            return true;
        }
        if (_options.DecimalRepresentation == LuaClrDecimalRepresentation.LossyFloat &&
            value.Kind is LuaValueKind.Integer or LuaValueKind.Float)
        {
            try
            {
                converted = value.Kind == LuaValueKind.Integer
                    ? (decimal)value.AsInteger() : (decimal)value.AsFloat();
                return true;
            }
            catch (OverflowException)
            {
            }
        }
        converted = null;
        score = 0;
        return false;
    }

    private bool TryConvertEnum(LuaValue value, Type enumType, out object? converted, out int score)
    {
        score = 1;
        if (value.Kind == LuaValueKind.Table && _options.EnumRepresentation == LuaClrEnumRepresentation.NameAndInteger)
        {
            var table = value.AsTable();
            value = table.Get(StringValue("value"));
        }
        if (value.Kind == LuaValueKind.String)
        {
            var name = value.AsString().ToString();
            try
            {
                var parsed = Enum.Parse(enumType, name, ignoreCase: false);
                if (string.Equals(parsed.ToString(), name, StringComparison.Ordinal))
                {
                    converted = parsed;
                    return true;
                }
            }
            catch (ArgumentException)
            {
            }
        }
        if (value.TryGetInteger(out var integer))
        {
            try
            {
                var underlying = Convert.ChangeType(integer, Enum.GetUnderlyingType(enumType),
                    CultureInfo.InvariantCulture);
                converted = Enum.ToObject(enumType, underlying!);
                score = 2;
                return true;
            }
            catch (Exception exception) when (exception is OverflowException or InvalidCastException)
            {
            }
        }
        converted = null;
        score = 0;
        return false;
    }

    private bool TryConvertArray(
        LuaTable table,
        Type arrayType,
        ConversionContext context,
        int depth,
        out object? converted,
        out int score)
    {
        context.Enter(table, depth);
        try
        {
            var elementType = arrayType.GetElementType()!;
            var length = table.ArrayLength;
            context.Charge(length, checked(length * 24L));
            var array = Array.CreateInstance(elementType, length);
            for (var index = 0; index < length; index++)
            {
                if (!TryConvert(table.Get(LuaValue.FromInteger(index + 1L)), elementType,
                    context, depth + 1, out var element, out _))
                {
                    converted = null;
                    score = 0;
                    return false;
                }
                array.SetValue(element, index);
            }
            converted = array;
            score = 3;
            return true;
        }
        finally
        {
            context.Exit(table);
        }
    }

    private bool TryConvertTuple(
        LuaTable table,
        Type tupleType,
        ConversionContext context,
        int depth,
        out object? converted,
        out int score)
    {
        context.Enter(table, depth);
        try
        {
            var arguments = tupleType.GetGenericArguments();
            if (arguments.Length == 0 || table.ArrayLength != arguments.Length)
            {
                converted = null;
                score = 0;
                return false;
            }
            var values = new object?[arguments.Length];
            for (var index = 0; index < arguments.Length; index++)
            {
                if (!TryConvert(table.Get(LuaValue.FromInteger(index + 1L)), arguments[index],
                    context, depth + 1, out values[index], out _))
                {
                    converted = null;
                    score = 0;
                    return false;
                }
            }
            var binding = GetRegisteredBinding(tupleType.FullName ?? tupleType.Name);
            if (binding is not null)
            {
                var constructor = binding.Constructors.SingleOrDefault(candidate =>
                    candidate.Parameters.Length == values.Length);
                if (constructor is null)
                {
                    converted = null;
                    score = 0;
                    return false;
                }
                converted = constructor.Invoker(values);
            }
            else
            {
                EnsureReflectionFallback(tupleType);
                converted = Activator.CreateInstance(tupleType, values);
            }
            score = 4;
            return converted is not null;
        }
        finally
        {
            context.Exit(table);
        }
    }

    private bool TryConvertCollection(
        LuaTable table,
        Type targetType,
        ConversionContext context,
        int depth,
        out object? converted,
        out int score)
    {
        if (_options.CollectionProjection != LuaClrCollectionProjection.TablesAndIterators)
        {
            converted = null;
            score = 0;
            return false;
        }
        var dictionaryInterface = FindGenericInterface(targetType, typeof(IDictionary<,>)) ??
            FindGenericInterface(targetType, typeof(IReadOnlyDictionary<,>));
        if (dictionaryInterface is not null)
        {
            return TryConvertDictionary(table, targetType, dictionaryInterface.GetGenericArguments(),
                context, depth, out converted, out score);
        }
        var sequenceInterface = FindGenericInterface(targetType, typeof(IList<>)) ??
            FindGenericInterface(targetType, typeof(IReadOnlyList<>)) ??
            FindGenericInterface(targetType, typeof(IEnumerable<>));
        if (sequenceInterface is null)
        {
            converted = null;
            score = 0;
            return false;
        }

        context.Enter(table, depth);
        try
        {
            var elementType = sequenceInterface.GetGenericArguments()[0];
            var length = table.ArrayLength;
            context.Charge(length, checked(length * 24L));
            var array = Array.CreateInstance(elementType, length);
            for (var index = 0; index < length; index++)
            {
                if (!TryConvert(table.Get(LuaValue.FromInteger(index + 1L)), elementType,
                    context, depth + 1, out var element, out _))
                {
                    converted = null;
                    score = 0;
                    return false;
                }
                array.SetValue(element, index);
            }
            if (targetType.IsAssignableFrom(array.GetType()))
            {
                converted = array;
                score = 4;
                return true;
            }
            var collection = CreateBoundCollection(targetType);
            if (collection is not IList list)
            {
                converted = null;
                score = 0;
                return false;
            }
            foreach (var item in array)
            {
                list.Add(item);
            }
            converted = collection;
            score = 5;
            return true;
        }
        finally
        {
            context.Exit(table);
        }
    }

    private bool TryConvertDictionary(
        LuaTable table,
        Type targetType,
        Type[] genericArguments,
        ConversionContext context,
        int depth,
        out object? converted,
        out int score)
    {
        context.Enter(table, depth);
        try
        {
            var collection = CreateBoundCollection(targetType);
            if (collection is not IDictionary dictionary)
            {
                converted = null;
                score = 0;
                return false;
            }
            var key = LuaValue.Nil;
            var count = 0;
            while (table.Next(key, out var nextKey, out var nextValue))
            {
                key = nextKey;
                context.Charge(2, 48);
                if (!TryConvert(nextKey, genericArguments[0], context, depth + 1,
                        out var clrKey, out _) ||
                    !TryConvert(nextValue, genericArguments[1], context, depth + 1,
                        out var clrValue, out _) || clrKey is null)
                {
                    converted = null;
                    score = 0;
                    return false;
                }
                dictionary.Add(clrKey, clrValue);
                count++;
            }
            converted = collection;
            score = 5 + count / 1024;
            return true;
        }
        finally
        {
            context.Exit(table);
        }
    }

    private object? CreateBoundCollection(Type targetType)
    {
        var binding = GetRegisteredBinding(targetType.FullName ?? targetType.Name);
        var constructor = binding?.Constructors.FirstOrDefault(static item => item.Parameters.Length == 0);
        if (constructor is not null)
        {
            return constructor.Invoker([]);
        }
        if (!targetType.IsInterface && !targetType.IsAbstract && ReflectionFallbackAllowed)
        {
            return Activator.CreateInstance(targetType);
        }
        return null;
    }

    private static Type? FindGenericInterface(Type type, Type definition)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == definition)
        {
            return type;
        }
        return type.GetInterfaces().FirstOrDefault(candidate =>
            candidate.IsGenericType && candidate.GetGenericTypeDefinition() == definition);
    }

    private static bool IsTupleType(Type type) => type.IsGenericType &&
        (type.GetGenericTypeDefinition().FullName?.StartsWith("System.Tuple`", StringComparison.Ordinal) == true ||
         type.GetGenericTypeDefinition().FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true);
}
