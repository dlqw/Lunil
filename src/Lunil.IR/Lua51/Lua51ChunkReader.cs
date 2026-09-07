using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Collections.Generic;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua53;

#pragma warning disable CA1720

namespace Lunil.IR.Lua51;

public static class Lua51ChunkReader
{
    public static Lua51Chunk Read(ReadOnlySpan<byte> data, Lua51ChunkReaderOptions? options = null)
    {
        options ??= Lua51ChunkReaderOptions.Default;
        if (data.Length > options.MaximumChunkBytes)
            throw new Lua51ChunkFormatException("chunk exceeds configured size limit");
        var reader = new Reader(data, options);
        return reader.ReadChunk();
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly Lua51ChunkReaderOptions _options;
        private int _offset, _prototypes, _instructions, _constants, _strings, _debug;
        private Lua51ChunkTarget _target;
        public Reader(ReadOnlySpan<byte> data, Lua51ChunkReaderOptions options) { _data = data; _options = options; }
        public Lua51Chunk ReadChunk()
        {
            Expect([0x1b, (byte)'L', (byte)'u', (byte)'a']); ExpectByte(0x51); ExpectByte(0);
            var endian = ReadByte();
            if (endian is not (0 or 1)) Fail("invalid endianness marker");
            var si = ReadByte(); var ss = ReadByte(); var inst = ReadByte(); var num = ReadByte(); var integral = ReadByte();
            if (si != 4 || ss is not (4 or 8) || inst != 4 || num is not (4 or 8) || integral != 0)
                Fail("unsupported scalar layout");
            _target = new(endian == 1 ? Lua51ByteOrder.LittleEndian : Lua51ByteOrder.BigEndian, si, ss, inst, num);
            var main = ReadPrototype(null, 1);
            if (!_options.AllowTrailingData && _offset != _data.Length) Fail("trailing data after main prototype");
            return new(_target, main);
        }
        private Lua51Prototype ReadPrototype(Lua51String? parentSource, int depth)
        {
            if (depth > _options.MaximumPrototypeDepth) Fail("prototype nesting exceeds limit");
            Add(ref _prototypes, 1, _options.MaximumPrototypeCount, "prototype count");
            var source = ReadString() ?? parentSource;
            var ld = ReadInt(); var lld = ReadInt(); var nups = ReadByte(); var pars = ReadByte();
            var vararg = ReadByte(); var stack = ReadByte();
            var codeCount = ReadInt(); Add(ref _instructions, codeCount, _options.MaximumInstructionCount, "instruction count");
            Ensure(codeCount, 4); var code = ImmutableArray.CreateBuilder<Lua51Instruction>(codeCount);
            for (var i = 0; i < codeCount; i++) code.Add(new(ReadUInt()));
            var constantCount = ReadInt(); Add(ref _constants, constantCount, _options.MaximumConstantCount, "constant count"); Ensure(constantCount, 1);
            var constants = ImmutableArray.CreateBuilder<Lua51Constant>(constantCount);
            for (var i = 0; i < constantCount; i++) constants.Add(ReadConstant());
            var nestedCount = ReadInt(); Ensure(nestedCount, 2); var nested = ImmutableArray.CreateBuilder<Lua51Prototype>(nestedCount);
            for (var i = 0; i < nestedCount; i++) nested.Add(ReadPrototype(source, depth + 1));
            var lineCount = ReadInt(); Add(ref _debug, lineCount, _options.MaximumDebugEntryCount, "debug entry count"); Ensure(lineCount, 4);
            var lines = ImmutableArray.CreateBuilder<int>(lineCount); for (var i = 0; i < lineCount; i++) lines.Add(ReadSignedInt());
            var localCount = ReadInt(); Add(ref _debug, localCount, _options.MaximumDebugEntryCount, "debug entry count"); Ensure(localCount, 1);
            var locals = ImmutableArray.CreateBuilder<Lua51LocalVariable>(localCount);
            for (var i = 0; i < localCount; i++) locals.Add(new(ReadString(), ReadInt(), ReadInt()));
            var upvalueNameCount = ReadInt(); Add(ref _debug, upvalueNameCount, _options.MaximumDebugEntryCount, "debug entry count"); Ensure(upvalueNameCount, 1);
            var names = ImmutableArray.CreateBuilder<Lua51String?>(upvalueNameCount); for (var i = 0; i < upvalueNameCount; i++) names.Add(ReadString());
            return new()
            {
                Source = source,
                LineDefined = ld,
                LastLineDefined = lld,
                UpvalueCount = nups,
                ParameterCount = pars,
                VarArgFlags = vararg,
                MaximumStackSize = stack,
                Code = code.MoveToImmutable(),
                Constants = constants.MoveToImmutable(),
                NestedPrototypes = nested.MoveToImmutable(),
                LineInfo = lines.MoveToImmutable(),
                LocalVariables = locals.MoveToImmutable(),
                UpvalueNames = names.MoveToImmutable()
            };
        }
        private Lua51Constant ReadConstant() => ReadByte() switch
        {
            0 => Lua51Constant.Nil,
            1 => Lua51Constant.FromBoolean(ReadByte() != 0),
            3 => Lua51Constant.FromNumber(ReadNumber()),
            4 => Lua51Constant.FromString(ReadString() ?? Fail<Lua51String>("null string constant")),
            var tag => Fail<Lua51Constant>($"unknown constant tag {tag}"),
        };
        private Lua51String? ReadString()
        {
            var size = ReadSizeT(); if (size == 0) return null; if (size > int.MaxValue) Fail("string too large");
            var bytes = ReadBytes((int)size); Add(ref _strings, checked((int)size - 1), _options.MaximumStringBytes, "string bytes");
            return new(bytes[..^1].ToArray());
        }
        private int ReadInt() { var value = ReadSignedInt(); if (value < 0) Fail("negative integer"); return value; }
        private int ReadSignedInt() => _target.ByteOrder == Lua51ByteOrder.LittleEndian ? BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(4)) : BinaryPrimitives.ReadInt32BigEndian(ReadBytes(4));
        private uint ReadUInt() => _target.ByteOrder == Lua51ByteOrder.LittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4)) : BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4));
        private ulong ReadSizeT() => _target.SizeOfSizeT == 4 ? (_target.ByteOrder == Lua51ByteOrder.LittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4)) : BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4))) : (_target.ByteOrder == Lua51ByteOrder.LittleEndian ? BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(8)) : BinaryPrimitives.ReadUInt64BigEndian(ReadBytes(8)));
        private double ReadNumber() { var bytes = ReadBytes(_target.NumberSize); return bytes.Length == 4 ? (_target.ByteOrder == Lua51ByteOrder.LittleEndian ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)) : BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes))) : (_target.ByteOrder == Lua51ByteOrder.LittleEndian ? BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes)) : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(bytes))); }
        private byte ReadByte() { if ((uint)_offset >= (uint)_data.Length) Fail("unexpected end of chunk"); return _data[_offset++]; }
        private ReadOnlySpan<byte> ReadBytes(int count) { if (count < 0 || count > _data.Length - _offset) Fail("truncated chunk"); var result = _data.Slice(_offset, count); _offset += count; return result; }
        private void Expect(ReadOnlySpan<byte> value) { if (!ReadBytes(value.Length).SequenceEqual(value)) Fail("not a binary chunk"); }
        private void ExpectByte(byte value) { if (ReadByte() != value) Fail("version or format mismatch"); }
        private void Ensure(int count, int minimum) { if (count < 0 || count > (_data.Length - _offset) / minimum) Fail("invalid count"); }
        private static void Add(ref int current, int amount, int max, string name) { if (amount < 0 || current > max - amount) throw new Lua51ChunkFormatException($"{name} exceeds configured limit"); current += amount; }
        private void Fail(string reason) => throw new Lua51ChunkFormatException(reason, _offset);
        private T Fail<T>(string reason) => throw new Lua51ChunkFormatException(reason, _offset);
    }
}
