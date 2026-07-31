using System.Collections.Immutable;
using Lunil.Core.Text;

namespace Lunil.Syntax.Parsing;

/// <summary>A lossless immutable Lua syntax node composed of nodes and tokens.</summary>
public sealed class LuaSyntaxNode
{
    private readonly LuaSyntaxArena? _arena;
    private readonly int _arenaIndex;
    private readonly int _positionDelta;
    private ImmutableArray<LuaSyntaxElement> _children;

    public LuaSyntaxNode(
        LuaSyntaxKind kind,
        IEnumerable<LuaSyntaxElement> children,
        int emptyPosition = 0)
    {
        LunilGuard.NotNull(children);
        Kind = kind;
        _children = children.ToImmutableArray();
        Span = CalculateSpan(_children, includeTrivia: false, emptyPosition);
        FullSpan = CalculateSpan(_children, includeTrivia: true, emptyPosition);
    }

    internal LuaSyntaxNode(LuaSyntaxArena arena, int arenaIndex, int positionDelta)
    {
        _arena = arena;
        _arenaIndex = arenaIndex;
        _positionDelta = positionDelta;
        Kind = arena.GetKind(arenaIndex);
        Span = arena.GetSpan(arenaIndex, includeTrivia: false, positionDelta);
        FullSpan = arena.GetSpan(arenaIndex, includeTrivia: true, positionDelta);
    }

    public LuaSyntaxKind Kind { get; }

    public ImmutableArray<LuaSyntaxElement> Children
    {
        get
        {
            if (_children.IsDefault)
            {
                _children = _arena!.GetChildren(_arenaIndex, _positionDelta);
            }

            return _children;
        }
    }

    public TextSpan Span { get; }

    public TextSpan FullSpan { get; }

    internal LuaSyntaxNode WithPositionDelta(int delta)
    {
        if (delta == 0)
        {
            return this;
        }

        if (_arena is not null)
        {
            return _arena.CreateNode(
                _arenaIndex,
                checked(_positionDelta + delta));
        }

        return new LuaSyntaxNode(
            Kind,
            Children.Select(child => ShiftElement(child, delta)),
            checked(Span.Start + delta));
    }

    public IEnumerable<LuaSyntaxNode> ChildNodes() =>
        Children.Where(static child => child.IsNode).Select(static child => child.Node!);

    public IEnumerable<Lexing.LuaSyntaxToken> ChildTokens() =>
        Children.Where(static child => child.IsToken).Select(static child => child.Token!);

    public IEnumerable<LuaSyntaxNode> DescendantNodes()
    {
        var stack = new Stack<LuaSyntaxNode>();
        var children = Children;
        for (var index = children.Length - 1; index >= 0; index--)
        {
            if (children[index].Node is { } child)
            {
                stack.Push(child);
            }
        }

        while (stack.Count != 0)
        {
            var node = stack.Pop();
            yield return node;
            children = node.Children;
            for (var index = children.Length - 1; index >= 0; index--)
            {
                if (children[index].Node is { } child)
                {
                    stack.Push(child);
                }
            }
        }
    }

    public IEnumerable<Lexing.LuaSyntaxToken> DescendantTokens()
    {
        var stack = new Stack<LuaSyntaxElement>();
        var children = Children;
        for (var index = children.Length - 1; index >= 0; index--)
        {
            stack.Push(children[index]);
        }

        while (stack.Count != 0)
        {
            var child = stack.Pop();
            if (child.Token is { } token)
            {
                yield return token;
            }
            else if (child.Node is { } node)
            {
                children = node.Children;
                for (var index = children.Length - 1; index >= 0; index--)
                {
                    stack.Push(children[index]);
                }
            }
        }
    }

    private static TextSpan CalculateSpan(
        ImmutableArray<LuaSyntaxElement> children,
        bool includeTrivia,
        int emptyPosition)
    {
        int? start = null;
        var end = 0;

        foreach (var child in children)
        {
            var span = child.Node is not null
                ? includeTrivia ? child.Node.FullSpan : child.Node.Span
                : child.Token is not null
                    ? includeTrivia ? child.Token.FullSpan : child.Token.Span
                    : default;

            if (child.Node is null && child.Token is null)
            {
                continue;
            }

            start ??= span.Start;
            end = span.End;
        }

        return start is int value
            ? TextSpan.FromBounds(value, end)
            : new TextSpan(emptyPosition, 0);
    }

    private static LuaSyntaxElement ShiftElement(LuaSyntaxElement element, int delta)
    {
        if (element.Node is { } node)
        {
            return node.WithPositionDelta(delta);
        }

        var token = element.Token!;
        return new Lexing.LuaSyntaxToken(
            token.Kind,
            new TextSpan(checked(token.Span.Start + delta), token.Span.Length),
            [.. token.LeadingTrivia.Select(trivia => trivia with
            {
                Span = new TextSpan(checked(trivia.Span.Start + delta), trivia.Span.Length),
            })])
        {
            Value = token.Value,
            IsMissing = token.IsMissing,
        };
    }
}
