using System.Buffers.Binary;

namespace Lunil.IR;

/// <summary>
/// Single byte-level parsing core shared by every version-specific binary chunk reader.
/// It owns the cursor and the primitives (byte/word reads, signature expectations,
/// truncation failures) that the per-version readers previously cloned byte for byte.
/// Version logic — headers, instruction layouts, constant tags, and the per-version
/// budget semantics — stays in the version readers, which supply the version-specific
/// failure factory.
/// </summary>
internal ref struct ChunkByteReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly Func<string, int, LuaChunkFormatException> _createFailure;
    private int _offset;
    private bool _littleEndian;

    public ChunkByteReader(
        ReadOnlySpan<byte> data,
        Func<string, int, LuaChunkFormatException> createFailure)
    {
        _data = data;
        _createFailure = createFailure;
    }

    public readonly int Offset => _offset;

    public readonly int BytesRemaining => _data.Length - _offset;

    public readonly bool AtEnd => _offset == _data.Length;

    /// <summary>Sets the chunk byte order once the header has been decoded.</summary>
    public void SetLittleEndian(bool littleEndian) => _littleEndian = littleEndian;

    // ---- byte-order-aware scalar reads -------------------------------------------------

    public int ReadSignedInt32() => _littleEndian
        ? BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(4))
        : BinaryPrimitives.ReadInt32BigEndian(ReadBytes(4));

    public uint ReadUnsignedInt32() => _littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4))
        : BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4));

    public ulong ReadUnsignedInt64() => _littleEndian
        ? BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(8))
        : BinaryPrimitives.ReadUInt64BigEndian(ReadBytes(8));

    /// <summary>Reads a four-byte count that cannot be negative.</summary>
    public int ReadNonNegativeInt32(string description)
    {
        var value = ReadSignedInt32();
        if (value < 0)
        {
            Fail($"{description} cannot be negative");
        }

        return value;
    }

    // ---- cursor primitives -------------------------------------------------------------

    public byte ReadByte()
    {
        if ((uint)_offset >= (uint)_data.Length)
        {
            Fail("truncated chunk");
        }

        return _data[_offset++];
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if (count < 0 || count > _data.Length - _offset)
        {
            Fail("truncated chunk");
        }

        var result = _data.Slice(_offset, count);
        _offset += count;
        return result;
    }

    public void Expect(ReadOnlySpan<byte> expected, string reason)
    {
        var offset = _offset;
        if (!ReadBytes(expected.Length).SequenceEqual(expected))
        {
            throw _createFailure(reason, offset);
        }
    }

    public void ExpectByte(byte expected, string reason)
    {
        var offset = _offset;
        if (ReadByte() != expected)
        {
            throw _createFailure(reason, offset);
        }
    }

    public void EnsureCountFitsRemaining(int count, int minimumBytesPerEntry, string description)
    {
        if (count < 0 || count > (_data.Length - _offset) / minimumBytesPerEntry)
        {
            Fail($"truncated chunk: {description} cannot fit in the remaining chunk data");
        }
    }

    /// <summary>Fails when a prototype nesting depth exceeds its configured maximum.</summary>
    public readonly void FailIfDepthExceeds(int depth, int maximumDepth)
    {
        if (depth > maximumDepth)
        {
            Fail("prototype nesting exceeds the configured limit");
        }
    }

    public readonly void Fail(string reason) => throw _createFailure(reason, _offset);

    public readonly T Fail<T>(string reason) => throw _createFailure(reason, _offset);
}
