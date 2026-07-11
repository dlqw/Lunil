namespace Luac.IR.Lua54;

#pragma warning disable CA1720 // Lua's public type names are part of the domain vocabulary.
public enum Lua54ConstantKind : byte
{
    Nil,
    False,
    True,
    Float,
    Integer,
    ShortString,
    LongString,
}
#pragma warning restore CA1720

public readonly record struct Lua54Constant
{
    private Lua54Constant(
        Lua54ConstantKind kind,
        long integerValue,
        double floatValue,
        Lua54String? stringValue)
    {
        Kind = kind;
        IntegerValue = integerValue;
        FloatValue = floatValue;
        StringValue = stringValue;
    }

    public Lua54ConstantKind Kind { get; }

    public long IntegerValue { get; }

    public double FloatValue { get; }

    public Lua54String? StringValue { get; }

    public bool GetBooleanValue() => Kind switch
    {
        Lua54ConstantKind.False => false,
        Lua54ConstantKind.True => true,
        _ => throw new InvalidOperationException($"Constant {Kind} is not a boolean."),
    };

    public static Lua54Constant Nil { get; } = new(Lua54ConstantKind.Nil, 0, 0, null);

    public static Lua54Constant False { get; } = new(Lua54ConstantKind.False, 0, 0, null);

    public static Lua54Constant True { get; } = new(Lua54ConstantKind.True, 0, 0, null);

    public static Lua54Constant FromInteger(long value) =>
        new(Lua54ConstantKind.Integer, value, 0, null);

    public static Lua54Constant FromFloat(double value) =>
        new(Lua54ConstantKind.Float, 0, value, null);

    public static Lua54Constant FromString(Lua54String value, bool isShort)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Lua54Constant(
            isShort ? Lua54ConstantKind.ShortString : Lua54ConstantKind.LongString,
            0,
            0,
            value);
    }
}
