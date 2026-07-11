using System.Collections.Immutable;
using Luac.Core.Text;

namespace Luac.Syntax.Parsing;

/// <summary>A lossless immutable Lua syntax node composed of nodes and tokens.</summary>
public sealed class LuaSyntaxNode
{
    public LuaSyntaxNode(
        LuaSyntaxKind kind,
        IEnumerable<LuaSyntaxElement> children,
        int emptyPosition = 0)
    {
        ArgumentNullException.ThrowIfNull(children);
        Kind = kind;
        Children = children.ToImmutableArray();
        Span = CalculateSpan(Children, includeTrivia: false, emptyPosition);
        FullSpan = CalculateSpan(Children, includeTrivia: true, emptyPosition);
    }

    public LuaSyntaxKind Kind { get; }

    public ImmutableArray<LuaSyntaxElement> Children { get; }

    public TextSpan Span { get; }

    public TextSpan FullSpan { get; }

    public IEnumerable<LuaSyntaxNode> ChildNodes() =>
        Children.Where(static child => child.IsNode).Select(static child => child.Node!);

    public IEnumerable<Lexing.LuaSyntaxToken> ChildTokens() =>
        Children.Where(static child => child.IsToken).Select(static child => child.Token!);

    public IEnumerable<LuaSyntaxNode> DescendantNodes()
    {
        foreach (var child in Children)
        {
            if (child.Node is null)
            {
                continue;
            }

            yield return child.Node;
            foreach (var descendant in child.Node.DescendantNodes())
            {
                yield return descendant;
            }
        }
    }

    public IEnumerable<Lexing.LuaSyntaxToken> DescendantTokens()
    {
        foreach (var child in Children)
        {
            if (child.Token is not null)
            {
                yield return child.Token;
            }
            else if (child.Node is not null)
            {
                foreach (var token in child.Node.DescendantTokens())
                {
                    yield return token;
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
}
