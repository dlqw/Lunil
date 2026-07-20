using System.Collections.Immutable;
using System.Text;
using Lunil.Core.Text;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Analysis;

internal static class ControlFlowGraphBuilder
{
    public static ImmutableArray<LuaControlFlowGraph> BuildAll(
        LuaSemanticModel semantics,
        LuaAnalysisContext context)
    {
        var roots = FindFunctionRoots(semantics.Syntax.Root, semantics.Functions);
        return semantics.Functions
            .OrderBy(static function => function.Id)
            .Select(function => new Builder(
                function,
                roots[function.Id],
                semantics.Syntax.Source,
                context).Build())
            .ToImmutableArray();
    }

    private static ImmutableDictionary<int, LuaSyntaxNode> FindFunctionRoots(
        LuaSyntaxNode root,
        ImmutableArray<LuaFunctionInfo> functions)
    {
        var result = ImmutableDictionary.CreateBuilder<int, LuaSyntaxNode>();
        var main = root.ChildNodes().Single(static node => node.Kind == LuaSyntaxKind.Block);
        result[0] = main;
        var owners = root.DescendantNodes()
            .Where(static node => node.Kind is
                LuaSyntaxKind.FunctionDeclarationStatement or
                LuaSyntaxKind.GlobalDeclarationStatement or
                LuaSyntaxKind.LocalFunctionDeclarationStatement or
                LuaSyntaxKind.FunctionExpression)
            .ToLookup(static node => node.Span);
        foreach (var function in functions.Where(static function => function.Id != 0))
        {
            var owner = owners[function.Span].FirstOrDefault();
            var body = owner?.DescendantNodes().FirstOrDefault(static node =>
                node.Kind == LuaSyntaxKind.FunctionBody);
            var block = body?.ChildNodes().FirstOrDefault(static node =>
                node.Kind == LuaSyntaxKind.Block);
            result[function.Id] = block ?? new LuaSyntaxNode(
                LuaSyntaxKind.Block,
                [],
                function.Span.Start);
        }

        return result.ToImmutable();
    }

    private sealed class Builder
    {
        private readonly LuaFunctionInfo _function;
        private readonly LuaSyntaxNode _rootBlock;
        private readonly Lunil.Core.Text.SourceText _source;
        private readonly LuaAnalysisContext _context;
        private readonly List<MutableBlock> _blocks = [];
        private readonly Dictionary<string, int> _labels = new(StringComparer.Ordinal);
        private readonly List<(int Source, string Label, TextSpan Span)> _gotos = [];
        private readonly int _entry;
        private readonly int _exit;
        private bool _truncated;

        public Builder(
            LuaFunctionInfo function,
            LuaSyntaxNode rootBlock,
            Lunil.Core.Text.SourceText source,
            LuaAnalysisContext context)
        {
            _function = function;
            _rootBlock = rootBlock;
            _source = source;
            _context = context;
            _entry = AddBlock(LuaControlFlowBlockKind.Entry, new TextSpan(function.Span.Start, 0));
            _exit = AddBlock(LuaControlFlowBlockKind.Exit, new TextSpan(function.Span.End, 0));
        }

        public LuaControlFlowGraph Build()
        {
            var tails = BuildSequence(_rootBlock.ChildNodes(), [_entry], breakTarget: null);
            Connect(tails, _exit, LuaControlFlowEdgeKind.Next);
            foreach (var (source, label, span) in _gotos)
            {
                if (_labels.TryGetValue(label, out var target))
                {
                    Connect(source, target, LuaControlFlowEdgeKind.Goto, span);
                }
                else
                {
                    Connect(source, _exit, LuaControlFlowEdgeKind.Goto, span);
                }
            }

            var reachable = ComputeReachable();
            var predecessors = Enumerable.Range(0, _blocks.Count)
                .Select(static _ => new List<int>())
                .ToArray();
            foreach (var block in _blocks)
            {
                foreach (var edge in block.Successors)
                {
                    if ((uint)edge.TargetBlockId < (uint)predecessors.Length)
                    {
                        predecessors[edge.TargetBlockId].Add(block.Id);
                    }
                }
            }

            var blocks = _blocks.Select(block => new LuaControlFlowBlock(
                block.Id,
                block.Kind,
                block.Span,
                block.StatementSpans.ToImmutableArray(),
                block.Successors
                    .Distinct()
                    .OrderBy(static edge => edge.TargetBlockId)
                    .ThenBy(static edge => edge.Kind)
                    .ToImmutableArray(),
                predecessors[block.Id].Distinct().Order().ToImmutableArray(),
                reachable.Contains(block.Id))).ToImmutableArray();
            return new LuaControlFlowGraph(
                _function.Id,
                _function.Span,
                _entry,
                _exit,
                blocks);
        }

