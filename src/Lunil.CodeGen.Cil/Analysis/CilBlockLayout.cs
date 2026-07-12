using System.Collections.Immutable;
using Lunil.CodeGen.Cil.Planning;
using Lunil.IR.Canonical;

namespace Lunil.CodeGen.Cil.Analysis;

public sealed record CilBlockLayout(ImmutableArray<CilCanonicalBlock> Blocks)
{
    public static CilBlockLayout Build(
        LuaIrFunction function,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(function);
        cancellationToken.ThrowIfCancellationRequested();
        var blocks = function.BasicBlocks.IsDefaultOrEmpty
            ? LuaIrControlFlow.Build(function.Instructions)
            : function.BasicBlocks;
        var result = ImmutableArray.CreateBuilder<CilCanonicalBlock>(blocks.Length);
        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new CilCanonicalBlock(block.Start, block.Length, block.Successors));
        }

        return new CilBlockLayout(result.MoveToImmutable());
    }
}
