using System.Collections.Immutable;
using Luac.IR.Lua54;

namespace Luac.IR.Tests.Lua54;

public sealed class Lua54ChunkTests
{
    private const string PucLua548Fixture =
        "G0x1YVQAGZMNChoKBAgIeFYAAAAAAAAAAAAAACh3QAGUQC5jb2RleC9maXh0dXJlLmx1YYCAAAEDhlEAAAADAAAAgYAUgAABAADGAAMBxgABAYEEhmhlbGxvgQEAAICGAQABAAAAgIGIbWVzc2FnZYKGgYVfRU5W";

    [Theory]
    [InlineData(Lua54ByteOrder.LittleEndian, 8, 8)]
    [InlineData(Lua54ByteOrder.BigEndian, 8, 8)]
    [InlineData(Lua54ByteOrder.LittleEndian, 4, 4)]
    public void WriterAndReaderRoundTripSupportedTargets(
        Lua54ByteOrder byteOrder,
        byte integerSize,
        byte numberSize)
    {
        var target = new Lua54ChunkTarget(byteOrder, 4, integerSize, numberSize);
        var prototype = CreatePrototype();
        var chunk = new Lua54Chunk(target, 0, prototype);

        var bytes = Lua54ChunkWriter.Write(chunk);
        var result = Lua54ChunkReader.Read(bytes);

        Assert.Equal(target, result.Target);
        Assert.Equal(0, result.MainUpvalueCount);
        Assert.Equal("@roundtrip.lua", result.MainPrototype.Source?.ToString());
        Assert.Equal(
            prototype.Code.Select(static instruction => instruction.RawValue),
            result.MainPrototype.Code.Select(static instruction => instruction.RawValue));
        Assert.Equal(7L, result.MainPrototype.Constants[0].IntegerValue);
        Assert.Equal(1.5, result.MainPrototype.Constants[1].FloatValue);
        Assert.Equal("value", result.MainPrototype.Constants[2].StringValue?.ToString());
    }

    [Fact]
    public void ReaderAcceptsChunkProducedByPucLua548()
    {
        var bytes = Convert.FromBase64String(PucLua548Fixture);

        var chunk = Lua54ChunkReader.Read(bytes);

        Assert.Equal(Lua54ChunkTarget.Host, chunk.Target);
        Assert.Equal(1, chunk.MainUpvalueCount);
        Assert.EndsWith(".codex/fixture.lua", chunk.MainPrototype.Source?.ToString());
        Assert.Contains(
            chunk.MainPrototype.Code,
            instruction => instruction.Opcode == Lua54Opcode.LoadInteger &&
                           instruction.SignedBx == 42);
        Assert.Contains(
            chunk.MainPrototype.Constants,
            constant => constant.StringValue?.ToString() == "hello");
    }

    [Fact]
    public void StrippingRemovesSourceAndDebugTables()
    {
        var chunk = new Lua54Chunk(Lua54ChunkTarget.Host, 0, CreatePrototype());

        var bytes = Lua54ChunkWriter.Write(chunk, stripDebugInformation: true);
        var result = Lua54ChunkReader.Read(bytes);

        Assert.Null(result.MainPrototype.Source);
        Assert.Empty(result.MainPrototype.LineInfo);
        Assert.Empty(result.MainPrototype.AbsoluteLineInfo);
        Assert.Empty(result.MainPrototype.LocalVariables);
        Assert.Empty(result.MainPrototype.UpvalueNames);
    }

    [Fact]
    public void ReaderRejectsVersionMismatchAtHeader()
    {
        var bytes = Convert.FromBase64String(PucLua548Fixture);
        bytes[4] = 0x53;

        var exception = Assert.Throws<Lua54ChunkFormatException>(() =>
            Lua54ChunkReader.Read(bytes));

        Assert.Contains("version mismatch", exception.Reason);
    }

    [Fact]
    public void ReaderRejectsTrailingDataByDefault()
    {
        var valid = Lua54ChunkWriter.Write(
            new Lua54Chunk(Lua54ChunkTarget.Host, 0, CreatePrototype()));
        var bytes = new byte[valid.Length + 1];
        valid.CopyTo(bytes, 0);

        var exception = Assert.Throws<Lua54ChunkFormatException>(() =>
            Lua54ChunkReader.Read(bytes));

        Assert.Contains("trailing data", exception.Reason);
    }

    [Fact]
    public void ReaderEnforcesAggregateInstructionBudget()
    {
        var bytes = Convert.FromBase64String(PucLua548Fixture);
        var options = Lua54ChunkReaderOptions.Default with { MaximumInstructionCount = 1 };

        var exception = Assert.Throws<Lua54ChunkFormatException>(() =>
            Lua54ChunkReader.Read(bytes, options));

        Assert.Contains("instruction count", exception.Reason);
    }

    [Fact]
    public void VerifierRejectsJumpOutsideFunction()
    {
        var prototype = CreatePrototype() with
        {
            Code =
            [
                Lua54Instruction.CreateSignedJump(Lua54Opcode.Jump, 100),
                Lua54Instruction.CreateAbc(Lua54Opcode.ReturnZero, 0, 0, 0),
            ],
            LineInfo = [0, 0],
        };
        var chunk = new Lua54Chunk(Lua54ChunkTarget.Host, 0, prototype);

        var errors = Lua54ChunkVerifier.Verify(chunk);

        Assert.Contains(errors, error => error.Message.Contains("Jump target", StringComparison.Ordinal));
    }

    private static Lua54Prototype CreatePrototype() => new()
    {
        Source = Lua54String.FromUtf8("@roundtrip.lua"),
        MaximumStackSize = 2,
        Code = [Lua54Instruction.CreateAbc(Lua54Opcode.ReturnZero, 0, 0, 0)],
        Constants =
        [
            Lua54Constant.FromInteger(7),
            Lua54Constant.FromFloat(1.5),
            Lua54Constant.FromString(Lua54String.FromUtf8("value"), isShort: true),
            Lua54Constant.True,
            Lua54Constant.Nil,
        ],
        Upvalues = [],
        NestedPrototypes = [],
        LineInfo = [0],
        AbsoluteLineInfo = [new Lua54AbsoluteLineInfo(0, 1)],
        LocalVariables =
        [
            new Lua54LocalVariable(Lua54String.FromUtf8("x"), 0, 1),
        ],
        UpvalueNames = [],
    };
}
