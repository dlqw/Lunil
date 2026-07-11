using System.Collections.Immutable;
using Lunil.Core.Text;

namespace Lunil.Syntax.Lexing;

public sealed record LuaSyntaxToken(
    LuaTokenKind Kind,
    TextSpan Span,
    ImmutableArray<LuaSyntaxTrivia> LeadingTrivia)
{
    public LuaTokenValue? Value { get; init; }

    public bool IsMissing { get; init; }

    public TextSpan FullSpan => LeadingTrivia.IsEmpty
        ? Span
        : TextSpan.FromBounds(LeadingTrivia[0].Span.Start, Span.End);
}
