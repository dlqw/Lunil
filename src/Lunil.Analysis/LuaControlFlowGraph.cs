using System.Collections.Immutable;
using Lunil.Core.Text;

namespace Lunil.Analysis;

public enum LuaControlFlowBlockKind : byte
{
    Entry,
    Exit,
    Statement,
    Condition,
    LoopHeader,
    Label,
}

public enum LuaControlFlowEdgeKind : byte
{
    Next,
    True,
    False,
    Loop,
    Break,
    Goto,
    Return,
}

public sealed record LuaControlFlowEdge(
    int TargetBlockId,
    LuaControlFlowEdgeKind Kind,
    TextSpan ConditionSpan = default);

public sealed record LuaControlFlowBlock(
    int Id,
    LuaControlFlowBlockKind Kind,
    TextSpan Span,
    ImmutableArray<TextSpan> StatementSpans,
    ImmutableArray<LuaControlFlowEdge> Successors,
    ImmutableArray<int> Predecessors,
    bool IsReachable);

public sealed record LuaControlFlowGraph(
    int FunctionId,
    TextSpan Span,
    int EntryBlockId,
    int ExitBlockId,
    ImmutableArray<LuaControlFlowBlock> Blocks);
