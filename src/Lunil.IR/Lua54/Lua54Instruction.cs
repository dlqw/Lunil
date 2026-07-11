namespace Lunil.IR.Lua54;

/// <summary>A binary-compatible PUC Lua 5.4 32-bit instruction.</summary>
public readonly record struct Lua54Instruction(uint RawValue)
{
    public const int MaximumA = 0xff;
    public const int MaximumB = 0xff;
    public const int MaximumC = 0xff;
    public const int MaximumBx = 0x1ffff;
    public const int MaximumAx = 0x1ffffff;
    public const int SignedBxOffset = MaximumBx >> 1;
    public const int SignedJumpOffset = MaximumAx >> 1;
    public const int SignedCOffset = MaximumC >> 1;

    public Lua54Opcode Opcode => (Lua54Opcode)(RawValue & 0x7f);

    public int A => (int)((RawValue >> 7) & 0xff);

    public bool K => ((RawValue >> 15) & 1) != 0;

    public int B => (int)((RawValue >> 16) & 0xff);

    public int C => (int)((RawValue >> 24) & 0xff);

    public int SignedB => B - SignedCOffset;

    public int SignedC => C - SignedCOffset;

    public int Bx => (int)((RawValue >> 15) & MaximumBx);

    public int SignedBx => Bx - SignedBxOffset;

    public int Ax => (int)((RawValue >> 7) & MaximumAx);

    public int SignedJump => Ax - SignedJumpOffset;

    public Lua54InstructionMode Mode => Lua54OpcodeInfo.Get(Opcode).Mode;

    public static Lua54Instruction CreateAbc(
        Lua54Opcode opcode,
        int a,
        int b,
        int c,
        bool k = false)
    {
        EnsureMode(opcode, Lua54InstructionMode.Abc);
        EnsureRange(a, MaximumA, nameof(a));
        EnsureRange(b, MaximumB, nameof(b));
        EnsureRange(c, MaximumC, nameof(c));

        var raw = (uint)opcode
            | ((uint)a << 7)
            | ((k ? 1u : 0u) << 15)
            | ((uint)b << 16)
            | ((uint)c << 24);
        return new Lua54Instruction(raw);
    }

    public static Lua54Instruction CreateABx(Lua54Opcode opcode, int a, int bx)
    {
        EnsureMode(opcode, Lua54InstructionMode.ABx);
        EnsureRange(a, MaximumA, nameof(a));
        EnsureRange(bx, MaximumBx, nameof(bx));
        return new Lua54Instruction((uint)opcode | ((uint)a << 7) | ((uint)bx << 15));
    }

    public static Lua54Instruction CreateASignedBx(Lua54Opcode opcode, int a, int signedBx)
    {
        EnsureMode(opcode, Lua54InstructionMode.ASignedBx);
        EnsureRange(a, MaximumA, nameof(a));
        ArgumentOutOfRangeException.ThrowIfLessThan(signedBx, -SignedBxOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(signedBx, MaximumBx - SignedBxOffset);
        return new Lua54Instruction(
            (uint)opcode | ((uint)a << 7) | ((uint)(signedBx + SignedBxOffset) << 15));
    }

    public static Lua54Instruction CreateAx(Lua54Opcode opcode, int ax)
    {
        EnsureMode(opcode, Lua54InstructionMode.Ax);
        EnsureRange(ax, MaximumAx, nameof(ax));
        return new Lua54Instruction((uint)opcode | ((uint)ax << 7));
    }

    public static Lua54Instruction CreateSignedJump(Lua54Opcode opcode, int signedJump)
    {
        EnsureMode(opcode, Lua54InstructionMode.SignedJump);
        ArgumentOutOfRangeException.ThrowIfLessThan(signedJump, -SignedJumpOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(signedJump, MaximumAx - SignedJumpOffset);
        return new Lua54Instruction(
            (uint)opcode | ((uint)(signedJump + SignedJumpOffset) << 7));
    }

    private static void EnsureMode(Lua54Opcode opcode, Lua54InstructionMode expected)
    {
        var actual = Lua54OpcodeInfo.Get(opcode).Mode;
        if (actual != expected)
        {
            throw new ArgumentException(
                $"Opcode {opcode} uses {actual}, not {expected}.",
                nameof(opcode));
        }
    }

    private static void EnsureRange(int value, int maximum, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, parameterName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, maximum, parameterName);
    }
}
