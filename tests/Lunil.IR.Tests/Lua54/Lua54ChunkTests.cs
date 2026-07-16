using System.Collections.Immutable;
using System.Diagnostics;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;

namespace Lunil.IR.Tests.Lua54;

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
    public void WriterUsesNumericTagsAcceptedByPucLua548()
    {
        if (!IsPucLuaAvailable())
        {
            return;
        }

        var prototype = CreatePrototype() with
        {
            Code =
            [
                Lua54Instruction.CreateABx(Lua54Opcode.LoadConstant, 0, 0),
                Lua54Instruction.CreateABx(Lua54Opcode.LoadConstant, 1, 1),
                Lua54Instruction.CreateAbc(Lua54Opcode.Return, 0, 3, 0),
            ],
            Constants = [Lua54Constant.FromInteger(7), Lua54Constant.FromFloat(1.5)],
            LineInfo = [],
            AbsoluteLineInfo = [],
            LocalVariables = [],
        };
        var path = Path.Combine(Path.GetTempPath(), $"luac-writer-{Guid.NewGuid():N}.luac");
        try
        {
            File.WriteAllBytes(
                path,
                Lua54ChunkWriter.Write(new Lua54Chunk(Lua54ChunkTarget.Host, 0, prototype)));
            var startInfo = new ProcessStartInfo
            {
                FileName = "lua",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(
                $"local f=assert(loadfile([==[{path}]==], 'b')); " +
                "local a,b=f(); print(math.type(a), b)");
            using var process = Process.Start(startInfo) ??
                throw new InvalidOperationException("Could not start PUC Lua.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
            Assert.Equal("integer\t1.5", output.Trim());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CanonicalWriterRecreatesPucExecutableChunkAndStripsDebugInformation()
    {
        var module = Lua54PrototypeConverter.Convert(
            Convert.FromBase64String(PucLua548Fixture));
        var path = Path.Combine(Path.GetTempPath(), $"canonical-writer-{Guid.NewGuid():N}.luac");
        try
        {
            var bytes = Lua54CanonicalPrototypeWriter.Write(
                module,
                module.MainFunctionId,
                stripDebugInformation: true);
            var chunk = Lua54ChunkReader.Read(bytes);
            Assert.Null(chunk.MainPrototype.Source);
            Assert.Empty(chunk.MainPrototype.LineInfo);
            Assert.Empty(chunk.MainPrototype.LocalVariables);

            if (!IsPucLuaAvailable())
            {
                return;
            }

            File.WriteAllBytes(path, bytes);
            var startInfo = new ProcessStartInfo
            {
                FileName = "lua",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(
                $"local f=assert(loadfile([==[{path}]==], 'b')); print(f())");
            using var process = Process.Start(startInfo) ??
                throw new InvalidOperationException("Could not start PUC Lua.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
            Assert.Equal("42\thello", output.Trim());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PrototypeConverterExpandsSetListBlocksToCanonicalArrayIndexes()
    {
        var prototype = new Lua54Prototype
        {
            MaximumStackSize = 52,
            Code =
            [
                Lua54Instruction.CreateAbc(Lua54Opcode.NewTable, 0, 0, 51),
                Lua54Instruction.CreateAx(Lua54Opcode.ExtraArgument, 0),
                Lua54Instruction.CreateAbc(Lua54Opcode.SetList, 0, 50, 0),
                Lua54Instruction.CreateAbc(Lua54Opcode.SetList, 0, 1, 1),
                Lua54Instruction.CreateAbc(Lua54Opcode.ReturnOne, 0, 0, 0),
            ],
        };
        var module = Lua54PrototypeConverter.Convert(
            new Lua54Chunk(Lua54ChunkTarget.Host, 0, prototype));

        var setLists = module.Functions[0].Instructions
            .Where(static instruction => instruction.Opcode == LuaIrOpcode.SetList)
            .ToArray();

        Assert.Equal(1, setLists[0].B);
        Assert.Equal(51, setLists[1].B);
    }

    [Fact]
    public void PrototypeConverterDoesNotCloseUpvaluesForPlainLua54Jumps()
    {
        var prototype = new Lua54Prototype
        {
            MaximumStackSize = 2,
            Code =
            [
                Lua54Instruction.CreateSignedJump(Lua54Opcode.Jump, 0),
                Lua54Instruction.CreateAbc(Lua54Opcode.ReturnZero, 0, 0, 0),
            ],
        };
        var module = Lua54PrototypeConverter.Convert(
            new Lua54Chunk(Lua54ChunkTarget.Host, 0, prototype));

        var jump = Assert.Single(
            module.Functions[0].Instructions,
            static instruction => instruction.Opcode == LuaIrOpcode.Jump);

        Assert.Equal(-1, jump.C);
    }

    [Fact]
    public void CanonicalWriterPreservesMissingUpvalueDebugNames()
    {
        var prototype = new Lua54Prototype
        {
            MaximumStackSize = 2,
            Upvalues = [new Lua54UpvalueDescriptor(0, 0, 0)],
            Code = [Lua54Instruction.CreateAbc(Lua54Opcode.ReturnZero, 0, 0, 0)],
            UpvalueNames = [],
        };
        var module = Lua54PrototypeConverter.Convert(
            new Lua54Chunk(Lua54ChunkTarget.Host, 1, prototype));

        var bytes = Lua54CanonicalPrototypeWriter.Write(
            module,
            module.MainFunctionId,
            stripDebugInformation: false);
        var roundTripped = Lua54ChunkReader.Read(bytes);

        Assert.Single(roundTripped.MainPrototype.UpvalueNames);
        Assert.Null(roundTripped.MainPrototype.UpvalueNames[0]);
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

    [Fact]
    public void VerifierRejectsUnpairedMetamethodAndOutOfRangeRegisters()
    {
        var prototype = CreatePrototype() with
        {
            Code =
            [
                Lua54Instruction.CreateAbc(Lua54Opcode.MetamethodBinary, 0, 1, 6),
                Lua54Instruction.CreateAbc(Lua54Opcode.Move, 0, 2, 0),
                Lua54Instruction.CreateAbc(Lua54Opcode.ReturnZero, 0, 0, 0),
            ],
            LineInfo = [],
            AbsoluteLineInfo = [],
            LocalVariables = [],
        };

        var errors = Lua54ChunkVerifier.Verify(
            new Lua54Chunk(Lua54ChunkTarget.Host, 0, prototype));

        Assert.Contains(errors, error => error.Message.Contains("MMBIN", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Message.Contains("MOVE", StringComparison.Ordinal));
    }

    [Fact]
    public void VerifierRejectsControlFlowIntoAssociatedInstruction()
    {
        var prototype = CreatePrototype() with
        {
            Code =
            [
                Lua54Instruction.CreateSignedJump(Lua54Opcode.Jump, 1),
                Lua54Instruction.CreateAbc(Lua54Opcode.NewTable, 0, 0, 0),
                Lua54Instruction.CreateAx(Lua54Opcode.ExtraArgument, 0),
                Lua54Instruction.CreateAbc(Lua54Opcode.ReturnZero, 0, 0, 0),
            ],
            LineInfo = [],
            AbsoluteLineInfo = [],
            LocalVariables = [],
        };

        var errors = Lua54ChunkVerifier.Verify(
            new Lua54Chunk(Lua54ChunkTarget.Host, 0, prototype));

        Assert.Contains(
            errors,
            error => error.Message.Contains("associated instruction", StringComparison.Ordinal));
    }

    [Fact]
    public void VerifierRejectsInconsistentToBeClosedControlFlow()
    {
        var prototype = CreatePrototype() with
        {
            Code =
            [
                Lua54Instruction.CreateAbc(Lua54Opcode.Test, 0, 0, 0, k: true),
                Lua54Instruction.CreateSignedJump(Lua54Opcode.Jump, 2),
                Lua54Instruction.CreateAbc(Lua54Opcode.ToBeClosed, 1, 0, 0),
                Lua54Instruction.CreateSignedJump(Lua54Opcode.Jump, 1),
                Lua54Instruction.CreateAbc(Lua54Opcode.Move, 0, 0, 0),
                Lua54Instruction.CreateAbc(Lua54Opcode.ReturnZero, 0, 0, 0),
            ],
            LineInfo = [],
            AbsoluteLineInfo = [],
            LocalVariables = [],
        };

        var errors = Lua54ChunkVerifier.Verify(
            new Lua54Chunk(Lua54ChunkTarget.Host, 0, prototype));

        Assert.Contains(
            errors,
            error => error.Message.Contains("to-be-closed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CanonicalWriterAndConverterPreserveFlippedConstantArithmeticOperands()
    {
        var module = CreateConstantLeftArithmeticModule(LuaIrConstant.FromFloat(5.25));

        var chunk = Lua54CanonicalPrototypeWriter.CreateChunk(module, module.MainFunctionId);

        var arithmetic = Assert.Single(
            chunk.MainPrototype.Code,
            static instruction => instruction.Opcode == Lua54Opcode.AddConstant);
        var metamethod = Assert.Single(
            chunk.MainPrototype.Code,
            static instruction => instruction.Opcode == Lua54Opcode.MetamethodBinaryConstant);
        Assert.True(metamethod.K);
        Assert.Empty(Lua54ChunkVerifier.Verify(chunk));

        var converted = Lua54PrototypeConverter.Convert(chunk).Functions[0];
        var load = Assert.Single(
            converted.Instructions,
            static instruction => instruction.Opcode == LuaIrOpcode.LoadConstant);
        var binary = Assert.Single(
            converted.Instructions,
            static instruction => instruction.Opcode == LuaIrOpcode.Binary);
        Assert.Equal(arithmetic.A, binary.A);
        Assert.Equal(load.A, binary.B);
        Assert.Equal(0, binary.C);
    }

    [Fact]
    public void VerifierAndConverterAcceptFlippedAddImmediateOperands()
    {
        var module = CreateConstantLeftArithmeticModule(LuaIrConstant.FromInteger(5));

        var chunk = Lua54CanonicalPrototypeWriter.CreateChunk(module, module.MainFunctionId);

        Assert.Contains(
            chunk.MainPrototype.Code,
            static instruction => instruction.Opcode == Lua54Opcode.AddImmediate);
        var metamethod = Assert.Single(
            chunk.MainPrototype.Code,
            static instruction => instruction.Opcode == Lua54Opcode.MetamethodBinaryImmediate);
        Assert.True(metamethod.K);
        Assert.Empty(Lua54ChunkVerifier.Verify(chunk));

        var converted = Lua54PrototypeConverter.Convert(chunk).Functions[0];
        var load = Assert.Single(
            converted.Instructions,
            static instruction => instruction.Opcode == LuaIrOpcode.LoadConstant);
        var binary = Assert.Single(
            converted.Instructions,
            static instruction => instruction.Opcode == LuaIrOpcode.Binary);
        Assert.Equal(load.A, binary.B);
        Assert.Equal(0, binary.C);
    }

    [Fact]
    public void CanonicalWriterDoesNotAliasComparisonMaterializationWithPreservedOperand()
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.Move, 1, 0, sourceLine: 1),
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, 0, 0, sourceLine: 1),
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                0,
                0,
                1,
                (int)LuaIrBinaryOperator.LessThan,
                sourceLine: 1),
            new LuaIrInstruction(LuaIrOpcode.JumpIfTrue, 0, 5, sourceLine: 1),
            new LuaIrInstruction(LuaIrOpcode.Return, 0, 0, sourceLine: 1),
            new LuaIrInstruction(LuaIrOpcode.Return, 0, 0, sourceLine: 1));
        var function = CreateIrFunction(
            0,
            registerCount: 2,
            instructions,
            constants: [LuaIrConstant.FromInteger(7)]);
        var module = new LuaIrModule { Functions = [function] };

        var chunk = Lua54CanonicalPrototypeWriter.CreateChunk(module, 0);

        Assert.Equal(Lua54Opcode.Move, chunk.MainPrototype.Code[0].Opcode);
        Assert.Equal(1, chunk.MainPrototype.Code[0].A);
        Assert.Equal(0, chunk.MainPrototype.Code[0].B);
        Assert.Empty(Lua54ChunkVerifier.Verify(chunk));
    }

    [Fact]
    public void CanonicalWriterDoesNotUseIntegerOnlyBitwiseConstantOpcodeForFloat()
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, 1, 0, sourceLine: 1),
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                2,
                0,
                1,
                (int)LuaIrBinaryOperator.BitwiseAnd,
                sourceLine: 1),
            new LuaIrInstruction(LuaIrOpcode.Return, 2, 1, sourceLine: 1));
        var function = CreateIrFunction(
            0,
            registerCount: 3,
            instructions,
            constants: [LuaIrConstant.FromFloat(1.0)],
            parameterCount: 1);

        var chunk = Lua54CanonicalPrototypeWriter.CreateChunk(
            new LuaIrModule { Functions = [function] },
            0);

        Assert.DoesNotContain(
            chunk.MainPrototype.Code,
            static instruction => instruction.Opcode == Lua54Opcode.BitwiseAndConstant);
        Assert.Contains(
            chunk.MainPrototype.Code,
            static instruction => instruction.Opcode == Lua54Opcode.BitwiseAnd);
        Assert.Empty(Lua54ChunkVerifier.Verify(chunk));
    }

    [Fact]
    public void CanonicalWriterPreservesComparisonDebugLocalBoundary()
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                2,
                0,
                1,
                (int)LuaIrBinaryOperator.LessThan,
                sourceLine: 1),
            new LuaIrInstruction(LuaIrOpcode.JumpIfTrue, 2, 3, sourceLine: 1),
            new LuaIrInstruction(LuaIrOpcode.Return, 0, 0, sourceLine: 1),
            new LuaIrInstruction(LuaIrOpcode.Return, 0, 0, sourceLine: 1));
        var function = CreateIrFunction(
            0,
            registerCount: 3,
            instructions,
            localVariables: [new LuaIrLocalVariable([(byte)'x'], 1, 2)]);

        var chunk = Lua54CanonicalPrototypeWriter.CreateChunk(
            new LuaIrModule { Functions = [function] },
            0);

        var local = Assert.Single(chunk.MainPrototype.LocalVariables);
        Assert.True(local.StartProgramCounter > 0);
        Assert.True(local.EndProgramCounter > local.StartProgramCounter);
    }

    [Fact]
    public void CanonicalWriterDoesNotCollapseBinaryPreparationAcrossSourceLines()
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, 1, 0, sourceLine: 1),
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                2,
                0,
                1,
                (int)LuaIrBinaryOperator.Add,
                sourceLine: 2),
            new LuaIrInstruction(LuaIrOpcode.Return, 2, 1, sourceLine: 2));
        var function = CreateIrFunction(
            0,
            registerCount: 3,
            instructions,
            constants: [LuaIrConstant.FromFloat(5.25)],
            parameterCount: 1);

        var chunk = Lua54CanonicalPrototypeWriter.CreateChunk(
            new LuaIrModule { Functions = [function] },
            0);

        Assert.Contains(
            chunk.MainPrototype.Code,
            static instruction => instruction.Opcode == Lua54Opcode.LoadConstant);
        Assert.DoesNotContain(
            chunk.MainPrototype.Code,
            static instruction => instruction.Opcode == Lua54Opcode.AddConstant);
    }

    [Fact]
    public void CanonicalWriterTreatsParametersAsLocalsWithoutDebugMetadata()
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.Move, 1, 0, sourceLine: 1),
            new LuaIrInstruction(LuaIrOpcode.Return, 1, 1, sourceLine: 1));
        var function = CreateIrFunction(
            0,
            registerCount: 2,
            instructions,
            parameterCount: 1);

        var chunk = Lua54CanonicalPrototypeWriter.CreateChunk(
            new LuaIrModule { Functions = [function] },
            0);

        Assert.Equal(Lua54Opcode.Move, chunk.MainPrototype.Code[0].Opcode);
        Assert.Equal(0, chunk.MainPrototype.Code[0].B);
    }

    [Fact]
    public void CanonicalWriterPreservesCapturedRegisterWritesWithoutDebugMetadata()
    {
        var parentInstructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, 0, 0, sourceLine: 1),
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, 2, 1, sourceLine: 1),
            new LuaIrInstruction(LuaIrOpcode.Closure, 1, 1, sourceLine: 1),
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, 0, 2, sourceLine: 1),
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                3,
                0,
                2,
                (int)LuaIrBinaryOperator.Add,
                sourceLine: 1),
            new LuaIrInstruction(
                LuaIrOpcode.Call,
                1,
                0,
                1,
                (int)LuaIrCallKind.Regular,
                sourceLine: 1),
            new LuaIrInstruction(LuaIrOpcode.Return, 1, 1, sourceLine: 1));
        var parent = CreateIrFunction(
            0,
            registerCount: 4,
            parentInstructions,
            constants:
            [
                LuaIrConstant.FromInteger(10),
                LuaIrConstant.FromInteger(1),
                LuaIrConstant.FromInteger(42),
            ]);
        var childInstructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.GetUpvalue, 0, 0, sourceLine: 1),
            new LuaIrInstruction(LuaIrOpcode.Return, 0, 1, sourceLine: 1));
        var child = CreateIrFunction(
            1,
            registerCount: 1,
            childInstructions,
            parentFunctionId: 0,
            upvalues:
            [
                new LuaIrUpvalue("x", -1, LuaIrUpvalueSourceKind.Register, 0),
            ]);
        var module = new LuaIrModule { Functions = [parent, child] };

        var bytes = Lua54CanonicalPrototypeWriter.Write(
            module,
            0,
            stripDebugInformation: true);
        var chunk = Lua54ChunkReader.Read(bytes);

        Assert.Empty(chunk.MainPrototype.LocalVariables);
        var closure = Array.FindIndex(
            chunk.MainPrototype.Code.ToArray(),
            static instruction => instruction.Opcode == Lua54Opcode.Closure);
        Assert.True(closure >= 0);
        Assert.Contains(
            chunk.MainPrototype.Code.Skip(closure + 1),
            static instruction => instruction.Opcode == Lua54Opcode.LoadInteger &&
                                  instruction.A == 0 && instruction.SignedBx == 42);
        Assert.Empty(Lua54ChunkVerifier.Verify(chunk));

        if (IsPucLuaAvailable())
        {
            Assert.Equal("42", ExecutePucChunk(bytes));
        }
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
        LineInfo = [sbyte.MinValue],
        AbsoluteLineInfo = [new Lua54AbsoluteLineInfo(0, 1)],
        LocalVariables =
        [
            new Lua54LocalVariable(Lua54String.FromUtf8("x"), 0, 1),
        ],
        UpvalueNames = [],
    };

    private static LuaIrModule CreateConstantLeftArithmeticModule(LuaIrConstant constant)
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, 1, 0, sourceLine: 1),
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                2,
                1,
                0,
                (int)LuaIrBinaryOperator.Add,
                sourceLine: 1),
            new LuaIrInstruction(LuaIrOpcode.Return, 2, 1, sourceLine: 1));
        var function = CreateIrFunction(
            0,
            registerCount: 3,
            instructions,
            constants: [constant],
            parameterCount: 1);
        return new LuaIrModule { Functions = [function] };
    }

    private static LuaIrFunction CreateIrFunction(
        int id,
        int registerCount,
        ImmutableArray<LuaIrInstruction> instructions,
        ImmutableArray<LuaIrConstant> constants = default,
        int parameterCount = 0,
        int parentFunctionId = -1,
        ImmutableArray<LuaIrUpvalue> upvalues = default,
        ImmutableArray<LuaIrLocalVariable> localVariables = default)
    {
        var function = new LuaIrFunction
        {
            Id = id,
            ParentFunctionId = parentFunctionId,
            Span = default,
            ParameterCount = parameterCount,
            RegisterCount = registerCount,
            Constants = constants.IsDefault ? [] : constants,
            Upvalues = upvalues.IsDefault ? [] : upvalues,
            Instructions = instructions,
            LocalVariables = localVariables.IsDefault ? [] : localVariables,
            BasicBlocks = [],
        };
        return function with { BasicBlocks = LuaIrControlFlow.Build(function.Instructions) };
    }

    private static string ExecutePucChunk(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"canonical-writer-{Guid.NewGuid():N}.luac");
        try
        {
            File.WriteAllBytes(path, bytes);
            var startInfo = new ProcessStartInfo
            {
                FileName = "lua",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-e");
            startInfo.ArgumentList.Add(
                $"local f=assert(loadfile([==[{path}]==], 'b')); print(f())");
            using var process = Process.Start(startInfo) ??
                throw new InvalidOperationException("Could not start PUC Lua.");
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
            return output.Trim();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static bool IsPucLuaAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "lua",
                Arguments = "-v",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(5_000);
            return process is { ExitCode: 0 } &&
                (process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd())
                .Contains("Lua 5.4", StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
