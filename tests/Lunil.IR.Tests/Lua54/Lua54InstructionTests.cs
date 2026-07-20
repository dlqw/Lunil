using Lunil.IR.Lua54;

namespace Lunil.IR.Tests.Lua54;

public sealed class Lua54InstructionTests
{
    [Fact]
    public void OpcodeValuesMatchPucLua54Order()
    {
        Assert.Equal(83, Lua54OpcodeInfo.PucOpcodeCount);
        Assert.Equal(0, (int)Lua54Opcode.Move);
        Assert.Equal(56, (int)Lua54Opcode.Jump);
        Assert.Equal(82, (int)Lua54Opcode.ExtraArgument);
        Assert.False(Lua54OpcodeInfo.Get(Lua54Opcode.VarArgPrepare).SetsRegisterA);
    }

    [Fact]
    public void AbcLayoutMatchesPucLua54BitPositions()
    {
        var instruction = Lua54Instruction.CreateAbc(
            Lua54Opcode.Add,
            a: 1,
            b: 2,
            c: 3,
            k: true);

        Assert.Equal(Lua54Opcode.Add, instruction.Opcode);
        Assert.Equal(1, instruction.A);
        Assert.Equal(2, instruction.B);
        Assert.Equal(3, instruction.C);
        Assert.True(instruction.K);
        Assert.Equal(
            (uint)Lua54Opcode.Add | (1u << 7) | (1u << 15) | (2u << 16) | (3u << 24),
            instruction.RawValue);
    }

    [Theory]
    [InlineData(-65535)]
    [InlineData(0)]
    [InlineData(65536)]
    public void SignedBxRoundTrips(int value)
    {
        var instruction = Lua54Instruction.CreateASignedBx(Lua54Opcode.LoadInteger, 7, value);

        Assert.Equal(7, instruction.A);
        Assert.Equal(value, instruction.SignedBx);
    }

    [Theory]
    [InlineData(-16777215)]
    [InlineData(0)]
    [InlineData(16777216)]
    public void SignedJumpRoundTrips(int value)
    {
        var instruction = Lua54Instruction.CreateSignedJump(Lua54Opcode.Jump, value);

        Assert.Equal(value, instruction.SignedJump);
    }

    [Fact]
    public void ConstructorRejectsWrongInstructionMode()
    {
        Assert.Throws<ArgumentException>(() =>
            Lua54Instruction.CreateAbc(Lua54Opcode.LoadConstant, 0, 0, 0));
    }
}