        private List<int> BuildSequence(
            IEnumerable<LuaSyntaxNode> statements,
            List<int> incoming,
            int? breakTarget)
        {
            var tails = incoming;
            foreach (var statement in statements)
            {
                if (_truncated)
                {
                    return [_exit];
                }

                tails = BuildStatement(statement, tails, breakTarget);
            }

            return tails;
        }

        private List<int> BuildStatement(
            LuaSyntaxNode statement,
            List<int> incoming,
            int? breakTarget)
        {
            return statement.Kind switch
            {
                LuaSyntaxKind.IfStatement => BuildIf(statement, incoming, breakTarget),
                LuaSyntaxKind.WhileStatement => BuildWhile(statement, incoming),
                LuaSyntaxKind.RepeatStatement => BuildRepeat(statement, incoming),
                LuaSyntaxKind.NumericForStatement or LuaSyntaxKind.GenericForStatement =>
                    BuildFor(statement, incoming),
                LuaSyntaxKind.DoStatement => BuildDo(statement, incoming, breakTarget),
                LuaSyntaxKind.ReturnStatement => BuildTerminal(
                    statement,
                    incoming,
                    _exit,
                    LuaControlFlowEdgeKind.Return),
                LuaSyntaxKind.BreakStatement => BuildTerminal(
                    statement,
                    incoming,
                    breakTarget ?? _exit,
                    LuaControlFlowEdgeKind.Break),
                LuaSyntaxKind.GotoStatement => BuildGoto(statement, incoming),
                LuaSyntaxKind.LabelStatement => BuildLabel(statement, incoming),
                _ => BuildSimple(statement, incoming),
            };
        }

        private List<int> BuildSimple(LuaSyntaxNode statement, List<int> incoming)
        {
            var block = AddBlock(LuaControlFlowBlockKind.Statement, statement.Span, statement.Span);
            Connect(incoming, block, LuaControlFlowEdgeKind.Next);
            return [block];
        }

        private List<int> BuildDo(
            LuaSyntaxNode statement,
            List<int> incoming,
            int? breakTarget)
        {
            var marker = AddBlock(LuaControlFlowBlockKind.Statement, statement.Span, statement.Span);
            Connect(incoming, marker, LuaControlFlowEdgeKind.Next);
            var body = statement.ChildNodes().Single(static node => node.Kind == LuaSyntaxKind.Block);
            return BuildSequence(body.ChildNodes(), [marker], breakTarget);
        }

        private List<int> BuildIf(
            LuaSyntaxNode statement,
            List<int> incoming,
            int? breakTarget)
        {
            var join = AddBlock(
                LuaControlFlowBlockKind.Statement,
                new TextSpan(statement.Span.End, 0));
            var nodes = statement.ChildNodes().ToArray();
            var condition = nodes.First(static node => node.Kind != LuaSyntaxKind.Block &&
                node.Kind is not LuaSyntaxKind.ElseIfClause and not LuaSyntaxKind.ElseClause);
            var conditionBlock = AddBlock(
                LuaControlFlowBlockKind.Condition,
                condition.Span,
                statement.Span);
            Connect(incoming, conditionBlock, LuaControlFlowEdgeKind.Next);
            var body = nodes.First(static node => node.Kind == LuaSyntaxKind.Block);
            var thenEntry = AddBlock(
                LuaControlFlowBlockKind.Statement,
                new TextSpan(body.Span.Start, 0));
            Connect(conditionBlock, thenEntry, LuaControlFlowEdgeKind.True, condition.Span);
            var thenTails = BuildSequence(body.ChildNodes(), [thenEntry], breakTarget);
            Connect(thenTails, join, LuaControlFlowEdgeKind.Next);

            var falseSource = conditionBlock;
            foreach (var clause in nodes.Where(static node => node.Kind == LuaSyntaxKind.ElseIfClause))
            {
                var clauseCondition = clause.ChildNodes().First(static node =>
                    node.Kind != LuaSyntaxKind.Block);
                var clauseConditionBlock = AddBlock(
                    LuaControlFlowBlockKind.Condition,
                    clauseCondition.Span,
                    clause.Span);
                Connect(falseSource, clauseConditionBlock, LuaControlFlowEdgeKind.False, condition.Span);
                var clauseBody = clause.ChildNodes().Single(static node => node.Kind == LuaSyntaxKind.Block);
                var clauseEntry = AddBlock(
                    LuaControlFlowBlockKind.Statement,
                    new TextSpan(clauseBody.Span.Start, 0));
                Connect(
                    clauseConditionBlock,
                    clauseEntry,
                    LuaControlFlowEdgeKind.True,
                    clauseCondition.Span);
                var clauseTails = BuildSequence(clauseBody.ChildNodes(), [clauseEntry], breakTarget);
                Connect(clauseTails, join, LuaControlFlowEdgeKind.Next);
                falseSource = clauseConditionBlock;
                condition = clauseCondition;
            }

            var elseClause = nodes.FirstOrDefault(static node => node.Kind == LuaSyntaxKind.ElseClause);
            if (elseClause is not null)
            {
                var elseBody = elseClause.ChildNodes().Single(static node => node.Kind == LuaSyntaxKind.Block);
                var elseEntry = AddBlock(
                    LuaControlFlowBlockKind.Statement,
                    new TextSpan(elseBody.Span.Start, 0));
                Connect(falseSource, elseEntry, LuaControlFlowEdgeKind.False, condition.Span);
                var elseTails = BuildSequence(elseBody.ChildNodes(), [elseEntry], breakTarget);
                Connect(elseTails, join, LuaControlFlowEdgeKind.Next);
            }
            else
            {
                Connect(falseSource, join, LuaControlFlowEdgeKind.False, condition.Span);
            }

            return [join];
        }

