using System.Collections.Immutable;
using Lunil.CodeGen.Cil.Planning;
using Lunil.IR.Canonical;

namespace Lunil.CodeGen.Cil.Analysis;

public sealed record CilBlockLayout(ImmutableArray<CilCanonicalBlock> Blocks)
{
    public static CilBlockLayout Build(LuaIrFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        var blocks = function.BasicBlocks.IsDefaultOrEmpty
            ? LuaIrControlFlow.Build(function.Instructions)
            : function.BasicBlocks;
        return new CilBlockLayout(blocks.Select(static block => new CilCanonicalBlock(
            block.Start,
            block.Length,
            block.Successors)).ToImmutableArray());
    }
}
