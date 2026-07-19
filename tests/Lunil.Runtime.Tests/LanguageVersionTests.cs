using System.Buffers.Binary;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua53;
using Lunil.Runtime;
using Lunil.Runtime.Execution;

namespace Lunil.Runtime.Tests;

public sealed class LanguageVersionTests
{
    [Fact]
    public void StatePublishesConfiguredLanguageVersion()
    {
        var state = new LuaState(new LuaStateOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
        });

        Assert.Equal(LuaLanguageVersion.Lua53, state.LanguageVersion);
    }

    [Fact]
    public void Lua53StateLoadsLua53BinaryChunk()
    {
        var state = new LuaState(new LuaStateOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
        });

        var result = new LuaInterpreter().ExecuteBinaryChunk(state, CreateSimpleReturnChunk());

        Assert.Equal(LuaVmSignal.Completed, result.Signal);
        Assert.Equal(42, result.Values[0].AsInteger());
    }

    [Fact]
    public void StateRejectsAClosureFromAnotherLanguageContract()
    {
        var state = new LuaState(new LuaStateOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
        });
        var module = new LuaIrModule
        {
            LanguageVersion = LuaLanguageVersion.Lua54,
            Functions =
            [
                new LuaIrFunction
                {
                    Id = 0,
                    Span = default,
                    RegisterCount = 1,
                    Instructions = [new LuaIrInstruction(LuaIrOpcode.Return, 0, 0)],
                    BasicBlocks = LuaIrControlFlow.Build(
                        [new LuaIrInstruction(LuaIrOpcode.Return, 0, 0)]),
                },
            ],
        };

        var error = Assert.Throws<LuaRuntimeException>(() => state.CreateMainClosure(module));

        Assert.Contains("Lua 5.3", error.Message, StringComparison.Ordinal);
        Assert.Contains("Lua 5.4", error.Message, StringComparison.Ordinal);
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
        WriteSizeT(bytes, 0);
        WriteInt32(bytes, 0);
        WriteInt32(bytes, 0);
        bytes.AddRange([0, 0, 2]);
        WriteInt32(bytes, 2);
        WriteUInt32(bytes, Instruction(Lua53Opcode.LoadConstant, bx: 0));
        WriteUInt32(bytes, Instruction(Lua53Opcode.Return, b: 2));
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
        WriteSizeT(bytes, 5);
        bytes.AddRange("_ENV"u8.ToArray());
        return bytes.ToArray();
    }

    private static uint Instruction(Lua53Opcode opcode, int a = 0, int b = 0, int c = 0, int bx = 0) =>
        (uint)opcode |
        ((uint)a << 6) |
        ((uint)c << 14) |
        ((uint)b << 23) |
        ((uint)bx << 14);

    private static void WriteSizeT(List<byte> bytes, ulong value) => WriteInt64(bytes, checked((long)value));

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
