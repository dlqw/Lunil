using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Lunil.Core.Text;
using Lunil.Syntax.Lexing;

namespace Lunil.Syntax.Parsing;

/// <summary>Retained compact-tree counts before compatibility nodes are materialized.</summary>
public sealed record LuaCompactSyntaxMetrics(
    int NodeCount,
    int TokenCount,
    int TriviaCount,
    int ElementCount);

internal sealed class LuaSyntaxArena
{
    private readonly ImmutableArray<NodeEntry> _nodes;
    private readonly ImmutableArray<ElementEntry> _elements;
    private readonly ImmutableArray<TokenEntry> _tokens;
    private readonly ImmutableArray<LuaSyntaxTrivia> _trivia;

    private LuaSyntaxArena(
        ImmutableArray<NodeEntry> nodes,
        ImmutableArray<ElementEntry> elements,
        ImmutableArray<TokenEntry> tokens,
        ImmutableArray<LuaSyntaxTrivia> trivia,
        int rootIndex)
    {
        _nodes = nodes;
        _elements = elements;
        _tokens = tokens;
        _trivia = trivia;
        RootIndex = rootIndex;
        Metrics = new LuaCompactSyntaxMetrics(
            nodes.Length,
            tokens.Length,
            trivia.Length,
            elements.Length);
    }

    public int RootIndex { get; }

    public LuaCompactSyntaxMetrics Metrics { get; }

    public static LuaSyntaxArena Create(
        LuaSyntaxNode root,
        int estimatedNodeCount,
        int estimatedTokenCount)
    {
        var nodes = new List<NodeEntry>(Math.Max(1, estimatedNodeCount));
        var elements = new List<ElementEntry>(Math.Max(2, estimatedNodeCount * 2));
        var tokens = new List<TokenEntry>(Math.Max(1, estimatedTokenCount));
        var trivia = new List<LuaSyntaxTrivia>(Math.Max(1, estimatedTokenCount));
        var nodeIndices = new Dictionary<LuaSyntaxNode, int>(ReferenceComparer<LuaSyntaxNode>.Instance);
        var tokenIndices = new Dictionary<LuaSyntaxToken, int>(ReferenceComparer<LuaSyntaxToken>.Instance);
        var stack = new Stack<(LuaSyntaxNode Node, bool Visited)>();
        stack.Push((root, false));
        while (stack.Count != 0)
        {
            var (node, visited) = stack.Pop();
            if (nodeIndices.ContainsKey(node))
            {
                continue;
            }

            if (!visited)
            {
                stack.Push((node, true));
                var children = node.Children;
                for (var index = children.Length - 1; index >= 0; index--)
                {
                    if (children[index].Node is { } child && !nodeIndices.ContainsKey(child))
                    {
                        stack.Push((child, false));
                    }
                }

                continue;
            }

            var childStart = elements.Count;
            foreach (var child in node.Children)
            {
                if (child.Node is { } childNode)
                {
                    elements.Add(new ElementEntry(true, nodeIndices[childNode]));
                    continue;
                }

                var token = child.Token!;
                if (!tokenIndices.TryGetValue(token, out var tokenIndex))
                {
                    var triviaStart = trivia.Count;
                    trivia.AddRange(token.LeadingTrivia);
                    tokenIndex = tokens.Count;
                    tokens.Add(new TokenEntry(
                        token,
                        token.Kind,
                        token.Span,
                        triviaStart,
                        token.LeadingTrivia.Length,
                        token.Value,
                        token.IsMissing));
                    tokenIndices.Add(token, tokenIndex);
                }

                elements.Add(new ElementEntry(false, tokenIndex));
            }

            var nodeIndex = nodes.Count;
            nodes.Add(new NodeEntry(
                node.Kind,
                childStart,
                elements.Count - childStart,
                node.Span,
                node.FullSpan));
            nodeIndices.Add(node, nodeIndex);
        }

        return new LuaSyntaxArena(
            nodes.ToImmutableArray(),
            elements.ToImmutableArray(),
            tokens.ToImmutableArray(),
            trivia.ToImmutableArray(),
            nodeIndices[root]);
    }

    public LuaSyntaxNode CreateNode(int index, int positionDelta = 0) =>
        new(this, index, positionDelta);

    public LuaSyntaxKind GetKind(int index) => _nodes[index].Kind;

    public TextSpan GetSpan(int index, bool includeTrivia, int positionDelta)
    {
        var span = includeTrivia ? _nodes[index].FullSpan : _nodes[index].Span;
        return Shift(span, positionDelta);
    }

    public ImmutableArray<LuaSyntaxElement> GetChildren(int index, int positionDelta)
    {
        var node = _nodes[index];
        var builder = ImmutableArray.CreateBuilder<LuaSyntaxElement>(node.ChildCount);
        for (var offset = 0; offset < node.ChildCount; offset++)
        {
            var element = _elements[node.ChildStart + offset];
            builder.Add(element.IsNode
                ? CreateNode(element.Index, positionDelta)
                : CreateToken(element.Index, positionDelta));
        }

        return builder.MoveToImmutable();
    }

    private LuaSyntaxToken CreateToken(int index, int positionDelta)
    {
        var token = _tokens[index];
        if (positionDelta == 0)
        {
            return token.Original;
        }

        var leadingTrivia = ImmutableArray.CreateBuilder<LuaSyntaxTrivia>(token.TriviaCount);
        for (var offset = 0; offset < token.TriviaCount; offset++)
        {
            var item = _trivia[token.TriviaStart + offset];
            leadingTrivia.Add(item with { Span = Shift(item.Span, positionDelta) });
        }

        return new LuaSyntaxToken(
            token.Kind,
            Shift(token.Span, positionDelta),
            leadingTrivia.MoveToImmutable())
        {
            Value = token.Value,
            IsMissing = token.IsMissing,
        };
    }

    private static TextSpan Shift(TextSpan span, int delta) =>
        delta == 0 ? span : new TextSpan(checked(span.Start + delta), span.Length);

    private readonly record struct NodeEntry(
        LuaSyntaxKind Kind,
        int ChildStart,
        int ChildCount,
        TextSpan Span,
        TextSpan FullSpan);

    private readonly record struct ElementEntry(bool IsNode, int Index);

    private readonly record struct TokenEntry(
        LuaSyntaxToken Original,
        LuaTokenKind Kind,
        TextSpan Span,
        int TriviaStart,
        int TriviaCount,
        LuaTokenValue? Value,
        bool IsMissing);

    private sealed class ReferenceComparer<T> : IEqualityComparer<T>
        where T : class
    {
        public static ReferenceComparer<T> Instance { get; } = new();

        public bool Equals(T? left, T? right) => ReferenceEquals(left, right);

        public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
    }
}
