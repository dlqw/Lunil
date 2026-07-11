namespace Lunil.Core.Numerics;

#pragma warning disable CA1720 // Integer and float are exact Lua domain terms.
public enum LuaNumberKind : byte
{
    Integer,
    Float,
}

/// <summary>A decoded Lua 5.4 integer or floating-point numeral.</summary>
public readonly record struct LuaNumber
{
    private LuaNumber(LuaNumberKind kind, long integer, double floatingPoint)
    {
        Kind = kind;
        Integer = integer;
        Float = floatingPoint;
    }

    public LuaNumberKind Kind { get; }

    public long Integer { get; }

    public double Float { get; }

    public static LuaNumber FromInteger(long value) =>
        new(LuaNumberKind.Integer, value, 0);

    public static LuaNumber FromFloat(double value) =>
        new(LuaNumberKind.Float, 0, value);
}
#pragma warning restore CA1720
