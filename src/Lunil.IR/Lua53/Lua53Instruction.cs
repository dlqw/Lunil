namespace Lunil.IR.Lua53;

public readonly record struct Lua53Instruction(uint RawValue)
{
    public const int MaximumA = 0xff;
    public const int MaximumB = 0x1ff;
    public const int MaximumC = 0x1ff;
    public const int MaximumBx = 0x3ffff;
    public const int MaximumAx = 0x3ffffff;
    public const int SignedBxOffset = MaximumBx >> 1;

    public Lua53Opcode Opcode => (Lua53Opcode)(RawValue & 0x3f);
    public int A => (int)((RawValue >> 6) & 0xff);
    public int C => (int)((RawValue >> 14) & MaximumC);
    public int B => (int)((RawValue >> 23) & MaximumB);
    public int Bx => (int)((RawValue >> 14) & MaximumBx);
    public int SignedBx => Bx - SignedBxOffset;
    public int Ax => (int)((RawValue >> 6) & MaximumAx);

    public bool IsConstantB => B >= 1 << 8;
    public bool IsConstantC => C >= 1 << 8;
    public int RegisterB => B & 0xff;
    public int RegisterC => C & 0xff;
    public int ConstantB => B & 0xff;
    public int ConstantC => C & 0xff;

    public static Lua53Instruction CreateAbc(Lua53Opcode opcode, int a, int b, int c)
    {
        Validate(opcode, a, MaximumA, nameof(a));
        Validate(opcode, b, MaximumB, nameof(b));
        Validate(opcode, c, MaximumC, nameof(c));
        return new Lua53Instruction(
            (uint)opcode |
            ((uint)a << 6) |
            ((uint)c << 14) |
            ((uint)b << 23));
    }

    public static Lua53Instruction CreateABx(Lua53Opcode opcode, int a, int bx)
    {
        Validate(opcode, a, MaximumA, nameof(a));
        Validate(opcode, bx, MaximumBx, nameof(bx));
        return new Lua53Instruction((uint)opcode | ((uint)a << 6) | ((uint)bx << 14));
    }

    public static Lua53Instruction CreateASignedBx(Lua53Opcode opcode, int a, int signedBx)
    {
        if (signedBx < -SignedBxOffset || signedBx > MaximumBx - SignedBxOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(signedBx));
        }

        return CreateABx(opcode, a, signedBx + SignedBxOffset);
    }

    public static Lua53Instruction CreateAx(Lua53Opcode opcode, int ax)
    {
        Validate(opcode, ax, MaximumAx, nameof(ax));
        return new Lua53Instruction((uint)opcode | ((uint)ax << 6));
    }

    private static void Validate(Lua53Opcode opcode, int value, int maximum, string name)
    {
        if ((uint)value > (uint)maximum)
        {
            throw new ArgumentOutOfRangeException(name, value,
                $"Value {value} cannot be encoded in Lua 5.3 opcode {opcode}.");
        }
    }
}
