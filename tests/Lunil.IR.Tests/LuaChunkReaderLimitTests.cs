using Lunil.IR.Lua51;
using Lunil.IR.Lua52;
using Lunil.IR.Lua53;
using Lunil.IR.Lua54;

namespace Lunil.IR.Tests;

public sealed class LuaChunkReaderLimitTests
{
    [Fact]
    public void EveryVersionChunkReaderSharesOneDefaultResourceContract()
    {
        AssertDefaults(Lua51ChunkReaderOptions.Default);
        AssertDefaults(Lua52ChunkReaderOptions.Default);
        AssertDefaults(Lua53ChunkReaderOptions.Default);
        AssertDefaults(Lua54ChunkReaderOptions.Default);
    }

    private static void AssertDefaults(
        Lua51ChunkReaderOptions options)
    {
        Assert.Equal(64 * 1024 * 1024, options.MaximumChunkBytes);
        Assert.Equal(200, options.MaximumPrototypeDepth);
        Assert.Equal(100_000, options.MaximumPrototypeCount);
        Assert.Equal(10_000_000, options.MaximumInstructionCount);
        Assert.Equal(1_000_000, options.MaximumConstantCount);
        Assert.Equal(64 * 1024 * 1024, options.MaximumStringBytes);
        Assert.Equal(10_000_000, options.MaximumDebugEntryCount);
    }

    private static void AssertDefaults(
        Lua52ChunkReaderOptions options)
    {
        Assert.Equal(64 * 1024 * 1024, options.MaximumChunkBytes);
        Assert.Equal(200, options.MaximumPrototypeDepth);
        Assert.Equal(100_000, options.MaximumPrototypeCount);
        Assert.Equal(10_000_000, options.MaximumInstructionCount);
        Assert.Equal(1_000_000, options.MaximumConstantCount);
        Assert.Equal(1_000_000, options.MaximumUpvalueCount);
        Assert.Equal(64 * 1024 * 1024, options.MaximumStringBytes);
        Assert.Equal(10_000_000, options.MaximumDebugEntryCount);
    }

    private static void AssertDefaults(
        Lua53ChunkReaderOptions options)
    {
        Assert.Equal(64 * 1024 * 1024, options.MaximumChunkBytes);
        Assert.Equal(200, options.MaximumPrototypeDepth);
        Assert.Equal(100_000, options.MaximumPrototypeCount);
        Assert.Equal(10_000_000, options.MaximumInstructionCount);
        Assert.Equal(1_000_000, options.MaximumConstantCount);
        Assert.Equal(1_000_000, options.MaximumUpvalueCount);
        Assert.Equal(64 * 1024 * 1024, options.MaximumStringBytes);
        Assert.Equal(10_000_000, options.MaximumDebugEntryCount);
    }

    private static void AssertDefaults(
        Lua54ChunkReaderOptions options)
    {
        Assert.Equal(64 * 1024 * 1024, options.MaximumChunkBytes);
        Assert.Equal(200, options.MaximumPrototypeDepth);
        Assert.Equal(100_000, options.MaximumPrototypeCount);
        Assert.Equal(10_000_000, options.MaximumInstructionCount);
        Assert.Equal(1_000_000, options.MaximumConstantCount);
        Assert.Equal(1_000_000, options.MaximumUpvalueCount);
        Assert.Equal(64 * 1024 * 1024, options.MaximumStringBytes);
        Assert.Equal(10_000_000, options.MaximumDebugEntryCount);
    }
}
