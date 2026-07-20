namespace Lunil.IR.Lua53;

public enum Lua53ByteOrder : byte
{
    LittleEndian,
    BigEndian,
}

public readonly record struct Lua53ChunkTarget
{
    public Lua53ChunkTarget(
        Lua53ByteOrder byteOrder,
        byte sizeOfInt,
        byte sizeOfSizeT,
        byte instructionSize,
        byte integerSize,
        byte numberSize)
    {
        if (sizeOfInt != 4)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeOfInt), sizeOfInt,
                "Only 32-bit Lua 5.3 int layouts are supported.");
        }

        if (sizeOfSizeT is not (4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(sizeOfSizeT), sizeOfSizeT,
                "Only 32-bit and 64-bit Lua 5.3 size_t layouts are supported.");
        }

        if (instructionSize != 4)
        {
            throw new ArgumentOutOfRangeException(nameof(instructionSize), instructionSize,
                "Lua 5.3 instructions must be 4 bytes.");
        }

        if (integerSize is not (4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(integerSize), integerSize,
                "Only 32-bit and 64-bit lua_Integer layouts are supported.");
        }

        if (numberSize is not (4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(numberSize), numberSize,
                "Only IEEE-754 binary32 and binary64 lua_Number layouts are supported.");
        }

        ByteOrder = byteOrder;
        SizeOfInt = sizeOfInt;
        SizeOfSizeT = sizeOfSizeT;
        InstructionSize = instructionSize;
        IntegerSize = integerSize;
        NumberSize = numberSize;
    }

    public Lua53ByteOrder ByteOrder { get; }
    public byte SizeOfInt { get; }
    public byte SizeOfSizeT { get; }
    public byte InstructionSize { get; }
    public byte IntegerSize { get; }
    public byte NumberSize { get; }

    public static Lua53ChunkTarget Host { get; } = new(
        BitConverter.IsLittleEndian ? Lua53ByteOrder.LittleEndian : Lua53ByteOrder.BigEndian,
        sizeOfInt: 4,
        sizeOfSizeT: checked((byte)IntPtr.Size),
        instructionSize: 4,
        integerSize: 8,
        numberSize: 8);
}
