namespace Lunil.IR.Lua52;

/// <summary>Lua 5.2's six-bit opcode and 8/9/9 operand encoding.</summary>
public readonly record struct Lua52Instruction(uint RawValue)
{
    public const int MaximumA = Lua52GeneratedInstructionCodec.MaximumA;
    public const int MaximumB = Lua52GeneratedInstructionCodec.MaximumB;
    public const int MaximumC = Lua52GeneratedInstructionCodec.MaximumC;
    public const int MaximumBx = Lua52GeneratedInstructionCodec.MaximumBx;
    public const int MaximumAx = Lua52GeneratedInstructionCodec.MaximumAx;
    public const int SignedBxOffset = Lua52GeneratedInstructionCodec.SignedBxOffset;

    public Lua52Opcode Opcode => Lua52GeneratedInstructionCodec.DecodeOpcode(RawValue);
    public int A => Lua52GeneratedInstructionCodec.DecodeA(RawValue);
    public int C => Lua52GeneratedInstructionCodec.DecodeC(RawValue);
    public int B => Lua52GeneratedInstructionCodec.DecodeB(RawValue);
    public int Bx => Lua52GeneratedInstructionCodec.DecodeBx(RawValue);
    public int SignedBx => Bx - SignedBxOffset;
    public int Ax => Lua52GeneratedInstructionCodec.DecodeAx(RawValue);
    public bool IsConstantB => B >= 1 << 8;
    public bool IsConstantC => C >= 1 << 8;

    public static Lua52Instruction CreateAbc(Lua52Opcode opcode, int a, int b, int c) =>
        new(Lua52GeneratedInstructionCodec.EncodeAbc(
            opcode,
            Checked(a, MaximumA),
            Checked(b, MaximumB),
            Checked(c, MaximumC)));

    public static Lua52Instruction CreateABx(Lua52Opcode opcode, int a, int bx) =>
        new(Lua52GeneratedInstructionCodec.EncodeABx(opcode, Checked(a, MaximumA), Checked(bx, MaximumBx)));

    public static Lua52Instruction CreateASignedBx(Lua52Opcode opcode, int a, int signedBx)
    {
        if (signedBx < -SignedBxOffset || signedBx > MaximumBx - SignedBxOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(signedBx));
        }

        return CreateABx(opcode, a, signedBx + SignedBxOffset);
    }

    public static Lua52Instruction CreateAx(Lua52Opcode opcode, int ax) =>
        new(Lua52GeneratedInstructionCodec.EncodeAx(opcode, Checked(ax, MaximumAx)));

    private static int Checked(int value, int maximum) =>
        (uint)value <= (uint)maximum
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), $"Value {value} is outside the opcode field.");
}
