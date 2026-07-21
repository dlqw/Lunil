using System.Buffers.Binary;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua53;

namespace Lunil.IR.Tests;

public sealed class Lua53LanguageTests
{
    [Fact]
    public void GeneratedLua53InstructionCodecRoundTripsEveryLayoutBoundary()
    {
        var abc = Lua53Instruction.CreateAbc(
            Lua53Opcode.SetTable,
            Lua53Instruction.MaximumA,
            Lua53Instruction.MaximumB,
            Lua53Instruction.MaximumC);
        var abx = Lua53Instruction.CreateABx(
            Lua53Opcode.LoadConstant,
            Lua53Instruction.MaximumA,
            Lua53Instruction.MaximumBx);
        var signed = Lua53Instruction.CreateASignedBx(
            Lua53Opcode.Jump,
            Lua53Instruction.MaximumA,
            -Lua53Instruction.SignedBxOffset);
        var ax = Lua53Instruction.CreateAx(
            Lua53Opcode.ExtraArgument,
            Lua53Instruction.MaximumAx);

        Assert.Equal(Lua53Opcode.SetTable, abc.Opcode);
        Assert.Equal(Lua53Instruction.MaximumA, abc.A);
        Assert.Equal(Lua53Instruction.MaximumB, abc.B);
        Assert.Equal(Lua53Instruction.MaximumC, abc.C);
        Assert.Equal(Lua53Instruction.MaximumBx, abx.Bx);
        Assert.Equal(-Lua53Instruction.SignedBxOffset, signed.SignedBx);
        Assert.Equal(Lua53Instruction.MaximumAx, ax.Ax);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Lua53Instruction.CreateAbc(Lua53Opcode.Move, Lua53Instruction.MaximumA + 1, 0, 0));
    }

    [Fact]
    public void Lua53ChunkWriterRoundTripsReaderModel()
    {
        var original = Lua53ChunkReader.Read(CreateSimpleReturnChunk());
        var bytes = Lua53ChunkWriter.Write(original);
        var roundTrip = Lua53ChunkReader.Read(bytes);

        Assert.Equal(original.Target, roundTrip.Target);
        Assert.Equal(original.MainUpvalueCount, roundTrip.MainUpvalueCount);
        Assert.True(
            original.MainPrototype.Code.Select(static instruction => instruction.RawValue)
                .SequenceEqual(roundTrip.MainPrototype.Code.Select(static instruction => instruction.RawValue)));
        Assert.Equal(
            original.MainPrototype.Constants.Select(static constant => constant.Kind),
            roundTrip.MainPrototype.Constants.Select(static constant => constant.Kind));
        Assert.Equal(
            original.MainPrototype.Constants.Select(static constant => constant.IntegerValue),
            roundTrip.MainPrototype.Constants.Select(static constant => constant.IntegerValue));
    }

    [Fact]
    public void Lua53ChunkWriterUsesCanonicalStringSizeEncodingForLongStrings()
    {
        var longString = new Lua53String(Enumerable.Repeat((byte)'x', 300).ToArray());
        var original = new Lua53Chunk(
            Lua53ChunkTarget.Host,
            0,
            new Lua53Prototype
            {
                Source = longString,
                MaximumStackSize = 1,
                Code = [Lua53Instruction.CreateAbc(Lua53Opcode.Return, 0, 1, 0)],
                Constants = [Lua53Constant.FromString(longString, isShort: false)],
                Upvalues = [],
                NestedPrototypes = [],
                LineInfo = [1],
                LocalVariables = [],
                UpvalueNames = [],
            });

        var roundTrip = Lua53ChunkReader.Read(Lua53ChunkWriter.Write(original));

        Assert.Equal(300, roundTrip.MainPrototype.Source!.Value.Length);
        Assert.Equal(300, roundTrip.MainPrototype.Constants[0].StringValue!.Value.Length);
    }

    [Fact]
    public void Lua53TableHintsDecodeFromFloatingByteFields()
    {
        var chunk = new Lua53Chunk(
            Lua53ChunkTarget.Host,
            0,
            new Lua53Prototype
            {
                MaximumStackSize = 1,
                Code =
                [
                    Lua53Instruction.CreateAbc(Lua53Opcode.NewTable, 0, 34, 12),
                    Lua53Instruction.CreateAbc(Lua53Opcode.Return, 0, 1, 0),
                ],
                Constants = [],
                Upvalues = [],
                NestedPrototypes = [],
                LineInfo = [1, 1],
                LocalVariables = [],
                UpvalueNames = [],
            });

        var module = Lua53PrototypeConverter.Convert(chunk);
        var table = module.Functions[0].Instructions[0];

        Assert.Equal(LuaIrOpcode.NewTable, table.Opcode);
        Assert.Equal(5, table.B);
        Assert.Equal(80, table.C);
    }

    [Fact]
    public void ReadsAndConvertsLua53SimpleReturnChunk()
    {
        var chunk = Lua53ChunkReader.Read(CreateSimpleReturnChunk());
        var module = Lua53PrototypeConverter.Convert(chunk);

        Assert.Equal(LuaLanguageVersion.Lua53, module.LanguageVersion);
        Assert.Single(module.Functions);
        Assert.Equal(LuaIrOpcode.LoadConstant, module.Functions[0].Instructions[0].Opcode);
        Assert.Equal(LuaIrOpcode.Return, module.Functions[0].Instructions[1].Opcode);
        Assert.Equal(42, module.Functions[0].Constants[0].Integer);
    }

    [Fact]
    public void MainChunkOnlyTreatsItsFirstUpvalueAsTheEnvironment()
    {
        var module = Lua53PrototypeConverter.Convert(new Lua53Chunk(
            Lua53ChunkTarget.Host,
            2,
            new Lua53Prototype
            {
                MaximumStackSize = 1,
                Code = [Lua53Instruction.CreateAbc(Lua53Opcode.Return, 0, 1, 0)],
                Constants = [],
                Upvalues =
                [
                    new Lua53UpvalueDescriptor(1, 4),
                    new Lua53UpvalueDescriptor(0, 7),
                ],
                NestedPrototypes = [],
                LineInfo = [1],
                LocalVariables = [],
                UpvalueNames = [],
            }));

        var upvalues = module.Functions[0].Upvalues;
        Assert.Equal(LuaIrUpvalueSourceKind.Environment, upvalues[0].SourceKind);
        Assert.Equal(LuaIrUpvalueSourceKind.Upvalue, upvalues[1].SourceKind);
    }

    private static byte[] CreateSimpleReturnChunk()
    {
        var bytes = new List<byte>();
        bytes.AddRange([0x1b, (byte)'L', (byte)'u', (byte)'a', 0x53, 0]);
        bytes.AddRange([0x19, 0x93, (byte)'\r', (byte)'\n', 0x1a, (byte)'\n']);
        bytes.AddRange([4, 8, 4, 8, 8]);
        WriteInt64(bytes, 0x5678);
        WriteInt64(bytes, BitConverter.DoubleToInt64Bits(370.5));
        bytes.Add(1);

        bytes.Add(0);
        WriteInt32(bytes, 0);
        WriteInt32(bytes, 0);
        bytes.AddRange([0, 0, 2]);
        WriteInt32(bytes, 2);
        WriteUInt32(bytes, Instruction(Lua53Opcode.LoadConstant, a: 0, bx: 0));
        WriteUInt32(bytes, Instruction(Lua53Opcode.Return, a: 0, b: 2));
        WriteInt32(bytes, 1);
        bytes.Add(19);
        WriteInt64(bytes, 42);
        WriteInt32(bytes, 1);
        bytes.AddRange([1, 0]);
        WriteInt32(bytes, 0);
        WriteInt32(bytes, 2);
        WriteInt32(bytes, 1);
        WriteInt32(bytes, 1);
        WriteInt32(bytes, 0);
        WriteInt32(bytes, 1);
        bytes.Add(5);
        bytes.AddRange("_ENV"u8.ToArray());
        return bytes.ToArray();
    }

    private static uint Instruction(Lua53Opcode opcode, int a = 0, int b = 0, int c = 0, int bx = 0) =>
        (uint)opcode |
        ((uint)a << 6) |
        ((uint)c << 14) |
        ((uint)b << 23) |
        ((uint)bx << 14);

    private static void WriteInt32(List<byte> bytes, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }

    private static void WriteUInt32(List<byte> bytes, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }

    private static void WriteInt64(List<byte> bytes, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }
}
