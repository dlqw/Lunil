using System.Collections.Immutable;
using Luac.IR.Canonical;

namespace Luac.IR.Tests.Canonical;

public sealed class LuaIrVerifierTests
{
    [Fact]
    public void AcceptsAWellFormedFunctionAndReconstructsItsBlocks()
    {
        var code = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, 0, 0),
            new LuaIrInstruction(LuaIrOpcode.JumpIfFalse, 0, 3),
            new LuaIrInstruction(LuaIrOpcode.Jump, b: 0, c: -1),
            new LuaIrInstruction(LuaIrOpcode.Return, 0, 1));
        var module = Module(code, registerCount: 1, [LuaIrConstant.FromBoolean(true)]);

        Assert.Empty(LuaIrVerifier.Verify(module));
        Assert.Equal([0, 2, 3], module.Functions[0].BasicBlocks.Select(static block => block.Start));
    }

    [Fact]
    public void RejectsOutOfRangeRegistersTargetsAndCallWindows()
    {
        var code = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.Call, 0, 2, 2),
            new LuaIrInstruction(LuaIrOpcode.Jump, b: 99, c: -1),
            new LuaIrInstruction(LuaIrOpcode.Return, 0, 0));
        var module = Module(code, registerCount: 2, []);

        var errors = LuaIrVerifier.Verify(module);

        Assert.Contains(errors, error => error.ProgramCounter == 0);
        Assert.Contains(errors, error => error.ProgramCounter == 1);
    }

    [Fact]
    public void RejectsForgedBasicBlocksAndInvalidCaptureSources()
    {
        var parentCode = ImmutableArray.Create(new LuaIrInstruction(LuaIrOpcode.Return, 0, 0));
        var childCode = ImmutableArray.Create(new LuaIrInstruction(LuaIrOpcode.Return, 0, 0));
        var parent = new LuaIrFunction
        {
            Id = 0,
            Span = default,
            RegisterCount = 1,
            Instructions = parentCode,
            BasicBlocks = LuaIrControlFlow.Build(parentCode),
        };
        var child = new LuaIrFunction
        {
            Id = 1,
            Span = default,
            ParentFunctionId = 0,
            RegisterCount = 1,
            Upvalues = [new LuaIrUpvalue("x", 1, LuaIrUpvalueSourceKind.Register, 9)],
            Instructions = childCode,
            BasicBlocks = [new LuaIrBasicBlock(0, 1, [0])],
        };
        var module = new LuaIrModule { Functions = [parent, child] };

        var errors = LuaIrVerifier.Verify(module);

        Assert.Contains(errors, error => error.Message.Contains("invalid source", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Message.Contains("Basic blocks", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(97)]
    [InlineData(65537)]
    public void RandomMalformedModulesProduceDiagnosticsWithoutThrowing(int seed)
    {
        var random = new Random(seed);
        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            var functionCount = random.Next(1, 8);
            var functions = new LuaIrFunction[functionCount];
            for (var functionIndex = 0; functionIndex < functionCount; functionIndex++)
            {
                var instructionCount = random.Next(1, 33);
                var instructions = Enumerable.Range(0, instructionCount)
                    .Select(_ => new LuaIrInstruction(
                        (LuaIrOpcode)random.Next(0, 256),
                        random.Next(-4, 40),
                        random.Next(-4, 40),
                        random.Next(-4, 40),
                        random.Next(-4, 40)))
                    .ToImmutableArray();
                functions[functionIndex] = new LuaIrFunction
                {
                    Id = random.Next(-2, functionCount + 2),
                    ParentFunctionId = random.Next(-2, functionCount + 3),
                    Span = default,
                    ParameterCount = random.Next(-2, 8),
                    RegisterCount = random.Next(-2, 32),
                    Upvalues = random.Next(3) == 0
                        ? [new LuaIrUpvalue("x", 1, (LuaIrUpvalueSourceKind)random.Next(0, 8),
                            random.Next(-3, 40))]
                        : [],
                    Instructions = instructions,
                    BasicBlocks = random.Next(2) == 0
                        ? LuaIrControlFlow.Build(instructions)
                        : [new LuaIrBasicBlock(random.Next(-2, 10), random.Next(-2, 10), default)],
                };
            }

            var module = new LuaIrModule
            {
                FormatVersion = random.Next(0, 5),
                MainFunctionId = random.Next(-2, functionCount + 2),
                Functions = functions.ToImmutableArray(),
            };

            _ = LuaIrVerifier.Verify(module);
        }
    }

    [Fact]
    public void RejectsDefaultArraysAndNullFunctionEntriesWithoutThrowing()
    {
        var defaultFunctions = new LuaIrModule { Functions = default };
        var nullFunction = new LuaIrModule
        {
            Functions = ImmutableArray.CreateRange(new LuaIrFunction[] { null! }),
        };

        Assert.NotEmpty(LuaIrVerifier.Verify(defaultFunctions));
        Assert.Contains(
            LuaIrVerifier.Verify(nullFunction),
            static error => error.Message.Contains("null", StringComparison.Ordinal));
    }

    private static LuaIrModule Module(
        ImmutableArray<LuaIrInstruction> code,
        int registerCount,
        ImmutableArray<LuaIrConstant> constants)
    {
        var function = new LuaIrFunction
        {
            Id = 0,
            Span = default,
            RegisterCount = registerCount,
            Constants = constants,
            Upvalues = [new LuaIrUpvalue("_ENV", 0, LuaIrUpvalueSourceKind.Environment, 0)],
            Instructions = code,
            BasicBlocks = LuaIrControlFlow.Build(code),
        };
        return new LuaIrModule { Functions = [function] };
    }
}
