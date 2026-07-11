using Luac.Syntax.Lexing;

namespace Luac.Syntax.Parsing;

public readonly record struct LuaSyntaxElement
{
    private LuaSyntaxElement(LuaSyntaxNode? node, LuaSyntaxToken? token)
    {
        Node = node;
        Token = token;
    }

    public LuaSyntaxNode? Node { get; }

    public LuaSyntaxToken? Token { get; }

    public bool IsNode => Node is not null;

    public bool IsToken => Token is not null;

    public static implicit operator LuaSyntaxElement(LuaSyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new LuaSyntaxElement(node, null);
    }

    public static implicit operator LuaSyntaxElement(LuaSyntaxToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return new LuaSyntaxElement(null, token);
    }
}
