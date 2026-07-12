using System.Buffers.Binary;
using System.Collections.Immutable;
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
