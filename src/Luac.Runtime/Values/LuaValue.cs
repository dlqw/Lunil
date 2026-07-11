using System.Globalization;
using Luac.Runtime.Execution;

namespace Luac.Runtime.Values;

#pragma warning disable CA1720 // Lua's value kinds use the language's public terminology.
public enum LuaValueKind : byte
{
    Nil,
    Boolean,
    Integer,
    Float,
    String,
    Table,
    Function,
}
#pragma warning restore CA1720

/// <summary>
/// A GC-safe 16-byte tagged value. CLR references never overlap numeric payload bits.
/// </summary>
public readonly struct LuaValue : IEquatable<LuaValue>
{
    private static readonly object BooleanTag = new();
    private static readonly object IntegerTag = new();
    private static readonly object FloatTag = new();

    private readonly object? _tagOrReference;
    private readonly long _payload;

    private LuaValue(object? tagOrReference, long payload)
    {
        _tagOrReference = tagOrReference;
        _payload = payload;
    }

    public LuaValueKind Kind => _tagOrReference switch
    {
        null => LuaValueKind.Nil,
        var tag when ReferenceEquals(tag, BooleanTag) => LuaValueKind.Boolean,
        var tag when ReferenceEquals(tag, IntegerTag) => LuaValueKind.Integer,
        var tag when ReferenceEquals(tag, FloatTag) => LuaValueKind.Float,
        LuaString => LuaValueKind.String,
        LuaTable => LuaValueKind.Table,
        LuaClosure or LuaNativeFunction => LuaValueKind.Function,
        _ => throw new InvalidOperationException("The Lua value contains an unknown reference kind."),
    };

    public bool IsNil => _tagOrReference is null;

    public bool IsTruthy => !IsNil && (Kind != LuaValueKind.Boolean || _payload != 0);

    public static LuaValue Nil => default;

    public static LuaValue FromBoolean(bool value) => new(BooleanTag, value ? 1 : 0);

    public static LuaValue FromInteger(long value) => new(IntegerTag, value);

    public static LuaValue FromFloat(double value) =>
        new(FloatTag, BitConverter.DoubleToInt64Bits(value));

    public static LuaValue FromString(LuaString value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new LuaValue(value, 0);
    }

    public static LuaValue FromTable(LuaTable value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new LuaValue(value, 0);
    }

    public static LuaValue FromFunction(LuaClosure value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new LuaValue(value, 0);
    }

    public static LuaValue FromFunction(LuaNativeFunction value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new LuaValue(value, 0);
    }

    public bool AsBoolean() => Kind == LuaValueKind.Boolean
        ? _payload != 0
        : throw TypeError("boolean");

    public long AsInteger() => Kind == LuaValueKind.Integer
        ? _payload
        : throw TypeError("integer");

    public double AsFloat() => Kind switch
    {
        LuaValueKind.Float => BitConverter.Int64BitsToDouble(_payload),
        LuaValueKind.Integer => _payload,
        _ => throw TypeError("number"),
    };

    public LuaString AsString() => _tagOrReference as LuaString ?? throw TypeError("string");

    public LuaTable AsTable() => _tagOrReference as LuaTable ?? throw TypeError("table");

    public LuaClosure? TryGetClosure() => _tagOrReference as LuaClosure;

    public LuaNativeFunction? TryGetNativeFunction() => _tagOrReference as LuaNativeFunction;

    public bool TryGetInteger(out long value)
    {
        if (Kind == LuaValueKind.Integer)
        {
            value = _payload;
            return true;
        }

        if (Kind == LuaValueKind.Float)
        {
            var number = BitConverter.Int64BitsToDouble(_payload);
            if (number >= long.MinValue && number < 9_223_372_036_854_775_808d &&
                number == Math.Truncate(number))
            {
                value = (long)number;
                return true;
            }
        }

        value = 0;
        return false;
    }

    public bool Equals(LuaValue other)
    {
        if (Kind is LuaValueKind.Integer or LuaValueKind.Float &&
            other.Kind is LuaValueKind.Integer or LuaValueKind.Float)
        {
            return LuaValueOperations.NumberEquals(this, other);
        }

        if (Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            LuaValueKind.Nil => true,
            LuaValueKind.Boolean or LuaValueKind.Integer => _payload == other._payload,
            LuaValueKind.Float => AsFloat() == other.AsFloat(),
            LuaValueKind.String => AsString().Equals(other.AsString()),
            _ => ReferenceEquals(_tagOrReference, other._tagOrReference),
        };
    }

    public override bool Equals(object? obj) => obj is LuaValue other && Equals(other);

    public static bool operator ==(LuaValue left, LuaValue right) => left.Equals(right);

    public static bool operator !=(LuaValue left, LuaValue right) => !left.Equals(right);

    public override int GetHashCode()
    {
        if (TryGetInteger(out var integer))
        {
            return integer.GetHashCode();
        }

        return Kind switch
        {
            LuaValueKind.Nil => 0,
            LuaValueKind.Boolean => _payload.GetHashCode(),
            LuaValueKind.Float => AsFloat().GetHashCode(),
            LuaValueKind.String => AsString().GetHashCode(),
            _ => _tagOrReference?.GetHashCode() ?? 0,
        };
    }

    public override string ToString() => Kind switch
    {
        LuaValueKind.Nil => "nil",
        LuaValueKind.Boolean => _payload != 0 ? "true" : "false",
        LuaValueKind.Integer => _payload.ToString(CultureInfo.InvariantCulture),
        LuaValueKind.Float => AsFloat().ToString("G14", CultureInfo.InvariantCulture),
        LuaValueKind.String => AsString().ToString(),
        LuaValueKind.Table => $"table: 0x{_tagOrReference!.GetHashCode():x}",
        LuaValueKind.Function => $"function: 0x{_tagOrReference!.GetHashCode():x}",
        _ => throw new InvalidOperationException(),
    };

    private LuaRuntimeException TypeError(string expected) =>
        new($"Expected {expected}, got {Kind.ToString().ToLowerInvariant()}.");
}
