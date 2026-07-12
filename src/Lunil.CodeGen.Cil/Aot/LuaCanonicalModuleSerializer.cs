using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Lunil.IR.Canonical;

namespace Lunil.CodeGen.Cil.Artifacts;

internal static class LuaCanonicalModuleSerializer
{
    private static readonly byte[] Magic = "LUNILIR\0"u8.ToArray();

    public static byte[] Serialize(LuaIrModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(module.FormatVersion);
        writer.Write(module.MainFunctionId);
        writer.Write(module.Functions.Length);
        foreach (var function in module.Functions)
        {
            WriteFunction(writer, function);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public static string Sha256Hex(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    public static LuaIrModule Deserialize(ReadOnlySpan<byte> content)
    {
        var reader = new CanonicalModuleReader(content);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("The canonical module magic is invalid.");
        }

        var formatVersion = reader.ReadInt32();
        var mainFunctionId = reader.ReadInt32();
        var functionCount = reader.ReadCount();
        var functions = ImmutableArray.CreateBuilder<LuaIrFunction>(functionCount);
        for (var functionIndex = 0; functionIndex < functionCount; functionIndex++)
        {
            functions.Add(ReadFunction(ref reader));
        }

        if (!reader.IsAtEnd)
        {
            throw new InvalidDataException("The canonical module has trailing data.");
        }

        var module = new LuaIrModule
        {
            FormatVersion = formatVersion,
            MainFunctionId = mainFunctionId,
            Functions = functions.MoveToImmutable(),
        };
        if (formatVersion != LuaIrModule.CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Canonical IR format {formatVersion} is not supported; expected {LuaIrModule.CurrentFormatVersion}.");
        }

        var errors = LuaIrVerifier.Verify(module);
        if (!errors.IsEmpty)
        {
            throw new InvalidDataException(
                $"Canonical module verification failed in function {errors[0].FunctionId} " +
                $"at instruction {errors[0].ProgramCounter}: {errors[0].Message}");
        }

        return module;
    }

    public static CanonicalModuleSummary ReadSummary(ReadOnlySpan<byte> content)
    {
        var reader = new CanonicalModuleReader(content);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("The embedded canonical module magic is invalid.");
        }

        var formatVersion = reader.ReadInt32();
        var mainFunctionId = reader.ReadInt32();
        var functionCount = reader.ReadCount();
        var functions = ImmutableArray.CreateBuilder<CanonicalFunctionSummary>(functionCount);
        for (var functionIndex = 0; functionIndex < functionCount; functionIndex++)
        {
            var functionId = reader.ReadInt32();
            _ = reader.ReadInt32();
            reader.Skip(sizeof(int) * 2);
            reader.SkipLengthPrefixedBytes();
            reader.Skip(sizeof(int) * 3);
            reader.Skip(sizeof(byte));
            reader.Skip(sizeof(int));

            var constantCount = reader.ReadCount();
            for (var constantIndex = 0; constantIndex < constantCount; constantIndex++)
            {
                reader.Skip(sizeof(byte) + sizeof(long) + sizeof(long));
                reader.SkipLengthPrefixedBytes();
            }

            var upvalueCount = reader.ReadCount();
            for (var upvalueIndex = 0; upvalueIndex < upvalueCount; upvalueIndex++)
            {
                reader.SkipLengthPrefixedBytes();
                reader.Skip(sizeof(int));
                reader.Skip(sizeof(byte));
                reader.Skip(sizeof(int));
                reader.Skip(sizeof(byte));
                reader.SkipLengthPrefixedBytes();
            }

            var instructionCount = reader.ReadCount();
            reader.Skip(checked(instructionCount * 33));

            var localCount = reader.ReadCount();
            for (var localIndex = 0; localIndex < localCount; localIndex++)
            {
                reader.SkipLengthPrefixedBytes();
                reader.Skip(sizeof(int) * 2);
            }

            var blockCount = reader.ReadCount();
            for (var blockIndex = 0; blockIndex < blockCount; blockIndex++)
            {
                reader.Skip(sizeof(int) * 2);
                var successorCount = reader.ReadCount();
                reader.Skip(checked(successorCount * sizeof(int)));
            }

            functions.Add(new CanonicalFunctionSummary(functionId, instructionCount));
        }

        if (!reader.IsAtEnd)
        {
            throw new InvalidDataException("The embedded canonical module has trailing data.");
        }

        return new CanonicalModuleSummary(
            formatVersion,
            mainFunctionId,
            functions.ToImmutable());
    }

    private static void WriteFunction(BinaryWriter writer, LuaIrFunction function)
    {
        writer.Write(function.Id);
        writer.Write(function.ParentFunctionId);
        WriteSpan(writer, function.Span.Start, function.Span.Length);
        WriteBytes(writer, function.SourceName.AsSpan());
        writer.Write(function.LineDefined);
        writer.Write(function.LastLineDefined);
        writer.Write(function.ParameterCount);
        writer.Write(function.IsVarArg);
        writer.Write(function.RegisterCount);

        writer.Write(function.Constants.Length);
        foreach (var constant in function.Constants)
        {
            writer.Write((byte)constant.Kind);
            writer.Write(constant.Integer);
            writer.Write(BitConverter.DoubleToInt64Bits(constant.Float));
            WriteBytes(writer, constant.Bytes.AsSpan());
        }

        writer.Write(function.Upvalues.Length);
        foreach (var upvalue in function.Upvalues)
        {
            WriteString(writer, upvalue.Name);
            writer.Write(upvalue.SymbolId);
            writer.Write((byte)upvalue.SourceKind);
            writer.Write(upvalue.SourceIndex);
            writer.Write(upvalue.Kind);
            WriteBytes(writer, upvalue.DebugName.AsSpan());
        }

        writer.Write(function.Instructions.Length);
        foreach (var instruction in function.Instructions)
        {
            writer.Write((byte)instruction.Opcode);
            writer.Write(instruction.A);
            writer.Write(instruction.B);
            writer.Write(instruction.C);
            writer.Write(instruction.D);
            WriteSpan(writer, instruction.Span.Start, instruction.Span.Length);
            writer.Write(instruction.SourceLine);
            writer.Write(instruction.LogicalProgramCounter);
        }

        writer.Write(function.LocalVariables.Length);
        foreach (var local in function.LocalVariables)
        {
            WriteBytes(writer, local.Name.AsSpan());
            writer.Write(local.StartProgramCounter);
            writer.Write(local.EndProgramCounter);
        }

        writer.Write(function.BasicBlocks.Length);
        foreach (var block in function.BasicBlocks)
        {
            writer.Write(block.Start);
            writer.Write(block.Length);
            writer.Write(block.Successors.Length);
            foreach (var successor in block.Successors)
            {
                writer.Write(successor);
            }
        }
    }

    private static LuaIrFunction ReadFunction(ref CanonicalModuleReader reader)
    {
        var id = reader.ReadInt32();
        var parentFunctionId = reader.ReadInt32();
        var span = new Lunil.Core.Text.TextSpan(reader.ReadInt32(), reader.ReadInt32());
        var sourceName = reader.ReadLengthPrefixedBytes().ToArray().ToImmutableArray();
        var lineDefined = reader.ReadInt32();
        var lastLineDefined = reader.ReadInt32();
        var parameterCount = reader.ReadInt32();
        var isVarArg = reader.ReadBoolean();
        var registerCount = reader.ReadInt32();

        var constantCount = reader.ReadCount();
        var constants = ImmutableArray.CreateBuilder<LuaIrConstant>(constantCount);
        for (var index = 0; index < constantCount; index++)
        {
            var kindValue = reader.ReadByte();
            if (!Enum.IsDefined((LuaIrConstantKind)kindValue))
            {
                throw new InvalidDataException($"Canonical constant kind {kindValue} is invalid.");
            }

            var integer = reader.ReadInt64();
            var number = BitConverter.Int64BitsToDouble(reader.ReadInt64());
            var bytes = reader.ReadLengthPrefixedBytes();
            constants.Add((LuaIrConstantKind)kindValue switch
            {
                LuaIrConstantKind.Nil => LuaIrConstant.Nil,
                LuaIrConstantKind.Boolean => LuaIrConstant.FromBoolean(integer != 0),
                LuaIrConstantKind.Integer => LuaIrConstant.FromInteger(integer),
                LuaIrConstantKind.Float => LuaIrConstant.FromFloat(number),
                LuaIrConstantKind.String => LuaIrConstant.FromString(bytes),
                _ => throw new UnreachableException(),
            });
        }

        var upvalueCount = reader.ReadCount();
        var upvalues = ImmutableArray.CreateBuilder<LuaIrUpvalue>(upvalueCount);
        for (var index = 0; index < upvalueCount; index++)
        {
            var name = reader.ReadString();
            var symbolId = reader.ReadInt32();
            var sourceKindValue = reader.ReadByte();
            if (!Enum.IsDefined((LuaIrUpvalueSourceKind)sourceKindValue))
            {
                throw new InvalidDataException($"Canonical upvalue source kind {sourceKindValue} is invalid.");
            }

            var sourceIndex = reader.ReadInt32();
            var kind = reader.ReadByte();
            var debugName = reader.ReadLengthPrefixedBytes().ToArray().ToImmutableArray();
            upvalues.Add(new LuaIrUpvalue(
                name,
                symbolId,
                (LuaIrUpvalueSourceKind)sourceKindValue,
                sourceIndex)
            {
                Kind = kind,
                DebugName = debugName,
            });
        }

        var instructionCount = reader.ReadCount();
        var instructions = ImmutableArray.CreateBuilder<LuaIrInstruction>(instructionCount);
        for (var index = 0; index < instructionCount; index++)
        {
            var opcodeValue = reader.ReadByte();
            if (!Enum.IsDefined((LuaIrOpcode)opcodeValue))
            {
                throw new InvalidDataException($"Canonical opcode {opcodeValue} is invalid.");
            }

            instructions.Add(new LuaIrInstruction(
                (LuaIrOpcode)opcodeValue,
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                new Lunil.Core.Text.TextSpan(reader.ReadInt32(), reader.ReadInt32()),
                reader.ReadInt32(),
                reader.ReadInt32()));
        }

        var localCount = reader.ReadCount();
        var locals = ImmutableArray.CreateBuilder<LuaIrLocalVariable>(localCount);
        for (var index = 0; index < localCount; index++)
        {
            locals.Add(new LuaIrLocalVariable(
                reader.ReadLengthPrefixedBytes().ToArray().ToImmutableArray(),
                reader.ReadInt32(),
                reader.ReadInt32()));
        }

        var blockCount = reader.ReadCount();
        var blocks = ImmutableArray.CreateBuilder<LuaIrBasicBlock>(blockCount);
        for (var index = 0; index < blockCount; index++)
        {
            var start = reader.ReadInt32();
            var length = reader.ReadInt32();
            var successorCount = reader.ReadCount();
            var successors = ImmutableArray.CreateBuilder<int>(successorCount);
            for (var successorIndex = 0; successorIndex < successorCount; successorIndex++)
            {
                successors.Add(reader.ReadInt32());
            }

            blocks.Add(new LuaIrBasicBlock(start, length, successors.MoveToImmutable()));
        }

        return new LuaIrFunction
        {
            Id = id,
            ParentFunctionId = parentFunctionId,
            Span = span,
            SourceName = sourceName,
            LineDefined = lineDefined,
            LastLineDefined = lastLineDefined,
            ParameterCount = parameterCount,
            IsVarArg = isVarArg,
            RegisterCount = registerCount,
            Constants = constants.MoveToImmutable(),
            Upvalues = upvalues.MoveToImmutable(),
            Instructions = instructions.MoveToImmutable(),
            LocalVariables = locals.MoveToImmutable(),
            BasicBlocks = blocks.MoveToImmutable(),
        };
    }

    private static void WriteSpan(BinaryWriter writer, int start, int length)
    {
        writer.Write(start);
        writer.Write(length);
    }

    private static void WriteString(BinaryWriter writer, string value) =>
        WriteBytes(writer, Encoding.UTF8.GetBytes(value));

    private static void WriteBytes(BinaryWriter writer, ReadOnlySpan<byte> value)
    {
        writer.Write(value.Length);
        writer.Write(value);
    }

    internal sealed record CanonicalModuleSummary(
        int FormatVersion,
        int MainFunctionId,
        ImmutableArray<CanonicalFunctionSummary> Functions);

    internal sealed record CanonicalFunctionSummary(
        int FunctionId,
        int InstructionCount);

    private ref struct CanonicalModuleReader
    {
        private readonly ReadOnlySpan<byte> _content;
        private int _offset;

        public CanonicalModuleReader(ReadOnlySpan<byte> content)
        {
            _content = content;
            _offset = 0;
        }

        public bool IsAtEnd => _offset == _content.Length;

        public int ReadInt32()
        {
            var bytes = ReadBytes(sizeof(int));
            return BinaryPrimitives.ReadInt32LittleEndian(bytes);
        }

        public long ReadInt64()
        {
            var bytes = ReadBytes(sizeof(long));
            return BinaryPrimitives.ReadInt64LittleEndian(bytes);
        }

        public byte ReadByte() => ReadBytes(1)[0];

        public bool ReadBoolean() => ReadByte() switch
        {
            0 => false,
            1 => true,
            var value => throw new InvalidDataException(
                $"The canonical module contains invalid boolean byte {value}."),
        };

        public ReadOnlySpan<byte> ReadLengthPrefixedBytes() => ReadBytes(ReadCount());

        public string ReadString() => Encoding.UTF8.GetString(ReadLengthPrefixedBytes());

        public int ReadCount()
        {
            var count = ReadInt32();
            if (count < 0)
            {
                throw new InvalidDataException("The embedded canonical module contains a negative count.");
            }

            if (count > _content.Length - _offset)
            {
                throw new InvalidDataException(
                    "The embedded canonical module count exceeds the remaining payload.");
            }

            return count;
        }

        public ReadOnlySpan<byte> ReadBytes(int length)
        {
            Ensure(length);
            var result = _content.Slice(_offset, length);
            _offset += length;
            return result;
        }

        public void SkipLengthPrefixedBytes() => Skip(ReadCount());

        public void Skip(int length) => _ = ReadBytes(length);

        private void Ensure(int length)
        {
            if (length < 0 || length > _content.Length - _offset)
            {
                throw new InvalidDataException("The embedded canonical module is truncated.");
            }
        }
    }
}