        private List<int> BuildWhile(LuaSyntaxNode statement, List<int> incoming)
        {
            var nodes = statement.ChildNodes().ToArray();
            var condition = nodes.First(static node => node.Kind != LuaSyntaxKind.Block);
            var body = nodes.Single(static node => node.Kind == LuaSyntaxKind.Block);
            var header = AddBlock(
                LuaControlFlowBlockKind.LoopHeader,
                condition.Span,
                statement.Span);
            var exit = AddBlock(
                LuaControlFlowBlockKind.Statement,
                new TextSpan(statement.Span.End, 0));
            var bodyEntry = AddBlock(
                LuaControlFlowBlockKind.Statement,
                new TextSpan(body.Span.Start, 0));
            Connect(incoming, header, LuaControlFlowEdgeKind.Next);
            Connect(header, bodyEntry, LuaControlFlowEdgeKind.True, condition.Span);
            Connect(header, exit, LuaControlFlowEdgeKind.False, condition.Span);
            var tails = BuildSequence(body.ChildNodes(), [bodyEntry], exit);
            Connect(tails, header, LuaControlFlowEdgeKind.Loop, condition.Span);
            return [exit];
        }

        private List<int> BuildRepeat(LuaSyntaxNode statement, List<int> incoming)
        {
            var nodes = statement.ChildNodes().ToArray();
            var body = nodes.Single(static node => node.Kind == LuaSyntaxKind.Block);
            var condition = nodes.Last(static node => node.Kind != LuaSyntaxKind.Block);
            var exit = AddBlock(
                LuaControlFlowBlockKind.Statement,
                new TextSpan(statement.Span.End, 0));
            var bodyEntry = AddBlock(
                LuaControlFlowBlockKind.LoopHeader,
                new TextSpan(body.Span.Start, 0),
                statement.Span);
            Connect(incoming, bodyEntry, LuaControlFlowEdgeKind.Next);
            var tails = BuildSequence(body.ChildNodes(), [bodyEntry], exit);
            var conditionBlock = AddBlock(
                LuaControlFlowBlockKind.Condition,
                condition.Span);
            Connect(tails, conditionBlock, LuaControlFlowEdgeKind.Next);
            Connect(conditionBlock, exit, LuaControlFlowEdgeKind.True, condition.Span);
            Connect(conditionBlock, bodyEntry, LuaControlFlowEdgeKind.Loop, condition.Span);
            return [exit];
        }

