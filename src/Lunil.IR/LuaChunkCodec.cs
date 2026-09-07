using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua51;
using Lunil.IR.Lua52;
using Lunil.IR.Lua53;
using Lunil.IR.Lua54;
using Lunil.IR.Lua55;

namespace Lunil.IR;

/// <summary>
/// Single dispatch point from a <see cref="LuaChunkFormat"/> to the version-specific
/// prototype converters, chunk readers, and canonical writers. Adding a binary format
/// means extending this codec instead of editing every dispatch site. The canonical
/// <see cref="Lua54ChunkReaderOptions"/> record is the shared option surface; where an
/// older format has its own option record, the codec translates the fields.
/// </summary>
public static class LuaChunkCodec
{
    /// <summary>Reads a binary chunk of the given format into its version chunk model.</summary>
    public static LuaIrModule ReadPrototypeModule(LuaChunkFormat format, ReadOnlySpan<byte> bytes) =>
        ReadPrototypeModule(format, bytes, options: null);

    /// <summary>
    /// Reads a binary chunk of the given format with the canonical reader options;
    /// formats with a dedicated option record receive a field-by-field translation.
    /// </summary>
    public static LuaIrModule ReadPrototypeModule(
        LuaChunkFormat format,
        ReadOnlySpan<byte> bytes,
        Lua54ChunkReaderOptions? options) => format switch
        {
            LuaChunkFormat.Lua51 => Lua51PrototypeConverter.Convert(bytes),
            LuaChunkFormat.Lua52 => Lua52PrototypeConverter.Convert(bytes, TranslateOptions52(options)),
            LuaChunkFormat.Lua53 => Lua53PrototypeConverter.Convert(bytes, TranslateOptions(options)),
            LuaChunkFormat.Lua54 => Lua54PrototypeConverter.Convert(bytes, options),
            LuaChunkFormat.Lua55 => Lua55PrototypeConverter.Convert(bytes, options),
            _ => throw new NotSupportedException(
                "The selected binary adapter does not declare a chunk format."),
        };

    /// <summary>
    /// Writes the canonical module as a binary chunk of the given format.
    /// </summary>
    public static byte[] WriteCanonicalModule(
        LuaChunkFormat format,
        LuaIrModule module,
        int functionId,
        bool stripDebug) => format switch
        {
            LuaChunkFormat.Lua51 => Lua51CanonicalPrototypeWriter.Write(module, functionId, stripDebug),
            LuaChunkFormat.Lua52 => Lua52CanonicalPrototypeWriter.Write(module, functionId, stripDebug),
            LuaChunkFormat.Lua53 => Lua53CanonicalPrototypeWriter.Write(module, functionId, stripDebug),
            LuaChunkFormat.Lua54 => Lua54CanonicalPrototypeWriter.Write(module, functionId, stripDebug),
            LuaChunkFormat.Lua55 => Lua55CanonicalPrototypeWriter.Write(module, functionId, stripDebug),
            _ => throw new NotSupportedException(
                "The selected binary adapter does not declare a chunk format."),
        };

    private static Lua53ChunkReaderOptions? TranslateOptions(Lua54ChunkReaderOptions? options) => options is null
        ? null
        : new Lua53ChunkReaderOptions
        {
            MaximumChunkBytes = options.MaximumChunkBytes,
            MaximumPrototypeDepth = options.MaximumPrototypeDepth,
            MaximumPrototypeCount = options.MaximumPrototypeCount,
            MaximumInstructionCount = options.MaximumInstructionCount,
            MaximumConstantCount = options.MaximumConstantCount,
            MaximumUpvalueCount = options.MaximumUpvalueCount,
            MaximumStringBytes = options.MaximumStringBytes,
            MaximumDebugEntryCount = options.MaximumDebugEntryCount,
            AllowTrailingData = options.AllowTrailingData,
        };

    private static Lua52ChunkReaderOptions? TranslateOptions52(Lua54ChunkReaderOptions? options) => options is null
        ? null
        : new Lua52ChunkReaderOptions
        {
            MaximumChunkBytes = options.MaximumChunkBytes,
            MaximumPrototypeDepth = options.MaximumPrototypeDepth,
            MaximumPrototypeCount = options.MaximumPrototypeCount,
            MaximumInstructionCount = options.MaximumInstructionCount,
            MaximumConstantCount = options.MaximumConstantCount,
            MaximumUpvalueCount = options.MaximumUpvalueCount,
            MaximumStringBytes = options.MaximumStringBytes,
            MaximumDebugEntryCount = options.MaximumDebugEntryCount,
            AllowTrailingData = options.AllowTrailingData,
        };
}
