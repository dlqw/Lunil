namespace Luac.IR.Lua54;

/// <summary>Describes the platform-dependent scalar layout of a PUC Lua 5.4 chunk.</summary>
public readonly record struct Lua54ChunkTarget
{
    public Lua54ChunkTarget(
        Lua54ByteOrder byteOrder,
        byte instructionSize,
        byte integerSize,
        byte numberSize)
    {
        if (instructionSize != 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(instructionSize),
                instructionSize,
                "PUC Lua 5.4 instructions must be 4 bytes.");
        }

        if (integerSize is not (4 or 8))
        {
            throw new ArgumentOutOfRangeException(
                nameof(integerSize),
                integerSize,
                "Only 32-bit and 64-bit lua_Integer layouts are supported.");
        }

        if (numberSize is not (4 or 8))
        {
            throw new ArgumentOutOfRangeException(
                nameof(numberSize),
                numberSize,
                "Only IEEE-754 binary32 and binary64 lua_Number layouts are supported.");
        }

        ByteOrder = byteOrder;
        InstructionSize = instructionSize;
        IntegerSize = integerSize;
        NumberSize = numberSize;
    }

    public Lua54ByteOrder ByteOrder { get; }

    public byte InstructionSize { get; }

    public byte IntegerSize { get; }

    public byte NumberSize { get; }

    public static Lua54ChunkTarget Host { get; } = new(
        BitConverter.IsLittleEndian ? Lua54ByteOrder.LittleEndian : Lua54ByteOrder.BigEndian,
        instructionSize: 4,
        integerSize: 8,
        numberSize: 8);
}
