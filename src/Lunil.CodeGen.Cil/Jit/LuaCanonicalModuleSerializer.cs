using System.Security.Cryptography;
using System.Text;
using Lunil.IR.Canonical;

namespace Lunil.CodeGen.Cil.Jit;

/// <summary>
/// Produces the deterministic byte identity used by owner-free JIT profiles and generation guards.
/// This is an in-memory identity encoding, not a loadable Lua artifact format.
/// </summary>
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
}
