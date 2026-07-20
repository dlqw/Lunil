namespace Lunil.IR.Lua53;

public readonly record struct Lua53Instruction(uint RawValue)
{
    public const int MaximumA = Lua53GeneratedInstructionCodec.MaximumA;
    public const int MaximumB = Lua53GeneratedInstructionCodec.MaximumB;
    public const int MaximumC = Lua53GeneratedInstructionCodec.MaximumC;
    public const int MaximumBx = Lua53GeneratedInstructionCodec.MaximumBx;
    public const int MaximumAx = Lua53GeneratedInstructionCodec.MaximumAx;
    public const int SignedBxOffset = Lua53GeneratedInstructionCodec.SignedBxOffset;

    public Lua53Opcode Opcode => Lua53GeneratedInstructionCodec.DecodeOpcode(RawValue);
    public int A => Lua53GeneratedInstructionCodec.DecodeA(RawValue);
    public int C => Lua53GeneratedInstructionCodec.DecodeC(RawValue);
    public int B => Lua53GeneratedInstructionCodec.DecodeB(RawValue);
    public int Bx => Lua53GeneratedInstructionCodec.DecodeBx(RawValue);
    public int SignedBx => Bx - SignedBxOffset;
    public int Ax => Lua53GeneratedInstructionCodec.DecodeAx(RawValue);

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
        return new Lua53Instruction(Lua53GeneratedInstructionCodec.EncodeAbc(opcode, a, b, c));
    }

    public static Lua53Instruction CreateABx(Lua53Opcode opcode, int a, int bx)
    {
        Validate(opcode, a, MaximumA, nameof(a));
        Validate(opcode, bx, MaximumBx, nameof(bx));
        return new Lua53Instruction(Lua53GeneratedInstructionCodec.EncodeABx(opcode, a, bx));
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
        return new Lua53Instruction(Lua53GeneratedInstructionCodec.EncodeAx(opcode, ax));
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