        private List<int> BuildFor(LuaSyntaxNode statement, List<int> incoming)
        {
            var body = statement.ChildNodes().Single(static node => node.Kind == LuaSyntaxKind.Block);
            var header = AddBlock(
                LuaControlFlowBlockKind.LoopHeader,
                statement.Span,
                statement.Span);
            var exit = AddBlock(
                LuaControlFlowBlockKind.Statement,
                new TextSpan(statement.Span.End, 0));
            var bodyEntry = AddBlock(
                LuaControlFlowBlockKind.Statement,
                new TextSpan(body.Span.Start, 0));
            Connect(incoming, header, LuaControlFlowEdgeKind.Next);
            Connect(header, bodyEntry, LuaControlFlowEdgeKind.True, statement.Span);
            Connect(header, exit, LuaControlFlowEdgeKind.False, statement.Span);
            var tails = BuildSequence(body.ChildNodes(), [bodyEntry], exit);
            Connect(tails, header, LuaControlFlowEdgeKind.Loop, statement.Span);
            return [exit];
        }

        private List<int> BuildTerminal(
            LuaSyntaxNode statement,
            List<int> incoming,
            int target,
            LuaControlFlowEdgeKind edgeKind)
        {
            var block = AddBlock(LuaControlFlowBlockKind.Statement, statement.Span, statement.Span);
            Connect(incoming, block, LuaControlFlowEdgeKind.Next);
            Connect(block, target, edgeKind, statement.Span);
            return [];
        }

        private List<int> BuildGoto(LuaSyntaxNode statement, List<int> incoming)
        {
            var block = AddBlock(LuaControlFlowBlockKind.Statement, statement.Span, statement.Span);
            Connect(incoming, block, LuaControlFlowEdgeKind.Next);
            var name = GetIdentifier(statement);
            if (name is not null)
            {
                _gotos.Add((block, name, statement.Span));
            }
            else
            {
                Connect(block, _exit, LuaControlFlowEdgeKind.Goto, statement.Span);
            }

            return [];
        }

        private List<int> BuildLabel(LuaSyntaxNode statement, List<int> incoming)
        {
            var block = AddBlock(LuaControlFlowBlockKind.Label, statement.Span, statement.Span);
            Connect(incoming, block, LuaControlFlowEdgeKind.Next);
            var name = GetIdentifier(statement);
            if (name is not null)
            {
                _labels.TryAdd(name, block);
            }

            return [block];
        }

        private int AddBlock(
            LuaControlFlowBlockKind kind,
            TextSpan span,
            params TextSpan[] statementSpans)
        {
            if (!_context.TryAddControlFlowBlock(span))
            {
                _truncated = true;
                return _blocks.Count == 0 ? 0 : _blocks.Count - 1;
            }

            var id = _blocks.Count;
            _blocks.Add(new MutableBlock(id, kind, span, statementSpans));
            return id;
        }

        private void Connect(
            IEnumerable<int> sources,
            int target,
            LuaControlFlowEdgeKind kind,
            TextSpan conditionSpan = default)
        {
            foreach (var source in sources)
            {
                Connect(source, target, kind, conditionSpan);
            }
        }

        private void Connect(
            int source,
            int target,
            LuaControlFlowEdgeKind kind,
            TextSpan conditionSpan = default)
        {
            if ((uint)source >= (uint)_blocks.Count || (uint)target >= (uint)_blocks.Count)
            {
                return;
            }

            _blocks[source].Successors.Add(new LuaControlFlowEdge(target, kind, conditionSpan));
        }

        private HashSet<int> ComputeReachable()
        {
            var reachable = new HashSet<int>();
            var pending = new Queue<int>();
            pending.Enqueue(_entry);
            while (pending.TryDequeue(out var id))
            {
                if (!reachable.Add(id))
                {
                    continue;
                }

                foreach (var edge in _blocks[id].Successors)
                {
                    pending.Enqueue(edge.TargetBlockId);
                }
            }

            return reachable;
        }

        private string? GetIdentifier(LuaSyntaxNode node)
        {
            var token = node.ChildTokens().FirstOrDefault(static token =>
                token.Kind == LuaTokenKind.Identifier && !token.IsMissing);
            if (token is null)
            {
                return null;
            }

            return Encoding.UTF8.GetString(_source.GetSpan(token.Span));
        }

        private sealed class MutableBlock
        {
            public MutableBlock(
                int id,
                LuaControlFlowBlockKind kind,
                TextSpan span,
                IEnumerable<TextSpan> statementSpans)
            {
                Id = id;
                Kind = kind;
                Span = span;
                StatementSpans.AddRange(statementSpans);
            }

            public int Id { get; }

            public LuaControlFlowBlockKind Kind { get; }

            public TextSpan Span { get; }

            public List<TextSpan> StatementSpans { get; } = [];

            public List<LuaControlFlowEdge> Successors { get; } = [];
        }
    }
}
