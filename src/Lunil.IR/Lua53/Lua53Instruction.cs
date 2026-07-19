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
}
